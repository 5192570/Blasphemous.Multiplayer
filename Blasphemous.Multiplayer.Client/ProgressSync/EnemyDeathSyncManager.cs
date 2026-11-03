using System;
using Blasphemous.Multiplayer.Client.Network;
using System.Collections.Generic;
using System.Globalization;
using Blasphemous.ModdingAPI;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Framework.Damage;
using Gameplay.GameControllers.Entities;
using Framework.FrameworkCore;
using HarmonyLib;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Owns shared enemy identity, health, hit, and death synchronization for registered enemy handlers.
/// </summary>
public static class EnemyDeathSyncManager
{
    private static readonly Dictionary<string, int> generations = new Dictionary<string, int>();
    private static readonly Dictionary<string, PendingEnemyHealth> pendingEnemyHealth = new Dictionary<string, PendingEnemyHealth>();
    private static readonly Dictionary<string, List<PendingDamageReport>> pendingDamageReports = new Dictionary<string, List<PendingDamageReport>>();
    private static readonly Dictionary<string, int> pendingRemoteDeaths = new Dictionary<string, int>();
    private static bool applyingRemoteDeath;
    private static bool applyingRemoteDamage;
    private static EnemyDamageContext activeLocalHitContext;
    private static readonly HashSet<string> processedHitIds = new HashSet<string>();
    private static readonly Queue<string> processedHitOrder = new Queue<string>();
    private const int ProcessedHitLimit = 2048;
    private static readonly System.Reflection.FieldInfo DamageAreaDeltaRecoverTime = AccessTools.Field(typeof(DamageArea), "DeltaRecoverTime");

    internal static bool TryCaptureLocalHit(EnemyDamageArea damageArea, Hit hit, out EnemyDamageContext context)
    {
        context = null;
        if (applyingRemoteDeath || applyingRemoteDamage || Main.Multiplayer == null || Main.Multiplayer.NetworkManager == null ||
            !Main.Multiplayer.NetworkManager.IsConnected || Core.Logic.Penitent == null || damageArea == null)
            return false;

        GameObject attacker = hit.AttackingEntity;
        Transform playerTransform = Core.Logic.Penitent.transform;
        if (attacker == null || (attacker != Core.Logic.Penitent.gameObject && !attacker.transform.IsChildOf(playerTransform)))
            return false;

        Enemy enemy = damageArea.OwnerEntity as Enemy;
        if (!EnemySyncManager.Supports(enemy) || enemy.Status.Dead)
            return false;

        string sceneName = Core.LevelManager.currentLevel == null ? null : Core.LevelManager.currentLevel.LevelName;
        if (string.IsNullOrEmpty(sceneName) || !TryFindSpawnPoint(sceneName, enemy, out EnemySpawnPoint spawnPoint, out string spawnPointId))
            return false;
        if (!SceneAuthorityState.TryGet(sceneName, out string hostName, out long epoch) || string.IsNullOrEmpty(hostName))
            return false;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        context = new EnemyDamageContext(enemy, sceneName, spawnPointId, GetGeneration(key), epoch,
            string.Equals(Main.Multiplayer.PlayerName, hostName, StringComparison.Ordinal),
            enemy.Stats.Life.Current, enemy.entityCurrentState, hit.HitSoundId, hit.DamageType, activeLocalHitContext);
        activeLocalHitContext = context;
        return true;
    }

