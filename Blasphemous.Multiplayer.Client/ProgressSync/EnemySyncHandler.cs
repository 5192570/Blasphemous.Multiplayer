using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Enemy-specific gameplay bridge used by the shared enemy synchronization component.</summary>
public abstract class EnemySyncHandler : MonoBehaviour
{
    internal Enemy Enemy { get; private set; }

    // Individual handlers can tune how long their enemy commits to a selected player.
    internal virtual float TargetCommitmentDuration => 1.25f;

    internal void Attach(Enemy enemy)
    {
        if (enemy == null) return;
        Enemy = enemy;
        OnAttach();
    }

    protected virtual void OnAttach() { }

    // Runs every frame from the shared sync component, before any authority decision.
    // Handlers use it to validate presentation state against the session, never to
    // invent gameplay state.
    internal virtual void OnSyncUpdate() { }

    // Called when the scene authority changes (handoff, epoch bump, reconnection).
    // Presentation that was copied from the previous authority must not survive it.
    internal virtual void OnAuthorityContextChanged(string reason) { }

    internal abstract bool IsWalking { get; }
    internal virtual byte SpecialState => 0;

    // True when a death raised outside a captured hit belongs to this handler's
    // gameplay flow (the WheelCarrier riposte finisher). Everything else keeps the
    // existing hit-report path untouched.
    internal virtual bool OwnsUnattributedDeath => false;

    internal virtual void ApplySpecialState(byte state, string authorityName) { }
    internal abstract bool SyncHitOnDamage { get; }
    internal abstract void SetAuthority(bool authority);
    internal abstract void SetTarget(GameObject target);
    internal abstract void ApplySnapshot(byte orientation, byte state, bool walking);
    internal abstract void ApplyRemoteHit(byte orientation);
    internal abstract void ApplyAuthoritativeDamageReaction(bool dead, byte orientation);
    internal abstract void ApplyDeath();
}
