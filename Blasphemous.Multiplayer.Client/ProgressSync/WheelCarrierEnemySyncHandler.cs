using System;
using Blasphemous.ModdingAPI;
using Blasphemous.Multiplayer.Client.Network;
using Blasphemous.Multiplayer.Client.Players;
using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.AnimationBehaviours.Enemies.Stunt;
using Gameplay.GameControllers.Enemies.WheelCarrier;
using Gameplay.GameControllers.Enemies.WheelCarrier.Animator;
using Gameplay.GameControllers.Enemies.WheelCarrier.IA;
using Gameplay.GameControllers.Combat;
using Gameplay.GameControllers.Entities;
using Tools.Level;
using Tools.Level.Interactables;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Gameplay adapter for Zealot (the WheelCarrier enemy class).
///
/// The WheelCarrier's parry/execution cycle is not a plain boolean: it is driven by
/// two local, one-shot mechanisms that only exist on the client whose Penitent
/// performed the parry.
///
///   Parry()      -> Animator.Play("Parry"). The "Parry" animator state carries a
///                   ParryBehaviour which calls Alive() after ~1s.
///   Execution()  -> hides the sprite, disables the Attack object, moves the enemy to
///                   the Default layer and instantiates a player-interactable
///                   Execution prefab. Only that prefab's StuntBehaviour (i.e. the
///                   local Penitent actually performing the riposte) returns the
///                   enemy to Alive().
///
/// Therefore a client that merely *observes* the transition must never run those
/// methods: the parry would silently self-cancel after a second, and the execution
/// would hand control of the enemy to a Penitent that never asked to riposte and
/// would never finish, leaving the enemy frozen and unhittable. Observers instead
/// run a fully reversible presentation mirror (see ApplyMirrorPresentation) and let
/// the player who owns the transition drive it through the authority.
///
/// A mirror is *never* ended on a timer. It ends only on an authoritative condition:
/// the owner reporting the state ended, an authoritative death, the owner leaving the
/// session, a scene-authority handoff, or losing the connection. Elapsed time only
/// produces a diagnostic warning.
/// </summary>
public sealed class WheelCarrierEnemySyncHandler : EnemySyncHandler
{
    // A native transition on the owning client wins over authority echoes for a moment,
    // so a snapshot that crosses a local Parry -> Execution transition cannot undo it.
    private const float LocalOwnershipGrace = 1f;

    // Diagnostics only: how long a mirror may go without being confirmed before we log.
    private const float MirrorUnconfirmedWarning = 10f;
    private const float MirrorCheckInterval = 0.5f;

    // A presentation clone is a cosmetic object; it is never allowed to hold on to the scene.
    // The clip itself decides when it ends - this is only a leak guard for a clone whose
    // animator never reports a finished state.
    private const float ExecutionPresentationMinPlayTime = 0.25f;
    private const float ExecutionPresentationMaxLifetime = 20f;

    private WheelCarrier _wheelCarrier;
    private byte _specialState;
    private bool _applyingRemoteSpecialState;
    private bool _mirroringRemoteState;
    private string _mirrorOwner = string.Empty;
    private bool _mirrorOwnerIsAuthority;
    private float _specialStateChangedAt;
    private float _lastMirrorConfirmedAt;
    private float _nextMirrorCheck;
    private float _nextMirrorWarning;
    private bool _mirrorOwnerPresenceKnown;
    private byte _lastLeftLocalState;
    private bool _parryRecoverySuppressionLogged;

    private GameObject _executionPresentation;
    private Animator _executionPresentationAnimator;
    private Animator _executionInteractorAnimator;
    private float _executionPresentationStartedAt;
    private float _executionPresentationCreatedAt;
    private bool _executionClipStarted;

    // True once the owner's finisher was triggered on the clone. Until then the clone is only the
    // owner's executionable presentation (the victim's stun loop), which is a different object
    // lifetime: it can end without any death, and a death is not a completed execution.
    private bool _executionFinisherStarted;

    /// <summary>Owner whose remote avatar this client hid for the finisher, if any.</summary>
    private string _hiddenExecutionAvatar;

    // Leak guard for the executionable phase only. The owner's window is Enemy.StuntTime, and its
    // end arrives as a state report; this never restores presentation on its own.
    private const float ExecutionablePresentationMaxLifetime = 15f;
    private static bool _executionPrefabShapeLogged;

    // Diagnostics only: after a death we watch both hierarchies for a bounded window and log
    // every renderer/particle/animator that starts or stops drawing. This is how "which object
    // still draws the enemy?" gets answered instead of guessed.
    private const float VisualWatchSeconds = 8f;

    // Sampling interval for the renderer diff: transitions are what matter, and 10 Hz keeps the
    // diagnostic stream readable next to the queue drops seen in the last log.
    private const float VisualWatchSampleInterval = 0.1f;

    private float _visualWatchUntil;
    private float _nextVisualSample;
    private Transform _visualWatchClone;
    private System.Collections.Generic.Dictionary<string, string> _visualEnemyState;
    private System.Collections.Generic.Dictionary<string, string> _visualCloneState;

    protected override void OnAttach()
    {
        WheelCarrier wheelCarrier = Enemy as WheelCarrier;

        // EnemySyncManager.Attach() runs again for every late enemy report, so this must
        // be idempotent. Re-running the initialisation used to reset the ownership
        // timestamps, which made the grace window swallow the very transition the report
        // had just asked us to apply (visible as "age=0.000" in the diagnostics).
        if (ReferenceEquals(_wheelCarrier, wheelCarrier)) return;

        _wheelCarrier = wheelCarrier;
        _specialState = 0;
        _mirroringRemoteState = false;
        _mirrorOwner = string.Empty;
        _mirrorOwnerIsAuthority = false;
        _mirrorOwnerPresenceKnown = false;
        _lastLeftLocalState = 0;
        _parryRecoverySuppressionLogged = false;
        _executionPresentation = null;
        _executionPresentationAnimator = null;
        // Never "just changed": a freshly attached enemy has no local ownership to protect.
        _specialStateChangedAt = float.NegativeInfinity;
        _lastMirrorConfirmedAt = float.NegativeInfinity;

        if (wheelCarrier != null && wheelCarrier.Behaviour != null && wheelCarrier.Behaviour.GotParry)
            _specialState = wheelCarrier.EntExecution != null && wheelCarrier.EntExecution.enabled ? (byte)2 : (byte)1;
    }

    internal override bool IsWalking => _wheelCarrier != null && _wheelCarrier.Input != null && Mathf.Abs(_wheelCarrier.Input.HorizontalInput) > 0.01f;
    internal override bool SyncHitOnDamage => true;
    internal override byte SpecialState => _specialState;
    internal bool IsApplyingRemoteSpecialState => _applyingRemoteSpecialState;

    /// <summary>
    /// True while another player owns the parry/execution we are showing. The mirror is
    /// presentation only: it says nothing about whether *this* client's Penitent
    /// performed the native interaction.
    /// </summary>
    internal bool IsMirroringRemoteSpecialState => _mirroringRemoteState && _specialState != 0;

    /// <summary>
    /// A death raised while this client is showing an execution is the riposte finisher
    /// (Execution.OnUse -> KillEntity), which never opens a damage context.
    /// </summary>
    internal override bool OwnsUnattributedDeath => _specialState == 2;

