using System.Collections.Generic;
using System.Reflection;
using Blasphemous.Multiplayer.Client.Players;
using Gameplay.GameControllers.Enemies.Framework.IA;
using Gameplay.GameControllers.Entities;
using HarmonyLib;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Shared authority-side perception plumbing for enemies whose vanilla senses
/// only react to the real Penitent. Remote-player puppets have no collider and
/// are not Entities, so a vanilla CollisionSensor can never trip on them; this
/// helper makes the sensor state exist for remote players anyway:
///
/// - PlayerSeen/PlayerHeard backing fields are driven from the sensors' real
///   state (local player) OR'd with puppet positions tested against the same
///   sensor colliders (remote players), so every consumer of those properties
///   observes remote players.
/// - In-range puppets are mirrored into the visual and hearing sensors'
///   private contact lists (and back out when they leave, deactivate, or the
///   handler loses authority), so IsColliding() also reports them. Only
///   entries added here are ever removed; vanilla contacts are left alone.
///
/// What stays with each enemy adapter is only the policy: what to do while a
/// player is seen (for example the WaxCrawler drives Behaviour.Rise(), whose
/// tree wake branch gates on the touched-entity list a puppet can never
/// enter). One instance per enemy handler; mirroring bookkeeping is per enemy.
/// Extracted from WaxCrawlerEnemySyncHandler once a second enemy needed the
/// same senses plumbing; behaviour is unchanged from that implementation.
/// </summary>
internal sealed class EnemyRemotePerception
{
    // EnemyBehaviour.PlayerSeen/PlayerHeard are only set by vanilla from the
    // sensors. The setters are resolved by name with backing-field fallbacks;
    // both are null-guarded.
    private static readonly MethodInfo PlayerSeenSetter =
        AccessTools.PropertySetter(typeof(EnemyBehaviour), "PlayerSeen");
    private static readonly FieldInfo PlayerSeenField =
        AccessTools.Field(typeof(EnemyBehaviour), "<PlayerSeen>k__BackingField");
    private static readonly MethodInfo PlayerHeardSetter =
        AccessTools.PropertySetter(typeof(EnemyBehaviour), "PlayerHeard");
    private static readonly FieldInfo PlayerHeardField =
        AccessTools.Field(typeof(EnemyBehaviour), "<PlayerHeard>k__BackingField");

    // CollisionSensor keeps its live contacts in a private object list.
    private static readonly FieldInfo SensorObjectListField =
        AccessTools.Field(typeof(CollisionSensor), "objectList");

    private readonly HashSet<GameObject> _mirroredVisualObjects = new HashSet<GameObject>();
    private readonly HashSet<GameObject> _mirroredHearingObjects = new HashSet<GameObject>();
    private CollisionSensor _visualSensorComponent;
    private CollisionSensor _hearingSensorComponent;

    /// <summary>
    /// Drives sight and hearing for one authority frame. Returns whether any
    /// player (local or remote) is inside the visual sensor. Sets
    /// <paramref name="visualSensorMissing"/> when the visual sensor cannot be
    /// obtained at all (threw or null), in which case nothing was driven.
    /// </summary>
    internal bool DrivePerception(EnemyBehaviour behaviour, out bool localSeen, out bool remoteSeen, out bool visualSensorMissing)
    {
        localSeen = false;
        remoteSeen = false;
        visualSensorMissing = false;
        if (behaviour == null)
        {
            visualSensorMissing = true;
            return false;
        }

        Collider2D sensor;
        try
        {
            sensor = behaviour.GetVisualSensor();
        }
        catch (System.Exception)
        {
            visualSensorMissing = true;
            return false;
        }
        if (sensor == null)
        {
            visualSensorMissing = true;
            return false;
        }

        CollisionSensor collisionSensor = sensor.GetComponent<CollisionSensor>();
        _visualSensorComponent = collisionSensor;
        // Touched entities only ever contain real Entities (the Penitent), so
        // this stays a clean local-player signal even after puppets are
        // mirrored into the object list below. Any other unmirrored contact
        // also counts, so the drive never fights a vanilla sensor trip.
        localSeen = collisionSensor != null &&
            (collisionSensor.GetTouchedEntities().Length > 0 ||
             SensorHasUnmirroredContact(collisionSensor, _mirroredVisualObjects));
        remoteSeen = AnyPuppetInSensor(sensor);

        bool seen = localSeen || remoteSeen;
        if (behaviour.PlayerSeen != seen) SetPlayerSeen(behaviour, seen);

        // The mirror makes the sensors' live collision bookkeeping
        // (IsColliding) report remote players too; some behaviour trees read
        // that state rather than the sight fields.
        SyncSensorMirror(collisionSensor, sensor, _mirroredVisualObjects);
        DriveHearingPerception(behaviour);
        return seen;
    }

    /// <summary>Removes every mirrored puppet from both sensors (authority loss).</summary>
    internal void ClearMirrors()
    {
        RemoveMirrored(_visualSensorComponent, _mirroredVisualObjects);
        RemoveMirrored(_hearingSensorComponent, _mirroredHearingObjects);
    }

