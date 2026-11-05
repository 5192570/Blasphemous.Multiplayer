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
    // On by default: identifying which enemy is which in a crowd is worth more
    // during bring-up than the per-frame label cost. Flip off for perf A/B tests.
    internal const bool ShowEnemyHpLabels = true;
    // Spawn diagnostics: logs every CreateEnemy/IsSpawnerConsumed call in the scene.
    internal const bool LogSpawnDiagnostics = false;
    // WheelCarrier execution diagnostics are parked for now: the remaining issue is
    // visual-only, so keep its [WheelCarrierExecutionDiag]/[WheelCarrierVisualDiag]
    // stream and post-death renderer watch off until that work resumes. Execution
    // sync and the mirrored execution presentation are unaffected.
    internal const bool EnableWheelCarrierExecutionDiagnostics = false;

    // Enemy-specific debuggers stay in the code for future bring-up rounds, but
    // ship off. Flip the matching switch to reuse a stream when revisiting an
    // enemy; this does not gate generic enemy sync/death/damage logging.
    internal const bool EnableWaxCrawlerDiagnostics = false;
    internal const bool EnableWheelCarrierStateDiagnostics = false;
    internal const bool EnablePenitentVisibilityDiagnostics = false;
    internal const bool EnableEnemyTierDiagnostics = false;

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
