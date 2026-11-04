using Blasphemous.Multiplayer.Client.Network;
using Framework.Managers;
using Gameplay.GameControllers.Penitent;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.Players
{
    /// <summary>
    /// Diagnostic only. The finisher is supposed to hide the player who runs it - vanilla does that
    /// with Interactable.ShowPlayer(false), which disables the local Penitent's SpriteRenderer and
    /// DamageArea - and the mod hides the executor's remote avatar for the same window. This
    /// records every transition of those flags on the local Penitent, so a Penitent that is still
    /// drawn during an execution can be attributed to the frame in which it came back.
    ///
    /// It never changes a renderer or an active state.
    /// </summary>
    internal static class PenitentVisibilityProbe
    {
        private static bool _initialised;
        private static bool _lastSpriteEnabled;
        private static bool _lastDamageAreaEnabled;
        private static bool _lastActive;

        internal static void Tick()
        {
            Penitent penitent = Core.Logic != null ? Core.Logic.Penitent : null;
            if (penitent == null || penitent.SpriteRenderer == null) return;

            bool sprite = penitent.SpriteRenderer.enabled;
            bool damageArea = penitent.DamageArea != null && penitent.DamageArea.enabled;
            bool active = penitent.gameObject.activeInHierarchy;

            if (!_initialised)
            {
                _initialised = true;
                _lastSpriteEnabled = sprite;
                _lastDamageAreaEnabled = damageArea;
                _lastActive = active;
                Log("initial", sprite, damageArea, active, penitent);
                return;
            }

            if (sprite == _lastSpriteEnabled && damageArea == _lastDamageAreaEnabled && active == _lastActive)
                return;

            string change = $"sprite {Describe(_lastSpriteEnabled)}->{Describe(sprite)}, damageArea {Describe(_lastDamageAreaEnabled)}->{Describe(damageArea)}, active {Describe(_lastActive)}->{Describe(active)}";
            _lastSpriteEnabled = sprite;
            _lastDamageAreaEnabled = damageArea;
            _lastActive = active;
            Log(change, sprite, damageArea, active, penitent);
        }

        private static string Describe(bool value) => value ? "true" : "false";

        private static void Log(string source, bool sprite, bool damageArea, bool active, Penitent penitent)
        {
            string clip = "<none>";
            if (penitent.Animator != null)
            {
                AnimatorStateInfo state = penitent.Animator.GetCurrentAnimatorStateInfo(0);
                if (state.shortNameHash != 0 && penitent.Animator.GetCurrentAnimatorClipInfo(0).Length > 0)
                    clip = $"{penitent.Animator.GetCurrentAnimatorClipInfo(0)[0].clip?.name ?? "<null>"}@{state.normalizedTime:0.00}";
            }

            SharedDiagnosticLogger.Info($"[PenitentVisibilityDiag] local Penitent {source}, sprite={sprite}, damageArea={damageArea}, active={active}, onExecution={penitent.IsOnExecution}, clip={clip}, localPlayer={Main.Multiplayer?.PlayerName}, time={Time.realtimeSinceStartup:0.000}");
        }
    }
}
