using BepInEx;
using UnityEngine;
using Blasphemous.Multiplayer.Client.Network;
using Blasphemous.Multiplayer.Client.Ping;

namespace Blasphemous.Multiplayer.Client;

[BepInPlugin(ModInfo.MOD_ID, ModInfo.MOD_NAME, ModInfo.MOD_VERSION)]
[BepInDependency("Blasphemous.ModdingAPI", "3.0.0")]
[BepInDependency("Blasphemous.CheatConsole", "1.1.0")]
internal class Main : BaseUnityPlugin
{
    public static Multiplayer Multiplayer { get; private set; }
    public static Main Instance { get; private set; }

    private void Start()
    {
        Instance = this;
        Multiplayer = new Multiplayer();

        ApplyBackgroundExecution();

        gameObject.AddComponent<ConnectionDisplay>();
        gameObject.AddComponent<PlayerDisplay>();
    }

    /// <summary>
    /// Unity stops updating and rendering a window that loses focus, which pauses this client
    /// while a two-client test switches to the other window. This is opt-in because keeping the
    /// game simulating in the background is not free (it keeps audio and AI running), and the
    /// default has to stay the vanilla behaviour.
    /// </summary>
    private void ApplyBackgroundExecution()
    {
        bool requested = Multiplayer != null && Multiplayer.config != null && Multiplayer.config.runInBackground;
        Application.runInBackground = requested;
        SharedDiagnosticLogger.Info($"[MultiplayerConfig] runInBackground configured={requested}, applied={Application.runInBackground}, configLoaded={Multiplayer != null && Multiplayer.config != null}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        SharedDiagnosticLogger.Info($"[MultiplayerConfig] Application focus={hasFocus}, runInBackground={Application.runInBackground}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    private void OnApplicationPause(bool paused)
    {
        SharedDiagnosticLogger.Info($"[MultiplayerConfig] Application paused={paused}, runInBackground={Application.runInBackground}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }
}
