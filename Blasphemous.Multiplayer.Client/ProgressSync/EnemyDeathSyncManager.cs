using System;
using Blasphemous.Multiplayer.Client.Network;
using System.Collections.Generic;
using System.Globalization;
using Blasphemous.ModdingAPI;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Framework.Damage;
using Gameplay.GameControllers.Enemies.WheelCarrier;
using Gameplay.GameControllers.Entities;
using Framework.FrameworkCore;
using HarmonyLib;
using Tools.Level.Interactables;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Owns shared enemy identity, health, hit, and death synchronization for registered enemy handlers.
/// </summary>
public static class EnemyDeathSyncManager
{
    private static readonly Dictionary<string, int> generations = new Dictionary<string, int>();
    // Per-incarnation authoritative ordering.  The key intentionally includes the epoch:
    // a new scene host starts its own namespace at revision zero.
    private static readonly Dictionary<string, long> revisions = new Dictionary<string, long>();
    private static readonly Dictionary<string, PendingEnemyHealth> pendingEnemyHealth = new Dictionary<string, PendingEnemyHealth>();
    private static readonly Dictionary<string, List<PendingDamageReport>> pendingDamageReports = new Dictionary<string, List<PendingDamageReport>>();
    private static readonly Dictionary<string, int> pendingRemoteDeaths = new Dictionary<string, int>();