    internal static void CompleteLocalHit(EnemyDamageContext context)
    {
        if (context == null || !context.Valid || applyingRemoteDeath || context.Enemy == null)
            return;
        if (activeLocalHitContext == context)
            activeLocalHitContext = context.PreviousContext;

        string key = MakeGenerationKey(context.SceneName, context.SpawnPointId);
        int current = GetGeneration(key);
        if (current != context.Generation)
            return;

        if (context.IsAuthority)
        {
            if (context.Enemy.Status.Dead)
                CommitAuthorityDeath(context.SceneName, context.SpawnPointId, current, context.Epoch);
            else if (context.ResolvedDamage > 0f)
            {
                bool enteredHurtState = context.StartingState != EntityStates.Hurt && context.Enemy.entityCurrentState == EntityStates.Hurt;
                bool handlerNeedsHitCue = EnemySyncManager.TryGetHandler(context.Enemy, out EnemySyncHandler handler) && handler.SyncHitOnDamage;
                if (enteredHurtState || handlerNeedsHitCue)
                    Main.Multiplayer.NetworkManager.SendEnemyHit(context.SceneName, context.SpawnPointId, current, context.Epoch, Main.Multiplayer.PlayerName, (byte)context.DamageType, (byte)context.Enemy.Status.Orientation);
            }
            return;
        }

        if (context.ResolvedDamage <= 0f)
            return;
        if (context.Enemy.Status.Dead)
            SharedDiagnosticLogger.Warn($"[EnemyDamageSync] Follower unexpectedly committed local death before reconciliation scene={context.SceneName}, spawn={context.SpawnPointId}, generation={current}");

        string hitId = Guid.NewGuid().ToString("N");
        SharedDiagnosticLogger.Info($"[EnemyDamageSync] Reporting resolved hit scene={context.SceneName}, spawn={context.SpawnPointId}, generation={current}, damage={context.ResolvedDamage:0.###}, hit={hitId}");
        Main.Multiplayer.NetworkManager.SendEnemyDamageReport(context.SceneName, context.SpawnPointId, current,
            context.Epoch, hitId, context.ResolvedDamage, context.ImpactAudioId, (byte)context.DamageType, (byte)context.Enemy.Status.Orientation);
    }

