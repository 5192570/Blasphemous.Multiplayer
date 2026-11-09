using Gameplay.GameControllers.Entities;
using System;
using System.Collections.Generic;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Resolves explicitly registered enemy prefabs from Unity objects that are already loaded.
/// No SerializedFile or scene loading is performed here.
/// </summary>
internal static class EnemyPrefabCatalog
{
    private static readonly Dictionary<string, GameObject> Cache = new Dictionary<string, GameObject>();

    internal static bool TryGetPrefab(string prefabName, string enemyId, out GameObject prefab)
    {
        string key = prefabName + "|" + enemyId;
        if (Cache.TryGetValue(key, out prefab) && Matches(prefab, prefabName, enemyId)) return true;

        Cache.Remove(key);

        foreach (PreloadReferenceScriptableAsset catalog in Resources.FindObjectsOfTypeAll<PreloadReferenceScriptableAsset>())
        {
            if (catalog == null || catalog.enemyPrefabs == null) continue;

            foreach (GameObject candidate in catalog.enemyPrefabs)
            {
                if (UseCandidate(candidate, prefabName, enemyId, key, out prefab)) return true;
            }
        }

        foreach (Enemy enemy in Resources.FindObjectsOfTypeAll<Enemy>())
        {
            if (enemy == null) continue;
            if (UseCandidate(enemy.gameObject, prefabName, enemyId, key, out prefab)) return true;
        }

        foreach (EnemySpawnPoint point in UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>())
        {
            if (point == null) continue;
            if (UseCandidate(point.SelectedEnemy, prefabName, enemyId, key, out prefab)) return true;
        }

        prefab = null;
        return false;
    }

    private static bool UseCandidate(GameObject candidate, string prefabName, string enemyId, string key, out GameObject prefab)
    {
        if (!Matches(candidate, prefabName, enemyId))
        {
            prefab = null;
            return false;
        }

        Cache[key] = candidate;
        prefab = candidate;
        return true;
    }

    private static bool Matches(GameObject candidate, string prefabName, string enemyId)
    {
        if (candidate == null || !string.Equals(candidate.name, prefabName, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(candidate.scene.name)) return false;

        foreach (Enemy enemy in candidate.GetComponentsInChildren<Enemy>(true))
        {
            if (enemy != null && string.Equals(enemy.Id, enemyId, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
