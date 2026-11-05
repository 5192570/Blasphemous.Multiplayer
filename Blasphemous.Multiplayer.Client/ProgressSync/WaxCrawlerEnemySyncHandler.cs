using System.Reflection;
using Blasphemous.Multiplayer.Client.Network;
using Blasphemous.Multiplayer.Client.Players;
using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Framework.IA;
using Gameplay.GameControllers.Enemies.WaxCrawler;
using Gameplay.GameControllers.Enemies.WaxCrawler.AI;
using Gameplay.GameControllers.Entities;
using HarmonyLib;
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

    // Perception probe state (authority only, diagnostic): while any player is
    // in the sensor or the crawler is awake, all candidate wake inputs are
    // logged once a second so a known-good run (host in the mud) can be diffed
    // against a failing one (follower in the mud) to find the input the
    // behaviour tree actually gates on. Remove once perception is solved.
    private float _nextProbeTime;
    private bool _awakeObserved;
    private bool _sensorMissingLogged;

    // EnemyBehaviour.PlayerSeen drives this enemy's wake/chase decisions, but
    // vanilla only sets it from the visual CollisionSensor, which remote-player
    // puppets (no sensor-visible collider) can never trip. On the authority the
    // handler therefore drives the backing field directly (see
    // DriveAuthorityPerception). The setter is resolved by name with a
    // backing-field fallback; both are null-guarded.
    private static readonly MethodInfo PlayerSeenSetter =
        AccessTools.PropertySetter(typeof(EnemyBehaviour), "PlayerSeen");
    private static readonly FieldInfo PlayerSeenField =
        AccessTools.Field(typeof(EnemyBehaviour), "<PlayerSeen>k__BackingField");

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

        Collider2D sensor;
        try
        {
            sensor = _crawler.Behaviour.GetVisualSensor();
        }
        catch (System.Exception)
        {
            if (!_sensorMissingLogged)
            {
                _sensorMissingLogged = true;
                SharedDiagnosticLogger.Warn($"[WaxPerceptionDiag] GetVisualSensor threw on authority; perception drive inactive enemyInstance={_crawler.GetInstanceID()}");
            }
            return;
        }
        if (sensor == null)
        {
            if (!_sensorMissingLogged)
            {
                _sensorMissingLogged = true;
                SharedDiagnosticLogger.Warn($"[WaxPerceptionDiag] Visual sensor is null on authority; perception drive inactive enemyInstance={_crawler.GetInstanceID()}");
            }
            return;
        }

        CollisionSensor collisionSensor = sensor.GetComponent<CollisionSensor>();
        bool localSeen = collisionSensor != null && collisionSensor.IsColliding();

        bool remoteSeen = false;
        if (Main.Multiplayer != null && Main.Multiplayer.OtherPlayerManager != null)
        {
            foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
            {
                if (player == null || !player.gameObject.activeInHierarchy) continue;

                // The puppet root sits at the player's feet; test the body above
                // it too, matching what the Penitent's own collider would cover.
                // The bounds test backs up OverlapPoint in case the sensor
                // collider's enabled state makes the geometric test unreliable.
                Vector3 position = player.transform.position;
                if (sensor.OverlapPoint((Vector2)position) ||
                    sensor.OverlapPoint((Vector2)(position + Vector3.up)) ||
                    sensor.bounds.Contains(position + Vector3.up * 0.5f))
                {
                    remoteSeen = true;
                    break;
                }
            }
        }

        bool seen = localSeen || remoteSeen;
        if (_crawler.Behaviour.PlayerSeen != seen)
        {
            SetPlayerSeen(seen);
            SharedDiagnosticLogger.Info($"[WaxPerceptionDiag] PlayerSeen -> {seen} (localSensor={localSeen}, remoteInSensor={remoteSeen}), enemyInstance={_crawler.GetInstanceID()}, sensorBounds={sensor.bounds}");
        }

        bool awakeFlip = _crawler.Behaviour.Awake != _awakeObserved;
        _awakeObserved = _crawler.Behaviour.Awake;
        if (awakeFlip || ((seen || _awakeObserved) && Time.unscaledTime >= _nextProbeTime))
        {
            _nextProbeTime = Time.unscaledTime + 1f;
            LogPerceptionProbe(sensor, collisionSensor, localSeen, remoteSeen);
        }
    }

    private void LogPerceptionProbe(Collider2D sensor, CollisionSensor collisionSensor, bool localSeen, bool remoteSeen)
    {
        WaxCrawlerBehaviour behaviour = _crawler.Behaviour;

        string touched = "<none>";
        if (collisionSensor != null)
        {
            Entity[] entities = collisionSensor.GetTouchedEntities();
            if (entities.Length > 0)
            {
                touched = "";
                for (int i = 0; i < entities.Length; i++)
                {
                    if (i > 0) touched += ",";
                    touched += entities[i] == null ? "<null>" : entities[i].GetType().Name;
                }
            }
        }

        bool hearingColliding = false;
        try
        {
            Collider2D hearing = behaviour.GetHearingSensor();
            CollisionSensor hearingSensor = hearing == null ? null : hearing.GetComponent<CollisionSensor>();
            hearingColliding = hearingSensor != null && hearingSensor.IsColliding();
        }
        catch (System.Exception)
        {
        }

        string animatorState = "<none>";
        Animator animator = _crawler.AnimatorInyector == null ? null : _crawler.AnimatorInyector.Animator;
        if (animator != null)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            animatorState = info.IsName("Appear") ? "Appear"
                : info.IsName("Disappear") ? "Disappear"
                : info.IsName("Walk") ? "Walk"
                : info.IsName("Idle") ? "Idle"
                : info.IsName("Hurt") ? "Hurt"
                : info.IsName("Death") ? "Death"
                : "hash:" + info.shortNameHash;
        }

        float distPenitent = -1f;
        if (Core.Logic.Penitent != null)
            distPenitent = Vector2.Distance((Vector2)_crawler.transform.position, (Vector2)Core.Logic.Penitent.transform.position);
        float distPuppet = -1f;
        if (Main.Multiplayer != null && Main.Multiplayer.OtherPlayerManager != null)
        {
            foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
            {
                if (player == null || !player.gameObject.activeInHierarchy) continue;
                float d = Vector2.Distance((Vector2)_crawler.transform.position, (Vector2)player.transform.position);
                if (distPuppet < 0f || d < distPuppet) distPuppet = d;
            }
        }

        SharedDiagnosticLogger.Info($"[WaxPerceptionProbe] instance={_crawler.GetInstanceID()}, awake={behaviour.Awake}, melting={behaviour.Melting}, targetLost={behaviour.TargetLost}, playerSeen={behaviour.PlayerSeen}, playerHeard={behaviour.PlayerHeard}, entityTarget={DescribeTarget(_crawler.Target)}, behaviourTarget={DescribeTarget(behaviour.Target == null ? null : behaviour.Target.gameObject)}, visualColliding={(collisionSensor != null && collisionSensor.IsColliding())}, touched=[{touched}], hearingColliding={hearingColliding}, animator={animatorState}, distPenitent={distPenitent:0.00}, distPuppet={distPuppet:0.00}, seenLocal={localSeen}, seenRemote={remoteSeen}, sensorBounds={sensor.bounds}");
    }

    private static string DescribeTarget(GameObject target)
    {
        if (target == null) return "<null>";
        if (Core.Logic.Penitent != null && target == Core.Logic.Penitent.gameObject) return "Penitent(local)";
        if (target.GetComponent<OtherPlayerScript>() != null) return "OtherPlayer(remote)";
        return target.name;
    }

    private void SetPlayerSeen(bool seen)
    {
        if (PlayerSeenSetter != null)
            PlayerSeenSetter.Invoke(_crawler.Behaviour, new object[] { seen });
        else if (PlayerSeenField != null)
            PlayerSeenField.SetValue(_crawler.Behaviour, seen);
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
                // Replay the vanilla submerge entry. The follower's own Disappear
                // state behaviour then disables the damage collider and calls
                // Asleep() at the end of the clip, exactly as on the authority;
                // the position snap to origin arrives with the Hidden snapshot.
                _crawler.AnimatorInyector.EnableSpriteRenderer = true;
                _crawler.AnimatorInyector.AnimatorSpeed(1f);
                if (changed)
                {
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
