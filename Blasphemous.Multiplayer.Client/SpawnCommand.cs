using Blasphemous.CheatConsole;
using Blasphemous.Multiplayer.Client.ProgressSync;
using Framework.Managers;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client;

/// <summary>
/// Debug spawning commands and read-only prefab diagnostics.
/// </summary>
public sealed class SpawnCommand : ModCommand
{
    private delegate bool SpawnEnemy(Vector3 position, out string message);

    protected override string CommandName => "spawn";

    protected override bool AllowUppercase => false;

    protected override Dictionary<string, Action<string[]>> AddSubCommands()
    {
        return new Dictionary<string, Action<string[]>>
        {
            { "help", Help },
            { "probe", Probe },
            { "fool", Fool },
            { "wheelcarrier", WheelCarrier },
        };
    }

    private void Help(string[] parameters)
    {
        if (!ValidateParameterList(parameters, 0)) return;

        Write("spawn fool -relative center: Spawn a debug Fool at the center of the current camera view");
        Write("spawn fool -scene x y: Spawn a debug Fool at scene/world coordinates");
        Write("spawn wheelcarrier -relative center: Spawn a debug WheelCarrier at the center of the current camera view");
        Write("spawn wheelcarrier -scene x y: Spawn a debug WheelCarrier at scene/world coordinates");
        Write("spawn probe: Inspect loaded enemy prefab sources without spawning or modifying anything");
        Write("Debug enemy spawning is currently single-player only");
    }

    private void Probe(string[] parameters)
    {
        if (!ValidateParameterList(parameters, 0)) return;

        EnemyPrefabProbe.Run(Write);
    }

    private void Fool(string[] parameters)
    {
        Spawn(parameters, "fool", DebugEnemySpawner.TrySpawnFool);
    }

    private void WheelCarrier(string[] parameters)
    {
        Spawn(parameters, "wheelcarrier", DebugEnemySpawner.TrySpawnWheelCarrier);
    }

    private void Spawn(string[] parameters, string enemyName, SpawnEnemy spawnEnemy)
    {
        Vector3 position;

        if (parameters.Length == 2 && parameters[0] == "-relative" && parameters[1] == "center")
        {
            if (!TryGetCameraCenter(out position, out string cameraError))
            {
                Write(cameraError);
                return;
            }
        }
        else if (parameters.Length == 3 && parameters[0] == "-scene" &&
            TryParseCoordinate(parameters[1], out float x) && TryParseCoordinate(parameters[2], out float y))
        {
            position = new Vector3(x, y, 0f);
        }
        else
        {
            Write($"Usage: spawn {enemyName} -relative center | spawn {enemyName} -scene x y");
            Write($"Coordinates must use invariant numeric format, for example: spawn {enemyName} -scene -560 12");
            return;
        }

        spawnEnemy(position, out string message);
        Write(message);
    }

    private static bool TryGetCameraCenter(out Vector3 position, out string error)
    {
        position = Vector3.zero;

        if (Core.Logic.CameraManager == null || Core.Logic.CameraManager.ProCamera2D == null ||
            Core.Logic.CameraManager.ProCamera2D.GameCamera == null)
        {
            error = "The game camera is unavailable; spawn requires an active gameplay scene.";
            return false;
        }

        Camera camera = Core.Logic.CameraManager.ProCamera2D.GameCamera;
        float depth = Mathf.Abs(camera.transform.position.z);
        position = camera.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, depth));
        position.z = 0f;
        error = null;
        return true;
    }

    private static bool TryParseCoordinate(string parameter, out float value)
    {
        return float.TryParse(parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