    private static void CommitAuthorityDeath(string sceneName, string spawnPointId, int generation, long epoch)
    {
        if (!SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) || currentEpoch != epoch ||
            !string.Equals(hostName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal))
            return;
        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (GetGeneration(key) != generation) return;
        generations[key] = generation + 1;
        SharedDiagnosticLogger.Info($"[EnemyDeathSync] Authority committed death scene={sceneName}, spawn={spawnPointId}, generation={generation}, epoch={epoch}");
        Main.Multiplayer.NetworkManager.SendEnemyDeath(sceneName, spawnPointId, generation, epoch);
    }

    private static bool ApplyNativeHurtRecovery(Enemy enemy)
    {
        if (enemy == null || DamageAreaDeltaRecoverTime == null) return false;
        EnemyDamageArea[] damageAreas = enemy.GetComponentsInChildren<EnemyDamageArea>(true);
        foreach (EnemyDamageArea damageArea in damageAreas)
        {
            if (damageArea == null) continue;
            object value = DamageAreaDeltaRecoverTime.GetValue(damageArea);
            if (!(value is float elapsed) || elapsed < damageArea.RecoverTime) continue;
            DamageAreaDeltaRecoverTime.SetValue(damageArea, 0f);
            enemy.entityCurrentState = EntityStates.Hurt;
            return true;
        }
        return false;
    }

    internal static EnemyDamageContext ActiveLocalHitContext => activeLocalHitContext;

    internal static bool ShouldSuppressPredictedDeath(Entity entity)
    {
        EnemyDamageContext context = activeLocalHitContext;
        return context != null && context.Valid && context.Enemy == entity && !context.IsAuthority &&
            !applyingRemoteDeath && !applyingRemoteDamage;
    }

    public static void ApplyAuthoritativeDamageReport(string sourcePlayerName, string sceneName, string spawnPointId,
        int generation, long epoch, string hitId, float resolvedDamage, string impactAudioId, byte damageType, byte orientation)
    {
        if (string.IsNullOrEmpty(sourcePlayerName) || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) ||
            generation < 0 || epoch < 0 || string.IsNullOrEmpty(hitId) || resolvedDamage <= 0f ||
            float.IsNaN(resolvedDamage) || float.IsInfinity(resolvedDamage) || damageType > 5 || orientation > 1)
            return;
        if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName ||
            !SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) || currentEpoch != epoch ||
            !string.Equals(hostName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal))
            return;

        string dedupeKey = sourcePlayerName + "::" + hitId;
        if (!processedHitIds.Add(dedupeKey)) return;
        processedHitOrder.Enqueue(dedupeKey);
        while (processedHitOrder.Count > ProcessedHitLimit) processedHitIds.Remove(processedHitOrder.Dequeue());

        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (GetGeneration(key) != generation) return;
        if (!TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint point))
        {
            QueuePendingDamageReport(key, sourcePlayerName, hitId, generation, resolvedDamage, impactAudioId, damageType, epoch, orientation);
            return;
        }
        if (point.SpawnedEnemy == null)
            point.CreateEnemy();
        if (point.SpawnedEnemy == null && point.Consumed)
        {
            pendingDamageReports.Remove(key);
            SharedDiagnosticLogger.Info($"[EnemyDamageSync] Host spawn is already consumed; committing authoritative death scene={sceneName}, spawn={spawnPointId}, generation={generation}");
            CommitAuthorityDeath(sceneName, spawnPointId, generation, epoch);
            return;
        }
        if (point.SpawnedEnemy == null)
        {
            QueuePendingDamageReport(key, sourcePlayerName, hitId, generation, resolvedDamage, impactAudioId, epoch, orientation);
            return;
        }
        Enemy enemy = point.SpawnedEnemy;
        ApplyDamageReportToEnemy(sceneName, spawnPointId, generation, epoch, sourcePlayerName, hitId, resolvedDamage, impactAudioId, damageType, orientation, enemy);
    }

    private static void QueuePendingDamageReport(string key, string sourcePlayerName, string hitId, int generation,
        float resolvedDamage, string impactAudioId, byte damageType, long epoch, byte orientation)
    {
        if (!pendingDamageReports.TryGetValue(key, out List<PendingDamageReport> reports))
        {
            reports = new List<PendingDamageReport>();
            pendingDamageReports.Add(key, reports);
        }
        if (reports.Count >= 64) return;
        reports.Add(new PendingDamageReport(sourcePlayerName, hitId, generation, resolvedDamage, impactAudioId, damageType, epoch, orientation));
        SharedDiagnosticLogger.Info($"[EnemyDamageSync] Queued hit until host enemy spawn sceneKey={key}, generation={generation}, source={sourcePlayerName}, hit={hitId}");
    }

    internal static void ReconcilePendingDamage(EnemySpawnPoint spawnPoint)
    {
        if (spawnPoint == null || Core.LevelManager.currentLevel == null) return;
        string sceneName = Core.LevelManager.currentLevel.LevelName;
        string spawnPointId = BuildSpawnPointId(spawnPoint);
        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (!pendingDamageReports.TryGetValue(key, out List<PendingDamageReport> reports)) return;
        int generation = GetGeneration(key);
        if (!SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) ||
            !string.Equals(hostName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal)) return;

        if (spawnPoint.SpawnedEnemy == null)
        {
            if (!spawnPoint.Consumed) return;
            bool hasCurrentReport = false;
            foreach (PendingDamageReport report in reports)
                if (report.Generation == generation && report.Epoch == currentEpoch) { hasCurrentReport = true; break; }
            pendingDamageReports.Remove(key);
            if (hasCurrentReport)
            {
                SharedDiagnosticLogger.Info($"[EnemyDamageSync] Queued hit found consumed host spawn; committing death scene={sceneName}, spawn={spawnPointId}, generation={generation}");
                CommitAuthorityDeath(sceneName, spawnPointId, generation, currentEpoch);
            }
            return;
        }

        pendingDamageReports.Remove(key);
        foreach (PendingDamageReport report in reports)
        {
            if (report.Generation != generation || report.Epoch != currentEpoch) continue;
            ApplyDamageReportToEnemy(sceneName, spawnPointId, generation, report.Epoch, report.SourcePlayerName, report.HitId,
                report.ResolvedDamage, report.ImpactAudioId, report.DamageType, report.Orientation, spawnPoint.SpawnedEnemy);
            if (GetGeneration(key) != generation) break;
        }
    }

    private static void ApplyDamageReportToEnemy(string sceneName, string spawnPointId, int generation, long epoch,
        string sourcePlayerName, string hitId, float resolvedDamage, string impactAudioId, byte damageType, byte orientation, Enemy enemy)
    {
        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (GetGeneration(key) != generation || !EnemySyncManager.Supports(enemy) || enemy.Status.Dead) return;
        if (!EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler))
        {
            EnemySyncManager.Attach(enemy);
            EnemySyncManager.TryGetHandler(enemy, out handler);
        }
        if (handler == null) return;
        applyingRemoteDamage = true;
        try
        {
            bool wasActive = enemy.gameObject.activeInHierarchy;
            enemy.Damage(resolvedDamage + enemy.Stats.Defense.Final, impactAudioId ?? string.Empty);
            bool dead = enemy.Status.Dead;
            bool enteredHurtState = !dead && ApplyNativeHurtRecovery(enemy);
            if (!dead && !wasActive)
            {
                enemy.entityCurrentState = EntityStates.Hurt;
                enteredHurtState = true;
            }
            handler.ApplyAuthoritativeDamageReaction(dead, orientation);
            if (dead)
            {
                EnemyDamageArea[] damageAreas = enemy.GetComponentsInChildren<EnemyDamageArea>(true);
                foreach (EnemyDamageArea damageArea in damageAreas)
                {
                    if (damageArea != null && damageArea.DamageAreaCollider != null)
                        damageArea.DamageAreaCollider.enabled = false;
                }
            }
            SharedDiagnosticLogger.Info($"[EnemyDamageSync] Host applied hit scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, damage={resolvedDamage:0.###}, hp={enemy.Stats.Life.Current:0.###}, dead={dead}, hit={hitId}");
            if (dead) CommitAuthorityDeath(sceneName, spawnPointId, generation, epoch);
            else
            {
                if (enteredHurtState || handler.SyncHitOnDamage || !wasActive)
                    Main.Multiplayer.NetworkManager.SendEnemyHit(sceneName, spawnPointId, generation, epoch, sourcePlayerName, damageType, orientation);
            }
        }
        finally { applyingRemoteDamage = false; }
    }

    internal static bool TryGetSyncIdentity(Enemy enemy, out string sceneName, out string spawnPointId, out int generation)
    {
        sceneName = Core.LevelManager.currentLevel == null ? null : Core.LevelManager.currentLevel.LevelName;
        spawnPointId = null; generation = -1;
        if (!EnemySyncManager.Supports(enemy) || string.IsNullOrEmpty(sceneName) ||
            !TryFindSpawnPoint(sceneName, enemy, out EnemySpawnPoint point, out spawnPointId)) return false;
        generation = GetGeneration(MakeGenerationKey(sceneName, spawnPointId));
        return true;
    }

    private static bool TryGetSpawnedEnemy(string sceneName, string spawnPointId, int generation, out Enemy enemy)
    {
        enemy = null;
        if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName || generation < 0 ||
            GetGeneration(MakeGenerationKey(sceneName, spawnPointId)) != generation ||
            !TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint point)) return false;
        enemy = point.SpawnedEnemy;
        return EnemySyncManager.Supports(enemy) && !enemy.Status.Dead;
    }

    public static void ApplyRemoteAttack(string sceneName, string spawnPointId, int generation, long epoch, byte orientation, string sourcePlayerName)
    {
        SharedDiagnosticLogger.Info($"[EnemyAttackSyncDiag] Received attack scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, orientation={orientation}, localScene={Core.LevelManager.currentLevel?.LevelName}");

        if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || epoch < 0 ||
            orientation > 1 || string.IsNullOrEmpty(sourcePlayerName))
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: invalid payload scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, orientation={orientation}");
            return;
        }

        if (!SceneAuthorityState.TryGet(sceneName, out string assignedHost, out long currentEpoch) ||
            epoch != currentEpoch || string.IsNullOrEmpty(assignedHost))
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: stale or missing scene authority scene={sceneName}, packetEpoch={epoch}, currentEpoch={currentEpoch}, assignedHost={assignedHost}");
            return;
        }

        if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: scene mismatch packetScene={sceneName}, localScene={Core.LevelManager.currentLevel?.LevelName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}");
            return;
        }

        int localGeneration = GetGeneration(MakeGenerationKey(sceneName, spawnPointId));
        if (localGeneration != generation)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: generation mismatch scene={sceneName}, spawn={spawnPointId}, packetGeneration={generation}, localGeneration={localGeneration}, source={sourcePlayerName}");
            return;
        }

        if (!TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint point))
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: spawn point not found or identity ambiguous scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}");
            return;
        }

        Enemy enemy = point.SpawnedEnemy;
        if (enemy == null)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: spawn point has no spawned enemy scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}");
            return;
        }
        if (!EnemySyncManager.Supports(enemy) || enemy.Status.Dead)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: unsupported or dead enemy type={enemy.GetType().Name}, dead={enemy.Status.Dead}, scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}");
            return;
        }

        EnemySync sync = enemy.GetComponent<EnemySync>();
        if (sync == null)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] EnemySync missing; attempting late attach scene={sceneName}, spawn={spawnPointId}, generation={generation}, enemyInstance={enemy.GetInstanceID()}");
            EnemySyncManager.Attach(enemy);
            sync = enemy.GetComponent<EnemySync>();
        }
        if (sync == null)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: EnemySync unavailable after attach scene={sceneName}, spawn={spawnPointId}, generation={generation}, enemyInstance={enemy.GetInstanceID()}");
            return;
        }
        if (sync.IsAuthority)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: receiver considers itself authoritative scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, enemyInstance={enemy.GetInstanceID()}");
            return;
        }
        if (!sync.IsExpectedAuthority(sourcePlayerName, epoch))
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: sender failed authority validation scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, enemyInstance={enemy.GetInstanceID()}");
            return;
        }
        if (!EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler))
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: sync handler missing scene={sceneName}, spawn={spawnPointId}, generation={generation}, enemyInstance={enemy.GetInstanceID()}");
            return;
        }

        if (handler is WheelCarrierEnemySyncHandler wheelCarrierHandler)
        {
            SharedDiagnosticLogger.Info($"[EnemyAttackSyncDiag] Applying attack scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, orientation={orientation}, enemyInstance={enemy.GetInstanceID()}, localPlayer={Main.Multiplayer?.PlayerName}");
            wheelCarrierHandler.ApplyRemoteAttack(orientation);
        }
        else
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Rejected attack: unexpected handler type={handler.GetType().Name}, scene={sceneName}, spawn={spawnPointId}, generation={generation}, enemyInstance={enemy.GetInstanceID()}");
        }
    }

    public static void ApplyRemoteHit(string authorityPlayerName, string hitSourcePlayerName, string sceneName, string spawnPointId, int generation, long epoch, byte damageType, byte orientation)
    {
        if (damageType > 5 || orientation > 1 || string.IsNullOrEmpty(authorityPlayerName) || string.IsNullOrEmpty(hitSourcePlayerName) ||
            !SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) || currentEpoch != epoch ||
            !string.Equals(hostName, authorityPlayerName, StringComparison.Ordinal) ||
            !TryGetSpawnedEnemy(sceneName, spawnPointId, generation, out Enemy enemy) ||
            !EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler)) return;
        if (string.Equals(hitSourcePlayerName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal)) return;
        handler.ApplyRemoteHit(orientation);
    }

    public static void ApplyRemotePosition(string sceneName, string spawnPointId, int generation, long epoch, Vector3 position, byte orientation, byte state, bool walking, float currentHealth)
    {
        if (orientation > 1 || state > 4 || currentHealth <= 0f || float.IsNaN(currentHealth) || float.IsInfinity(currentHealth) ||
            string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || epoch < 0 ||
            !SceneAuthorityState.TryGet(sceneName, out string assignedHost, out long currentEpoch) ||
            string.IsNullOrEmpty(assignedHost) || epoch != currentEpoch ||
            Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName ||
            GetGeneration(MakeGenerationKey(sceneName, spawnPointId)) != generation) return;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        pendingEnemyHealth[key] = new PendingEnemyHealth(generation, currentHealth);

        if (!TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint point) ||
            !EnemySyncManager.Supports(point.SpawnedEnemy) || point.SpawnedEnemy.Status.Dead)
            return;

        Enemy enemy = point.SpawnedEnemy;
        EnemySync sync = enemy.GetComponent<EnemySync>();
        if (sync == null)
        {
            EnemySyncManager.Attach(enemy);
            sync = enemy.GetComponent<EnemySync>();
        }
        if (sync == null) return;
        sync.NotePeerSnapshot(generation);
        sync.ApplySnapshot(position, orientation, state, walking, currentHealth);
        pendingEnemyHealth.Remove(key);
    }

    internal static bool TryTakePendingRemoteHealth(Enemy enemy, int generation, out float currentHealth)
    {
        currentHealth = 0f;
        if (!EnemySyncManager.Supports(enemy) || !TryGetSyncIdentity(enemy, out string sceneName, out string spawnPointId, out int currentGeneration) ||
            currentGeneration != generation) return false;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (!pendingEnemyHealth.TryGetValue(key, out PendingEnemyHealth pending) || pending.Generation != generation) return false;
        pendingEnemyHealth.Remove(key);
        currentHealth = pending.CurrentHealth;
        return true;
    }

    public static void ApplyRemoteDeath(string sceneName, string spawnPointId, int generation)
    {
        if (applyingRemoteDeath || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0)
            return;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        int current = GetGeneration(key);
        if (current != generation)
        {
            if (pendingRemoteDeaths.TryGetValue(key, out int pendingGeneration) && pendingGeneration == generation)
                return;

            SharedDiagnosticLogger.Warn($"[EnemyDeathSync] Ignored stale/duplicate death scene={sceneName}, spawn={spawnPointId}, packetGeneration={generation}, currentGeneration={current}");
            return;
        }

        // Record and advance once, even when the receiver is in another scene. The pending
        // record survives scene changes and is consumed by CreateEnemy before native spawning.
        generations[key] = current + 1;
        pendingRemoteDeaths[key] = generation;

        if (Core.LevelManager.currentLevel != null && Core.LevelManager.currentLevel.LevelName == sceneName &&
            TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint spawnPoint))
            ReconcilePendingDeath(spawnPoint);

        SharedDiagnosticLogger.Info($"[EnemyDeathSync] Recorded remote death scene={sceneName}, spawn={spawnPointId}, generation={generation}");
    }

    internal static void ReconcilePendingDeath(EnemySpawnPoint spawnPoint)
    {
        if (spawnPoint == null || Core.LevelManager.currentLevel == null)
            return;

        string sceneName = Core.LevelManager.currentLevel.LevelName;
        string spawnPointId = BuildSpawnPointId(spawnPoint);
        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (!pendingRemoteDeaths.TryGetValue(key, out int generation))
            return;

        Enemy enemy = spawnPoint.SpawnedEnemy;
        if (enemy == null)
        {
            // CreateEnemy reads the consumed list before instantiating its prefab.
            Core.Logic.EnemySpawner.AddConsumedSpawner(spawnPoint);
            pendingRemoteDeaths.Remove(key);
            SharedDiagnosticLogger.Info($"[EnemyDeathSync] Suppressed defeated enemy spawn scene={sceneName}, spawn={spawnPointId}, generation={generation}");
            return;
        }

        pendingRemoteDeaths.Remove(key);
        ApplyNativeRemoteDeath(sceneName, spawnPointId, generation, enemy);
    }

    private static void ApplyNativeRemoteDeath(string sceneName, string spawnPointId, int generation, Enemy enemy)
    {
        if (enemy == null || enemy.Status.Dead)
            return;

        if (EnemySyncManager.Supports(enemy))
            EnemySyncManager.Attach(enemy);

        applyingRemoteDeath = true;
        try
        {
            // Preserve native death callbacks so EnemySpawnPoint marks this spawn consumed.
            enemy.SetHealth(0f);
            EnemyDamageArea[] damageAreas = enemy.GetComponentsInChildren<EnemyDamageArea>(true);
            int disabledColliders = 0;
            foreach (EnemyDamageArea damageArea in damageAreas)
            {
                if (damageArea == null)
                    continue;

                Collider2D damageCollider = damageArea.DamageAreaCollider;
                if (damageCollider != null && damageCollider.enabled)
                {
                    damageCollider.enabled = false;
                    disabledColliders++;
                }
            }

            if (EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler))
            {
                handler.ApplyDeath();
                SharedDiagnosticLogger.Info($"[EnemyDeathSync] Started {enemy.GetType().Name} death presentation; disabledColliders={disabledColliders}");
            }
            else
            {
                enemy.gameObject.SetActive(false);
                SharedDiagnosticLogger.Info($"[EnemyDeathSync] Remote cleanup disabledColliders={disabledColliders}, enemyDeactivated=true");
            }
        }
        finally
        {
            applyingRemoteDeath = false;
        }

        SharedDiagnosticLogger.Info($"[EnemyDeathSync] Applied remote death scene={sceneName}, spawn={spawnPointId}, generation={generation}");
    }

    internal static void ClearPendingDeathsForWorldReset()
    {
        // RespawnDeadEnemies clears the game's shared consumed-spawn list globally.
        // Pending remote deaths must therefore be cleared across every scene, while
        // generation counters remain advanced so newly spawned enemies use the
        // same generation on every client.
        int clearedCount = pendingRemoteDeaths.Count;
        pendingRemoteDeaths.Clear();
        int clearedDamageReports = pendingDamageReports.Count;
        pendingDamageReports.Clear();

        SharedDiagnosticLogger.Info($"[EnemyDeathSync] Cleared {clearedCount} pending remote death(s) and {clearedDamageReports} pending damage queue(s) across all scenes for world reset; generation counters preserved.");
    }

    public static void ResetSession()
    {
        generations.Clear();
        pendingEnemyHealth.Clear();
        pendingRemoteDeaths.Clear();
        pendingDamageReports.Clear();
        applyingRemoteDeath = false;
        applyingRemoteDamage = false;
        activeLocalHitContext = null;
        processedHitIds.Clear();
        processedHitOrder.Clear();
    }

    private readonly struct PendingDamageReport
    {
        internal readonly string SourcePlayerName;
        internal readonly string HitId;
        internal readonly int Generation;
        internal readonly float ResolvedDamage;
        internal readonly string ImpactAudioId;
        internal readonly byte DamageType;
        internal readonly long Epoch;
        internal readonly byte Orientation;
        internal PendingDamageReport(string sourcePlayerName, string hitId, int generation, float resolvedDamage, string impactAudioId, byte damageType, long epoch, byte orientation)
        { SourcePlayerName = sourcePlayerName; HitId = hitId; Generation = generation; ResolvedDamage = resolvedDamage; ImpactAudioId = impactAudioId; DamageType = damageType; Epoch = epoch; Orientation = orientation; }
    }

    private readonly struct PendingEnemyHealth
    {
        internal readonly int Generation;
        internal readonly float CurrentHealth;

        internal PendingEnemyHealth(int generation, float currentHealth)
        {
            Generation = generation;
            CurrentHealth = currentHealth;
        }
    }

    private static int GetGeneration(string key)
    {
        return generations.TryGetValue(key, out int generation) ? generation : 0;
    }

    private static string MakeGenerationKey(string sceneName, string spawnPointId)
    {
        return sceneName + "::" + spawnPointId;
    }

    private static bool TryFindSpawnPoint(string sceneName, Enemy enemy, out EnemySpawnPoint match, out string identity)
    {
        match = null;
        identity = null;
        EnemySpawnPoint[] points = UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>();
        foreach (EnemySpawnPoint point in points)
        {
            if (point == null || point.SpawnedEnemy != enemy)
                continue;

            string candidate = BuildSpawnPointId(point);
            int matches = 0;
            foreach (EnemySpawnPoint other in points)
            {
                if (other != null && BuildSpawnPointId(other) == candidate)
                    matches++;
            }

            if (matches != 1)
            {
                SharedDiagnosticLogger.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {candidate}");
                return false;
            }

            match = point;
            identity = candidate;
            return true;
        }
        return false;
    }

    private static bool TryFindSpawnPoint(string sceneName, string identity, out EnemySpawnPoint match)
    {
        match = null;
        EnemySpawnPoint[] points = UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>();
        foreach (EnemySpawnPoint point in points)
        {
            if (point == null || BuildSpawnPointId(point) != identity)
                continue;
            if (match != null)
            {
                SharedDiagnosticLogger.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {identity}");
                match = null;
                return false;
            }
            match = point;
        }
        return match != null;
    }

    private static string BuildSpawnPointId(EnemySpawnPoint point)
    {
        Vector3 position = point.Position;
        string entityName = point.EntityName ?? string.Empty;
        string prefabName = point.SelectedEnemy == null ? string.Empty : point.SelectedEnemy.name;
        return string.Join("|", new string[]
        {
            entityName,
            point.gameObject.name,
            prefabName,
            position.x.ToString("R", CultureInfo.InvariantCulture),
            position.y.ToString("R", CultureInfo.InvariantCulture),
            position.z.ToString("R", CultureInfo.InvariantCulture)
        });
    }
}

