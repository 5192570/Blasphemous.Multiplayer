using Blasphemous.Multiplayer.Client.Players;
using Gameplay.GameControllers.Enemies.WheelCarrier;
using Gameplay.GameControllers.Entities;
using HarmonyLib;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client;

/// <summary>
/// WheelCarrier's vanilla vision test expects a physics hit on the Penitent layer.
/// Remote co-op avatars are visual-only objects, so they have no collider for that
/// raycast to hit. Treat a remote avatar as visible when it is inside the same
/// distance/cone constraints and the ray to it is not blocked by world geometry.
/// </summary>
[HarmonyPatch(typeof(VisionCone), nameof(VisionCone.CanSeeTarget))]
internal static class WheelCarrierRemoteVisionPatch
{
    private static readonly RaycastHit2D[] Hits = new RaycastHit2D[1];

    private static void Postfix(VisionCone __instance, Transform t, string targetLayer, ref bool __result)
    {
        if (__result || __instance == null || t == null || targetLayer != "Penitent")
            return;

        WheelCarrier wheelCarrier = __instance.GetComponentInParent<WheelCarrier>();
        if (wheelCarrier == null || t.GetComponent<OtherPlayerScript>() == null)
            return;

        __result = CanSeeRemotePlayer(__instance, wheelCarrier, t, targetLayer);
    }

    private static bool CanSeeRemotePlayer(VisionCone vision, WheelCarrier wheelCarrier, Transform target, string targetLayer)
    {
        Vector2 origin = (Vector2)vision.transform.position + vision.sightOffset;
        Vector2 targetPosition = target.position + Vector3.up * vision.targetHeightOffset;
        float distance = Vector2.Distance(origin, target.position);

        if (distance > vision.sightDistance)
            return false;
        if (distance < vision.closeRadius)
            return true;

        Vector2 direction = targetPosition - origin;
        int facing = wheelCarrier.Status.Orientation == EntityOrientation.Right ? 1 : -1;
        float angle = Mathf.Atan2(Mathf.Abs(direction.y), Mathf.Abs(direction.x)) * Mathf.Rad2Deg;

        if (vision.cantSeeBackwards && ((facing == 1 && direction.x < 0f) || (facing == -1 && direction.x > 0f)))
            return false;
        if (angle > vision.visionAngle)
            return false;

        int hitCount = Physics2D.LinecastNonAlloc(origin, targetPosition, Hits, vision.sightCollisionMask);
        if (hitCount > 0 && Hits[0].collider != null && Hits[0].collider.gameObject.layer != LayerMask.NameToLayer(targetLayer))
            return false;

        // A remote avatar has no collider; no blocking hit therefore means a clear sightline.
        return true;
    }
}