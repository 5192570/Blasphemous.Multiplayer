using System;
using Blasphemous.Multiplayer.Client.Players;
using Gameplay.GameControllers.Enemies.Framework.IA;
using Gameplay.GameControllers.Enemies.WaxCrawler.AI;
using HarmonyLib;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// WaxCrawler wakes and chases off <see cref="EnemyBehaviour.PlayerSeen"/>, which
/// vanilla sets only from its visual CollisionSensor. Remote players are
/// visual-only puppets without a sensor-visible collider, so the sensor can
/// never trip for them and the crawler ignored followers entirely.
///
/// This patch preserves the native sensor test for the real Penitent (the
/// original getter still runs whenever no remote player qualifies) and only
/// *adds* sight: on the scene authority, a remote player whose position falls
/// inside the same visual sensor collider counts as seen. Both the property
/// getter and its IsPlayerSeen() wrapper are covered, since the behaviour
/// reads one and behaviour-tree conditions may read the other.
/// </summary>
internal static class WaxCrawlerRemotePlayerVisionPatch
{
    [HarmonyPatch(typeof(EnemyBehaviour), "PlayerSeen", MethodType.Getter)]
    private static bool PlayerSeenGetterPrefix(EnemyBehaviour __instance, ref bool __result)
    {
        if (!RemotePlayerSeen(__instance)) return true;
        __result = true;
        return false;
    }

    [HarmonyPatch(typeof(EnemyBehaviour), nameof(EnemyBehaviour.IsPlayerSeen))]
    private static bool IsPlayerSeenPrefix(EnemyBehaviour __instance, ref bool __result)
    {
        if (!RemotePlayerSeen(__instance)) return true;
        __result = true;
        return false;
    }

    private static bool RemotePlayerSeen(EnemyBehaviour behaviour)
    {
        // EnableBehaviourOnLoad is the adapter's authority flag for this enemy:
        // only the scene authority runs the AI, so only it needs synthetic sight.
        if (!(behaviour is WaxCrawlerBehaviour wax) || !wax.EnableBehaviourOnLoad)
            return false;
        if (Main.Multiplayer == null || Main.Multiplayer.OtherPlayerManager == null)
            return false;

        Collider2D sensor;
        try
        {
            sensor = wax.GetVisualSensor();
        }
        catch (Exception)
        {
            return false;
        }
        if (sensor == null) return false;

        foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
        {
            if (player == null || !player.gameObject.activeInHierarchy) continue;

            // The puppet root sits at the player's feet; test the body above it
            // as well, matching what the Penitent's own collider would cover.
            Vector3 position = player.transform.position;
            if (sensor.OverlapPoint((Vector2)position) ||
                sensor.OverlapPoint((Vector2)(position + Vector3.up)))
                return true;
        }
        return false;
    }
}
