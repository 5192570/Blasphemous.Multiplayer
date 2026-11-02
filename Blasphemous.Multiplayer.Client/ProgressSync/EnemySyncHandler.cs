using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Enemy-specific gameplay bridge used by the shared enemy synchronization component.</summary>
internal abstract class EnemySyncHandler : MonoBehaviour
{
    internal Enemy Enemy { get; private set; }

    internal void Attach(Enemy enemy)
    {
        if (enemy == null) return;
        Enemy = enemy;
        OnAttach();
    }

    protected virtual void OnAttach() { }

    internal abstract bool IsWalking { get; }
    internal abstract void SetAuthority(bool authority);
    internal abstract void SetTarget(GameObject target);
    internal abstract void ApplySnapshot(byte orientation, byte state, bool walking);
    internal abstract void ApplyRemoteHit(byte orientation);
    internal abstract void ApplyDeath();
}
