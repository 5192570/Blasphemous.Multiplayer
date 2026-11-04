using System;
using System.Collections.Generic;
using System.Reflection;
using Blasphemous.Multiplayer.Client.Network;
using Framework.Managers;
using Gameplay.GameControllers.Combat;
using Gameplay.GameControllers.Enemies.Framework.IA;
using Gameplay.GameControllers.Entities;

namespace Blasphemous.Multiplayer.Client.ProgressSync
{
    /// <summary>
    /// Diagnostic only. Writes one line per enemy type per scene, describing the parts of vanilla
    /// a sync handler would have to speak to. The point is to make the tier inventory a by-product
    /// of normal play-testing instead of an investigation: the executable tier is defined by
    /// <see cref="EnemyBehaviour.Execution"/>/<see cref="EnemyBehaviour.Alive"/> overrides plus an
    /// <see cref="EntityExecution"/> prefab, both of which are readable at runtime.
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
            if (_initialized) return;
            _initialized = true;
            Entity.Started -= OnEntityStarted;
            Entity.Started += OnEntityStarted;
        }

        /// <summary>Describes enemies that were already in the scene when the probe was installed.</summary>
        internal static void DescribeExisting()
        {
            foreach (Entity entity in Entity.LivingEntities) OnEntityStarted(entity);
        }

        private static void OnEntityStarted(Entity entity)
        {
            Enemy enemy = entity as Enemy;
            if (enemy == null) return;

            try
            {
                Describe(enemy);
            }
            catch (Exception ex)
            {
                SharedDiagnosticLogger.Warn($"[EnemyTierDiag] probe failed enemyInstance={entity.GetInstanceID()}, error={ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Describe(Enemy enemy)
        {
            string scene = CurrentScene();
            Type behaviourType = enemy.EnemyBehaviour != null ? enemy.EnemyBehaviour.GetType() : null;
            string key = (behaviourType != null ? behaviourType.FullName : enemy.GetType().FullName) + "|" + scene;
            if (Logged.Contains(key) || Logged.Count >= MaxLines) return;
            Logged.Add(key);

            EntityExecution execution = enemy.GetComponent<EntityExecution>();
            string prefab = execution != null && execution.ExecutionPrefab != null ? execution.ExecutionPrefab.name : "<none>";

            string animatorController = "<none>";
            if (enemy.Animator != null && enemy.Animator.runtimeAnimatorController != null)
                animatorController = enemy.Animator.runtimeAnimatorController.name;

            string attack = "<none>";
            try
            {
                EnemyAttack enemyAttack = enemy.EnemyAttack();
                if (enemyAttack != null) attack = enemyAttack.GetType().Name;
            }
            catch (Exception ex)
            {
                attack = $"<{ex.GetType().Name}>";
            }

            bool supported = false;
            try
            {
                supported = EnemySyncManager.Supports(enemy);
            }
            catch (Exception)
            {
            }

            SharedDiagnosticLogger.Info($"[EnemyTierDiag] type={enemy.GetType().Name}, behaviour={(behaviourType != null ? behaviourType.Name : "<none>")}, overridesExecution={Overrides(behaviourType, "Execution")}, overridesAlive={Overrides(behaviourType, "Alive")}, overridesParry={Overrides(behaviourType, "Parry")}, behaviourTree={ReadBehaviourTree(enemy)}, entityExecution={execution != null}, executionPrefab={prefab}, stuntTime={enemy.StuntTime:0.##}, isStunt={enemy.IsStunt}, attack={attack}, animatorController={animatorController}, layer={enemy.gameObject.layer}, dead={enemy.Status.Dead}, supported={supported}, scene={scene}, enemyInstance={enemy.GetInstanceID()}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
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
    }
}
