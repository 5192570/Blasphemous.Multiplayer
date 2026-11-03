using Blasphemous.ModdingAPI;
using Blasphemous.Multiplayer.Client.Network;
using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.AnimationBehaviours.Enemies.Stunt;
using Gameplay.GameControllers.Enemies.WheelCarrier;
using Gameplay.GameControllers.Enemies.WheelCarrier.Animator;
using Gameplay.GameControllers.Enemies.WheelCarrier.IA;
using Gameplay.GameControllers.Combat;
using Gameplay.GameControllers.Entities;
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
/// </summary>
public sealed class WheelCarrierEnemySyncHandler : EnemySyncHandler
{
    // A mirrored state has no native completion path on this client, so it always
    // expires. This only matters when the owner's "state ended" report is lost or the
    // owner's copy is destroyed mid-execution (the riposte kill does exactly that).
    private const float MirrorTimeout = 8f;

    // Native transitions on the owning client win over authority echoes for a moment,
    // so a snapshot that crosses a local Parry -> Execution transition cannot undo it.
    private const float LocalOwnershipGrace = 1f;

    private WheelCarrier _wheelCarrier;
    private byte _specialState;
    private bool _applyingRemoteSpecialState;
    private bool _mirroringRemoteState;
    private float _specialStateChangedAt;

    protected override void OnAttach()
    {
        _wheelCarrier = Enemy as WheelCarrier;
        if (_wheelCarrier != null && _wheelCarrier.Behaviour != null && _wheelCarrier.Behaviour.GotParry)
            _specialState = _wheelCarrier.EntExecution != null && _wheelCarrier.EntExecution.enabled ? (byte)2 : (byte)1;
        _specialStateChangedAt = Time.unscaledTime;
    }

    internal override bool IsWalking => _wheelCarrier != null && _wheelCarrier.Input != null && Mathf.Abs(_wheelCarrier.Input.HorizontalInput) > 0.01f;
    internal override bool SyncHitOnDamage => true;
    internal override byte SpecialState => _specialState;
    internal bool IsApplyingRemoteSpecialState => _applyingRemoteSpecialState;

    /// <summary>True while another player owns the parry/execution we are showing.</summary>
    internal bool IsMirroringRemoteSpecialState => _mirroringRemoteState && _specialState != 0;

    /// <summary>
    /// The "Parry" animator state recovers to Alive() on a 1s timer. On a mirroring
    /// client that timer would erase a state the owner is still playing (and Alive()
    /// also rewrites CurrentLife), so it is suppressed for mirrored states.
    /// </summary>
    internal bool SuppressNativeParryRecovery => IsMirroringRemoteSpecialState && !_applyingRemoteSpecialState;

    /// <summary>Called by the shared sync component every frame.</summary>
    internal override void OnSyncUpdate()
    {
        if (!IsMirroringRemoteSpecialState || _wheelCarrier == null || _wheelCarrier.Behaviour == null) return;
        if (Time.unscaledTime - _specialStateChangedAt < MirrorTimeout) return;

        SharedDiagnosticLogger.Warn($"[WheelCarrierStateDiag] Mirror expired without an owner update enemyInstance={_wheelCarrier.GetInstanceID()}, stuckState={_specialState}, age={(Time.unscaledTime - _specialStateChangedAt):0.000}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        RestoreAlivePresentation();
        ClearSpecialState("mirror timeout");
    }

    internal void NotifyNativeSpecialState(byte state)
    {
        if (_applyingRemoteSpecialState || _wheelCarrier == null || state > 2) return;
        if (_specialState == state && !_mirroringRemoteState) return;

        // A native transition means this client's Penitent just parried or executed the
        // enemy: ownership moves here, so drop any mirror before publishing the state.
        bool tookOverMirror = _mirroringRemoteState;
        if (tookOverMirror)
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Local transition took ownership from mirror enemyInstance={_wheelCarrier.GetInstanceID()}, mirroredState={_specialState}, nativeState={state}, localPlayer={Main.Multiplayer?.PlayerName}");

        _specialState = state;
        _mirroringRemoteState = false;
        _specialStateChangedAt = Time.unscaledTime;
        EnemyDeathSyncManager.ReportWheelCarrierStateChange(_wheelCarrier, state);
    }

    internal void ApplyRemoteSpecialState(byte state, byte orientation)
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null || state > 2) return;
        _wheelCarrier.SetOrientation((EntityOrientation)orientation);

