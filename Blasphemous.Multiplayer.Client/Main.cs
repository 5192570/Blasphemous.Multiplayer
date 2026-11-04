using BepInEx;
using BepInEx.Configuration;
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

    // BepInEx owns this setting. The mod's own JSON config is not an option here: ModdingAPI loads
    // it from Multiplayer.OnInitialize(), which runs after the plugins are built, so reading the
    // flag there resolves before it exists and Unity is left with the wrong value.
    private ConfigEntry<bool> _runInBackground;

    private void Start()
    {
        Instance = this;

        // Before anything else: a paused window would never reach the rest of Start().
        ApplyBackgroundExecution();

        Multiplayer = new Multiplayer();

        gameObject.AddComponent<ConnectionDisplay>();
        gameObject.AddComponent<PlayerDisplay>();
    }

    /// <summary>
    /// Unity stops updating and rendering a window that loses focus. A multiplayer client has to
    /// keep its network loop running while the player looks at another window, so background
    /// execution defaults to on and can be turned off in the BepInEx config.
    ///
    /// Only Unity's own property is set. Game or Unity systems are never resumed by hand from the
    /// pause callback, which would leave them in an inconsistent state.
    /// </summary>
    private void ApplyBackgroundExecution()
    {
        ConfigFile pluginConfig = base.Config;

        if (pluginConfig == null)
        {
            // BepInEx builds the config file during the plugin's Awake. If that ever changes, keep
            // the multiplayer behaviour instead of silently falling back to vanilla pausing.
            Application.runInBackground = true;
            SharedDiagnosticLogger.Warn($"[MultiplayerConfig] BepInEx config unavailable, forcing runInBackground={Application.runInBackground}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
            return;
        }

        _runInBackground = pluginConfig.Bind("General", "RunInBackground", true,
            "Keep the game simulating while its window is unfocused. A multiplayer client must keep " +
            "networking while you look at another window; disable for the vanilla pause behaviour.");

        Application.runInBackground = _runInBackground.Value;
        SharedDiagnosticLogger.Info($"[MultiplayerConfig] runInBackground configured={_runInBackground.Value}, applied={Application.runInBackground}, source={pluginConfig.ConfigFilePath}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    // These can fire before Start(), so the entry may not exist yet.
    private void OnApplicationFocus(bool hasFocus)
    {
        SharedDiagnosticLogger.Info($"[MultiplayerConfig] Application focus={hasFocus}, runInBackground={Application.runInBackground}, configured={DescribeRunInBackground()}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    private void OnApplicationPause(bool paused)
    {
        SharedDiagnosticLogger.Info($"[MultiplayerConfig] Application paused={paused}, runInBackground={Application.runInBackground}, configured={DescribeRunInBackground()}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    private string DescribeRunInBackground() => _runInBackground != null ? _runInBackground.Value.ToString() : "unbound";
}
