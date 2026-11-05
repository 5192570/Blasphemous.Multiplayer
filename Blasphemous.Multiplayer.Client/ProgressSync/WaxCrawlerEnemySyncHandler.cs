using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.WaxCrawler;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Gameplay adapter for the crawler enemy class (<see cref="WaxCrawler"/>), which
/// also backs the mud crawler prefab variants (they carry MudCrawlerAudio, a
/// CreepCrawlerAudio subclass, on the same WaxCrawler entity).
///
/// Phase 1 is host-puppet sync only: the scene authority runs the vanilla AI and
/// publishes position/health, followers keep their AI dormant and are posed from
/// snapshots. The emerge/disappear presentation (Rise/Asleep) is not mirrored yet:
/// a follower reveals its crawler when the authority reports it moving, and the
/// return-to-hidden transition is a known gap for a later special-state pass.
///
/// Vanilla traps this adapter deliberately avoids:
/// WaxCrawlerBehaviour.StopMovement(), Damage() and Attack() all throw
/// NotImplementedException, and WaxCrawler.EnemyAttack()/EnemyFloorChecker()/
/// EnemyBumper() do too. Only members that actually work on this class are used:
/// the base EnemyBehaviour.StopBehaviour(), the animator inyector, and Enemy.Target.
/// </summary>
public sealed class WaxCrawlerEnemySyncHandler : EnemySyncHandler
{
    private WaxCrawler _crawler;
    private Vector3 _lastObservedPosition;
    private bool _positionPrimed;
    private bool _isMoving;

    protected override void OnAttach()
    {
        _crawler = Enemy as WaxCrawler;
        _positionPrimed = false;
        _isMoving = false;
    }

    internal override void OnSyncUpdate()
    {
        if (_crawler == null) return;

        // The crawler has no NPCInputs to read walking from (unlike the Fool):
        // Chase() translates the transform directly, so actual displacement of the
        // authority copy is the honest movement signal for the snapshot flag.
        Vector3 position = _crawler.transform.position;
        if (!_positionPrimed)
        {
            _positionPrimed = true;
            _lastObservedPosition = position;
            return;
        }

        _isMoving = (position - _lastObservedPosition).sqrMagnitude > 0.000001f;
        _lastObservedPosition = position;
    }

    internal override bool IsWalking => _isMoving && _crawler != null && !_crawler.Status.Dead;
    internal override bool SyncHitOnDamage => false;

    internal override void SetAuthority(bool authority)
    {
        if (_crawler == null || _crawler.Behaviour == null) return;

        _crawler.Behaviour.EnableBehaviourOnLoad = authority;
        if (!authority)
        {
            // StopBehaviour() is the base-class behaviour-tree stop and is safe here.
            // StopMovement() must not be called: WaxCrawlerBehaviour throws it.
            _crawler.Behaviour.StopBehaviour();
            if (Core.Logic.Penitent != null)
                _crawler.Target = Core.Logic.Penitent.gameObject;
        }

        _crawler.Behaviour.enabled = authority;
    }

    internal override void SetTarget(GameObject target)
    {
        if (_crawler != null && target != null) _crawler.Target = target;
    }

    internal override void ApplySnapshot(byte orientation, byte state, bool walking)
    {
        if (_crawler == null) return;
        _crawler.SetOrientation((EntityOrientation)orientation);
        _crawler.entityCurrentState = (EntityStates)state;
        if (_crawler.AnimatorInyector == null) return;

        // There is no Walk() verb on this inyector: vanilla drives the crawl by
        // animator speed (Rise() sets it to 1, Idle() freezes it to 0), so the
        // follower mirrors that. A moving authority crawler is also a revealed
        // one, so the sprite the behaviour hid at spawn is shown again.
        if (walking && state != (byte)EntityStates.Hurt)
        {
            _crawler.AnimatorInyector.EnableSpriteRenderer = true;
            _crawler.AnimatorInyector.AnimatorSpeed(1f);
        }
        else if (state != (byte)EntityStates.Hurt)
        {
            _crawler.AnimatorInyector.AnimatorSpeed(0f);
        }
    }

    internal override void ApplyRemoteHit(byte orientation)
    {
        if (_crawler == null) return;
        _crawler.SetOrientation((EntityOrientation)orientation);
        if (_crawler.AnimatorInyector == null) return;
        // A hit the authority accepted can only have landed on a revealed crawler.
        _crawler.AnimatorInyector.EnableSpriteRenderer = true;
        _crawler.AnimatorInyector.Hurt();
    }

    internal override void ApplyAuthoritativeDamageReaction(bool dead, byte orientation)
    {
        if (_crawler == null) return;
        _crawler.SetOrientation((EntityOrientation)orientation);
        if (dead)
        {
            ApplyDeath();
        }
        else if (_crawler.AnimatorInyector != null)
        {
            _crawler.AnimatorInyector.EnableSpriteRenderer = true;
            _crawler.AnimatorInyector.Hurt();
            if (_crawler.Audio != null) _crawler.Audio.Hurt();
        }
    }

    internal override void ApplyDeath()
    {
        if (_crawler == null || _crawler.AnimatorInyector == null) return;
        // The death clip (WaxCrawlerDeathBehaviour) destroys the object at its end,
        // exactly as on the owning client; it must be visible and playing to do so.
        _crawler.AnimatorInyector.EnableSpriteRenderer = true;
        _crawler.AnimatorInyector.AnimatorSpeed(1f);
        _crawler.AnimatorInyector.Dead();
        if (_crawler.Audio != null) _crawler.Audio.Death();
    }
}
