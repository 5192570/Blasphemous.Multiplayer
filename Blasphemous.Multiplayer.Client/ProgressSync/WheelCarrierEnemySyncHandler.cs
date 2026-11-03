using Blasphemous.ModdingAPI;
using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.WheelCarrier;
using Gameplay.GameControllers.Enemies.WheelCarrier.Animator;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Gameplay adapter for Zealot (the WheelCarrier enemy class).</summary>
public sealed class WheelCarrierEnemySyncHandler : EnemySyncHandler
{
    private WheelCarrier _wheelCarrier;

    protected override void OnAttach() => _wheelCarrier = Enemy as WheelCarrier;

    internal override bool IsWalking => _wheelCarrier != null && _wheelCarrier.Input != null && Mathf.Abs(_wheelCarrier.Input.HorizontalInput) > 0.01f;
    internal override bool SyncHitOnDamage => true;

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
        if (_wheelCarrier.AnimatorInyector == null || _wheelCarrier.IsAttacking) return;
        if (walking && state != (byte)EntityStates.Hurt) _wheelCarrier.AnimatorInyector.Walk();
        else _wheelCarrier.AnimatorInyector.Stop();
    }

    private bool _replayingRemoteAttack;

    internal bool IsReplayingRemoteAttack => _replayingRemoteAttack;

    internal void ApplyRemoteAttack(byte orientation)
    {
        if (_wheelCarrier == null)
        {
            ModLog.Warn("[EnemyAttackSyncDiag] Cannot replay attack: WheelCarrier reference is null");
            return;
        }
        if (_wheelCarrier.AnimatorInyector == null)
        {
            ModLog.Warn($"[EnemyAttackSyncDiag] Cannot replay attack: AnimatorInyector is null enemyInstance={_wheelCarrier.GetInstanceID()}, object={_wheelCarrier.gameObject.name}");
            return;
        }
        if (_wheelCarrier.IsAttacking)
        {
            ModLog.Warn($"[EnemyAttackSyncDiag] Remote attack ignored: enemy already attacking enemyInstance={_wheelCarrier.GetInstanceID()}, object={_wheelCarrier.gameObject.name}");
            return;
        }

        ModLog.Info($"[EnemyAttackSyncDiag] Triggering native attack enemyInstance={_wheelCarrier.GetInstanceID()}, object={_wheelCarrier.gameObject.name}, orientation={orientation}, position={_wheelCarrier.transform.position}");
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
            ModLog.Warn($"[EnemyAttackSyncDiag] Native attack hook could not find parent WheelCarrier injectorInstance={__instance.GetInstanceID()}, object={__instance.gameObject.name}");
            return;
        }

        WheelCarrierEnemySyncHandler handler = wheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null)
        {
            ModLog.Warn($"[EnemyAttackSyncDiag] Native attack hook found WheelCarrier but no sync handler enemyInstance={wheelCarrier.GetInstanceID()}, object={wheelCarrier.gameObject.name}");
            return;
        }
        if (handler.IsReplayingRemoteAttack) return;

        EnemySync sync = wheelCarrier.GetComponent<EnemySync>();
        if (sync == null)
        {
            ModLog.Warn($"[EnemyAttackSyncDiag] Native attack hook found no EnemySync enemyInstance={wheelCarrier.GetInstanceID()}, object={wheelCarrier.gameObject.name}");
            return;
        }

        ModLog.Info($"[EnemyAttackSyncDiag] Native attack hook fired enemyInstance={wheelCarrier.GetInstanceID()}, object={wheelCarrier.gameObject.name}, position={wheelCarrier.transform.position}, authority={sync.IsAuthority}");
        sync.NotifyNativeAttackStarted();
    }
}
