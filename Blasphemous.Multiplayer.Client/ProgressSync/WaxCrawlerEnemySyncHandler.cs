using Blasphemous.Multiplayer.Client.Network;
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
/// snapshots. The emerge/disappear presentation is mirrored through the shared
/// special-state byte: Hidden (0), Emerged (1), Submerging (2). The follower
/// replays the vanilla Appear/Hide animator entries on transitions, so its own
/// animator state behaviours run the same collider/attackable and Asleep()
/// side effects as on the authority.
///
/// Vanilla traps this adapter deliberately avoids:
/// WaxCrawlerBehaviour.StopMovement(), Damage() and Attack() all throw
/// NotImplementedException, and WaxCrawler.EnemyAttack()/EnemyFloorChecker()/
/// EnemyBumper() do too. Only members that actually work on this class are used:
/// the base EnemyBehaviour.StopBehaviour(), the animator inyector, and Enemy.Target.
/// </summary>
public sealed class WaxCrawlerEnemySyncHandler : EnemySyncHandler
{
    // Special-state values for the emerge/disappear presentation. These ride the
    // existing snapshot specialState byte, whose protocol range is already 0-2.
    private const byte PresentationHidden = 0;
    private const byte PresentationEmerged = 1;
    private const byte PresentationSubmerging = 2;
    private const byte PresentationUnknown = 255;

    private WaxCrawler _crawler;
    private Vector3 _lastObservedPosition;
    private bool _positionPrimed;
    private bool _isMoving;
    private byte _appliedPresentationState = PresentationUnknown;
    private bool _isAuthority;

    private float _nextRiseDriveTime;
    private bool _sensorMissingLogged;

    // Shared senses plumbing (sight/hearing field drives + sensor contact
    // mirroring for remote players). This adapter's own perception policy is
    // only the Rise() drive in DriveAuthorityPerception below.
    private readonly EnemyRemotePerception _remotePerception = new EnemyRemotePerception();

    protected override void OnAttach()
    {
        _crawler = Enemy as WaxCrawler;
        _positionPrimed = false;
        _isMoving = false;
        _appliedPresentationState = PresentationUnknown;
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

        if (_isAuthority) DriveAuthorityPerception();
    }

    /// <summary>
    /// Authority-side perception for remote players. Computes "a player is inside
    /// the visual sensor" from the real Penitent (via the sensor's own collision
    /// list, exactly as vanilla leaves it) plus every remote puppet's position
    /// tested against the same sensor collider, then writes the result into
    /// PlayerSeen's backing field so every consumer (property, IsPlayerSeen(),
    /// behaviour-tree reflection) observes it. Transitions are logged so a test
    /// log shows whether this link in the chain is doing its part.
    /// </summary>
    private void DriveAuthorityPerception()
    {
        if (_crawler == null || _crawler.Behaviour == null) return;

        bool wasSeen = _crawler.Behaviour.PlayerSeen;
        bool seen = _remotePerception.DrivePerception(_crawler.Behaviour,
            out bool localSeen, out bool remoteSeen, out bool sensorMissing);
        if (sensorMissing)
        {
            if (!_sensorMissingLogged)
            {
                _sensorMissingLogged = true;
                SharedDiagnosticLogger.Warn($"[WaxPerceptionDiag] Visual sensor unavailable on authority; perception drive inactive enemyInstance={_crawler.GetInstanceID()}");
            }
            return;
        }
        if (seen != wasSeen)
            SharedDiagnosticLogger.Info($"[WaxPerceptionDiag] PlayerSeen -> {seen} (localSensor={localSeen}, remoteInSensor={remoteSeen}), enemyInstance={_crawler.GetInstanceID()}");

        // The tree's wake branch gates on the sensor's touched-entity list,
        // which can only ever contain real Entity objects -- a puppet can
        // never satisfy it (probe steps 5c/5d: every other wake input matched
        // the working host case, awake stayed false). The branch's only
        // effect is calling Rise(), which is self-contained (Awake/Melting,
        // sprite, animator; no blackboard state), so drive it directly while
        // a player is seen and the crawler is asleep. Throttled: if the tree
        // actively undoes it, these log lines repeating while seen proves it
        // and the graph-extraction route is next.
        if (seen && !_crawler.Behaviour.Awake && !_crawler.Status.Dead &&
            Time.unscaledTime >= _nextRiseDriveTime)
        {
            _nextRiseDriveTime = Time.unscaledTime + 0.5f;
            _crawler.Behaviour.Rise();
            SharedDiagnosticLogger.Info($"[WaxPerceptionDiag] Rise() driven by handler (localSeen={localSeen}, remoteSeen={remoteSeen}), enemyInstance={_crawler.GetInstanceID()}");
        }
    }

