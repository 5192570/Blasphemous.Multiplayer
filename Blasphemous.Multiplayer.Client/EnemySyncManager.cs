using System;
using System.Globalization;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Framework.Damage;
using Gameplay.GameControllers.Entities;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client;

/// <summary>
/// Synchronizes ordinary spawned enemies' remaining health. Combat reactions and AI are intentionally out of scope.
/// </summary>
public sealed class EnemySyncManager
{
    private bool _applyingRemoteHealth;

    public EnemySyncManager()
    {
        EnemyDamageArea.OnDamagedGlobal += OnEnemyDamaged;
    }

    private void OnEnemyDamaged(GameObject damaged, Hit hit)
    {
        if (_applyingRemoteHealth || damaged == null || !Main.Multiplayer.CurrentlyInLevel || !Main.Multiplayer.NetworkManager.IsConnected)
            return;

        Enemy enemy = damaged.GetComponent<Enemy>();
        if (enemy == null) enemy = damaged.GetComponentInChildren<Enemy>();
        if (enemy == null) return;

        EnemySpawnPoint spawnPoint = FindSpawnPoint(enemy);
        if (spawnPoint == null || IsBossLike(enemy, spawnPoint)) return;

        string key = BuildKey(spawnPoint, enemy);
        if (string.IsNullOrEmpty(key) || !IsUniqueKey(key))
            return;

        // The local game has already processed the hit, so send its resulting health immediately.
        Main.Multiplayer.NetworkManager.SendEnemyHealth(key, Mathf.Max(0f, enemy.CurrentLife));
    }

    public void ReceiveEnemyHealth(string key, float health)
    {
        if (string.IsNullOrEmpty(key) || !Main.Multiplayer.CurrentlyInLevel || float.IsNaN(health) || float.IsInfinity(health))
            return;

        Enemy match = null;
        int matches = 0;
        EnemySpawnPoint[] spawnPoints = UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>();
        foreach (EnemySpawnPoint point in spawnPoints)
        {
            if (point == null || point.SpawnedEnemy == null) continue;
            Enemy candidate = point.SpawnedEnemy;
            if (IsBossLike(candidate, point)) continue;
            if (BuildKey(point, candidate) != key) continue;

            match = candidate;
            matches++;
        }

        // Never guess when a key is ambiguous; a wrong enemy being killed is worse than a missed update.
        if (matches != 1 || match == null || match.Status.Dead || match.CurrentLife <= 0f)
            return;

        float newHealth = Mathf.Max(0f, Mathf.Min(match.CurrentLife, health));
        if (newHealth >= match.CurrentLife) return;

        _applyingRemoteHealth = true;
        try
        {
            // SetHealth invokes the game's normal death path at zero without replaying the attack or rebroadcasting it.
            match.SetHealth(newHealth);
        }
        finally
        {
            _applyingRemoteHealth = false;
        }
    }

    private static EnemySpawnPoint FindSpawnPoint(Enemy enemy)
    {
        foreach (EnemySpawnPoint point in UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>())
        {
            if (point != null && point.SpawnedEnemy == enemy)
                return point;
        }
        return null;
    }

    private static bool IsUniqueKey(string key)
    {
        int matches = 0;
        foreach (EnemySpawnPoint point in UnityEngine.Object.FindObjectsOfType<EnemySpawnPoint>())
        {
            if (point == null || point.SpawnedEnemy == null || IsBossLike(point.SpawnedEnemy, point)) continue;
            if (BuildKey(point, point.SpawnedEnemy) == key && ++matches > 1)
                return false;
        }
        return matches == 1;
    }

    private static string BuildKey(EnemySpawnPoint point, Enemy enemy)
    {
        if (point == null || enemy == null || IsBossLike(enemy, point)) return null;
        string sceneName = Core.LevelManager.currentLevel == null ? string.Empty : Core.LevelManager.currentLevel.LevelName;
        Vector3 position = point.Position;
        return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3:0.00}|{4:0.00}",
            sceneName, point.EntityName ?? string.Empty, enemy.GetType().FullName, position.x, position.y);
    }

    private static bool IsBossLike(Enemy enemy, EnemySpawnPoint point)
    {
        if (enemy == null) return false;
        if (enemy.GetType().Name.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return point != null && point.SelectedEnemy != null &&
            point.SelectedEnemy.name.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
