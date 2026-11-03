using Blasphemous.ModdingAPI;
using Blasphemous.Multiplayer.Client.Network;
using Gameplay.GameControllers.Entities;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Discovers supported enemy instances and attaches their handler plus shared sync component.</summary>
internal static class EnemySyncManager
{
    internal static void Initialize()
    {
        Entity.Started -= OnEntityStarted;
        Entity.Started += OnEntityStarted;
    }

    internal static void AttachExisting()
    {
        foreach (Entity entity in Entity.LivingEntities) Attach(entity);
    }

    private static void OnEntityStarted(Entity entity) => Attach(entity);

    internal static bool Supports(Enemy enemy)
    {
        return enemy is Gameplay.GameControllers.Enemies.Fool.Fool ||
               enemy is Gameplay.GameControllers.Enemies.WheelCarrier.WheelCarrier;
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
        bool isWheelCarrier = enemy is Gameplay.GameControllers.Enemies.WheelCarrier.WheelCarrier;
        bool addedHandler = false;
        EnemySyncHandler handler = enemy.GetComponent<EnemySyncHandler>();
        if (handler == null)
        {
            if (enemy is Gameplay.GameControllers.Enemies.Fool.Fool)
                handler = enemy.gameObject.AddComponent<FoolEnemySyncHandler>();
            else if (isWheelCarrier)
                handler = enemy.gameObject.AddComponent<WheelCarrierEnemySyncHandler>();
            else
                return;
            addedHandler = true;
        }

        handler.Attach(enemy);
        EnemySync sync = enemy.GetComponent<EnemySync>();
        bool addedSync = sync == null;
        if (sync == null) sync = enemy.gameObject.AddComponent<EnemySync>();
        sync.Attach(handler);

        if (isWheelCarrier && (addedHandler || addedSync))
        {
            bool hasIdentity = EnemyDeathSyncManager.TryGetSyncIdentity(enemy, out string scene, out string spawn, out int generation);
            SharedDiagnosticLogger.Info($"[EnemySyncDiag] WheelCarrier registered scene={(hasIdentity ? scene : "<unresolved>")}, spawn={(hasIdentity ? spawn : "<unresolved>")}, generation={(hasIdentity ? generation : -1)}, enemyInstance={enemy.GetInstanceID()}, object={enemy.gameObject.name}, position={enemy.transform.position}, handler={handler.GetType().Name}, addedHandler={addedHandler}, addedSync={addedSync}");
        }
    }

    internal static void Attach(Entity entity)
    {
        if (entity is Enemy enemy && Supports(enemy)) Attach(enemy);
    }
}
