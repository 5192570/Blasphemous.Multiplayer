using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Fool;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Gameplay adapter for Prosylte (the Fool enemy class).</summary>
public sealed class FoolEnemySyncHandler : EnemySyncHandler
{
    private Fool _fool;

    protected override void OnAttach() => _fool = Enemy as Fool;

    internal override bool IsWalking => _fool != null && _fool.Input != null && Mathf.Abs(_fool.Input.HorizontalInput) > 0.01f;
    internal override bool SyncHitOnDamage => false;

    internal override void SetAuthority(bool authority)
    {
        if (_fool == null || _fool.Behaviour == null) return;

        _fool.Behaviour.EnableBehaviourOnLoad = authority;
        if (!authority)
        {
            // FoolBehaviour.OnUpdate can restart its behaviour tree independently of
            // EnableBehaviourOnLoad. Disable the component to keep follower AI dormant.
            _fool.Behaviour.StopBehaviour();
            _fool.Behaviour.StopMovement();
            if (Core.Logic.Penitent != null)
                _fool.Target = Core.Logic.Penitent.gameObject;
        }

        _fool.Behaviour.enabled = authority;
    }

    internal override void SetTarget(GameObject target)
    {
        if (_fool != null && target != null) _fool.Target = target;
    }

    internal override void ApplySnapshot(byte orientation, byte state, bool walking)
    {
        if (_fool == null) return;
        _fool.SetOrientation((EntityOrientation)orientation);
        _fool.entityCurrentState = (EntityStates)state;
        if (_fool.AnimatorInyector == null) return;
        if (walking && state != (byte)EntityStates.Hurt) _fool.AnimatorInyector.Walk();
        else _fool.AnimatorInyector.StopWalk();
    }

    internal override void ApplyRemoteHit(byte orientation)
    {
        if (_fool == null) return;
        _fool.SetOrientation((EntityOrientation)orientation);
        _fool.entityCurrentState = EntityStates.Hurt;
    }

    internal override void ApplyDeath()
    {
        if (_fool != null && _fool.Behaviour != null) _fool.Behaviour.Death();
    }
}