    /// <summary>
    /// The "Parry" animator state recovers to Alive() on a 1s timer. On a mirroring
    /// client that timer would erase a state the owner is still playing (and Alive()
    /// also rewrites CurrentLife), so it is suppressed for mirrored states.
    /// </summary>
    internal bool SuppressNativeParryRecovery => IsMirroringRemoteSpecialState && !_applyingRemoteSpecialState;

    /// <summary>
    /// Logs the parry-recovery suppression once per mirrored parry episode instead of on
    /// every animator update.
    /// </summary>
    internal void LogParryRecoverySuppressedOnce()
    {
        if (_parryRecoverySuppressionLogged) return;
        _parryRecoverySuppressionLogged = true;
        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Suppressed native parry recovery on mirrored state enemyInstance={_wheelCarrier?.GetInstanceID()}, state={_specialState}, owner={DescribeMirrorOwner()}, time={Time.realtimeSinceStartup:0.000}");
    }

    internal override void OnSyncUpdate()
    {
        // The clone outlives the mirror (the owner's death lands at the start of the execution
        // and clears the state), so it has to be ticked before the mirror early-return.
        TickExecutionPresentation();
        TickVisualWatch();
        if (!IsMirroringRemoteSpecialState || Time.unscaledTime < _nextMirrorCheck) return;
        _nextMirrorCheck = Time.unscaledTime + MirrorCheckInterval;

        // The only mirror that can be orphaned without a network event is a host mirror:
        // the follower that reported the state may leave while the host still shows it.
        // Scene-authority mirrors are recovered through OnAuthorityContextChanged.
        if (!_mirrorOwnerIsAuthority)
        {
            if (IsPlayerStillConnected(_mirrorOwner))
            {
                // Presence is only policed after the owner was actually seen connected,
                // so a slow connection notice cannot drop a just-created mirror.
                _mirrorOwnerPresenceKnown = true;
            }
            else if (_mirrorOwnerPresenceKnown)
            {
                SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Mirror owner left the session; restoring presentation enemyInstance={_wheelCarrier?.GetInstanceID()}, owner={_mirrorOwner}, state={_specialState}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
                RestoreAlivePresentation();
                ClearSpecialState("mirror owner left session");
                return;
            }
        }

        // Diagnostic only: never restores presentation, because elapsed time does not
        // prove the owner abandoned the interaction.
        if (Time.unscaledTime - _lastMirrorConfirmedAt < MirrorUnconfirmedWarning || Time.unscaledTime < _nextMirrorWarning)
            return;
        _nextMirrorWarning = Time.unscaledTime + MirrorUnconfirmedWarning;
        SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Mirror still unconfirmed after {(Time.unscaledTime - _lastMirrorConfirmedAt):0.0}s enemyInstance={_wheelCarrier?.GetInstanceID()}, state={_specialState}, owner={DescribeMirrorOwner()}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    /// <summary>Scene authority changed: a follower mirror can no longer be trusted.</summary>
    internal override void OnAuthorityContextChanged(string reason)
    {
        if (!_mirroringRemoteState) return;
        SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Dropping mirror after authority change enemyInstance={_wheelCarrier?.GetInstanceID()}, state={_specialState}, owner={DescribeMirrorOwner()}, reason={reason}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        RestoreAlivePresentation();
        ClearSpecialState("authority context changed: " + reason);
    }

    internal void NotifyNativeSpecialState(byte state, string source)
    {
        if (_applyingRemoteSpecialState || _wheelCarrier == null || state > 2) return;
        if (_specialState == state && !_mirroringRemoteState) return;

        // A native transition means this client's Penitent just parried or executed the
        // enemy: ownership moves here, so drop any mirror before publishing the state.
        if (_mirroringRemoteState)
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Local transition took ownership from mirror enemyInstance={_wheelCarrier.GetInstanceID()}, mirroredState={_specialState}, mirroredOwner={DescribeMirrorOwner()}, nativeState={state}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");

        byte previousState = _specialState;
        _specialState = state;
        _mirroringRemoteState = false;
        _mirrorOwner = string.Empty;
        _mirrorOwnerIsAuthority = false;
        _mirrorOwnerPresenceKnown = false;
        // Only the state we just left can be echoed back by an in-flight snapshot; any
        // other transition belongs to another player and must be applied.
        _lastLeftLocalState = state == 0 ? previousState : (byte)0;
        _parryRecoverySuppressionLogged = false;
        _specialStateChangedAt = Time.unscaledTime;
        _lastMirrorConfirmedAt = Time.unscaledTime;
        EnemyDeathSyncManager.ReportWheelCarrierStateChange(_wheelCarrier, state, source);
    }

    internal void ApplyRemoteSpecialState(byte state, byte orientation, string ownerName, bool ownerIsAuthority)
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null || state > 2) return;
        _wheelCarrier.SetOrientation((EntityOrientation)orientation);

        if (_specialState == state)
        {
            // The owner is still confirming this state; refresh the watchdog only.
            if (_mirroringRemoteState && state != 0)
            {
                _lastMirrorConfirmedAt = Time.unscaledTime;
                if (!string.IsNullOrEmpty(ownerName))
                {
                    _mirrorOwner = ownerName;
                    // A report can only come from a player the server has already
                    // announced, so receiving one proves the owner is present.
                    if (!ownerIsAuthority) _mirrorOwnerPresenceKnown = true;
                }
            }
            return;
        }

