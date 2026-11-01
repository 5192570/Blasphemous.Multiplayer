using System;
using System.Collections;
using System.Reflection;
using System.Text;
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

    [HarmonyPatch(typeof(EnemySpawnPoint), "CreateEnemy")]
    internal static class EnemySpawnPointConsumedCheckDiagnostics_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(EnemySpawnPoint __instance)
        {
            // The IsSpawnerConsumed postfix below records the actual return value.
            // This extra marker makes it easier to find the check among CreateEnemy logs.
            if (__instance != null)
            {
                ModLog.Info($"[EnemySpawnDiagnostics] CreateEnemy:consumption-check-completed position={__instance.transform.position}");
            }
        }
    }

    [HarmonyPatch(typeof(EnemySpawner), "IsSpawnerConsumed")]
    internal static class EnemySpawnerIsSpawnerConsumedDiagnostics_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(EnemySpawnPoint __0, bool __result)
        {
            string position = __0 == null ? "[null]" : __0.transform.position.ToString();
            ModLog.Info($"[EnemySpawnDiagnostics] IsSpawnerConsumed:result position={position}, result={__result}");
        }
    }

    [HarmonyPatch(typeof(EnemySpawner), "RespawnDeadEnemies")]
    internal static class EnemySpawnerRespawnDiagnostics_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(EnemySpawner __instance)
        {
            EnemySpawnDiagnostics.LogConsumedSpawns("RespawnDeadEnemies:before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(EnemySpawner __instance)
        {
            EnemySpawnDiagnostics.LogConsumedSpawns("RespawnDeadEnemies:after", __instance);
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

        internal static void LogConsumedSpawns(string eventName, EnemySpawner spawner)
        {
            if (spawner == null)
            {
                ModLog.Info($"[EnemySpawnDiagnostics] {eventName}: spawner=null");
                return;
            }

            try
            {
                FieldInfo field = null;
                Type type = spawner.GetType();
                while (type != null && field == null)
                {
                    field = type.GetField("consumedSpawns",
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    type = type.BaseType;
                }

                if (field == null)
                {
                    ModLog.Info($"[EnemySpawnDiagnostics] {eventName}: consumedSpawns field not found");
                    return;
                }

                object owner = field.IsStatic ? null : (object)spawner;
                object value = field.GetValue(owner);
                if (value == null)
                {
                    ModLog.Info($"[EnemySpawnDiagnostics] {eventName}: consumedSpawns=null, count=0");
                    return;
                }

                IEnumerable entries = value as IEnumerable;
                if (entries == null)
                {
                    ModLog.Info($"[EnemySpawnDiagnostics] {eventName}: consumedSpawns type={value.GetType().FullName}, not enumerable");
                    return;
                }

                int count = 0;
                StringBuilder positions = new StringBuilder();
                foreach (object entry in entries)
                {
                    if (count > 0)
                        positions.Append("; ");
                    positions.Append(entry == null ? "null" : entry.ToString());
                    count++;
                }

                ModLog.Info($"[EnemySpawnDiagnostics] {eventName}: consumedSpawns count={count}, positions=[{positions}]");
            }
            catch (Exception ex)
            {
                ModLog.Info($"[EnemySpawnDiagnostics] {eventName}: failed to inspect consumedSpawns: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}