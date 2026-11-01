using System;
using System.Collections.Generic;
using System.Globalization;
using Blasphemous.ModdingAPI;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Framework.Damage;
using Gameplay.GameControllers.Entities;
using HarmonyLib;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Synchronizes enemy death only. Enemy health and intermediate damage are intentionally local.
/// </summary>
public static class EnemyDeathSyncManager
{
    private static readonly Dictionary<string, int> generations = new Dictionary<string, int>();
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
        if (enemy == null || enemy.Status.Dead)
            return false;

        string sceneName = Core.LevelManager.currentLevel == null ? null : Core.LevelManager.currentLevel.LevelName;
        if (string.IsNullOrEmpty(sceneName) || !TryFindSpawnPoint(sceneName, enemy, out EnemySpawnPoint spawnPoint, out string spawnPointId))
            return false;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        context = new EnemyDamageContext(enemy, sceneName, spawnPointId, GetGeneration(key));
        return true;
    }

    internal static void CompleteLocalHit(EnemyDamageContext context)
    {
        if (!context.Valid || applyingRemoteDeath || context.Enemy == null || !context.Enemy.Status.Dead)
            return;

        string key = MakeGenerationKey(context.SceneName, context.SpawnPointId);
        int current = GetGeneration(key);
        if (current != context.Generation)
            return;

        generations[key] = current + 1;
        ModLog.Info($"[EnemyDeathSync] Local death scene={context.SceneName}, spawn={context.SpawnPointId}, generation={current}");
        Main.Multiplayer.NetworkManager.SendEnemyDeath(context.SceneName, context.SpawnPointId, current);
    }

    public static void ApplyRemoteDeath(string sceneName, string spawnPointId, int generation)
    {
        if (applyingRemoteDeath || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0)
            return;
        if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName)
            return;

        string key = MakeGenerationKey(sceneName, spawnPointId);
        int current = GetGeneration(key);
        if (current != generation)
        {
            ModLog.Warn($"[EnemyDeathSync] Ignored stale/duplicate death scene={sceneName}, spawn={spawnPointId}, packetGeneration={generation}, currentGeneration={current}");
            return;
        }

        if (!TryFindSpawnPoint(sceneName, spawnPointId, out EnemySpawnPoint spawnPoint))
        {
            ModLog.Warn($"[EnemyDeathSync] Spawn point not found or identity is ambiguous: scene={sceneName}, spawn={spawnPointId}");
            return;
        }

        // Advance before invoking native death callbacks so a duplicate packet cannot kill a later generation.
        generations[key] = current + 1;
        Enemy enemy = spawnPoint.SpawnedEnemy;
        if (enemy == null)
        {
            ModLog.Warn($"[EnemyDeathSync] No spawned enemy for remote death scene={sceneName}, spawn={spawnPointId}");
            return;
        }

        if (!enemy.Status.Dead)
        {
            applyingRemoteDeath = true;
            try
            {
                enemy.SetHealth(0f);
            }
            finally
            {
                applyingRemoteDeath = false;
            }
        }

        ModLog.Info($"[EnemyDeathSync] Applied remote death scene={sceneName}, spawn={spawnPointId}, generation={generation}");
    }

    public static void ResetSession()
    {
        generations.Clear();
        applyingRemoteDeath = false;
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
    internal readonly bool Valid;

    internal EnemyDamageContext(Enemy enemy, string sceneName, string spawnPointId, int generation)
    {
        Enemy = enemy;
        SceneName = sceneName;
        SpawnPointId = spawnPointId;
        Generation = generation;
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
