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

    private const int MaxBackgroundCorrectionsLogged = 10;
    private int _backgroundCorrectionsLogged;

    /// <summary>
    /// The game forces the property back to false after we set it. Both entry points are vanilla
    /// and unconditional, in their Awake:
    ///
    ///   Gameplay.UI.Others.MenuLogic.Landing.Awake()      -> "Run in background was true in landing! Correcting."
    ///   Gameplay.UI.Others.MenuLogic.NewMainMenu.Awake()  -> "Run in background was true! Correcting."
    ///
    /// The second one runs when the main menu level loads, i.e. seconds after our Start(), which is
    /// why the previous test logged applied=True and then every focus loss with runInBackground=False.
    /// Re-asserting is the only option: those writes are not patchable through the property itself,
    /// and a per-frame comparison against the configured value is cheaper than tracking every
    /// vanilla caller.
    /// </summary>
    private void Update()
    {
        RestoreBackgroundExecution("update");
        Players.PenitentVisibilityProbe.Tick();
    }

    private void RestoreBackgroundExecution(string source)
    {
        if (_runInBackground == null) return;

        bool requested = _runInBackground.Value;
        if (Application.runInBackground == requested) return;

        bool previous = Application.runInBackground;
        Application.runInBackground = requested;

        if (_backgroundCorrectionsLogged < MaxBackgroundCorrectionsLogged)
        {
            _backgroundCorrectionsLogged++;
            SharedDiagnosticLogger.Info($"[MultiplayerConfig] runInBackground re-applied after external change configured={requested}, was={previous}, source={source}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        }
        else if (_backgroundCorrectionsLogged == MaxBackgroundCorrectionsLogged)
        {
            _backgroundCorrectionsLogged++;
            SharedDiagnosticLogger.Warn($"[MultiplayerConfig] runInBackground keeps being reset; further corrections are not logged, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        }
    }

    // These can fire before Start(), so the entry may not exist yet.
    private void OnApplicationFocus(bool hasFocus)
    {
        // Regaining focus is the one moment a client that was paused while the property was false
        // can be put back on the configured value.
        RestoreBackgroundExecution("focus");

        SharedDiagnosticLogger.Info($"[MultiplayerConfig] Application focus={hasFocus}, runInBackground={Application.runInBackground}, configured={DescribeRunInBackground()}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    private void OnApplicationPause(bool paused)
    {
        SharedDiagnosticLogger.Info($"[MultiplayerConfig] Application paused={paused}, runInBackground={Application.runInBackground}, configured={DescribeRunInBackground()}, localPlayer={Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
    }

    private string DescribeRunInBackground() => _runInBackground != null ? _runInBackground.Value.ToString() : "unbound";
}
