using Blasphemous.ModdingAPI;
using Blasphemous.Multiplayer.Client.Network;
using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.WheelCarrier;
using Gameplay.GameControllers.Enemies.WheelCarrier.Animator;
using Gameplay.GameControllers.Enemies.WheelCarrier.IA;
using Gameplay.GameControllers.Combat;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Gameplay adapter for Zealot (the WheelCarrier enemy class).</summary>
public sealed class WheelCarrierEnemySyncHandler : EnemySyncHandler
{
    private WheelCarrier _wheelCarrier;
    private byte _specialState;
    private bool _applyingRemoteSpecialState;

    protected override void OnAttach()
    {
        _wheelCarrier = Enemy as WheelCarrier;
        if (_wheelCarrier != null && _wheelCarrier.Behaviour != null && _wheelCarrier.Behaviour.GotParry)
        {
            _specialState = _wheelCarrier.EntExecution != null && _wheelCarrier.EntExecution.enabled ? (byte)2 : (byte)1;
        }
    }

    internal override bool IsWalking => _wheelCarrier != null && _wheelCarrier.Input != null && Mathf.Abs(_wheelCarrier.Input.HorizontalInput) > 0.01f;
    internal override bool SyncHitOnDamage => true;
    internal override byte SpecialState => _specialState;
    internal bool IsApplyingRemoteSpecialState => _applyingRemoteSpecialState;

    internal void NotifyNativeSpecialState(byte state)
    {
        if (_applyingRemoteSpecialState || _wheelCarrier == null || state > 2 || _specialState == state) return;
        _specialState = state;
        EnemyDeathSyncManager.ReportWheelCarrierStateChange(_wheelCarrier, state);
    }

    internal void ApplyRemoteSpecialState(byte state, byte orientation)
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null || state > 2) return;
        _wheelCarrier.SetOrientation((EntityOrientation)orientation);
        if (_specialState == state) return;

        _applyingRemoteSpecialState = true;
        try
        {
            switch (state)
            {
                case 0:
                    if (_specialState == 2) _wheelCarrier.Behaviour.Alive();
                    else _wheelCarrier.Behaviour.GotParry = false;
                    break;
                case 1:
                    _wheelCarrier.Behaviour.Parry();
                    break;
                case 2:
                    _wheelCarrier.Behaviour.Execution();
                    break;
            }
            _specialState = state;
            SharedDiagnosticLogger.Info($"[WheelCarrierStateSync] Applied remote state enemyInstance={_wheelCarrier.GetInstanceID()}, state={state}, orientation={orientation}");
        }
        finally
        {
            _applyingRemoteSpecialState = false;
        }
    }

    internal override void ApplySpecialState(byte state)
    {
        if (_wheelCarrier == null) return;
        ApplyRemoteSpecialState(state, (byte)_wheelCarrier.Status.Orientation);
    }

    internal override void SetAuthority(bool authority)
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null) return;

        _wheelCarrier.Behaviour.EnableBehaviourOnLoad = authority;
        if (!authority)
        {
            _wheelCarrier.Behaviour.StopBehaviour();
            _wheelCarrier.Behaviour.StopMovement();
            if (Core.Logic.Penitent != null)
                _wheelCarrier.Target = Core.Logic.Penitent.gameObject;
        }

        _wheelCarrier.Behaviour.enabled = authority;
    }

    internal override void SetTarget(GameObject target)
    {
        if (_wheelCarrier != null && target != null) _wheelCarrier.Target = target;
    }

    internal override void ApplySnapshot(byte orientation, byte state, bool walking)
    {
        if (_wheelCarrier == null) return;
        _wheelCarrier.SetOrientation((EntityOrientation)orientation);
        _wheelCarrier.entityCurrentState = (EntityStates)state;
        // Parry and execution own their native presentation; periodic movement snapshots
        // must not overwrite those animations or reactivate movement while special state is active.
        if (_specialState != 0 || _wheelCarrier.AnimatorInyector == null || _wheelCarrier.IsAttacking) return;
        if (walking && state != (byte)EntityStates.Hurt) _wheelCarrier.AnimatorInyector.Walk();
        else _wheelCarrier.AnimatorInyector.Stop();
    }

    private bool _replayingRemoteAttack;

    internal bool IsReplayingRemoteAttack => _replayingRemoteAttack;

    internal void ApplyRemoteAttack(byte orientation)
    {
        if (_wheelCarrier == null)
        {
            SharedDiagnosticLogger.Warn("[EnemyAttackSyncDiag] Cannot replay attack: WheelCarrier reference is null");
            return;
        }
        if (_wheelCarrier.AnimatorInyector == null)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Cannot replay attack: AnimatorInyector is null enemyInstance={_wheelCarrier.GetInstanceID()}, object={_wheelCarrier.gameObject.name}");
            return;
        }
        if (_wheelCarrier.IsAttacking)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Remote attack ignored: enemy already attacking enemyInstance={_wheelCarrier.GetInstanceID()}, object={_wheelCarrier.gameObject.name}");
            return;
        }

        SharedDiagnosticLogger.Info($"[EnemyAttackSyncDiag] Triggering native attack enemyInstance={_wheelCarrier.GetInstanceID()}, object={_wheelCarrier.gameObject.name}, orientation={orientation}, position={_wheelCarrier.transform.position}");
        _wheelCarrier.SetOrientation((EntityOrientation)orientation);
        _replayingRemoteAttack = true;
        try
        {
            _wheelCarrier.AnimatorInyector.Attack();
        }
        finally
        {
            _replayingRemoteAttack = false;
        }
    }

    internal override void ApplyRemoteHit(byte orientation)
    {
        if (_wheelCarrier == null) return;
        _wheelCarrier.SetOrientation((EntityOrientation)orientation);
        if (_wheelCarrier.DamageEffect != null) _wheelCarrier.DamageEffect.Blink(0f, 0.1f);
    }

    internal override void ApplyAuthoritativeDamageReaction(bool dead, byte orientation)
    {
        if (_wheelCarrier == null) return;
        _wheelCarrier.SetOrientation((EntityOrientation)orientation);
        if (dead)
        {
            if (_wheelCarrier.Behaviour != null) _wheelCarrier.Behaviour.Death();
            if (_wheelCarrier.Audio != null) _wheelCarrier.Audio.PlayDeath();
        }
        else
        {
            if (_wheelCarrier.DamageEffect != null)
                _wheelCarrier.DamageEffect.Blink(0f, 0.1f);
        }
    }

    internal override void ApplyDeath()
    {
        if (_wheelCarrier == null) return;
        if (_wheelCarrier.Behaviour != null) _wheelCarrier.Behaviour.Death();
        if (_wheelCarrier.Audio != null) _wheelCarrier.Audio.PlayDeath();
    }
}

