using Blasphemous.Multiplayer.Client.Players;
using Framework.FrameworkCore;
using Gameplay.GameControllers.Enemies.WheelCarrier.IA;
using HarmonyLib;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// WheelCarrier's native vision test expects a collider on the Penitent layer.
/// Multiplayer remote players are visual-only GameObjects without colliders, so
/// the native Linecast can never hit them. Preserve the native cone/range/occlusion
/// rules, but treat an unobstructed remote-player position as visible.
/// </summary>
[HarmonyPatch(typeof(WheelCarrierBehaviour), nameof(WheelCarrierBehaviour.TargetCanBeVisible))]
internal static class WheelCarrierRemotePlayerVisionPatch
{
    private static readonly RaycastHit2D[] Hits = new RaycastHit2D[1];

    private static bool Prefix(WheelCarrierBehaviour __instance, ref bool __result)
    {
        if (__instance == null || __instance.WheelCarrier == null || __instance.WheelCarrier.Target == null)
        {
            __result = false;
            return false;
        }

        Transform target = __instance.WheelCarrier.Target.transform;
        if (target.GetComponent<OtherPlayerScript>() == null)
            return true;

        var cone = __instance.WheelCarrier.VisionCone;
        if (cone == null)
        {
            __result = false;
            return false;
        }

        Vector2 origin = (Vector2)cone.transform.position + cone.sightOffset;
        Vector2 targetPoint = target.position + Vector3.up * cone.targetHeightOffset;
        float distance = Vector2.Distance(origin, target.position);
        if (distance > cone.sightDistance)
        {
            __result = false;
            return false;
        }
        if (distance < cone.closeRadius)
        {
            __result = true;
            return false;
        }

        Vector2 direction = targetPoint - origin;
        int facing = __instance.WheelCarrier.Status.Orientation == EntityOrientation.Right ? 1 : -1;
        float angle = Mathf.Atan2(Mathf.Abs(direction.y), Mathf.Abs(direction.x)) * Mathf.Rad2Deg;
        if (cone.cantSeeBackwards && ((facing == 1 && direction.x < 0f) || (facing == -1 && direction.x > 0f)))
        {
            __result = false;
            return false;
        }
        if (angle > cone.visionAngle)
        {
            __result = false;
            return false;
        }

        // Remote-player objects intentionally have no physics collider. Therefore
        // no hit means the target point is unobstructed. A blocking hit still wins;
        // a Penitent-layer hit retains the game's native visibility behavior.
        int hitCount = Physics2D.LinecastNonAlloc(origin, targetPoint, Hits, cone.sightCollisionMask);
        __result = hitCount == 0 || (Hits[0].collider != null && Hits[0].collider.gameObject.layer == LayerMask.NameToLayer("Penitent"));
        return false;
    }
}
