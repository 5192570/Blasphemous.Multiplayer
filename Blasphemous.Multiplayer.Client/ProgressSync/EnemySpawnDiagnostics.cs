using Blasphemous.ModdingAPI;
using Gameplay.GameControllers.Enemies.Framework.IA;
using HarmonyLib;
using Tools.Level.Layout;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client
{
    /// <summary>
    /// Temporary diagnostics for understanding local enemy spawn/death lifecycle.
    /// These patches only log state; they do not change gameplay or network behavior.
    /// </summary>
    [HarmonyPatch(typeof(EnemySpawnPoint), "CreateEnemy")]
    internal static class EnemySpawnPointCreateEnemyDiagnostics_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(EnemySpawnPoint __instance)
        {
            EnemySpawnDiagnostics.Log("CreateEnemy:before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(EnemySpawnPoint __instance)
        {
            EnemySpawnDiagnostics.Log("CreateEnemy:after", __instance);
        }
    }

    [HarmonyPatch(typeof(EnemySpawnPoint), "OnEntityDie")]
    internal static class EnemySpawnPointEntityDieDiagnostics_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(EnemySpawnPoint __instance)
        {
            EnemySpawnDiagnostics.Log("OnEntityDie:before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(EnemySpawnPoint __instance)
        {
            EnemySpawnDiagnostics.Log("OnEntityDie:after", __instance);
        }
    }

    [HarmonyPatch(typeof(EnemySpawnPoint), "OnDestroyEntity")]
    internal static class EnemySpawnPointDestroyEntityDiagnostics_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(EnemySpawnPoint __instance)
        {
            EnemySpawnDiagnostics.Log("OnDestroyEntity:before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(EnemySpawnPoint __instance)
        {
            EnemySpawnDiagnostics.Log("OnDestroyEntity:after", __instance);
        }
    }

    [HarmonyPatch(typeof(EnemySpawner), "RespawnDeadEnemies")]
    internal static class EnemySpawnerRespawnDiagnostics_Patch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            ModLog.Info("[EnemySpawnDiagnostics] RespawnDeadEnemies:before");
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            ModLog.Info("[EnemySpawnDiagnostics] RespawnDeadEnemies:after");
        }
    }

    internal static class EnemySpawnDiagnostics
    {
        internal static void Log(string eventName, EnemySpawnPoint spawnPoint)
        {
            if (spawnPoint == null)
            {
                ModLog.Info($"[EnemySpawnDiagnostics] {eventName}: spawnPoint=null");
                return;
            }

            var enemy = spawnPoint.SpawnedEnemy;
            var selectedEnemy = spawnPoint.SelectedEnemy;
            string enemyState = enemy == null
                ? "none"
                : $"name={enemy.name}, instanceId={enemy.gameObject.GetInstanceID()}, spawningId={enemy.SpawningId}, active={enemy.gameObject.activeInHierarchy}";

            string selectedEnemyName = selectedEnemy == null ? "none" : selectedEnemy.name;

            ModLog.Info(
                $"[EnemySpawnDiagnostics] {eventName}: " +
                $"spawnPoint={spawnPoint.name}, instanceId={spawnPoint.gameObject.GetInstanceID()}, " +
                $"position={spawnPoint.transform.position}, spawningId={spawnPoint.SpawningId}, " +
                $"hasEnemySpawned={spawnPoint.HasEnemySpawned}, consumed={spawnPoint.Consumed}, " +
                $"selectedEnemy={selectedEnemyName}, spawnedEnemy=[{enemyState}]");
        }
    }
}
