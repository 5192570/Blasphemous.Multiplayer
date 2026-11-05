using System;
using Gameplay.GameControllers.Entities;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// The one switch for verbose per-enemy diagnostics.
///
/// Per-snapshot logging is useful while bringing up one enemy type and pure
/// noise the rest of the time: snapshots fire every 0.08s, so a single verbose
/// enemy floods both the local log and the shared diagnostic queue and drowns
/// the event logs (state, execution, death) that are actually triaged.
///
/// To diagnose an enemy, set <see cref="VerboseEnemyTypeName"/> to its class
/// name (the <c>type=</c> value from <c>[EnemyTierDiag]</c>, e.g.
/// <c>"WheelCarrier"</c> or <c>"Fool"</c>), or to <c>"*"</c> for every enemy.
/// Leave it empty to keep all per-snapshot diagnostics off. Event-driven logs
/// are never gated by this switch.
/// </summary>
internal static class EnemyDiagnostics
{
    // === Diagnose switches: edit these lines, then rebuild. ===
    // "" (default) = quiet, "WheelCarrier" / "Fool" / ... = that enemy only, "*" = all enemies.
    private const string VerboseEnemyTypeName = "";

    // Per-enemy diagnostic layers that cost continuously while enabled. All off by
    // default: per-enemy/per-event logs (registration, state, death, [EnemyTierDiag])
    // stay on, per-frame layers stay off until a test actually needs them.
    // HP labels: one TextMesh + LateUpdate (Camera.main + scale math) on EVERY enemy.
    internal const bool ShowEnemyHpLabels = false;
    // Spawn diagnostics: logs every CreateEnemy/IsSpawnerConsumed call in the scene.
    internal const bool LogSpawnDiagnostics = false;

    internal static bool IsVerbose(Enemy enemy)
    {
        return enemy != null && IsVerbose(enemy.GetType().Name);
    }

    internal static bool IsVerbose(string enemyTypeName)
    {
        if (string.IsNullOrEmpty(VerboseEnemyTypeName) || string.IsNullOrEmpty(enemyTypeName))
            return false;
        return VerboseEnemyTypeName == "*" ||
               string.Equals(VerboseEnemyTypeName, enemyTypeName, StringComparison.Ordinal);
    }
}
