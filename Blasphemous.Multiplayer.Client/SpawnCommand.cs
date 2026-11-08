using Blasphemous.CheatConsole;
using Blasphemous.Multiplayer.Client.ProgressSync;
using System;
using System.Collections.Generic;

namespace Blasphemous.Multiplayer.Client;

/// <summary>
/// Debug spawning commands. Only the read-only prefab probe is implemented at this stage.
/// </summary>
public sealed class SpawnCommand : ModCommand
{
    protected override string CommandName => "spawn";

    protected override bool AllowUppercase => false;

    protected override Dictionary<string, Action<string[]>> AddSubCommands()
    {
        return new Dictionary<string, Action<string[]>>
        {
            { "help", Help },
            { "probe", Probe },
        };
    }

    private void Help(string[] parameters)
    {
        if (!ValidateParameterList(parameters, 0)) return;

        Write("spawn probe: Inspect loaded enemy prefab sources without spawning or modifying anything");
        Write("Run spawn probe again after a scene transition to check earlier prefab cache references");
    }

    private void Probe(string[] parameters)
    {
        if (!ValidateParameterList(parameters, 0)) return;

        EnemyPrefabProbe.Run(Write);
    }
}
