using Blasphemous.ModdingAPI;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Framework.IA;
using HarmonyLib;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Applies the native enemy respawn operation on the remote client after a Prie Dieu is used.
/// </summary>
public static class WorldResetManager
{
    private static bool _applyingRemoteReset;

    public static bool IsApplyingRemoteReset => _applyingRemoteReset;

    public static void ApplyRemoteReset(string sceneName)
    {
        if (_applyingRemoteReset || string.IsNullOrEmpty(sceneName))
            return;

        if (Core.LevelManager.currentLevel == null || Core.LevelManager.currentLevel.LevelName != sceneName)
            return;

        _applyingRemoteReset = true;
        try
        {
            ModLog.Info($"Applying shared Prie Dieu enemy reset in {sceneName}");
            Core.Logic.EnemySpawner.RespawnDeadEnemies();
        }
        finally
        {
            _applyingRemoteReset = false;
        }
    }
}

[HarmonyPatch(typeof(EnemySpawner), "RespawnDeadEnemies")]
internal static class RespawnDeadEnemiesSync_Patch
{
    public static void Postfix()
    {
        // A reset received from the peer must never be broadcast back to them.
        if (WorldResetManager.IsApplyingRemoteReset)
            return;

        if (!Main.Multiplayer.NetworkManager.IsConnected)
            return;

        Main.Multiplayer.NetworkManager.SendWorldReset();
        ModLog.Info("Sent shared Prie Dieu enemy reset");
    }
}
