using System;
using System.Collections.Generic;
using System.Reflection;
using Blasphemous.Multiplayer.Client.Network;
using Framework.Managers;
using Gameplay.GameControllers.Combat;
using Gameplay.GameControllers.Enemies.Framework.IA;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync
{
    /// <summary>
    /// Diagnostic only. Writes one line per enemy <em>variant</em> per scene, describing the parts
    /// of vanilla a sync handler would have to speak to. The point is to make the tier inventory a
    /// by-product of normal play-testing instead of an investigation: the executable tier is defined
    /// by <see cref="EnemyBehaviour.Execution"/>/<see cref="EnemyBehaviour.Alive"/> overrides plus an
    /// <see cref="EntityExecution"/> prefab, both of which are readable at runtime.
    ///
    /// Variants matter: the same behaviour type is used by differently configured prefabs (the
    /// WheelCarrier tutorial and regular spawns are both <c>WheelCarrierBehaviour</c>), so the
    /// de-duplication key is a signature built from the prefab name, the animator controller, the
    /// execution prefab, the attack type, the base life and the stunt time - not the type name alone.
    ///
    /// It never touches an enemy: no component is added, no state is written, and failures are
    /// swallowed into a warning so a diagnostic can not break a session.
    /// </summary>
    internal static class EnemyTierProbe
    {
        private const int MaxLines = 400;
        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static bool _initialized;

        internal static void Initialize()
        {
            if (!EnemyDiagnostics.EnableEnemyTierDiagnostics) return;
            if (_initialized) return;
            _initialized = true;
            Entity.Started -= OnEntityStarted;
            Entity.Started += OnEntityStarted;
        }

        /// <summary>Describes enemies that were already in the scene when the probe was installed.</summary>
        internal static void DescribeExisting()
        {
            if (!EnemyDiagnostics.EnableEnemyTierDiagnostics) return;
            foreach (Entity entity in Entity.LivingEntities) OnEntityStarted(entity);
        }

        private static void OnEntityStarted(Entity entity)
        {
            if (!EnemyDiagnostics.EnableEnemyTierDiagnostics) return;
            Enemy enemy = entity as Enemy;
            if (enemy == null) return;

            try
            {
                Describe(enemy);
            }
            catch (Exception ex)
            {
                SharedDiagnosticLogger.Warn($"[EnemyTierDiag] probe failed enemyInstance={entity.GetInstanceID()}, object={entity.gameObject.name}, error={ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Describe(Enemy enemy)
        {
            string scene = CurrentScene();
            Type behaviourType = enemy.EnemyBehaviour != null ? enemy.EnemyBehaviour.GetType() : null;

            EntityExecution execution = enemy.GetComponent<EntityExecution>();
            string executionPrefab = execution != null && execution.ExecutionPrefab != null ? execution.ExecutionPrefab.name : "<none>";
            string animatorController = enemy.Animator != null && enemy.Animator.runtimeAnimatorController != null
                ? enemy.Animator.runtimeAnimatorController.name
                : "<none>";
            string attack = ReadAttack(enemy);
            float lifeMax = ReadLifeMax(enemy);

            string objectName = enemy.gameObject.name;
            string variant = VariantOf(objectName);

            // A variant is everything that can differ between two prefabs sharing a behaviour type.
            string signature = string.Join("|", new[]
            {
                enemy.GetType().Name,
                variant,
                animatorController,
                executionPrefab,
                attack,
                lifeMax.ToString("0.##"),
                enemy.StuntTime.ToString("0.##")
            });

            string key = scene + "|" + signature;
            if (Logged.Contains(key) || Logged.Count >= MaxLines) return;
            Logged.Add(key);

            bool supported = false;
            try
            {
                supported = EnemySyncManager.Supports(enemy);
            }
            catch (Exception)
            {
            }

            SharedDiagnosticLogger.Info($"[EnemyTierDiag] type={enemy.GetType().Name}, behaviour={(behaviourType != null ? behaviourType.Name : "<none>")}, variant={variant}, object={objectName}, overridesExecution={Overrides(behaviourType, "Execution")}, overridesAlive={Overrides(behaviourType, "Alive")}, overridesParry={Overrides(behaviourType, "Parry")}, behaviourTree={ReadBehaviourTree(enemy)}, entityExecution={execution != null}, executionPrefab={executionPrefab}, stuntTime={enemy.StuntTime:0.##}, isStunt={enemy.IsStunt}, attack={attack}, animatorController={animatorController}, lifeMax={lifeMax:0.##}, layer={enemy.gameObject.layer}, dead={enemy.Status.Dead}, supported={supported}, scene={scene}, enemyInstance={enemy.GetInstanceID()}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        }

        private static string CurrentScene()
        {
            try
            {
                if (Core.LevelManager != null && Core.LevelManager.currentLevel != null)
                    return Core.LevelManager.currentLevel.LevelName;
            }
            catch (Exception)
            {
            }

            return "<none>";
        }

        /// <summary>
        /// Strips the per-instance suffix Unity/prefab naming adds ("Regular" and
        /// "Regular493695552" are the same prefab variant) so the signature groups instances of one
        /// prefab and separates genuinely different ones.
        /// </summary>
        private static string VariantOf(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return "<none>";

            string name = objectName.Replace("(Clone)", string.Empty);

            int end = name.Length;
            while (end > 0)
            {
                char c = name[end - 1];
                if (char.IsDigit(c) || c == '-' || c == '_' || c == ' ' || c == '(' || c == ')' || c == '.')
                {
                    end--;
                    continue;
                }

                break;
            }

            string trimmed = name.Substring(0, end);
            return trimmed.Length == 0 ? "<none>" : trimmed;
        }

        /// <summary>
        /// True when the enemy's behaviour type declares the method itself instead of inheriting
        /// EnemyBehaviour's empty implementation. GetMethod returns the most derived match.
        /// </summary>
        private static bool Overrides(Type behaviourType, string method)
        {
            if (behaviourType == null) return false;

            MethodInfo info = behaviourType.GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
            return info != null && info.DeclaringType != typeof(EnemyBehaviour);
        }

        private static bool ReadBehaviourTree(Enemy enemy)
        {
            if (enemy.EnemyBehaviour == null) return false;

            PropertyInfo property = enemy.EnemyBehaviour.GetType().GetProperty("BehaviourTree", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (property == null) return false;

            try
            {
                return property.GetValue(enemy.EnemyBehaviour, null) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ReadAttack(Enemy enemy)
        {
            try
            {
                var attack = enemy.EnemyAttack();
                return attack != null ? $"{attack.GetType().Name}:{attack.enabled}" : "<none>";
            }
            catch (Exception ex)
            {
                return $"<{ex.GetType().Name}>";
            }
        }

        private static float ReadLifeMax(Enemy enemy)
        {
            try
            {
                // CurrentMax is the tuned maximum (MaxValue with MaxFactor applied); there is no
                // public Base on VariableAttribute.
                if (enemy.Stats != null && enemy.Stats.Life != null) return enemy.Stats.Life.CurrentMax;
            }
            catch (Exception)
            {
            }

            return -1f;
        }
    }
}