internal sealed class EnemyDamageContext
{
    internal readonly Enemy Enemy;
    internal readonly string SceneName;
    internal readonly string SpawnPointId;
    internal readonly int Generation;
    internal readonly long Epoch;
    internal readonly bool IsAuthority;
    internal readonly float StartingHealth;
    internal readonly EntityStates StartingState;
    internal readonly EnemyDamageContext PreviousContext;
    internal readonly string ImpactAudioId;
    internal readonly DamageArea.DamageType DamageType;
    internal float ResolvedDamage;
    internal readonly bool Valid;

    internal EnemyDamageContext(Enemy enemy, string sceneName, string spawnPointId, int generation, long epoch, bool isAuthority,
        float startingHealth, EntityStates startingState, string impactAudioId, DamageArea.DamageType damageType, EnemyDamageContext previousContext)
    {
        Enemy = enemy; SceneName = sceneName; SpawnPointId = spawnPointId; Generation = generation;
        Epoch = epoch; IsAuthority = isAuthority; StartingHealth = startingHealth; StartingState = startingState;
        ImpactAudioId = impactAudioId; DamageType = damageType; PreviousContext = previousContext; Valid = true;
    }
}

[HarmonyPatch(typeof(EnemyDamageArea), nameof(EnemyDamageArea.TakeDamage))]
internal static class EnemyDamageAreaDeathSync_Patch
{
    [HarmonyPrefix]
    private static void Prefix(EnemyDamageArea __instance, Hit hit, out EnemyDamageContext __state)
    {
        EnemyDeathSyncManager.TryCaptureLocalHit(__instance, hit, out __state);
    }

