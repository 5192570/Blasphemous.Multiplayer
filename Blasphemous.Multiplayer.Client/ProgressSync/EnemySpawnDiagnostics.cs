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
            if (__instance != null)
            {
                EnemySpawnDiagnostics.Write($"CreateEnemy:consumption-check-completed position={__instance.transform.position}");
            }
        }
    }

    // EnemySpawner has two overloads: IsSpawnerConsumed(string) and
    // IsSpawnerConsumed(EnemySpawnPoint). Explicitly select the latter.
    [HarmonyPatch(typeof(EnemySpawner), "IsSpawnerConsumed", new Type[] { typeof(EnemySpawnPoint) })]
    internal static class EnemySpawnerIsSpawnerConsumedDiagnostics_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(EnemySpawnPoint __0, bool __result)
        {
            string position = __0 == null ? "[null]" : __0.transform.position.ToString();
            EnemySpawnDiagnostics.Write($"IsSpawnerConsumed:result position={position}, result={__result}");
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
        internal static void Write(string message)
        {
            // UTC keeps timestamps directly comparable between the two players' logs.
            ModLog.Info($"[EnemySpawnDiagnostics] {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {message}");
        }

        internal static void Log(string eventName, EnemySpawnPoint spawnPoint)
        {
            if (spawnPoint == null)
            {
                Write($"{eventName}: spawnPoint=null");
                return;
            }

            var enemy = spawnPoint.SpawnedEnemy;
            var selectedEnemy = spawnPoint.SelectedEnemy;
            string enemyState = enemy == null
                ? "none"
                : $"name={enemy.name}, instanceId={enemy.gameObject.GetInstanceID()}, spawningId={enemy.SpawningId}, active={enemy.gameObject.activeInHierarchy}";
            string selectedEnemyName = selectedEnemy == null ? "none" : selectedEnemy.name;

            Write(
                $"{eventName}: " +
                $"spawnPoint={spawnPoint.name}, instanceId={spawnPoint.gameObject.GetInstanceID()}, " +
                $"position={spawnPoint.transform.position}, spawningId={spawnPoint.SpawningId}, " +
                $"hasEnemySpawned={spawnPoint.HasEnemySpawned}, consumed={spawnPoint.Consumed}, " +
                $"selectedEnemy={selectedEnemyName}, spawnedEnemy=[{enemyState}]");
        }

        internal static void LogConsumedSpawns(string eventName, EnemySpawner spawner)
        {
            if (spawner == null)
            {
                Write($"{eventName}: spawner=null");
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
                    Write($"{eventName}: consumedSpawns field not found");
                    return;
                }

                object owner = field.IsStatic ? null : (object)spawner;
                object value = field.GetValue(owner);
                if (value == null)
                {
                    Write($"{eventName}: consumedSpawns=null, count=0");
                    return;
                }

                IEnumerable entries = value as IEnumerable;
                if (entries == null)
                {
                    Write($"{eventName}: consumedSpawns type={value.GetType().FullName}, not enumerable");
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

                Write($"{eventName}: consumedSpawns count={count}, positions=[{positions}]");
            }
            catch (Exception ex)
            {
                Write($"{eventName}: failed to inspect consumedSpawns: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}