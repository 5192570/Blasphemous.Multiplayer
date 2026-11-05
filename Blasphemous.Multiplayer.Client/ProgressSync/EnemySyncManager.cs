using System;
using System.Collections.Generic;
using Blasphemous.ModdingAPI;
using Blasphemous.Multiplayer.Client.Network;
using Gameplay.GameControllers.Entities;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Discovers supported enemy instances and attaches their handler plus shared sync component.</summary>
internal static class EnemySyncManager
{
    /// <summary>
    /// The sync gate: enemy type -> handler (adapter) type. This table is the only
    /// place that decides which enemies are synced.
    ///
    /// Player tracking (authority target selection in <see cref="EnemySync"/>),
    /// position/health snapshots and damage reconciliation are already generic:
    /// they operate on the base <see cref="Enemy"/>/<see cref="EnemySyncHandler"/>
    /// surface and gate only on <see cref="Supports"/>. Adding a row therefore
    /// opens tracking and health
    /// sync for that enemy as soon as its adapter supplies the per-enemy verbs
    /// (authority on/off, walk, hit reaction).
    ///
    /// A row also opens death *transport* (authority commit, generation/revision
    /// bookkeeping, remote-death application in <see cref="EnemyDeathSyncManager"/>),
    /// which is likewise generic. Death *presentation* is not: it stays behind the
    /// adapter's <see cref="EnemySyncHandler.ApplyDeath"/> (vanilla Death() exists
    /// only on concrete behaviour types), so a new row must not be added until that
    /// enemy's death has been verified. Boss/scripted (T4) enemies stay out.
    ///
    /// To sync a new enemy: write its thin adapter, then add one row here.
    /// </summary>
    private static readonly Dictionary<Type, Type> HandlerTypes = new Dictionary<Type, Type>
    {
        { typeof(Gameplay.GameControllers.Enemies.Fool.Fool), typeof(FoolEnemySyncHandler) },
        { typeof(Gameplay.GameControllers.Enemies.WheelCarrier.WheelCarrier), typeof(WheelCarrierEnemySyncHandler) },
        // WaxCrawler also backs the mud crawler prefab variants (MudCrawlerAudio).
        { typeof(Gameplay.GameControllers.Enemies.WaxCrawler.WaxCrawler), typeof(WaxCrawlerEnemySyncHandler) },
    };

    internal static void Initialize()
    {
        Entity.Started -= OnEntityStarted;
        Entity.Started += OnEntityStarted;
        EnemyTierProbe.Initialize();
    }

    internal static void AttachExisting()
    {
        foreach (Entity entity in Entity.LivingEntities) Attach(entity);
        EnemyTierProbe.DescribeExisting();
    }

    private static void OnEntityStarted(Entity entity) => Attach(entity);

    internal static bool Supports(Enemy enemy)
    {
        return TryGetHandlerType(enemy, out Type _);
    }

    private static bool TryGetHandlerType(Enemy enemy, out Type handlerType)
    {
        handlerType = null;
        if (enemy == null) return false;
        foreach (KeyValuePair<Type, Type> entry in HandlerTypes)
        {
            // IsInstanceOfType preserves the previous `is` checks, subclass included.
            if (entry.Key.IsInstanceOfType(enemy))
            {
                handlerType = entry.Value;
                return true;
            }
        }
        return false;
    }

    internal static bool TryGetHandler(Enemy enemy, out EnemySyncHandler handler)
    {
        handler = null;
        if (enemy == null) return false;
        handler = enemy.GetComponent<EnemySyncHandler>();
        return handler != null;
    }

    internal static void Attach(Enemy enemy)
    {
        if (enemy == null || enemy.Status.Dead) return;
        bool addedHandler = false;
        EnemySyncHandler handler = enemy.GetComponent<EnemySyncHandler>();
        if (handler == null)
        {
            if (!TryGetHandlerType(enemy, out Type handlerType)) return;
            handler = enemy.gameObject.AddComponent(handlerType) as EnemySyncHandler;
            if (handler == null) return;
            addedHandler = true;
        }

        handler.Attach(enemy);
        EnemySync sync = enemy.GetComponent<EnemySync>();
        bool addedSync = sync == null;
        if (sync == null) sync = enemy.gameObject.AddComponent<EnemySync>();
        sync.Attach(handler);

        if (addedHandler || addedSync)
        {
            bool hasIdentity = EnemyDeathSyncManager.TryGetSyncIdentity(enemy, out string scene, out string spawn, out int generation);
            SharedDiagnosticLogger.Info($"[EnemySyncDiag] Registered enemy={enemy.GetType().Name}, scene={(hasIdentity ? scene : "<unresolved>")}, spawn={(hasIdentity ? spawn : "<unresolved>")}, generation={(hasIdentity ? generation : -1)}, enemyInstance={enemy.GetInstanceID()}, object={enemy.gameObject.name}, position={enemy.transform.position}, handler={handler.GetType().Name}, addedHandler={addedHandler}, addedSync={addedSync}");
        }
    }

    internal static void Attach(Entity entity)
    {
        if (entity is Enemy enemy && Supports(enemy)) Attach(enemy);
    }
}
