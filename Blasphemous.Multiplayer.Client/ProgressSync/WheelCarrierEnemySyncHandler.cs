using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.WheelCarrier;
using Gameplay.GameControllers.Entities;
using UnityEngine;

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
        if (_wheelCarrier.AnimatorInyector == null) return;
        if (walking && state != (byte)EntityStates.Hurt) _wheelCarrier.AnimatorInyector.Walk();
        else _wheelCarrier.AnimatorInyector.Stop();
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