    [HarmonyPostfix]
    private static void Postfix(EnemyDamageContext __state)
    {
        EnemyDeathSyncManager.CompleteLocalHit(__state);
    }
}
[HarmonyPatch(typeof(Entity), nameof(Entity.Damage))]
internal static class EnemyResolvedDamageCapture_Patch
{
    [HarmonyPrefix]
    private static void Prefix(Entity __instance, float amount)
    {
        EnemyDamageContext context = EnemyDeathSyncManager.ActiveLocalHitContext;
        if (context != null && context.Enemy == __instance && __instance.Stats != null)
            context.ResolvedDamage += Mathf.Max(amount - __instance.Stats.Defense.Final, 0f);
    }
}

[HarmonyPatch(typeof(Entity), nameof(Entity.KillInstanteneously))]
internal static class EnemyPredictedDeathSuppression_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(Entity __instance)
    {
        if (!EnemyDeathSyncManager.ShouldSuppressPredictedDeath(__instance)) return true;
        __instance.SetHealth(1f);
        return false;
    }
}
[HarmonyPatch(typeof(EnemySpawnPoint), nameof(EnemySpawnPoint.CreateEnemy))]
internal static class EnemySpawnPointDeathSync_Patch
{
    [HarmonyPrefix]
    private static void Prefix(EnemySpawnPoint __instance)
    {
        EnemyDeathSyncManager.ReconcilePendingDeath(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix(EnemySpawnPoint __instance)
    {
        EnemyDeathSyncManager.ReconcilePendingDamage(__instance);
    }
}