    private void DriveHearingPerception(EnemyBehaviour behaviour)
    {
        Collider2D hearing;
        try
        {
            hearing = behaviour.GetHearingSensor();
        }
        catch (System.Exception)
        {
            return;
        }
        if (hearing == null) return;

        CollisionSensor hearingSensor = hearing.GetComponent<CollisionSensor>();
        _hearingSensorComponent = hearingSensor;

        bool localHeard = hearingSensor != null &&
            (hearingSensor.GetTouchedEntities().Length > 0 ||
             SensorHasUnmirroredContact(hearingSensor, _mirroredHearingObjects));
        bool heard = localHeard || AnyPuppetInSensor(hearing);
        if (behaviour.PlayerHeard != heard) SetPlayerHeard(behaviour, heard);

        SyncSensorMirror(hearingSensor, hearing, _mirroredHearingObjects);
    }

    private static void SetPlayerSeen(EnemyBehaviour behaviour, bool seen)
    {
        if (PlayerSeenSetter != null)
            PlayerSeenSetter.Invoke(behaviour, new object[] { seen });
        else if (PlayerSeenField != null)
            PlayerSeenField.SetValue(behaviour, seen);
    }

    private static void SetPlayerHeard(EnemyBehaviour behaviour, bool heard)
    {
        if (PlayerHeardSetter != null)
            PlayerHeardSetter.Invoke(behaviour, new object[] { heard });
        else if (PlayerHeardField != null)
            PlayerHeardField.SetValue(behaviour, heard);
    }

    private static bool AnyPuppetInSensor(Collider2D sensor)
    {
        if (Main.Multiplayer == null || Main.Multiplayer.OtherPlayerManager == null) return false;
        foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
        {
            if (player == null || !player.gameObject.activeInHierarchy) continue;
            if (IsPuppetInSensor(player.gameObject, sensor)) return true;
        }
        return false;
    }

    private static bool IsPuppetInSensor(GameObject puppet, Collider2D sensor)
    {
        // The puppet root sits at the player's feet; test the body above it
        // too, matching what the Penitent's own collider would cover. The
        // bounds test backs up OverlapPoint in case the sensor collider's
        // enabled state makes the geometric test unreliable.
        Vector3 position = puppet.transform.position;
        return sensor.OverlapPoint((Vector2)position) ||
               sensor.OverlapPoint((Vector2)(position + Vector3.up)) ||
               sensor.bounds.Contains(position + Vector3.up * 0.5f);
    }

    private static List<GameObject> GetSensorObjects(CollisionSensor sensorComponent)
    {
        if (sensorComponent == null || SensorObjectListField == null) return null;
        return SensorObjectListField.GetValue(sensorComponent) as List<GameObject>;
    }

    private static bool SensorHasUnmirroredContact(CollisionSensor sensorComponent, HashSet<GameObject> mirrored)
    {
        List<GameObject> objects = GetSensorObjects(sensorComponent);
        if (objects == null) return sensorComponent != null && sensorComponent.IsColliding();
        foreach (GameObject contact in objects)
        {
            if (contact != null && !mirrored.Contains(contact)) return true;
        }
        return false;
    }

    private void SyncSensorMirror(CollisionSensor sensorComponent, Collider2D sensorCollider, HashSet<GameObject> mirrored)
    {
        List<GameObject> objects = GetSensorObjects(sensorComponent);
        if (objects == null || sensorCollider == null) return;

        if (Main.Multiplayer != null && Main.Multiplayer.OtherPlayerManager != null)
        {
            foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
            {
                if (player == null || !player.gameObject.activeInHierarchy) continue;
                GameObject puppet = player.gameObject;
                if (!IsPuppetInSensor(puppet, sensorCollider)) continue;
                if (!objects.Contains(puppet)) objects.Add(puppet);
                mirrored.Add(puppet);
            }
        }

        // Remove only entries mirrored here that no longer qualify (left the
        // sensor, left the scene, destroyed). Vanilla contacts are never
        // tracked in the mirrored set and are left alone.
        List<GameObject> stale = null;
        foreach (GameObject puppet in mirrored)
        {
            if (puppet != null && puppet.activeInHierarchy && IsPuppetInSensor(puppet, sensorCollider)) continue;
            if (stale == null) stale = new List<GameObject>();
            stale.Add(puppet);
        }
        if (stale == null) return;
        foreach (GameObject puppet in stale)
        {
            objects.Remove(puppet);
            mirrored.Remove(puppet);
        }
    }

    private static void RemoveMirrored(CollisionSensor sensorComponent, HashSet<GameObject> mirrored)
    {
        List<GameObject> objects = GetSensorObjects(sensorComponent);
        if (objects != null)
        {
            foreach (GameObject puppet in mirrored) objects.Remove(puppet);
        }
        mirrored.Clear();
    }
}