        if (_specialState == state)
        {
            // The owner is still confirming this state in every snapshot: keep it alive.
            if (_mirroringRemoteState && state != 0) _specialStateChangedAt = Time.unscaledTime;
            return;
        }

        // Execution is terminal for the owner. A late parry packet must not regress it.
        if (_specialState == 2 && state == 1) return;

        if (!_mirroringRemoteState && Time.unscaledTime - _specialStateChangedAt < LocalOwnershipGrace)
        {
            SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Ignored stale authority state enemyInstance={_wheelCarrier.GetInstanceID()}, localState={_specialState}, requested={state}, age={(Time.unscaledTime - _specialStateChangedAt):0.000}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        byte before = _specialState;
        _applyingRemoteSpecialState = true;
        try
        {
            _specialState = state;
            _mirroringRemoteState = state != 0;
            _specialStateChangedAt = Time.unscaledTime;
            ApplyMirrorPresentation();
            SharedDiagnosticLogger.Info($"[WheelCarrierStateSync] Mirroring remote state enemyInstance={_wheelCarrier.GetInstanceID()}, before={before}, state={state}, orientation={orientation}, localPlayer={Main.Multiplayer?.PlayerName}");
        }
        finally
        {
            _applyingRemoteSpecialState = false;
        }
    }

    /// <summary>Re-asserts the mirrored presentation after a local hit cleared GotParry.</summary>
    internal void ReassertMirroredSpecialState()
    {
        if (!IsMirroringRemoteSpecialState || _wheelCarrier == null || _wheelCarrier.Behaviour == null) return;
        ApplyMirrorPresentation();
        SharedDiagnosticLogger.Info($"[WheelCarrierStateSync] Re-asserted mirrored state after local damage enemyInstance={_wheelCarrier.GetInstanceID()}, state={_specialState}");
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
            if (_wheelCarrier.Behaviour != null) _wheelCarrier.Behaviour.Death();
            if (_wheelCarrier.Audio != null) _wheelCarrier.Audio.PlayDeath();
            ClearSpecialState("authoritative death");
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
        ClearSpecialState("remote death");
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

        switch (_specialState)
        {
            case 1:
            case 2:
                // Freeze the enemy exactly like the native parried state does; the owner
                // plays the riposte on their own screen.
                _wheelCarrier.Behaviour.GotParry = true;
                StopMovement();
                if (_wheelCarrier.AnimatorInyector != null) _wheelCarrier.AnimatorInyector.ParryReaction();
                // Execution() disables the attack in vanilla. Keep the sprite visible on
                // observers: the Execution prefab that replaces it only exists on the
                // owner's client.
                if (_specialState == 2 && _wheelCarrier.Attack != null) _wheelCarrier.Attack.gameObject.SetActive(false);
                break;
            default:
                RestoreAlivePresentation();
                break;
        }
    }

    /// <summary>
    /// The presentation half of WheelCarrierBehaviour.Alive(), without the native
    /// "CurrentLife = Life.Base / 2" rewrite, which would corrupt synced health.
    /// </summary>
    private void RestoreAlivePresentation()
    {
        if (_wheelCarrier == null || _wheelCarrier.Behaviour == null) return;

        _wheelCarrier.Behaviour.GotParry = false;
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

        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Clearing special state enemyInstance={_wheelCarrier?.GetInstanceID()}, state={_specialState}, mirrored={_mirroringRemoteState}, reason={reason}, time={Time.realtimeSinceStartup:0.000}");
        _specialState = 0;
        _mirroringRemoteState = false;
        _specialStateChangedAt = Time.unscaledTime;
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

        WheelCarrierEnemySyncHandler handler = wheelCarrier.GetComponent<WheelCarrierEnemySyncHandler>();
        if (handler == null || !handler.SuppressNativeParryRecovery) return true;

        SharedDiagnosticLogger.Info($"[WheelCarrierStateDiag] Suppressed native parry recovery on mirrored state enemyInstance={wheelCarrier.GetInstanceID()}, state={handler.SpecialState}, time={UnityEngine.Time.realtimeSinceStartup:0.000}");
        return false;
    }
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
            handler.NotifyNativeSpecialState(0);
    }
}
