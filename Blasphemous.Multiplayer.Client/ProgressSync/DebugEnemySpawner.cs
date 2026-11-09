using Blasphemous.ModdingAPI;
using Framework.Audio;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Framework.IA;
using Gameplay.GameControllers.Entities;
using System;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Single-player debug instantiation that deliberately stays outside the authored spawn-point lifecycle.
/// </summary>
internal static class DebugEnemySpawner
{
    internal static bool TrySpawnFool(Vector3 position, out string message)
    {
        return TrySpawn("Fool", "EV11", position, out message);
    }

    internal static bool TrySpawnWheelCarrier(Vector3 position, out string message)
    {
        return TrySpawn("WheelCarrier", "EV10", position, out message);
    }

    private static bool TrySpawn(string prefabName, string enemyId, Vector3 position, out string message)
    {
        if (Main.Multiplayer.NetworkManager != null && Main.Multiplayer.NetworkManager.IsConnected)
        {
            message = "Debug enemy spawning is currently single-player only. Disconnect before using spawn.";
            return false;
        }

        if (!Main.Multiplayer.CurrentlyInLevel || Core.Logic.Penitent == null || Core.Logic.CurrentLevelConfig == null)
        {
            message = "Debug enemy spawning requires an active gameplay scene.";
            return false;
        }

        if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z) ||
            float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
        {
            message = "Spawn coordinates must be finite numbers.";
            return false;
        }

        if (!EnemyPrefabCatalog.TryGetPrefab(prefabName, enemyId, out GameObject prefab))
        {
            message = $"The {prefabName} prefab ({enemyId}) is not available in the loaded game assets.";
            return false;
        }

        position.z = 0f;
        GameObject instance = null;

        try
        {
            LevelInitializer currentLevel = Core.Logic.CurrentLevelConfig;
            Enemy prefabEnemy = FindEnemy(prefab, enemyId);
            Type enemyType = prefabEnemy.GetType();
            int audioCatalogs = RegisterAudioCatalogs(enemyType);

            instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            instance.transform.parent = currentLevel.transform;

            Enemy enemy = FindEnemy(instance, enemyId);
            if (enemy == null)
            {
                UnityEngine.Object.Destroy(instance);
                message = $"The selected {prefabName} prefab did not contain its expected {enemyId} component.";
                return false;
            }

            EnemyBehaviour behaviour = enemy.GetComponentInChildren<EnemyBehaviour>(true);
            if (behaviour != null) behaviour.EnableBehaviourOnLoad = true;

            // Match the useful initialization from EnemySpawnPoint.CreateEnemy without creating
            // a spawn point, assigning an authored identity, or subscribing spawn/death events.
            currentLevel.EnemyStatsImporter?.SetEnemyStats(enemy);
            instance.name = $"Debug{prefabName}_{instance.GetInstanceID()}";

            string sceneName = Core.LevelManager.currentLevel == null ? currentLevel.name : Core.LevelManager.currentLevel.LevelName;
            message = $"Spawned debug {prefabName} at {enemy.transform.position} in {sceneName}.";
            ModLog.Info($"[DebugEnemySpawner] {message} prefabInstance={prefab.GetInstanceID()}, enemyInstance={enemy.GetInstanceID()}, enemyType={enemyType.Name}, audioCatalogs={audioCatalogs}");
            return true;
        }
        catch (Exception ex)
        {
            if (instance != null) UnityEngine.Object.Destroy(instance);
            ModLog.Error($"[DebugEnemySpawner] Failed to instantiate {prefabName} ({enemyId}): {ex}");
            message = $"Failed to spawn the debug {prefabName}. See the mod log for details.";
            return false;
        }
    }

    private static Enemy FindEnemy(GameObject root, string enemyId)
    {
        foreach (Enemy enemy in root.GetComponentsInChildren<Enemy>(true))
        {
            if (enemy != null && string.Equals(enemy.Id, enemyId, StringComparison.Ordinal)) return enemy;
        }
        return null;
    }

    private static int RegisterAudioCatalogs(Type enemyType)
    {
        if (Core.Audio.EnemiesAudioCatalogs == null) return 0;

        // Scene AudioLoader discovers this catalog from authored spawn points. Reproduce its
        // exact-type selection for this debug enemy without modifying the scene or its loader.
        foreach (FMODAudioCatalog catalog in Core.Audio.EnemiesAudioCatalogs)
        {
            if (catalog == null || catalog.Owner == null || catalog.Owner.GetType() != enemyType) continue;

            Core.Audio.RegisterCatalog(catalog);
            int registered = 1;
            if (catalog.HasAssociatedCatalog && catalog.AssociatedCatalog != null)
            {
                Core.Audio.RegisterCatalog(catalog.AssociatedCatalog);
                registered++;
            }
            return registered;
        }

        ModLog.Warn($"[DebugEnemySpawner] No loaded FMOD catalog is owned by {enemyType.Name}.");
        return 0;
    }
}