        // This client performed the transition natively and the game is still driving it.
        // The authority's copy of that state is an echo of what we reported, so it must
        // never downgrade a live local parry/execution (that is what used to cancel a
        // riposte in progress).
        if (_specialState != 0 && !_mirroringRemoteState)
        {
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Ignored authority state on locally owned transition enemyInstance={_wheelCarrier.GetInstanceID()}, localState={_specialState}, requested={state}, owner={ownerName}, age={(Time.unscaledTime - _specialStateChangedAt):0.000}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        // Execution is terminal for the owner: a late parry update must not regress it.
        if (_mirroringRemoteState && _specialState == 2 && state == 1)
        {
            // This used to drop the report silently, which made an owner-side regression to the
            // parry state indistinguishable from "the owner never said anything".
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Ignored parry report while mirroring executionable enemyInstance={_wheelCarrier.GetInstanceID()}, heldFor={(Time.unscaledTime - _specialStateChangedAt):0.000}, owner={DescribeMirrorOwner()}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        // A local transition ended moments ago; ignore the echo of the state we just
        // left (a snapshot that crossed our report). Anything else is a genuine
        // transition, possibly owned by another player, and must be applied even if it
        // arrives in the same frame as a local change.
        bool echoOfLeftState = _specialState == 0 && _lastLeftLocalState != 0 && state == _lastLeftLocalState;
        if (!_mirroringRemoteState && echoOfLeftState && Time.unscaledTime - _specialStateChangedAt < LocalOwnershipGrace)
        {
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Ignored echo of locally ended state enemyInstance={_wheelCarrier.GetInstanceID()}, leftState={_lastLeftLocalState}, requested={state}, age={(Time.unscaledTime - _specialStateChangedAt):0.000}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        byte before = _specialState;
        float previousHeldFor = Time.unscaledTime - _specialStateChangedAt;
        _applyingRemoteSpecialState = true;
        try
        {
            _specialState = state;
            _mirroringRemoteState = state != 0;
            _mirrorOwner = ownerName ?? string.Empty;
            _mirrorOwnerIsAuthority = ownerIsAuthority;
            // A routed report can only arrive for a player the server already announced,
            // so the report itself proves the owner was present. Starting presence
            // tracking from that fact (rather than from the first poll observing them)
            // is what lets this mirror still be cleared when the owner disconnects
            // before that poll ever sees them connected.
            _mirrorOwnerPresenceKnown = !ownerIsAuthority && !string.IsNullOrEmpty(_mirrorOwner);
            _lastLeftLocalState = 0;
            _parryRecoverySuppressionLogged = false;
            _specialStateChangedAt = Time.unscaledTime;
            _lastMirrorConfirmedAt = Time.unscaledTime;
            ApplyMirrorPresentation();
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Mirror apply (presentation only) enemyInstance={_wheelCarrier.GetInstanceID()}, before={before}, previousHeldFor={previousHeldFor:0.000}, state={state}, orientation={orientation}, owner={DescribeMirrorOwner()}, ownerIsAuthority={ownerIsAuthority}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        }
        finally
        {
            _applyingRemoteSpecialState = false;
        }
    }

    /// <summary>
    /// Vanilla hides the player who runs a finisher with Interactable.ShowPlayer(false), which only
    /// touches the local Penitent. On an observer the executor is a remote avatar, so it has to be
    /// hidden here for the finisher and restored on every exit path.
    /// </summary>
    private void HideExecutionOwnerAvatar(string ownerName)
    {
        if (ownerName == Main.Multiplayer.PlayerName) return;
        if (_hiddenExecutionAvatar != null) return;

        OtherPlayerScript avatar = Main.Multiplayer.OtherPlayerManager.FindActivePlayer(ownerName);
        if (avatar == null)
        {
            SharedDiagnosticLogger.Info($"[PenitentVisibilityDiag] Executor avatar not found owner={ownerName}, enemyInstance={_wheelCarrier?.GetInstanceID()}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        bool wasEnabled = avatar.PresentationVisible;
        avatar.SetPresentationVisible(false);
        _hiddenExecutionAvatar = ownerName;
        SharedDiagnosticLogger.Info($"[PenitentVisibilityDiag] Hiding executor avatar owner={ownerName}, wasEnabled={wasEnabled}, enemyInstance={_wheelCarrier?.GetInstanceID()}, time={Time.realtimeSinceStartup:0.000}");
    }

    private void RestoreExecutionOwnerAvatar(string reason)
    {
        if (_hiddenExecutionAvatar == null) return;

        string ownerName = _hiddenExecutionAvatar;
        _hiddenExecutionAvatar = null;

        OtherPlayerScript avatar = Main.Multiplayer.OtherPlayerManager.FindActivePlayer(ownerName);
        if (avatar == null)
        {
            // Scene change or disconnect: the avatar is gone, nothing to restore.
            SharedDiagnosticLogger.Info($"[PenitentVisibilityDiag] Executor avatar already gone owner={ownerName}, reason={reason}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        avatar.SetPresentationVisible(true);
        SharedDiagnosticLogger.Info($"[PenitentVisibilityDiag] Restored executor avatar owner={ownerName}, reason={reason}, time={Time.realtimeSinceStartup:0.000}");
    }

    /// <summary>
    /// Replays the owner's finisher for a player who is only watching it.
    ///
    /// The prefab owns the entire presentation: the executor duplicate that plays the clip, the
    /// prompt art and the animator that drives both. It also owns a live interaction against
    /// whichever Penitent is local, so every part of it that reaches for the local player is
    /// switched off before it can run:
    ///
    ///   Execution              - OnStart/OnUpdate/OnUse are what consume the interaction, bind
    ///                            the local Penitent and call ExecutedEntity.KillEntity(). It is
    ///                            disabled before Start, so none of that runs.
    ///   ExecutionAnimationEvents - local camera zoom, level slow motion, rumble and the
    ///                            execution sound. Disabled, and its `_execution` field (set in
    ///                            Start) stays null so the guarded entry points no-op.
    ///   INTERACTION_START/END  - animation events delivered to the disabled Interactable. They
    ///                            would block the observer's input and pay them fervour; the
    ///                            Interactable.OnEventLaunched prefix drops them for this clone.
    ///
    /// Nothing here commits a death: the authoritative death still arrives from the owner and is
    /// what removes the enemy.
    /// </summary>
    internal void ApplyRemoteExecutionStart(string ownerName, byte side)
    {
        if (_wheelCarrier == null || _wheelCarrier.EntExecution == null)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Execution presentation skipped: enemy not ready enemyInstance={_wheelCarrier?.GetInstanceID()}, owner={ownerName}");
            return;
        }

        // A mirrored executionable state already created the prefab; the finisher reuses it.
        if (_executionPresentation == null && !CreateExecutionPresentation(ownerName, side)) return;

        if (_executionFinisherStarted)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Info($"[WheelCarrierExecutionDiag] Execution already presented enemyInstance={_wheelCarrier.GetInstanceID()}, owner={ownerName}, aliveFor={(Time.unscaledTime - _executionPresentationStartedAt):0.000}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        try
        {
            Execution interactable = _executionPresentation.GetComponentInChildren<Execution>(true);
            Animator prompt = ReadInteractableAnimator(interactable, "interactableAnimator");
            Animator interactor = _executionInteractorAnimator ?? ReadInteractableAnimator(interactable, "interactorAnimator");

            // Execution.OnUse hides the prompt art right before it starts the clip; until then that
            // art *is* the executionable presentation.
            if (prompt != null && prompt.gameObject.activeSelf) prompt.gameObject.SetActive(false);

            _executionFinisherStarted = true;
            _executionPresentationStartedAt = Time.unscaledTime;
            HideExecutionOwnerAvatar(ownerName);
            _executionClipStarted = false;
            _executionPresentationAnimator = TriggerExecutionClip(_executionPresentation, interactor);

            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Info($"[WheelCarrierExecutionDiag] Execution presentation started enemyInstance={_wheelCarrier.GetInstanceID()}, owner={ownerName}, side={side}, animator={(_executionPresentationAnimator != null ? _executionPresentationAnimator.name : "<none>")}, interactor={(interactor != null ? interactor.name : "<none>")}, prompt={(prompt != null ? prompt.name : "<none>")}, reused={(Time.unscaledTime - _executionPresentationCreatedAt):0.000}s, time={Time.realtimeSinceStartup:0.000}");
        }
        catch (Exception ex)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Execution presentation failed enemyInstance={_wheelCarrier?.GetInstanceID()}, owner={ownerName}, error={ex.GetType().Name}: {ex.Message}");
            DestroyExecutionPresentation("presentation failed");
        }
    }

    /// <summary>
    /// Instantiates the owner's Execution prefab as a pure visual: no interaction, no sensors, no
    /// colliders, no animation events, no input notifier. Used both for the mirrored executionable
    /// state and, if the state report is missed, for the finisher itself.
    /// </summary>
    private bool CreateExecutionPresentation(string ownerName, byte side)
    {
        if (_wheelCarrier == null || _wheelCarrier.EntExecution == null) return false;

        GameObject prefab = _wheelCarrier.EntExecution.ExecutionPrefab;
        if (prefab == null)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Execution presentation skipped: EntExecution has no prefab enemyInstance={_wheelCarrier.GetInstanceID()}, owner={ownerName}");
            return false;
        }

        try
        {
            _executionPresentation = (GameObject)Instantiate(prefab, _wheelCarrier.transform.position, Quaternion.identity);
            _executionPresentationCreatedAt = Time.unscaledTime;
            _executionFinisherStarted = false;
            _executionClipStarted = false;

            Execution interactable = _executionPresentation.GetComponentInChildren<Execution>(true);
            if (interactable != null)
            {
                // StuntBehaviour.OnStateEnter dereferences ExecutedEntity on the clone's animator,
                // so it cannot be left null even though the interaction itself never runs.
                interactable.ExecutedEntity = _wheelCarrier;
                interactable.ObjectOrientation = (EntityOrientation)side;
                interactable.enabled = false;
            }

            // InputNotifier is independent of Execution.enabled: its Start instantiates a button
            // icon and it reacts to the global input lock events, so a clone that carries one keeps
            // a prompt alive that nothing will ever answer.
            foreach (Tools.UI.InputNotifier notifier in _executionPresentation.GetComponentsInChildren<Tools.UI.InputNotifier>(true))
            {
                if (notifier != null) notifier.enabled = false;
            }

            ExecutionAnimationEvents[] effects = _executionPresentation.GetComponentsInChildren<ExecutionAnimationEvents>(true);
            foreach (ExecutionAnimationEvents effect in effects)
            {
                if (effect != null) effect.enabled = false;
            }

            // Pure visuals: no sensor, prompt or damage collider may react to the observer's
            // Penitent, and the clone's own EnemyDamageArea must never receive a hit.
            foreach (Collider2D collider in _executionPresentation.GetComponentsInChildren<Collider2D>(true))
            {
                if (collider != null) collider.enabled = false;
            }

            _executionInteractorAnimator = ReadInteractableAnimator(interactable, "interactorAnimator");

            LogExecutionPrefabShapeOnce(_executionPresentation, interactable);

            // The owner's Execution() stops drawing the enemy and hands the visuals to the prefab.
            if (_wheelCarrier.SpriteRenderer != null) _wheelCarrier.SpriteRenderer.enabled = false;
            if (_wheelCarrier.Attack != null) _wheelCarrier.Attack.gameObject.SetActive(false);

            Animator prompt = ReadInteractableAnimator(interactable, "interactableAnimator");
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Info($"[WheelCarrierExecutionDiag] Executionable presentation created enemyInstance={_wheelCarrier.GetInstanceID()}, owner={ownerName}, side={side}, prompt={(prompt != null ? prompt.name : "<none>")}, interactor={(_executionInteractorAnimator != null ? _executionInteractorAnimator.name : "<none>")}, effects={effects.Length}, time={Time.realtimeSinceStartup:0.000}");
            return true;
        }
        catch (Exception ex)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Execution presentation failed enemyInstance={_wheelCarrier?.GetInstanceID()}, owner={ownerName}, error={ex.GetType().Name}: {ex.Message}");
            DestroyExecutionPresentation("presentation failed");
            return false;
        }
    }

    /// <summary>
    /// Plays the finisher clip exactly like Execution.OnUse does: one animator, the serialized
    /// Interactable.interactorAnimator.
    ///
    /// The prefab carries two interactor hierarchies, and triggering every animator that declares
    /// USED made both of them draw the victim - the same YellowCartwheel_execution_N frames twice,
    /// in the same sorting slot, which is what "effects=2" was counting.
    /// </summary>
    private static Animator TriggerExecutionClip(GameObject clone, Animator interactor)
    {
        if (interactor != null)
        {
            interactor.SetTrigger("USED");
            return interactor;
        }

        // The field is protected on the vanilla base type; if reflection ever misses it, fall back
        // to the old behaviour rather than presenting nothing.
        if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn("[WheelCarrierExecutionDiag] interactorAnimator not readable; falling back to every animator with USED");
        Animator started = null;
        Animator[] animators = clone.GetComponentsInChildren<Animator>(true);
        foreach (Animator animator in animators)
        {
            if (animator == null || !HasTriggerParameter(animator, "USED")) continue;
            animator.SetTrigger("USED");
            if (started == null) started = animator;
        }
        return started;
    }

    /// <summary>
    /// Reads one of Interactable's serialized animator fields (interactorAnimator /
    /// interactableAnimator). They are protected on the vanilla base type, so this is the only way
    /// to reach the animator the game itself would have used.
    /// </summary>
    private static Animator ReadInteractableAnimator(Execution interactable, string fieldName)
    {
        if (interactable == null) return null;
        try
        {
            System.Reflection.FieldInfo field = HarmonyLib.AccessTools.Field(typeof(Tools.Level.Interactable), fieldName);
            return field != null ? field.GetValue(interactable) as Animator : null;
        }
        catch (Exception ex)
        {
            if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Warn($"[WheelCarrierExecutionDiag] Could not read {fieldName}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool HasTriggerParameter(Animator animator, string name)
    {
        AnimatorControllerParameter[] parameters = animator.parameters;
        if (parameters == null) return false;
        foreach (AnimatorControllerParameter parameter in parameters)
        {
            if (parameter.type == AnimatorControllerParameterType.Trigger && string.Equals(parameter.name, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// One-shot description of what the execution prefab actually contains. The prefab is an
    /// asset, so its shape cannot be read from the decompiled assemblies; this is what tells us
    /// whether the clone draws the victim, where the animator events live and how long the clip
    /// is, without needing an editor.
    /// </summary>
    private static void LogExecutionPrefabShapeOnce(GameObject clone, Execution interactable)
    {
        if (!EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics || _executionPrefabShapeLogged || clone == null) return;
        _executionPrefabShapeLogged = true;

        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        builder.Append($"[WheelCarrierExecutionDiag] Execution prefab shape root={clone.name}, interactable={(interactable != null)}");
        Transform[] transforms = clone.GetComponentsInChildren<Transform>(true);
        int logged = 0;
        foreach (Transform child in transforms)
        {
            if (logged >= 24) { builder.Append(" | ..."); break; }
            logged++;
            Component[] components = child.GetComponents<Component>();
            builder.Append($" | {child.name}[");
            for (int i = 0; i < components.Length; i++)
            {
                if (i > 0) builder.Append(",");
                builder.Append(components[i] == null ? "null" : components[i].GetType().Name);
            }
            builder.Append("]");
        }
        SharedDiagnosticLogger.Info(builder.ToString());
    }

    private void TickExecutionPresentation()
    {
        if (_executionPresentation == null) return;

        if (!_executionFinisherStarted)
        {
            // The owner's window ends with a state report; this only stops a clone from leaking if
            // that report never arrives.
            if (Time.unscaledTime - _executionPresentationCreatedAt >= ExecutionablePresentationMaxLifetime)
                TearDownExecutionablePresentation($"executionable lifetime cap {ExecutionablePresentationMaxLifetime:0.0}s");
            return;
        }

        float age = Time.unscaledTime - _executionPresentationStartedAt;
        if (age < ExecutionPresentationMinPlayTime) return;

        if (age >= ExecutionPresentationMaxLifetime)
        {
            DestroyExecutionPresentation($"lifetime cap {ExecutionPresentationMaxLifetime:0.0}s");
            return;
        }

        if (_executionPresentationAnimator == null) return;

        // The clip ends by leaving its state (clip == null) rather than by reporting a finished
        // normalizedTime, which is why the clone used to linger ~1.7s past its own animation. End it
        // the moment the state is gone - but only after the state was actually entered, so a trigger
        // that has not been consumed yet cannot retire the clone on the first frame.
        string clip = WheelCarrierVisualProbe.DescribeClip(_executionPresentationAnimator);
        if (!_executionClipStarted)
        {
            if (clip != "null") _executionClipStarted = true;
        }
        else if (clip == "null")
        {
            DestroyExecutionPresentation("execution clip ended");
            return;
        }

        AnimatorStateInfo info = _executionPresentationAnimator.GetCurrentAnimatorStateInfo(0);
        if (info.normalizedTime >= 1f) DestroyExecutionPresentation("execution clip finished");
    }

    private void DestroyExecutionPresentation(string reason)
    {
        if (_executionPresentation == null) return;

        // The executionable phase never set a finisher start; measure from creation instead.
        float age = Time.unscaledTime - (_executionFinisherStarted ? _executionPresentationStartedAt : _executionPresentationCreatedAt);
        if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Info($"[WheelCarrierExecutionDiag] Execution presentation ended enemyInstance={_wheelCarrier?.GetInstanceID()}, reason={reason}, aliveFor={age:0.000}, time={Time.realtimeSinceStartup:0.000}");
        if (EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics) SharedDiagnosticLogger.Info($"[WheelCarrierVisualDiag] Execution presentation final renderers enemyInstance={_wheelCarrier?.GetInstanceID()}, reason={reason}, clone={WheelCarrierVisualProbe.Describe(_executionPresentation.transform)}, time={Time.realtimeSinceStartup:0.000}");
        Destroy(_executionPresentation);
        _executionPresentation = null;
        _executionPresentationAnimator = null;
        _executionInteractorAnimator = null;
        _executionClipStarted = false;
        _executionFinisherStarted = false;
        RestoreExecutionOwnerAvatar(reason);

        // Only a survivor needs its sprite back; after a completed execution the enemy is dead
        // and has already been hidden by ApplyDeath.
        if (_wheelCarrier != null && _wheelCarrier.SpriteRenderer != null && !_wheelCarrier.Status.Dead)
            _wheelCarrier.SpriteRenderer.enabled = true;
    }

    private void OnDestroy() => DestroyExecutionPresentation("sync handler destroyed");

    /// <summary>
    /// Diagnostic only: records what every renderer in the enemy and the clone hierarchy is
    /// doing at the moment of death, then logs each later transition for a bounded window.
    /// Nothing here mutates presentation state - visibility fixes come after the log names
    /// the object.
    /// </summary>
    private void StartVisualWatch(string reason)
    {
        if (!EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics)
        {
            _visualEnemyState = null;
            _visualCloneState = null;
            _visualWatchClone = null;
            return;
        }

        _visualWatchUntil = Time.realtimeSinceStartup + VisualWatchSeconds;
        _nextVisualSample = 0f;
        _visualWatchClone = _executionPresentation != null ? _executionPresentation.transform : null;
        _visualEnemyState = WheelCarrierVisualProbe.Capture(_wheelCarrier.transform);
        _visualCloneState = WheelCarrierVisualProbe.Capture(_visualWatchClone);

        SharedDiagnosticLogger.Info($"[WheelCarrierVisualDiag] Post-death renderers reason={reason}, enemyInstance={_wheelCarrier.GetInstanceID()}, enemyActive={_wheelCarrier.gameObject.activeInHierarchy}, attackActive={(_wheelCarrier.Attack != null ? _wheelCarrier.Attack.gameObject.activeInHierarchy.ToString() : "none")}, enemy={WheelCarrierVisualProbe.Describe(_wheelCarrier.transform)}, cloneActive={(_visualWatchClone != null ? _visualWatchClone.gameObject.activeInHierarchy.ToString() : "none")}, clone={WheelCarrierVisualProbe.Describe(_visualWatchClone)}, window={VisualWatchSeconds:0.0}s, time={Time.realtimeSinceStartup:0.000}");
    }

    private void TickVisualWatch()
    {
        if (!EnemyDiagnostics.EnableWheelCarrierExecutionDiagnostics)
        {
            _visualEnemyState = null;
            _visualCloneState = null;
            _visualWatchClone = null;
            return;
        }

        if (_visualEnemyState == null && _visualCloneState == null) return;

        if (Time.realtimeSinceStartup >= _visualWatchUntil)
        {
            SharedDiagnosticLogger.Info($"[WheelCarrierVisualDiag] Post-death renderer watch ended enemyInstance={_wheelCarrier?.GetInstanceID()}, time={Time.realtimeSinceStartup:0.000}");
            _visualEnemyState = null;
            _visualCloneState = null;
            _visualWatchClone = null;
            return;
        }

        if (Time.realtimeSinceStartup < _nextVisualSample) return;
        _nextVisualSample = Time.realtimeSinceStartup + VisualWatchSampleInterval;

        if (_wheelCarrier != null)
        {
            System.Collections.Generic.Dictionary<string, string> current = WheelCarrierVisualProbe.Capture(_wheelCarrier.transform);
            string changes = WheelCarrierVisualProbe.Diff(_visualEnemyState, current);
            if (changes != null)
            {
                SharedDiagnosticLogger.Info($"[WheelCarrierVisualDiag] Enemy renderer changed enemyInstance={_wheelCarrier.GetInstanceID()}, changes={changes}, time={Time.realtimeSinceStartup:0.000}");
                _visualEnemyState = current;
            }
        }
        else if (_visualEnemyState != null)
        {
            SharedDiagnosticLogger.Info($"[WheelCarrierVisualDiag] Enemy object destroyed, time={Time.realtimeSinceStartup:0.000}");
            _visualEnemyState = null;
        }

        if (_visualWatchClone == null)
        {
            if (_visualCloneState != null)
            {
                SharedDiagnosticLogger.Info($"[WheelCarrierVisualDiag] Execution clone gone, time={Time.realtimeSinceStartup:0.000}");
                _visualCloneState = null;
            }
            return;
        }

        System.Collections.Generic.Dictionary<string, string> cloneCurrent = WheelCarrierVisualProbe.Capture(_visualWatchClone);
        string cloneChanges = WheelCarrierVisualProbe.Diff(_visualCloneState, cloneCurrent);
        if (cloneChanges != null)
        {
            SharedDiagnosticLogger.Info($"[WheelCarrierVisualDiag] Execution clone renderer changed enemyInstance={_wheelCarrier?.GetInstanceID()}, changes={cloneChanges}, time={Time.realtimeSinceStartup:0.000}");
            _visualCloneState = cloneCurrent;
        }
    }

    /// <summary>Re-asserts the mirrored presentation after a local hit cleared GotParry.</summary>
    internal void ReassertMirroredSpecialState()
    {
        if (!IsMirroringRemoteSpecialState || _wheelCarrier == null || _wheelCarrier.Behaviour == null) return;
        ApplyMirrorPresentation();
        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Re-asserted mirror after local damage enemyInstance={_wheelCarrier.GetInstanceID()}, state={_specialState}, owner={DescribeMirrorOwner()}, time={Time.realtimeSinceStartup:0.000}");
    }

    internal override void ApplySpecialState(byte state, string authorityName)
    {
        if (_wheelCarrier == null) return;
        ApplyRemoteSpecialState(state, (byte)_wheelCarrier.Status.Orientation, authorityName, true);
    }

    internal override void SetAuthority(bool authority)
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null) return;

        // Becoming authoritative (including losing the connection) means nobody else is
        // driving this enemy any more: a mirror cannot be maintained.
        if (authority && _mirroringRemoteState)
        {
            SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Dropping mirror on authority transition enemyInstance={_wheelCarrier.GetInstanceID()}, state={_specialState}, owner={DescribeMirrorOwner()}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
            RestoreAlivePresentation();
            ClearSpecialState("became authority");
        }

        _wheelCarrier.Behaviour.EnableBehaviourOnLoad = authority;
        if (!authority)
        {
            _wheelCarrier.Behaviour.StopBehaviour();
            _wheelCarrier.Behaviour.StopMovement();
            if (Core.Logic.Penitent != null)
                _wheelCarrier.Target = Core.Logic.Penitent.gameObject;
        }

        // Only the authority runs the WheelCarrier AI. Followers stay inert and are
        // posed from snapshots; a mirrored parry/execution keeps them frozen through
        // GotParry, which WheelCarrierBehaviour.Update already checks.
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
        if (_specialState != 0)
        {
            SharedDiagnosticLogger.Info($"[EnemyAttackSyncDiag] Remote attack ignored: enemy is parried/executed enemyInstance={_wheelCarrier.GetInstanceID()}, state={_specialState}, object={_wheelCarrier.gameObject.name}");
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
            // The host applies a follower's lethal report through this path, so it needs the
            // same cleanup as a death that arrives as a packet.
            ApplyDeathPresentationCleanup("authoritative death");
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
        ApplyDeathPresentationCleanup("remote death");
    }

    /// <summary>
    /// The single death cleanup for every path that applies a remote death: a received death
    /// packet (<see cref="ApplyDeath"/>) and a lethal damage report applied by the scene host
    /// (<see cref="ApplyAuthoritativeDamageReaction"/>).
    ///
    /// It only ever removes presentation state this client added on top of the authority's
    /// death. It never calls <see cref="WheelCarrierBehaviour.Alive"/>: no health is restored,
    /// the renderer is not resurrected into a living pose, and the enemy is not reactivated.
    /// </summary>
    private void ApplyDeathPresentationCleanup(string reason)
    {
        // Only a triggered finisher makes a death "completed execution". A clone that is still just
        // the mirrored executionable presentation belongs to a window that ended, not to a riposte.
        bool completedExecution = _executionPresentation != null && _executionFinisherStarted;
        bool wasParried = _wheelCarrier.Behaviour != null && _wheelCarrier.Behaviour.GotParry;
        float lifeBefore = _wheelCarrier.Stats != null && _wheelCarrier.Stats.Life != null ? _wheelCarrier.Stats.Life.Current : -1f;

        // WheelCarrierAnimatorInyector.OnUpdate writes IS_PARRIED = Behaviour.GotParry on every
        // frame, so leaving it set would pin the animator in the parry/executionable pose and the
        // DEATH trigger could never take it. Clearing it first is what lets the death presentation
        // play at all, and it is also what stops the mirror from claiming a dead enemy is parried.
        if (_wheelCarrier.Behaviour != null) _wheelCarrier.Behaviour.GotParry = false;

        // Undo the state-2 parity applied by ApplyExecutionParity(). Both are presentation or
        // collision flags; neither touches Status.Dead, health or the spawn point.
        _wheelCarrier.IsStunt = false;
        int enemyLayer = LayerMask.NameToLayer("Enemy");
        if (enemyLayer >= 0) _wheelCarrier.gameObject.layer = enemyLayer;

        if (completedExecution)
        {
            // The owner runs no death clip for a completed execution: it hides the enemy from
            // Execution() onwards and destroys it when the finisher ends, because KillEntity
            // happens before the clip starts. The clone is the whole presentation, so the enemy
            // stays hidden and only the clone plays out.
            if (_wheelCarrier.SpriteRenderer != null) _wheelCarrier.SpriteRenderer.enabled = false;
            if (_wheelCarrier.Attack != null) _wheelCarrier.Attack.gameObject.SetActive(false);
        }
        else
        {
            // An executionable clone that never became a finisher is stale here.
            if (_executionPresentation != null) DestroyExecutionPresentation("death without a finisher");

            // Ordinary or interrupted death: the command is presentation, so the sprite has to be
            // back on for the death clip to be visible.
            if (_wheelCarrier.SpriteRenderer != null) _wheelCarrier.SpriteRenderer.enabled = true;
            if (_wheelCarrier.Behaviour != null) _wheelCarrier.Behaviour.Death();
            if (_wheelCarrier.Audio != null) _wheelCarrier.Audio.PlayDeath();
        }

        ClearSpecialState(reason);
        StartVisualWatch(reason);
        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Death presentation cleanup enemyInstance={_wheelCarrier.GetInstanceID()}, reason={reason}, completedExecution={completedExecution}, clearedGotParry={wasParried}, layer={(enemyLayer >= 0 ? enemyLayer : -1)}, sprite={(_wheelCarrier.SpriteRenderer != null && _wheelCarrier.SpriteRenderer.enabled)}, dead={_wheelCarrier.Status.Dead}, lifeBefore={lifeBefore:0.###}, lifeAfter={(_wheelCarrier.Stats != null && _wheelCarrier.Stats.Life != null ? _wheelCarrier.Stats.Life.Current : -1f):0.###}, time={Time.realtimeSinceStartup:0.000}");
    }

    /// <summary>
    /// Applies the *consequences* of the owner's parry/execution without running the
    /// native transition methods. Everything here is reversible from
    /// <see cref="RestoreAlivePresentation"/>, and nothing is spawned or bound to the
    /// local Penitent.
    /// </summary>
    private void ApplyMirrorPresentation()
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null) return;

        // A clone belongs to the executionable state. Leaving that state without a finisher means
        // the owner's window ended, so the enemy has to be drawn again.
        if (_specialState != 2) TearDownExecutionablePresentation("state is no longer executionable");

        switch (_specialState)
        {
            case 1:
            case 2:
                // Freeze the enemy exactly like the native parried state does; the owner
                // plays the riposte on their own screen.
                _wheelCarrier.Behaviour.GotParry = true;
                StopMovement();
                if (_wheelCarrier.AnimatorInyector != null) _wheelCarrier.AnimatorInyector.ParryReaction();
                if (_specialState == 2)
                {
                    if (_wheelCarrier.Attack != null) _wheelCarrier.Attack.gameObject.SetActive(false);
                    ApplyExecutionParity();
                    // Native Execution() does not merely stun the enemy: it hides the enemy and
                    // hands the visuals to the Execution prefab, whose Interactable Animation plays
                    // the victim's stun loop - the blinking executionable look. Both states used to
                    // play the parry pose here, which is why a mirrored enemy looked staggered and
                    // never became executionable on the observer.
                    EnsureExecutionablePresentation();
                }
                break;
            default:
                RestoreAlivePresentation();
                break;
        }
    }

    /// <summary>
    /// Creates the owner's Execution prefab as the observer's executionable presentation, if it is
    /// not already there, and hides the enemy behind it.
    /// </summary>
    private void EnsureExecutionablePresentation()
    {
        if (_executionPresentation == null && !CreateExecutionPresentation(DescribeMirrorOwner(), (byte)_wheelCarrier.Status.Orientation))
            return;

        if (_wheelCarrier.SpriteRenderer != null) _wheelCarrier.SpriteRenderer.enabled = false;
        if (_wheelCarrier.Attack != null) _wheelCarrier.Attack.gameObject.SetActive(false);
    }

    /// <summary>
    /// Removes an executionable clone and draws the enemy again. A clone whose finisher already
    /// started is left alone: from that point on the death path owns it.
    /// </summary>
    private void TearDownExecutionablePresentation(string reason)
    {
        if (_executionPresentation == null || _executionFinisherStarted) return;

        DestroyExecutionPresentation(reason);
        if (_wheelCarrier == null) return;
        if (_wheelCarrier.SpriteRenderer != null && !_wheelCarrier.Status.Dead) _wheelCarrier.SpriteRenderer.enabled = true;
        if (_wheelCarrier.Attack != null) _wheelCarrier.Attack.gameObject.SetActive(true);
    }

    /// <summary>
    /// The gameplay half of WheelCarrierBehaviour.Execution() that is not presentation:
    /// EnemyBehaviour.Execution() (WheelCarrierBehaviour.cs calls it through base.Execution())
    /// sets <see cref="Enemy.IsStunt"/>, and Execution() moves the root to the Default layer so
    /// the player's weapon stops colliding with an enemy that is already being finished.
    ///
    /// Mirroring both keeps the observer's attacks from rolling a local
    /// WheelCarrier.Damage -> Enemy.Execution(hit) on an enemy the owner has already taken out
    /// of play, and stops it dealing contact damage or being displaced while it is stunted
    /// (ContactDamage.cs / EntityDisplacement.cs both test IsStunt).
    ///
    /// The flag is set directly rather than through Behaviour.Execution(): that is the native,
    /// virtual transition method, and running it here is exactly what the mirror exists to avoid.
    /// Sprite visibility is deliberately *not* part of this patch; observers keep the enemy
    /// visible because the Execution prefab that replaces it only exists on the owner's client.
    /// </summary>
    private void ApplyExecutionParity()
    {
        _wheelCarrier.IsStunt = true;
        int executionLayer = LayerMask.NameToLayer("Default");
        if (executionLayer >= 0) _wheelCarrier.gameObject.layer = executionLayer;
    }

    /// <summary>
    /// The presentation half of WheelCarrierBehaviour.Alive(), without the native
    /// "CurrentLife = Life.Base / 2" rewrite, which would corrupt synced health.
    /// </summary>
    private void RestoreAlivePresentation()
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null) return;

        // Any clone belongs to the state that is ending; the owner never leaves one behind.
        DestroyExecutionPresentation("mirror restored");

        _wheelCarrier.Behaviour.GotParry = false;
        // Undo the state-2 mirror parity alongside the rest of the restoration. Native
        // Alive() clears IsStunt (EnemyBehaviour.cs) and puts the entity back on Enemy; a
        // parry-only mirror (state 1) never set either, so this is a no-op for it.
        _wheelCarrier.IsStunt = false;
        int enemyLayer = LayerMask.NameToLayer("Enemy");
        if (enemyLayer >= 0) _wheelCarrier.gameObject.layer = enemyLayer;
        if (_wheelCarrier.SpriteRenderer != null) _wheelCarrier.SpriteRenderer.enabled = true;
        if (_wheelCarrier.Attack != null) _wheelCarrier.Attack.gameObject.SetActive(true);
        if (_wheelCarrier.Animator != null) _wheelCarrier.Animator.Play("Idle");
        if (_wheelCarrier.EntExecution != null) _wheelCarrier.EntExecution.enabled = false;

        if (_wheelCarrier.Behaviour.enabled) StopMovement();
    }

    private void StopMovement()
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null) return;
        _wheelCarrier.Behaviour.StopMovement();
    }

    private void ClearSpecialState(string reason)
    {
        if (_specialState == 0 && !_mirroringRemoteState) return;

        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Clearing special state enemyInstance={_wheelCarrier?.GetInstanceID()}, state={_specialState}, mirrored={_mirroringRemoteState}, owner={DescribeMirrorOwner()}, heldFor={(Time.unscaledTime - _specialStateChangedAt):0.000}, reason={reason}, time={Time.realtimeSinceStartup:0.000}");
        _specialState = 0;
        _mirroringRemoteState = false;
        _mirrorOwner = string.Empty;
        _mirrorOwnerIsAuthority = false;
        _mirrorOwnerPresenceKnown = false;
        _lastLeftLocalState = 0;
        _parryRecoverySuppressionLogged = false;
        _specialStateChangedAt = Time.unscaledTime;
        _lastMirrorConfirmedAt = Time.unscaledTime;
    }

    private string DescribeMirrorOwner()
    {
        if (string.IsNullOrEmpty(_mirrorOwner)) return _mirrorOwnerIsAuthority ? "<scene-authority>" : "<unknown>";
        return _mirrorOwnerIsAuthority ? _mirrorOwner + " (scene authority)" : _mirrorOwner;
    }

    private static bool IsPlayerStillConnected(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return false;
        if (Main.Multiplayer == null || Main.Multiplayer.OtherPlayerManager == null) return false;
        foreach (PlayerStatus player in Main.Multiplayer.OtherPlayerManager.AllConnectedPlayers)
        {
            if (player != null && string.Equals(player.Name, playerName, StringComparison.Ordinal))
                return true;
        }
        return false;
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
        if (handler.SpecialState != 0) return;

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
    private static void Postfix(WheelCarrierBehaviour __instance) => WheelCarrierStateSyncPatch.Notify(__instance, 1, "parry");
}

[HarmonyLib.HarmonyPatch(typeof(WheelCarrierBehaviour), nameof(WheelCarrierBehaviour.Execution))]
internal static class WheelCarrierExecutionStateSyncPatch
{
    [HarmonyLib.HarmonyPostfix]
    private static void Postfix(WheelCarrierBehaviour __instance) => WheelCarrierStateSyncPatch.Notify(__instance, 2, "execution");
}

[HarmonyLib.HarmonyPatch(typeof(WheelCarrierBehaviour), nameof(WheelCarrierBehaviour.Alive))]
internal static class WheelCarrierAliveStateSyncPatch
{
    [HarmonyLib.HarmonyPostfix]
    private static void Postfix(WheelCarrierBehaviour __instance)
    {
        if (__instance == null || __instance.WheelCarrier == null || __instance.WheelCarrier.Status.Dead) return;
        WheelCarrierStateSyncPatch.Notify(__instance, 0, "alive");
    }
}

/// <summary>
/// Last line of defence: WheelCarrierBehaviour.Alive() is never allowed to run on a dead enemy.
///
/// Alive() is the one method that undoes a death in this class - it re-enables the renderer,
/// restores the Enemy layer, re-enables the attack object, plays "Idle" and rewrites
/// CurrentLife to Stats.Life.Base / 2. Every native caller of it (the 1s ParryBehaviour timer,
/// the execution prefab's StuntBehaviour expiry, anything else) is wrong once Status.Dead is set,
/// so the guard lives on the method rather than on each caller.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(WheelCarrierBehaviour), nameof(WheelCarrierBehaviour.Alive))]
internal static class WheelCarrierDeadAliveGuardPatch
{
    private static readonly System.Collections.Generic.HashSet<int> logged = new System.Collections.Generic.HashSet<int>();

    [HarmonyLib.HarmonyPrefix]
    private static bool Prefix(WheelCarrierBehaviour __instance)
    {
        if (__instance == null || __instance.WheelCarrier == null) return true;
        WheelCarrier wheelCarrier = __instance.WheelCarrier;
        if (wheelCarrier.Status == null || !wheelCarrier.Status.Dead) return true;

        int instanceId = wheelCarrier.GetInstanceID();
        if (logged.Count > 64) logged.Clear();
        if (logged.Add(instanceId))
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Refused Alive() on a dead enemy enemyInstance={instanceId}, life={(wheelCarrier.Stats != null && wheelCarrier.Stats.Life != null ? wheelCarrier.Stats.Life.Current : -1f):0.###}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        return false;
    }
}

/// <summary>
/// The vanilla "Parry" animator state recovers the enemy on its own after ~1s
/// (ParryBehaviour.OnStateUpdate -> EnemyBehaviour.Alive). That is correct for the
/// player who parried, but on a client that is only mirroring the parry it silently
/// cancels a state the owner is still playing and rewrites the shared health. Block
/// the timer while the state is mirrored; the owner still ends the state normally and
/// the authority's "state ended" update is what restores this client.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(ParryBehaviour), nameof(ParryBehaviour.OnStateUpdate))]
internal static class WheelCarrierRemoteParryRecoveryPatch
{
    [HarmonyLib.HarmonyPrefix]
    private static bool Prefix(Animator animator)
    {
        if (animator == null) return true;
        WheelCarrier wheelCarrier = animator.GetComponentInParent<WheelCarrier>();
        if (wheelCarrier == null) return true;

        // Death cleanup drops the mirror, so the suppression below stops applying at exactly the
        // moment the enemy dies. Without this check the 1s parry timer would fire Alive() on a
        // corpse: the renderer comes back, the parry state is dropped and CurrentLife is rewritten
        // to half of base life. Dead enemies never run native parry recovery, mirrored or not.
        if (IsDead(wheelCarrier))
        {
            LogDeadParryRecoverySuppressedOnce(wheelCarrier);
            return false;
        }

        WheelCarrierEnemySyncHandler handler = wheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null || !handler.SuppressNativeParryRecovery) return true;

        handler.LogParryRecoverySuppressedOnce();
        return false;
    }

    private static readonly System.Collections.Generic.HashSet<int> deadRecoveryLogged = new System.Collections.Generic.HashSet<int>();

    private static bool IsDead(WheelCarrier wheelCarrier) =>
        wheelCarrier != null && wheelCarrier.Status != null && wheelCarrier.Status.Dead;

    private static void LogDeadParryRecoverySuppressedOnce(WheelCarrier wheelCarrier)
    {
        int instanceId = wheelCarrier.GetInstanceID();
        if (deadRecoveryLogged.Count > 64) deadRecoveryLogged.Clear();
        if (!deadRecoveryLogged.Add(instanceId)) return;
        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Suppressed native parry recovery on a dead enemy enemyInstance={instanceId}, life={(wheelCarrier.Stats != null && wheelCarrier.Stats.Life != null ? wheelCarrier.Stats.Life.Current : -1f):0.###}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }
}

internal static class WheelCarrierStateSyncPatch
{
    internal static void Notify(WheelCarrierBehaviour behaviour, byte state, string source)
    {
        if (behaviour == null || behaviour.WheelCarrier == null) return;
        WheelCarrierEnemySyncHandler handler = behaviour.WheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null)
        {
            EnemySyncManager.Attach(behaviour.WheelCarrier);
            handler = behaviour.WheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        }
        if (handler != null && !handler.IsApplyingRemoteSpecialState)
            handler.NotifyNativeSpecialState(state, source);
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
        if (__instance == null || __instance.Behaviour == null) return;

        WheelCarrierEnemySyncHandler handler = __instance.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null) return;

        // WheelCarrier.Damage clears GotParry on every hit. For a state we are only
        // mirroring, that would strand the enemy half-restored (alive AI, dead pose) and
        // would report a bogus "parry finished" to the authority, so re-assert the mirror.
        if (handler.IsMirroringRemoteSpecialState)
        {
            handler.ReassertMirroredSpecialState();
            return;
        }

        if (__instance.Behaviour.GotParry) return;
        if (handler.SpecialState == 1)
            handler.NotifyNativeSpecialState(0, "hit cleared parry");
    }
}

/// <summary>
/// Announces that the local player consumed an execution interaction.
///
/// Use() is patched rather than Execution.OnUse because OnUse is an iterator: Harmony patches
/// its MoveNext, so a prefix there fires on every yield - hundreds of times for the
/// "while (!Consumed)" loop. Use() runs exactly once per interaction, on the frame the button is
/// pressed, which is also a frame before the owner's KillEntity goes out.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(Interactable), nameof(Interactable.Use))]
internal static class WheelCarrierExecutionStartPatch
{
    [HarmonyLib.HarmonyPrefix]
    private static void Prefix(Interactable __instance)
    {
        if (!(__instance is Execution execution) || execution.ExecutedEntity == null) return;
        if (!(execution.ExecutedEntity is WheelCarrier wheelCarrier)) return;
        EnemyDeathSyncManager.ReportWheelCarrierExecutionStarted(wheelCarrier, execution);
    }
}

/// <summary>
/// The execution prefab raises INTERACTION_START / INTERACTION_END through its animator, which
/// Interactable turns into an input blocker and, at the end, a fervour bonus for the local
/// Penitent. A presentation clone has its Execution component disabled for exactly that reason,
/// so those events must be dropped instead of being applied to the observer.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(Interactable), "OnEventLaunched")]
internal static class ExecutionPresentationEventPatch
{
    [HarmonyLib.HarmonyPrefix]
    private static bool Prefix(Interactable __instance)
    {
        return !(__instance is Execution execution) || execution.enabled;
    }
}

/// <summary>
/// StuntBehaviour is the execution prefab's own expiry: StuntTime after the prompt appears it
/// calls Alive() on the executed enemy and destroys the prefab. On a presentation clone that
/// would resurrect the mirrored enemy and report a bogus "state ended" to the authority, and the
/// clone has its own lifetime, so the expiry is skipped for clones (their Execution is disabled).
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(StuntBehaviour), nameof(StuntBehaviour.OnStateUpdate))]
internal static class ExecutionPresentationStuntPatch
{
    [HarmonyLib.HarmonyPrefix]
    private static bool Prefix(Animator animator)
    {
        if (animator == null) return true;
        Execution execution = animator.GetComponentInParent<Execution>();
        return execution == null || execution.enabled;
    }
}