    internal override bool IsWalking => _isMoving && _crawler != null && !_crawler.Status.Dead;
    internal override bool SyncHitOnDamage => false;

    internal override byte SpecialState
    {
        get
        {
            if (_crawler == null || _crawler.Behaviour == null || !_crawler.Behaviour.Awake)
                return PresentationHidden;
            return IsSubmerging() ? PresentationSubmerging : PresentationEmerged;
        }
    }

    private bool IsSubmerging()
    {
        Animator animator = _crawler.AnimatorInyector == null ? null : _crawler.AnimatorInyector.Animator;
        if (animator == null) return false;
        // The Disappear state is where vanilla plays the submerge clip; its state
        // behaviour calls Asleep() (hide + return to origin) at the end of it.
        return animator.GetCurrentAnimatorStateInfo(0).IsName("Disappear") ||
               animator.GetNextAnimatorStateInfo(0).IsName("Disappear");
    }

    internal override void ApplySpecialState(byte state, string authorityName)
    {
        if (_crawler == null || state > PresentationSubmerging || _crawler.Status.Dead) return;

        bool changed = state != _appliedPresentationState;
        _appliedPresentationState = state;
        if (_crawler.AnimatorInyector == null) return;
        Animator animator = _crawler.AnimatorInyector.Animator;

        switch (state)
        {
            case PresentationHidden:
                _crawler.AnimatorInyector.EnableSpriteRenderer = false;
                _crawler.AnimatorInyector.AnimatorSpeed(0f);
                if (changed) SetAttackable(false);
                break;

            case PresentationEmerged:
                _crawler.AnimatorInyector.EnableSpriteRenderer = true;
                _crawler.AnimatorInyector.AnimatorSpeed(1f);
                if (changed)
                {
                    _crawler.AnimatorInyector.Appear();
                    SetAttackable(true);
                }
                break;

            case PresentationSubmerging:
                // Enter Disappear by state name rather than replaying Hide()'s
                // HIDE trigger. The extracted AnimatorController showed the
                // trigger is only consumed by the Crawling -> Disappear
                // transition: a HIDE set while the follower is in Appear
                // latches until Crawling (early submerge), and trigger timing
                // races can leave the follower stuck in Crawling instead.
                // Play() enters the state deterministically from anywhere,
                // and the Disappear state behaviour still runs its vanilla
                // side effects (damage collider off, Asleep() at clip end,
                // return to origin); the Hidden snapshot follows from the
                // authority as before.
                _crawler.AnimatorInyector.EnableSpriteRenderer = true;
                _crawler.AnimatorInyector.AnimatorSpeed(1f);
                if (changed)
                {
                    if (animator != null)
                        animator.Play("Disappear", 0, 0f);
                    else
                        _crawler.AnimatorInyector.Hide();
                    SetAttackable(false);
                }
                break;
        }
    }

    private void SetAttackable(bool attackable)
    {
        if (_crawler == null) return;
        _crawler.CanBeAttacked = attackable;
        if (_crawler.DamageArea != null && _crawler.DamageArea.DamageAreaCollider != null)
            _crawler.DamageArea.DamageAreaCollider.enabled = attackable;
    }

    internal override void OnAuthorityContextChanged(string reason)
    {
        // Presentation copied from the previous authority must be re-evaluated
        // against the new authority's next snapshot, not kept as-is.
        _appliedPresentationState = PresentationUnknown;
    }

    internal override void SetAuthority(bool authority)
    {
        if (_crawler == null || _crawler.Behaviour == null) return;

        // Whichever role comes next must (re)apply presentation from its own
        // source: vanilla AI on the authority, snapshots on a follower.
        _appliedPresentationState = PresentationUnknown;
        _isAuthority = authority;
        if (!authority) _remotePerception.ClearMirrors();
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

        if (authority)
        {
            // WaxCrawlerBehaviour.OnUpdate never restarts its behaviour tree
            // (unlike FoolBehaviour, which self-starts when a player is near),
            // so the tree stopped by the follower pass above would stay stopped
            // forever and the crawler would never emerge or move on the host.
            // StartBehaviour() is guarded vanilla-side: no-op when the tree is
            // null or already running.
            _crawler.Behaviour.StartBehaviour();
        }
    }

    internal override void SetTarget(GameObject target)
    {
        if (_crawler == null || target == null) return;
        _crawler.Target = target;
        // The behaviour carries its own Target transform, separate from the
        // entity's; keep both pointing at the committed player in case the
        // behaviour tree reads the behaviour-side one.
        if (_crawler.Behaviour != null) _crawler.Behaviour.Target = target.transform;
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
