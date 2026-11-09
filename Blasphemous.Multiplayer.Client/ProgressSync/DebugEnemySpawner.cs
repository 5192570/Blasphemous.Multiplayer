using Blasphemous.ModdingAPI;
using Framework.Audio;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Fool;
using Gameplay.GameControllers.Enemies.Framework.IA;
using System;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Single-player debug instantiation that deliberately stays outside the authored spawn-point lifecycle.
/// </summary>
internal static class DebugEnemySpawner
{
    private const string FoolPrefabName = "Fool";
    private const string FoolEnemyId = "EV11";

    internal static bool TrySpawnFool(Vector3 position, out string message)
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

        if (!EnemyPrefabCatalog.TryGetPrefab(FoolPrefabName, FoolEnemyId, out GameObject prefab))
        {
            message = $"The {FoolPrefabName} prefab ({FoolEnemyId}) is not available in the loaded game assets.";
            return false;
        }

        position.z = 0f;
        GameObject instance = null;

        try
        {
            LevelInitializer currentLevel = Core.Logic.CurrentLevelConfig;
            int audioCatalogs = RegisterFoolAudioCatalogs();
            instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            instance.transform.parent = currentLevel.transform;

            Fool fool = instance.GetComponentInChildren<Fool>(true);
            if (fool == null)
            {
                UnityEngine.Object.Destroy(instance);
                message = "The selected Fool prefab did not contain a Fool component.";
                return false;
            }

            EnemyBehaviour behaviour = fool.GetComponentInChildren<EnemyBehaviour>(true);
            if (behaviour != null) behaviour.EnableBehaviourOnLoad = true;

            // Match the useful initialization from EnemySpawnPoint.CreateEnemy without creating
            // a spawn point, assigning an authored identity, or subscribing spawn/death events.
            currentLevel.EnemyStatsImporter?.SetEnemyStats(fool);
            instance.name = "DebugFool_" + instance.GetInstanceID();

            string sceneName = Core.LevelManager.currentLevel == null ? currentLevel.name : Core.LevelManager.currentLevel.LevelName;
            message = $"Spawned debug Fool at {fool.transform.position} in {sceneName}.";
            ModLog.Info($"[DebugEnemySpawner] {message} prefabInstance={prefab.GetInstanceID()}, enemyInstance={fool.GetInstanceID()}, audioCatalogs={audioCatalogs}");
            return true;
        }
        catch (Exception ex)
        {
            if (instance != null) UnityEngine.Object.Destroy(instance);
            ModLog.Error($"[DebugEnemySpawner] Failed to instantiate Fool: {ex}");
            message = "Failed to spawn the debug Fool. See the mod log for details.";
            return false;
        }
    }

    private static int RegisterFoolAudioCatalogs()
    {
        if (Core.Audio.EnemiesAudioCatalogs == null) return 0;

        // Scene AudioLoader discovers this catalog from authored spawn points. Reproduce its
        // exact-type selection for this debug enemy without modifying the scene or its loader.
        foreach (FMODAudioCatalog catalog in Core.Audio.EnemiesAudioCatalogs)
        {
            if (catalog == null || catalog.Owner == null || catalog.Owner.GetType() != typeof(Fool)) continue;

            Core.Audio.RegisterCatalog(catalog);
            int registered = 1;
            if (catalog.HasAssociatedCatalog && catalog.AssociatedCatalog != null)
            {
                Core.Audio.RegisterCatalog(catalog.AssociatedCatalog);
                registered++;
            }
            return registered;
        }

        ModLog.Warn("[DebugEnemySpawner] No loaded FMOD catalog is owned by Fool.");
        return 0;
    }
}