[HarmonyLib.HarmonyPatch(typeof(WheelCarrierAnimatorInyector), nameof(WheelCarrierAnimatorInyector.Attack))]
internal static class WheelCarrierAttackSyncPatch
{
    [HarmonyLib.HarmonyPostfix]
    private static void Postfix(WheelCarrierAnimatorInyector __instance)
    {
        if (__instance == null) return;
        WheelCarrier wheelCarrier = __instance.GetComponentInParent<WheelCarrier>();
        if (wheelCarrier == null)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Native attack hook could not find parent WheelCarrier injectorInstance={__instance.GetInstanceID()}, object={__instance.gameObject.name}");
            return;
        }

        WheelCarrierEnemySyncHandler handler = wheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Native attack hook found WheelCarrier but no sync handler enemyInstance={wheelCarrier.GetInstanceID()}, object={wheelCarrier.gameObject.name}");
            return;
        }
        if (handler.IsReplayingRemoteAttack) return;

        EnemySync sync = wheelCarrier.GetComponent<EnemySync>();
        if (sync == null)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Native attack hook found no EnemySync enemyInstance={wheelCarrier.GetInstanceID()}, object={wheelCarrier.gameObject.name}");
            return;
        }

        SharedDiagnosticLogger.Info($"[EnemyAttackSyncDiag] Native attack hook fired enemyInstance={wheelCarrier.GetInstanceID()}, object={wheelCarrier.gameObject.name}, position={wheelCarrier.transform.position}, authority={sync.IsAuthority}");
        sync.NotifyNativeAttackStarted();
    }
}

[HarmonyLib.HarmonyPatch(typeof(WheelCarrierBehaviour), nameof(WheelCarrierBehaviour.Parry))]
internal static class WheelCarrierParryStateSyncPatch
{
    [HarmonyLib.HarmonyPostfix]
    private static void Postfix(WheelCarrierBehaviour __instance) => WheelCarrierStateSyncPatch.Notify(__instance, 1);
}

[HarmonyLib.HarmonyPatch(typeof(WheelCarrierBehaviour), nameof(WheelCarrierBehaviour.Execution))]
internal static class WheelCarrierExecutionStateSyncPatch
{
    [HarmonyLib.HarmonyPostfix]
    private static void Postfix(WheelCarrierBehaviour __instance) => WheelCarrierStateSyncPatch.Notify(__instance, 2);
}

[HarmonyLib.HarmonyPatch(typeof(WheelCarrierBehaviour), nameof(WheelCarrierBehaviour.Alive))]
internal static class WheelCarrierAliveStateSyncPatch
{
    [HarmonyLib.HarmonyPostfix]
    private static void Postfix(WheelCarrierBehaviour __instance) => WheelCarrierStateSyncPatch.Notify(__instance, 0);
}

internal static class WheelCarrierStateSyncPatch
{
    internal static void Notify(WheelCarrierBehaviour behaviour, byte state)
    {
        if (behaviour == null || behaviour.WheelCarrier == null) return;
        WheelCarrierEnemySyncHandler handler = behaviour.WheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null)
        {
            EnemySyncManager.Attach(behaviour.WheelCarrier);
            handler = behaviour.WheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        }
        if (handler != null && !handler.IsApplyingRemoteSpecialState)
            handler.NotifyNativeSpecialState(state);
    }
}

// Capture one complete WheelCarrier hit around both its execution and ordinary damage branches.
// The nested EnemyDamageArea patch is suppressed by the active same-enemy damage context.
[HarmonyLib.HarmonyPatch(typeof(WheelCarrier), nameof(WheelCarrier.Damage))]
internal static class WheelCarrierDamageSyncPatch
{
    [HarmonyLib.HarmonyPrefix]
    private static void Prefix(WheelCarrier __instance, Hit hit, out EnemyDamageContext __state)
    {
        EnemyDeathSyncManager.TryCaptureLocalHit(__instance, hit, out __state);
    }

    [HarmonyLib.HarmonyPostfix]
    private static void Postfix(WheelCarrier __instance, EnemyDamageContext __state)
    {
        EnemyDeathSyncManager.CompleteLocalHit(__state);
        if (__instance == null || __instance.Behaviour == null || __instance.Behaviour.GotParry) return;
        WheelCarrierEnemySyncHandler handler = __instance.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler != null && handler.SpecialState == 1)
            handler.NotifyNativeSpecialState(0);
    }
}
