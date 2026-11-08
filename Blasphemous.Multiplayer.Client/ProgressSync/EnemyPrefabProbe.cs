using Blasphemous.ModdingAPI;
using Gameplay.GameControllers.Entities;
using System;
using System.Collections.Generic;
using Tools.Level.Layout;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Read-only diagnostics for the game's preload catalog and other already-loaded prefab sources.
/// This never loads scenes/assets, instantiates objects, or changes spawn points. Repeated runs
/// deliberately retain prefab-asset references so their lifetime can be checked after transitions.
/// </summary>
internal static class EnemyPrefabProbe
{
    private sealed class ProbeTarget
    {
        internal readonly string Name;
        internal readonly Type EnemyType;

        internal ProbeTarget(string name, Type enemyType)
        {
            Name = name;
            EnemyType = enemyType;
        }
    }

    private sealed class Candidate
    {
        internal readonly GameObject Prefab;
        internal readonly Enemy Enemy;
        internal bool FromPreloadCatalog;
        internal bool FromLoadedAssets;
        internal bool FromSpawnPoint;

        internal Candidate(GameObject prefab, Enemy enemy)
        {
            Prefab = prefab;
            Enemy = enemy;
        }
    }

    private sealed class CachedReference
    {
        internal readonly string TargetName;
        internal readonly string PrefabName;
        internal readonly GameObject Prefab;

        internal CachedReference(string targetName, GameObject prefab)
        {
            TargetName = targetName;
            PrefabName = prefab.name;
            Prefab = prefab;
        }
    }

    private static readonly ProbeTarget[] Targets =
    {
        new ProbeTarget("Fool", typeof(Gameplay.GameControllers.Enemies.Fool.Fool)),
        new ProbeTarget("WheelCarrier", typeof(Gameplay.GameControllers.Enemies.WheelCarrier.WheelCarrier)),
    };

    private static readonly Dictionary<int, CachedReference> Cache = new Dictionary<int, CachedReference>();
    private static int _runNumber;

    internal static void Run(Action<string> write)
    {
        _runNumber++;
        string activeScene = SceneManager.GetActiveScene().name;

        PreloadReferenceScriptableAsset[] catalogs = Resources.FindObjectsOfTypeAll<PreloadReferenceScriptableAsset>();
        PreloadReferenceHolder[] holders = Resources.FindObjectsOfTypeAll<PreloadReferenceHolder>();
        Enemy[] loadedEnemies = Resources.FindObjectsOfTypeAll<Enemy>();
        EnemySpawnPoint[] spawnPoints = UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>();

        string summary = $"Prefab probe #{_runNumber}: scene={activeScene}, catalogs={catalogs.Length}, holders={holders.Length}, loadedEnemies={loadedEnemies.Length}, activeSpawnPoints={spawnPoints.Length}";
        write(summary);
        ModLog.Info($"[EnemyPrefabProbe] {summary}, loadedScenes={SceneManager.sceneCount}, time={Time.realtimeSinceStartup:0.000}");

        LogCatalogs(catalogs);
        LogHolders(holders);

        foreach (ProbeTarget target in Targets)
        {
            InspectTarget(target, catalogs, loadedEnemies, spawnPoints, write);
        }

        write("Probe complete; details are in the local mod log. Repeat after a scene transition to check earlier cache references.");
    }

    private static void LogCatalogs(PreloadReferenceScriptableAsset[] catalogs)
    {
        foreach (PreloadReferenceScriptableAsset catalog in catalogs)
        {
            if (catalog == null) continue;

            int nullEntries = 0;
            int entries = catalog.enemyPrefabs == null ? 0 : catalog.enemyPrefabs.Count;
            if (catalog.enemyPrefabs != null)
            {
                foreach (GameObject prefab in catalog.enemyPrefabs)
                {
                    if (prefab == null) nullEntries++;
                }
            }

            ModLog.Info($"[EnemyPrefabProbe] Catalog name={catalog.name}, instance={catalog.GetInstanceID()}, enemyPrefabs={entries}, nullEntries={nullEntries}");
        }
    }

    private static void LogHolders(PreloadReferenceHolder[] holders)
    {
        foreach (PreloadReferenceHolder holder in holders)
        {
            if (holder == null) continue;

            string catalogName = holder.preloadAsset == null ? "<null>" : holder.preloadAsset.name;
            ModLog.Info($"[EnemyPrefabProbe] Holder name={holder.name}, instance={holder.GetInstanceID()}, scene={DescribeScene(holder.gameObject)}, catalog={catalogName}");
        }
    }

