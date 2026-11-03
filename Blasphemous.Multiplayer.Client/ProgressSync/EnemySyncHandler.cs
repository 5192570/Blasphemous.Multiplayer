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

    // Runs every frame from the shared sync component, before any authority decision,
    // so handlers can expire local presentation state that has no native completion path.
    internal virtual void OnSyncUpdate() { }

    internal abstract bool IsWalking { get; }
    internal virtual byte SpecialState => 0;
    internal virtual void ApplySpecialState(byte state) { }
    internal abstract bool SyncHitOnDamage { get; }
    internal abstract void SetAuthority(bool authority);
    internal abstract void SetTarget(GameObject target);
    internal abstract void ApplySnapshot(byte orientation, byte state, bool walking);
    internal abstract void ApplyRemoteHit(byte orientation);
    internal abstract void ApplyAuthoritativeDamageReaction(bool dead, byte orientation);
    internal abstract void ApplyDeath();
}
