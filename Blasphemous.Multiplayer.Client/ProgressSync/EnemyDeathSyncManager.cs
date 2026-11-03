using System;
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
    private static readonly Dictionary<string, int> pendingRemoteDeaths = new Dictionary<string, int>();
    private static bool applyingRemoteDeath;

    internal static bool TryCaptureLocalHit(EnemyDamageArea damageArea, Hit hit, out EnemyDamageContext context)
    {
        context = default;
        if (applyingRemoteDeath || Main.Multiplayer == null || Main.Multiplayer.NetworkManager == null ||
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

        string key = MakeGenerationKey(sceneName, spawnPointId);
        context = new EnemyDamageContext(enemy, sceneName, spawnPointId, GetGeneration(key), enemy.Stats.Life.Current, enemy.entityCurrentState);
        return true;
    }

    internal static void CompleteLocalHit(EnemyDamageContext context)
    {
        if (!context.Valid || applyingRemoteDeath || context.Enemy == null)
            return;

        string key = MakeGenerationKey(context.SceneName, context.SpawnPointId);
        int current = GetGeneration(key);
        if (current != context.Generation)
            return;

        // Death remains a separate packet so the receiver can run the native death lifecycle.
        if (context.Enemy.Status.Dead)
        {
            generations[key] = current + 1;
            ModLog.Info($"[EnemyDeathSync] Local death scene={context.SceneName}, spawn={context.SpawnPointId}, generation={current}");
            Main.Multiplayer.NetworkManager.SendEnemyDeath(context.SceneName, context.SpawnPointId, current);
            return;
        }

        // Measure actual HP before/after native damage processing so defense and
        // invulnerability are already accounted for, regardless of the enemy handler.
        if (!EnemySyncManager.Supports(context.Enemy))
            return;

        float damage = context.StartingHealth - context.Enemy.Stats.Life.Current;
        bool tookDamage = damage > 0.001f;
        if (tookDamage)
        {
            ModLog.Info($"[EnemyHealthSync] Local damage scene={context.SceneName}, spawn={context.SpawnPointId}, generation={current}, damage={damage:0.##}");
            Main.Multiplayer.NetworkManager.SendEnemyHealth(context.SceneName, context.SpawnPointId, current, damage);
        }

        bool enteredHurtState = context.StartingState != EntityStates.Hurt && context.Enemy.entityCurrentState == EntityStates.Hurt;
        bool handlerNeedsHitCue = tookDamage && EnemySyncManager.TryGetHandler(context.Enemy, out EnemySyncHandler handler) && handler.SyncHitOnDamage;
        if (enteredHurtState || handlerNeedsHitCue)
            Main.Multiplayer.NetworkManager.SendEnemyHit(context.SceneName, context.SpawnPointId, current, (byte)context.Enemy.Status.Orientation);
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

    public static void ApplyRemoteAttack(string sceneName, string spawnPointId, int generation, byte orientation, string sourcePlayerName)
    {
        if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || orientation > 1 ||
            string.IsNullOrEmpty(sourcePlayerName) || Core.LevelManager.currentLevel == null ||
            Core.LevelManager.currentLevel.LevelName != sceneName ||
            !TryGetSpawnedEnemy(sceneName, spawnPointId, generation, out Enemy enemy)) return;

        EnemySync sync = enemy.GetComponent<EnemySync>();
        if (sync == null)
        {
            EnemySyncManager.Attach(enemy);
            sync = enemy.GetComponent<EnemySync>();
        }
        if (sync == null || sync.IsAuthority || !sync.IsExpectedAuthority(sourcePlayerName) ||
            !EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler)) return;

        if (handler is WheelCarrierEnemySyncHandler wheelCarrierHandler)
        {
            ModLog.Info($"[EnemyAttackSync] Applying WheelCarrier attack scene={sceneName}, spawn={spawnPointId}, generation={generation}, source={sourcePlayerName}, orientation={orientation}");
            wheelCarrierHandler.ApplyRemoteAttack(orientation);
        }
    }

    public static void ApplyRemoteHit(string sceneName, string spawnPointId, int generation, byte orientation)
    {
        if (orientation > 1 || !TryGetSpawnedEnemy(sceneName, spawnPointId, generation, out Enemy enemy) ||
            !EnemySyncManager.TryGetHandler(enemy, out EnemySyncHandler handler)) return;
        handler.ApplyRemoteHit(orientation);
    }

    public static void ApplyRemotePosition(string sceneName, string spawnPointId, int generation, Vector3 position, byte orientation, byte state, bool walking, float currentHealth)
    {
        if (orientation > 1 || state > 4 || currentHealth <= 0f || float.IsNaN(currentHealth) || float.IsInfinity(currentHealth) ||
            string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 ||
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

    public static void ApplyRemoteDamage(string sceneName, string spawnPointId, int generation, float damage)
    {
        if (applyingRemoteDeath || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) ||
            generation < 0 || damage <= 0f || float.IsNaN(damage) || float.IsInfinity(damage))
            return;
        if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName)
            return;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        int current = GetGeneration(key);
        if (current != generation)
            return;

        if (!TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint spawnPoint))
        {
            ModLog.Warn($"[EnemyHealthSync] Spawn point not found or identity is ambiguous: scene={sceneName}, spawn={spawnPointId}");
            return;
        }

        Enemy enemy = spawnPoint.SpawnedEnemy;
        if (!EnemySyncManager.Supports(enemy) || enemy.Status.Dead)
            return;

        float newHealth = enemy.Stats.Life.Current - damage;
        if (newHealth <= 0f)
        {
            // Multiple clients can land individually nonlethal hits before receiving each
            // other's packets. Their combined damage may be lethal, so promote that result
            // through the same native death path used by the explicit death packet.
            ApplyRemoteDeath(sceneName, spawnPointId, generation);
            // This client may be the first to observe lethal combined damage from multiple
            // peers. Rebroadcast the resulting death so every session member advances state.
            Main.Multiplayer.NetworkManager.SendEnemyDeath(sceneName, spawnPointId, generation);
            return;
        }

        enemy.SetHealth(newHealth);
        ModLog.Info($"[EnemyHealthSync] Applied remote damage scene={sceneName}, spawn={spawnPointId}, generation={generation}, damage={damage:0.##}, hp={newHealth:0.##}");
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

            ModLog.Warn($"[EnemyDeathSync] Ignored stale/duplicate death scene={sceneName}, spawn={spawnPointId}, packetGeneration={generation}, currentGeneration={current}");
            return;
        }

        // Record and advance once, even when the receiver is in another scene. The pending
        // record survives scene changes and is consumed by CreateEnemy before native spawning.
        generations[key] = current + 1;
        pendingRemoteDeaths[key] = generation;

        if (Core.LevelManager.currentLevel != null && Core.LevelManager.currentLevel.LevelName == sceneName &&
            TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint spawnPoint))
            ReconcilePendingDeath(spawnPoint);

        ModLog.Info($"[EnemyDeathSync] Recorded remote death scene={sceneName}, spawn={spawnPointId}, generation={generation}");
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
            ModLog.Info($"[EnemyDeathSync] Suppressed defeated enemy spawn scene={sceneName}, spawn={spawnPointId}, generation={generation}");
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
                ModLog.Info($"[EnemyDeathSync] Started {enemy.GetType().Name} death presentation; disabledColliders={disabledColliders}");
            }
            else
            {
                enemy.gameObject.SetActive(false);
                ModLog.Info($"[EnemyDeathSync] Remote cleanup disabledColliders={disabledColliders}, enemyDeactivated=true");
            }
        }
        finally
        {
            applyingRemoteDeath = false;
        }

        ModLog.Info($"[EnemyDeathSync] Applied remote death scene={sceneName}, spawn={spawnPointId}, generation={generation}");
    }

    internal static void ClearPendingDeathsForWorldReset()
    {
        // RespawnDeadEnemies clears the game's shared consumed-spawn list globally.
        // Pending remote deaths must therefore be cleared across every scene, while
        // generation counters remain advanced so newly spawned enemies use the
        // same generation on every client.
        int clearedCount = pendingRemoteDeaths.Count;
        pendingRemoteDeaths.Clear();

        ModLog.Info($"[EnemyDeathSync] Cleared {clearedCount} pending remote death(s) across all scenes for world reset; generation counters preserved.");
    }

    public static void ResetSession()
    {
        generations.Clear();
        pendingEnemyHealth.Clear();
        pendingRemoteDeaths.Clear();
        applyingRemoteDeath = false;
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
                ModLog.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {candidate}");
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
                ModLog.Warn($"[EnemyDeathSync] Duplicate spawn identity in {sceneName}: {identity}");
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

internal struct EnemyDamageContext
{
    internal readonly Enemy Enemy;
    internal readonly string SceneName;
    internal readonly string SpawnPointId;
    internal readonly int Generation;
    internal readonly float StartingHealth;
    internal readonly EntityStates StartingState;
    internal readonly bool Valid;

    internal EnemyDamageContext(Enemy enemy, string sceneName, string spawnPointId, int generation, float startingHealth, EntityStates startingState)
    {
        Enemy = enemy;
        SceneName = sceneName;
        SpawnPointId = spawnPointId;
        Generation = generation;
        StartingHealth = startingHealth;
        StartingState = startingState;
        Valid = true;
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
[HarmonyPatch(typeof(EnemySpawnPoint), nameof(EnemySpawnPoint.CreateEnemy))]
internal static class EnemySpawnPointDeathSync_Patch
{
    [HarmonyPrefix]
    private static void Prefix(EnemySpawnPoint __instance)
    {
        EnemyDeathSyncManager.ReconcilePendingDeath(__instance);
    }
}