    private static void InspectTarget(ProbeTarget target, PreloadReferenceScriptableAsset[] catalogs,
        Enemy[] loadedEnemies, EnemySpawnPoint[] spawnPoints, Action<string> write)
    {
        Dictionary<int, Candidate> candidates = new Dictionary<int, Candidate>();
        int catalogMatches = 0;
        int liveSpawnSources = 0;

        foreach (PreloadReferenceScriptableAsset catalog in catalogs)
        {
            if (catalog == null || catalog.enemyPrefabs == null) continue;

            foreach (GameObject prefab in catalog.enemyPrefabs)
            {
                Enemy enemy = FindTargetEnemy(prefab, target.EnemyType);
                if (enemy == null) continue;

                catalogMatches++;
                AddCandidate(candidates, prefab, enemy).FromPreloadCatalog = true;
            }
        }

        int loadedAssetMatches = 0;
        foreach (Enemy enemy in loadedEnemies)
        {
            if (enemy == null || !target.EnemyType.IsInstanceOfType(enemy) || !IsPrefabAsset(enemy.gameObject)) continue;

            loadedAssetMatches++;
            AddCandidate(candidates, enemy.gameObject, enemy).FromLoadedAssets = true;
        }

        int spawnSourceMatches = 0;
        foreach (EnemySpawnPoint point in spawnPoints)
        {
            if (point == null || point.SelectedEnemy == null) continue;

            Enemy enemy = FindTargetEnemy(point.SelectedEnemy, target.EnemyType);
            if (enemy == null) continue;

            spawnSourceMatches++;
            if (IsPrefabAsset(point.SelectedEnemy))
            {
                AddCandidate(candidates, point.SelectedEnemy, enemy).FromSpawnPoint = true;
            }
            else
            {
                liveSpawnSources++;
            }

            ModLog.Info($"[EnemyPrefabProbe] {target.Name} spawn source point={point.name}, pointScene={DescribeScene(point.gameObject)}, prefab={point.SelectedEnemy.name}, prefabScene={DescribeScene(point.SelectedEnemy)}, position={point.transform.position}");
        }

        CountCachedReferences(target.Name, out int previousValid, out int previousMissing);

        int addedThisRun = 0;
        foreach (Candidate candidate in candidates.Values)
        {
            if (!IsPrefabAsset(candidate.Prefab)) continue;

            int instanceId = candidate.Prefab.GetInstanceID();
            if (Cache.ContainsKey(instanceId)) continue;

            Cache.Add(instanceId, new CachedReference(target.Name, candidate.Prefab));
            addedThisRun++;
        }

        foreach (Candidate candidate in candidates.Values)
        {
            string enemyId = candidate.Enemy == null || string.IsNullOrEmpty(candidate.Enemy.Id) ? "<empty>" : candidate.Enemy.Id;
            ModLog.Info($"[EnemyPrefabProbe] {target.Name} candidate prefab={candidate.Prefab.name}, instance={candidate.Prefab.GetInstanceID()}, enemyId={enemyId}, scene={DescribeScene(candidate.Prefab)}, sources={DescribeSources(candidate)}");
        }

        write($"{target.Name}: catalog={catalogMatches}, loadedAssets={loadedAssetMatches}, spawnSources={spawnSourceMatches}, liveSpawnSources={liveSpawnSources}, previousCache(valid={previousValid}, missing={previousMissing}), candidates={candidates.Count}, newCached={addedThisRun}");
    }

    private static Candidate AddCandidate(Dictionary<int, Candidate> candidates, GameObject prefab, Enemy enemy)
    {
        int instanceId = prefab.GetInstanceID();
        if (candidates.TryGetValue(instanceId, out Candidate candidate)) return candidate;

        candidate = new Candidate(prefab, enemy);
        candidates.Add(instanceId, candidate);
        return candidate;
    }

    private static Enemy FindTargetEnemy(GameObject prefab, Type enemyType)
    {
        if (prefab == null) return null;

        foreach (Enemy enemy in prefab.GetComponentsInChildren<Enemy>(true))
        {
            if (enemy != null && enemyType.IsInstanceOfType(enemy)) return enemy;
        }
        return null;
    }

    private static bool IsPrefabAsset(GameObject gameObject)
    {
        return gameObject != null && string.IsNullOrEmpty(gameObject.scene.name);
    }

    private static string DescribeScene(GameObject gameObject)
    {
        return IsPrefabAsset(gameObject) ? "<asset>" : gameObject.scene.name;
    }

    private static string DescribeSources(Candidate candidate)
    {
        List<string> sources = new List<string>();
        if (candidate.FromPreloadCatalog) sources.Add("preload");
        if (candidate.FromLoadedAssets) sources.Add("loaded");
        if (candidate.FromSpawnPoint) sources.Add("spawnPoint");
        return string.Join("+", sources.ToArray());
    }

    private static void CountCachedReferences(string targetName, out int valid, out int missing)
    {
        valid = 0;
        missing = 0;

        foreach (KeyValuePair<int, CachedReference> entry in Cache)
        {
            CachedReference cached = entry.Value;
            if (!string.Equals(cached.TargetName, targetName, StringComparison.Ordinal)) continue;

            if (cached.Prefab != null)
            {
                valid++;
            }
            else
            {
                missing++;
                ModLog.Warn($"[EnemyPrefabProbe] Previous cache reference is missing target={targetName}, prefab={cached.PrefabName}, instance={entry.Key}");
            }
        }
    }
}