    // Spawn-point identities are authored scene data: the points and their IDs do
    // not change while a scene is loaded. They are therefore resolved once and
    // cached, instead of rebuilding every identity string for every lookup.
    // Snapshot reception is the hot path here (one lookup per packet per enemy),
    // and the previous per-packet FindObjectsOfType + string-building scan was a
    // steady source of follower-side GC pressure in enemy-dense scenes.
    private static string spawnPointCacheScene;
    private static readonly List<SpawnPointCacheEntry> spawnPointCache = new List<SpawnPointCacheEntry>();
    private static readonly Dictionary<string, EnemySpawnPoint> spawnPointsByIdentity = new Dictionary<string, EnemySpawnPoint>();
    private static readonly HashSet<string> ambiguousSpawnPointIds = new HashSet<string>();

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
        if (damageArea == null) return false;
        return TryCaptureLocalHit(damageArea.OwnerEntity as Enemy, hit, out context);
    }

    internal static bool TryCaptureLocalHit(Enemy enemy, Hit hit, out EnemyDamageContext context)
    {
        context = null;
        // WheelCarrier.Damage wraps both its execution and ordinary damage branches.
        // Its nested EnemyDamageArea call must not open a second context for the same hit.
        if (enemy == null || (activeLocalHitContext != null && activeLocalHitContext.Enemy == enemy) ||
            applyingRemoteDeath || applyingRemoteDamage || Main.Multiplayer == null || Main.Multiplayer.NetworkManager == null ||
            !Main.Multiplayer.NetworkManager.IsConnected || Core.Logic.Penitent == null)
            return false;

        GameObject attacker = hit.AttackingEntity;
        Transform playerTransform = Core.Logic.Penitent.transform;
        if (attacker == null || (attacker != Core.Logic.Penitent.gameObject && !attacker.transform.IsChildOf(playerTransform)))
            return false;

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

    /// <summary>
    /// Reports a local death that no enemy.Damage(Hit) call produced. Without this the
    /// WheelCarrier riposte desyncs: Execution.OnUse kills the enemy with KillEntity(),
    /// which never opens a damage context, so the client whose Penitent performed the
    /// riposte destroys its copy silently while the authority keeps a one-hit-point
    /// leftover that still publishes snapshots (frozen AI, unhittable enemy).
    ///
    /// The hook is deliberately narrow: <see cref="EnemySyncHandler.OwnsUnattributedDeath"/>
    /// is only true for a WheelCarrier that is being executed, so no other enemy type
    /// and no other death cause changes behaviour.
    /// </summary>
    internal static void ReportUnattributedLocalDeath(Entity entity)
    {
        // Runs from the KillInstanteneously postfix, i.e. inside the dying entity's own
        // method: Status.Dead was assigned on its first line and nothing can have
        // destroyed the object yet, so no liveness re-check is needed here.
        if (!(entity is Enemy enemy) || !enemy.Status.Dead)
            return;
        if (applyingRemoteDeath || applyingRemoteDamage)
            return;
        if (!EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler) || !handler.OwnsUnattributedDeath)
            return;
        // A death produced by a local hit is already reported by CompleteLocalHit.
        if (activeLocalHitContext != null && activeLocalHitContext.Enemy == enemy)
            return;
        if (Main.Multiplayer == null || Main.Multiplayer.NetworkManager == null || !Main.Multiplayer.NetworkManager.IsConnected)
            return;
        if (!TryGetSyncIdentity(enemy, out string sceneName, out string spawnPointId, out int generation))
            return;
        if (GetGeneration(MakeGenerationKey(sceneName, spawnPointId)) != generation)
            return;
        if (!SceneAuthorityState.TryGet(sceneName, out string hostName, out long epoch) || string.IsNullOrEmpty(hostName))
            return;

        if (string.Equals(hostName, Main.Multiplayer.PlayerName, StringComparison.Ordinal))
        {
            SharedDiagnosticLogger.Info($"[EnemyDeathSync] Authority committed unattributed death scene={sceneName}, spawn={spawnPointId}, generation={generation}, type={enemy.GetType().Name}");
            CommitAuthorityDeath(sceneName, spawnPointId, generation, epoch);
            return;
        }

        // Deterministic id per enemy incarnation: the authority's existing hit-id dedupe
        // then collapses repeated kills of the same corpse (Execution.Damage can call
        // KillEntity a second time) into a single report.
        string hitId = "unattributed-death:" + sceneName + ":" + spawnPointId + ":" + generation.ToString(CultureInfo.InvariantCulture);
        // Report enough damage to reach zero so the authority commits the same death.
        float lethalDamage = Mathf.Max(enemy.Stats.Life.Final, 1f) + enemy.Stats.Defense.Final;
        SharedDiagnosticLogger.Info($"[EnemyDamageSync] Reporting unattributed lethal hit scene={sceneName}, spawn={spawnPointId}, generation={generation}, type={enemy.GetType().Name}, damage={lethalDamage:0.###}, hit={hitId}");
        Main.Multiplayer.NetworkManager.SendEnemyDamageReport(sceneName, spawnPointId, generation, epoch,
            GetRevision(sceneName, spawnPointId, generation, epoch), hitId, lethalDamage, string.Empty, 0, (byte)enemy.Status.Orientation);
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
                long revision = AdvanceRevision(context.SceneName, context.SpawnPointId, current, context.Epoch, "local nonlethal damage");
                if (enteredHurtState || handlerNeedsHitCue)
                    Main.Multiplayer.NetworkManager.SendEnemyHit(context.SceneName, context.SpawnPointId, current, context.Epoch, revision, Main.Multiplayer.PlayerName, (byte)context.DamageType, (byte)context.Enemy.Status.Orientation);
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
            context.Epoch, GetRevision(context.SceneName, context.SpawnPointId, current, context.Epoch), hitId, context.ResolvedDamage, context.ImpactAudioId, (byte)context.DamageType, (byte)context.Enemy.Status.Orientation);
    }

    private static void CommitAuthorityDeath(string sceneName, string spawnPointId, int generation, long epoch)
    {
        if (!SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) || currentEpoch != epoch ||
            !string.Equals(hostName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal))
            return;
        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (GetGeneration(key) != generation) return;
        long revision = AdvanceRevision(sceneName, spawnPointId, generation, epoch, "committed death");
        generations[key] = generation + 1;
        SharedDiagnosticLogger.Info($"[EnemyDeathSync] Authority committed death scene={sceneName}, spawn={spawnPointId}, generation={generation}, epoch={epoch}, revision={revision}");
        Main.Multiplayer.NetworkManager.SendEnemyDeath(sceneName, spawnPointId, generation, epoch, revision);
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
        int generation, long epoch, long observedRevision, string hitId, float resolvedDamage, string impactAudioId, byte damageType, byte orientation)
    {
        if (string.IsNullOrEmpty(sourcePlayerName) || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) ||
            generation < 0 || epoch < 0 || observedRevision < 0 || string.IsNullOrEmpty(hitId) || resolvedDamage <= 0f ||
            float.IsNaN(resolvedDamage) || float.IsInfinity(resolvedDamage) || damageType > 5 || orientation > 1)
            return;
        if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName ||
            !SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) || currentEpoch != epoch ||
            !string.Equals(hostName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal))
            return;

        // Nothing logged the arrival of a report before it was applied, so "the host learned about
        // the death late" and "the hit happened late" were indistinguishable in the logs.
        SharedDiagnosticLogger.Info($"[EnemyDamageSync] Received damage report scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, damage={resolvedDamage:0.###}, damageType={damageType}, hit={hitId}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");

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
            QueuePendingDamageReport(key, sourcePlayerName, hitId, generation, resolvedDamage, impactAudioId, damageType, epoch, orientation);
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
        if (reports.Count >= 512)
        {
            SharedDiagnosticLogger.Warn($"[EnemyDamageSync] Pending host damage queue full; dropped hit sceneKey={key}, generation={generation}, source={sourcePlayerName}, hit={hitId}");
            return;
        }
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
                long revision = AdvanceRevision(sceneName, spawnPointId, generation, epoch, "reconciled nonlethal damage");
                if (enteredHurtState || handler.SyncHitOnDamage || !wasActive)
                    Main.Multiplayer.NetworkManager.SendEnemyHit(sceneName, spawnPointId, generation, epoch, revision, sourcePlayerName, damageType, orientation);
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

    public static void ApplyRemoteAttack(string sceneName, string spawnPointId, int generation, long epoch, long revision, byte orientation, string sourcePlayerName)
    {
        SharedDiagnosticLogger.Info($"[EnemyAttackSyncDiag] Received attack scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, orientation={orientation}, localScene={Core.LevelManager.currentLevel?.LevelName}");

        if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || epoch < 0 ||
            revision < 0 || orientation > 1 || string.IsNullOrEmpty(sourcePlayerName))
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
        if (!AcceptRevision(sceneName, spawnPointId, generation, epoch, revision, "attack")) return;

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

    public static void ApplyRemoteHit(string authorityPlayerName, string hitSourcePlayerName, string sceneName, string spawnPointId, int generation, long epoch, long revision, byte damageType, byte orientation)
    {
        if (damageType > 5 || orientation > 1 || string.IsNullOrEmpty(authorityPlayerName) || string.IsNullOrEmpty(hitSourcePlayerName) ||
            !SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) || currentEpoch != epoch ||
            !string.Equals(hostName, authorityPlayerName, StringComparison.Ordinal) ||
            revision < 0 || !AcceptRevision(sceneName, spawnPointId, generation, epoch, revision, "hit") ||
            !TryGetSpawnedEnemy(sceneName, spawnPointId, generation, out Enemy enemy) ||
            !EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler)) return;
        if (string.Equals(hitSourcePlayerName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal)) return;
        handler.ApplyRemoteHit(orientation);
    }

    internal static void ReportWheelCarrierStateChange(WheelCarrier wheelCarrier, byte state, string source)
    {
        if (wheelCarrier == null || state > 2 || Main.Multiplayer == null || Main.Multiplayer.NetworkManager == null ||
            !Main.Multiplayer.NetworkManager.IsConnected ||
            !TryGetSyncIdentity(wheelCarrier, out string sceneName, out string spawnPointId, out int generation) ||
            !SceneAuthorityState.TryGet(sceneName, out string hostName, out long epoch) || string.IsNullOrEmpty(hostName))
            return;

        if (string.Equals(hostName, Main.Multiplayer.PlayerName, StringComparison.Ordinal))
        {
            AdvanceRevision(sceneName, spawnPointId, generation, epoch, "local WheelCarrier special-state transition");
            return;
        }

        long observedRevision = GetRevision(sceneName, spawnPointId, generation, epoch);
        // The executionable window is Enemy.StuntTime, counted by StuntBehaviour on the owner's
        // Execution prefab: logging the deadline next to the report is what makes it comparable
        // with the death timestamp on another client.
        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Report send scene={sceneName}, spawn={spawnPointId}, generation={generation}, state={state}, source={source}, epoch={epoch}, observedRevision={observedRevision}, orientation={(byte)wheelCarrier.Status.Orientation}, stuntTime={(state == 2 ? wheelCarrier.StuntTime : 0f):0.000}, expiresIn={(state == 2 ? wheelCarrier.StuntTime : 0f):0.000}, life={wheelCarrier.Stats.Life.Current:0.###}, time={Time.realtimeSinceStartup:0.000}");
        Main.Multiplayer.NetworkManager.SendEnemyStateReport(sceneName, spawnPointId, generation, epoch, observedRevision, state,
            (byte)wheelCarrier.Status.Orientation);
        SharedDiagnosticLogger.Info($"[WheelCarrierStateSync] Reported local state scene={sceneName}, spawn={spawnPointId}, generation={generation}, state={state}, epoch={epoch}");
    }

    public static void ApplyRemoteWheelCarrierStateReport(string sourcePlayerName, string sceneName, string spawnPointId,
        int generation, long epoch, long observedRevision, byte state, byte orientation)
    {
        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Host report received scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, state={state}, epoch={epoch}, observedRevision={observedRevision}, orientation={orientation}, time={Time.realtimeSinceStartup:0.000}");
        string rejection = null;
        string hostName = null;
        long currentEpoch = -1;
        EnemySpawnPoint point = null;
        if (string.IsNullOrEmpty(sourcePlayerName) || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) ||
            generation < 0 || epoch < 0 || observedRevision < 0 || state > 2 || orientation > 1)
            rejection = "invalid report fields";
        else if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName)
            rejection = $"host scene mismatch current={Core.LevelManager.currentLevel?.LevelName}";
        else if (!SceneAuthorityState.TryGet(sceneName, out hostName, out currentEpoch) || currentEpoch != epoch)
            rejection = $"authority epoch mismatch host={hostName}, currentEpoch={currentEpoch}";
        else if (!string.Equals(hostName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal))
            rejection = $"local player is not host host={hostName}, local={Main.Multiplayer?.PlayerName}";
        else if (string.Equals(hostName, sourcePlayerName, StringComparison.Ordinal))
            rejection = "source is host";
        else if (GetGeneration(MakeGenerationKey(sceneName, spawnPointId)) != generation)
            rejection = $"generation mismatch current={GetGeneration(MakeGenerationKey(sceneName, spawnPointId))}";
        else if (!TryFindSpawnPoint(sceneName, spawnPointId, out point))
            rejection = "spawn point not found";

        if (rejection != null)
        {
            SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Host report rejected scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, state={state}, epoch={epoch}, observedRevision={observedRevision}, reason={rejection}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        if (point.SpawnedEnemy == null)
            point.CreateEnemy();
        if (!(point.SpawnedEnemy is WheelCarrier wheelCarrier))
        {
            SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Host report rejected scene={sceneName}, spawn={spawnPointId}, source={sourcePlayerName}, reason=spawned enemy is {point.SpawnedEnemy?.GetType().Name ?? "null"}");
            return;
        }

        EnemySyncManager.Attach(wheelCarrier);
        WheelCarrierEnemySyncHandler handler = wheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null)
        {
            SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Host report rejected scene={sceneName}, spawn={spawnPointId}, source={sourcePlayerName}, reason=sync handler missing");
            return;
        }
        byte currentState = handler.SpecialState;
        if (currentState == state)
        {
            // Repeated reports are the host's liveness signal for the mirrored state;
            // they must not be dropped or the mirror looks unconfirmed forever.
            handler.ApplyRemoteSpecialState(state, orientation, sourcePlayerName, false);
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Host report redundant scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, requestedState={state}, hostState={currentState}, epoch={epoch}, observedRevision={observedRevision}, hostRevision={GetRevision(sceneName, spawnPointId, generation, epoch)}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Host applying report scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, beforeState={currentState}, requestedState={state}, epoch={epoch}, observedRevision={observedRevision}, hostRevision={GetRevision(sceneName, spawnPointId, generation, epoch)}, time={Time.realtimeSinceStartup:0.000}");
        handler.ApplyRemoteSpecialState(state, orientation, sourcePlayerName, false);
        long revision = AdvanceRevision(sceneName, spawnPointId, generation, epoch, "WheelCarrier special-state transition");
        SharedDiagnosticLogger.Info($"[WheelCarrierStateSync] Host mirrors follower state (presentation only) scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, state={state}, epoch={epoch}, revision={revision}");
    }

    /// <summary>
    /// Announces that the local player consumed the execution interaction on this enemy.
    ///
    /// This is deliberately *not* part of the state report: the executionable state and the
    /// execution itself are different moments (the timed attack creates the prompt, E/Y starts
    /// the finisher), and only the second one has a presentation to replay. It is also not the
    /// death report, even though the owner's kill happens at the same instant: the observer has
    /// to start the clip, and a lone death cannot be told apart from an interrupted execution.
    /// </summary>
    internal static void ReportWheelCarrierExecutionStarted(WheelCarrier wheelCarrier, Execution execution)
    {
        if (wheelCarrier == null || execution == null || Main.Multiplayer == null ||
            Main.Multiplayer.NetworkManager == null || !Main.Multiplayer.NetworkManager.IsConnected ||
            !TryGetSyncIdentity(wheelCarrier, out string sceneName, out string spawnPointId, out int generation) ||
            !SceneAuthorityState.TryGet(sceneName, out string hostName, out long epoch) || string.IsNullOrEmpty(hostName))
            return;

        // Which side of the enemy the executing player is standing on. The owner is the only
        // client that can answer this: PlayerDirection is measured against the local Penitent,
        // and an observer's Penitent is not the one performing the finisher.
        byte side = (byte)execution.PlayerDirection;
        if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Info($"[WheelCarrierExecutionDiag] Report send scene={sceneName}, spawn={spawnPointId}, generation={generation}, epoch={epoch}, side={side}, enemyInstance={wheelCarrier.GetInstanceID()}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        Main.Multiplayer.NetworkManager.SendEnemyExecutionStarted(sceneName, spawnPointId, generation, epoch, side);
    }

    public static void ApplyRemoteExecutionStarted(string sourcePlayerName, string sceneName, string spawnPointId, int generation, long epoch, byte side)
    {
        if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Info($"[WheelCarrierExecutionDiag] Report received scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, epoch={epoch}, side={side}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        string rejection = null;
        EnemySpawnPoint point = null;
        if (string.IsNullOrEmpty(sourcePlayerName) || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) ||
            generation < 0 || epoch < 0 || side > 2)
            rejection = "invalid report fields";
        else if (string.Equals(sourcePlayerName, Main.Multiplayer?.PlayerName, StringComparison.Ordinal))
            rejection = "source is local player";
        else if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName)
            rejection = $"scene mismatch current={Core.LevelManager.currentLevel?.LevelName}";
        else if (!SceneAuthorityState.TryGet(sceneName, out string hostName, out long currentEpoch) || currentEpoch != epoch)
            rejection = $"authority epoch mismatch host={hostName}, currentEpoch={currentEpoch}";
        else if (GetGeneration(MakeGenerationKey(sceneName, spawnPointId)) != generation)
            rejection = $"generation mismatch current={GetGeneration(MakeGenerationKey(sceneName, spawnPointId))}";
        else if (!TryFindSpawnPoint(sceneName, spawnPointId, out point))
            rejection = "spawn point not found";

        if (rejection != null)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Report rejected scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, side={side}, reason={rejection}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        if (!(point.SpawnedEnemy is WheelCarrier wheelCarrier))
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Report rejected scene={sceneName}, spawn={spawnPointId}, source={sourcePlayerName}, reason=spawned enemy is {point.SpawnedEnemy?.GetType().Name ?? "null"}");
            return;
        }

        EnemySyncManager.Attach(wheelCarrier);
        WheelCarrierEnemySyncHandler handler = wheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Report rejected scene={sceneName}, spawn={spawnPointId}, source={sourcePlayerName}, reason=sync handler missing");
            return;
        }

        handler.ApplyRemoteExecutionStart(sourcePlayerName, side);
    }

    public static void ApplyRemotePosition(string sceneName, string spawnPointId, int generation, long epoch, long revision, Vector3 position, byte orientation, byte state, byte specialState, bool walking, float currentHealth)
    {
        if (orientation > 1 || state > 4 || specialState > 2 || currentHealth <= 0f || float.IsNaN(currentHealth) || float.IsInfinity(currentHealth) ||
            string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || epoch < 0 || revision < 0 ||
            !SceneAuthorityState.TryGet(sceneName, out string assignedHost, out long currentEpoch) ||
            string.IsNullOrEmpty(assignedHost) || epoch != currentEpoch ||
            Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName ||
            GetGeneration(MakeGenerationKey(sceneName, spawnPointId)) != generation) return;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (!AcceptRevision(sceneName, spawnPointId, generation, epoch, revision, "snapshot")) return;
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
        if (!sync.NotePeerSnapshot(generation, revision)) return;
        sync.ApplySnapshot(position, orientation, state, specialState, walking, currentHealth);
        pendingEnemyHealth.Remove(key);
    }

    internal static bool TryTakePendingRemoteHealth(Enemy enemy, int generation, out float currentHealth)
    {
        currentHealth = 0f;
        // This is polled every frame by every follower EnemySync, and resolving the
        // identity costs a scene-wide spawn-point scan. Pending health only exists
        // between a snapshot packet landing and the next snapshot applying, so the
        // empty case (virtually every frame) must not pay for the scan.
        if (pendingEnemyHealth.Count == 0) return false;
        if (!EnemySyncManager.Supports(enemy) || !TryGetSyncIdentity(enemy, out string sceneName, out string spawnPointId, out int currentGeneration) ||
            currentGeneration != generation) return false;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        if (!pendingEnemyHealth.TryGetValue(key, out PendingEnemyHealth pending) || pending.Generation != generation) return false;
        pendingEnemyHealth.Remove(key);
        currentHealth = pending.CurrentHealth;
        return true;
    }

    public static void ApplyRemoteDeath(string sceneName, string spawnPointId, int generation, long epoch, long revision)
    {
        if (applyingRemoteDeath || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || epoch < 0 || revision < 0)
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
        if (!AcceptRevision(sceneName, spawnPointId, generation, epoch, revision, "death")) return;
        generations[key] = current + 1;
        pendingRemoteDeaths[key] = generation;

        if (Core.LevelManager.currentLevel != null && Core.LevelManager.currentLevel.LevelName == sceneName &&
            TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint spawnPoint))
            ReconcilePendingDeath(spawnPoint);

        SharedDiagnosticLogger.Info($"[EnemyDeathSync] Recorded remote death scene={sceneName}, spawn={spawnPointId}, generation={generation}, epoch={epoch}, revision={revision}");
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
        revisions.Clear();
        pendingEnemyHealth.Clear();
        pendingRemoteDeaths.Clear();
        pendingDamageReports.Clear();
        spawnPointCacheScene = null;
        spawnPointCache.Clear();
        spawnPointsByIdentity.Clear();
        ambiguousSpawnPointIds.Clear();
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

    internal static long GetRevision(string sceneName, string spawnPointId, int generation, long epoch)
    {
        string key = MakeRevisionKey(sceneName, spawnPointId, generation, epoch);
        return revisions.TryGetValue(key, out long revision) ? revision : 0;
    }

    private static long AdvanceRevision(string sceneName, string spawnPointId, int generation, long epoch, string reason)
    {
        string key = MakeRevisionKey(sceneName, spawnPointId, generation, epoch);
        long revision = GetRevision(sceneName, spawnPointId, generation, epoch) + 1;
        revisions[key] = revision;
        SharedDiagnosticLogger.Info($"[EnemyRevision] Advanced scene={sceneName}, spawn={spawnPointId}, generation={generation}, epoch={epoch}, revision={revision}, reason={reason}");
        return revision;
    }

    private static bool AcceptRevision(string sceneName, string spawnPointId, int generation, long epoch, long revision, string kind)
    {
        long current = GetRevision(sceneName, spawnPointId, generation, epoch);
        if (revision < current)
        {
            SharedDiagnosticLogger.Warn($"[EnemyRevision] Rejected stale {kind} scene={sceneName}, spawn={spawnPointId}, generation={generation}, epoch={epoch}, packetRevision={revision}, localRevision={current}");
            return false;
        }
        if (revision > current) revisions[MakeRevisionKey(sceneName, spawnPointId, generation, epoch)] = revision;
        return true;
    }

    private static string MakeRevisionKey(string sceneName, string spawnPointId, int generation, long epoch)
    {
        return sceneName + "::" + spawnPointId + "::" + generation + "::" + epoch;
    }

    private static string MakeGenerationKey(string sceneName, string spawnPointId)
    {
        return sceneName + "::" + spawnPointId;
    }

    private sealed class SpawnPointCacheEntry
    {
        internal readonly EnemySpawnPoint Point;
        internal readonly string Identity;

        internal SpawnPointCacheEntry(EnemySpawnPoint point, string identity)
        {
            Point = point;
            Identity = identity;
        }
    }

    private static void EnsureSpawnPointCache(string sceneName, bool forceRebuild = false)
    {
        if (!forceRebuild && spawnPointCache.Count > 0 &&
            string.Equals(spawnPointCacheScene, sceneName, StringComparison.Ordinal))
        {
            bool stale = false;
            foreach (SpawnPointCacheEntry entry in spawnPointCache)
            {
                if (entry.Point == null)
                {
                    stale = true;
                    break;
                }
            }
            if (!stale) return;
        }

        spawnPointCache.Clear();
        spawnPointsByIdentity.Clear();
        ambiguousSpawnPointIds.Clear();
        spawnPointCacheScene = sceneName;

        EnemySpawnPoint[] points = UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>();
        foreach (EnemySpawnPoint point in points)
        {
            if (point == null) continue;

            string identity = BuildSpawnPointId(point);
            spawnPointCache.Add(new SpawnPointCacheEntry(point, identity));
            if (ambiguousSpawnPointIds.Contains(identity)) continue;
            if (spawnPointsByIdentity.ContainsKey(identity))
            {
                spawnPointsByIdentity.Remove(identity);
                ambiguousSpawnPointIds.Add(identity);
                SharedDiagnosticLogger.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {identity}");
            }
            else
            {
                spawnPointsByIdentity.Add(identity, point);
            }
        }
    }

    private static bool TryFindCachedSpawnPoint(Enemy enemy, out EnemySpawnPoint match, out string identity)
    {
        match = null;
        identity = null;
        foreach (SpawnPointCacheEntry entry in spawnPointCache)
        {
            if (entry.Point == null || entry.Point.SpawnedEnemy != enemy)
                continue;

            match = entry.Point;
            identity = entry.Identity;
            return true;
        }
        return false;
    }

    private static bool TryFindSpawnPoint(string sceneName, Enemy enemy, out EnemySpawnPoint match, out string identity)
    {
        match = null;
        identity = null;
        if (enemy == null) return false;

        EnsureSpawnPointCache(sceneName);
        if (!TryFindCachedSpawnPoint(enemy, out match, out identity))
        {
            // A spawn point that appeared after the cache was built (late scene
            // activation) gets one forced rebuild before the lookup gives up,
            // matching the old rescan-every-time behaviour for that rare case.
            EnsureSpawnPointCache(sceneName, true);
            if (!TryFindCachedSpawnPoint(enemy, out match, out identity))
                return false;
        }

        if (ambiguousSpawnPointIds.Contains(identity))
        {
            SharedDiagnosticLogger.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {identity}");
            match = null;
            identity = null;
            return false;
        }
        return true;
    }

    private static bool TryFindSpawnPoint(string sceneName, string identity, out EnemySpawnPoint match)
    {
        match = null;
        if (string.IsNullOrEmpty(identity)) return false;

        EnsureSpawnPointCache(sceneName);
        if (ambiguousSpawnPointIds.Contains(identity))
        {
            SharedDiagnosticLogger.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {identity}");
            return false;
        }
        if (spawnPointsByIdentity.TryGetValue(identity, out match) && match != null)
            return true;

        // Same late-appearance fallback as the by-enemy lookup.
        match = null;
        EnsureSpawnPointCache(sceneName, true);
        if (ambiguousSpawnPointIds.Contains(identity))
        {
            SharedDiagnosticLogger.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {identity}");
            return false;
        }
        return spawnPointsByIdentity.TryGetValue(identity, out match) && match != null;
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

    // Deaths raised outside a captured hit (riposte finishers, environmental kills)
    // still have to reach the authority.
    [HarmonyPostfix]
    private static void Postfix(Entity __instance) => EnemyDeathSyncManager.ReportUnattributedLocalDeath(__instance);
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
