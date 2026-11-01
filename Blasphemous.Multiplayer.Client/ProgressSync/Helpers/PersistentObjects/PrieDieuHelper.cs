using Blasphemous.ModdingAPI;
using Framework.Managers;
using HarmonyLib;
using Tools.Level.Interactables;

namespace Blasphemous.Multiplayer.Client.ProgressSync.Helpers.PersistentObjects
{
    [HarmonyPatch(typeof(PrieDieu), "OnUse")]
    class PrieDieuUse_Patch
    {
        public static void Postfix(PrieDieu __instance)
        {
            if (Main.Multiplayer.ProgressManager.CurrentlyUpdatingProgress) return;

            string persistentId = __instance.GetPersistentID();
            ModLog.Info($"Used PrieDieu: {persistentId}");
            Main.Multiplayer.ProgressManager.UsePersistentObject(persistentId);
        }
    }

    [HarmonyPatch(typeof(PrieDieu), "OnUpdate")]
    class PrieDieuRoomRestriction_Patch
    {
        public static bool Prefix(PrieDieu __instance)
        {
            bool canUse = CanUsePrieDieu();
            __instance.EnableInputIcon(canUse);

            // Keep the original update running while an interaction is already in progress,
            // so the shrine can finish repositioning the player if the other player leaves.
            return canUse || __instance.BeingUsed;
        }

        private static bool CanUsePrieDieu()
        {
            if (!Main.Multiplayer.NetworkManager.IsConnected)
                return true;

            var connectedPlayers = Main.Multiplayer.OtherPlayerManager.AllConnectedPlayers;

            // Preserve normal single-player behavior when nobody else is connected.
            if (connectedPlayers.Count == 0)
                return true;

            string currentScene = Core.LevelManager.currentLevel.LevelName;

            foreach (var player in connectedPlayers)
            {
                if (player.CurrentScene != currentScene)
                    return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(PrieDieu), "GetCurrentPersistentState")]
    class PrieDieuReceive_Patch
    {
        public static bool Prefix(string dataPath, PrieDieu __instance)
        {
            if (dataPath != "use") return true;

            // Maybe play activation animation
            __instance.Lighthed = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(PrieDieu), "SetCurrentPersistentState")]
    class PrieDieuLoad_Patch
    {
        public static bool Prefix(PersistentManager.PersistentData data, PrieDieu __instance)
        {
            if (data != null)
            {
                // This method is being called normally - only execute if object hasn't been interacted with
                return !Main.Multiplayer.ProgressManager.IsObjectInteracted(__instance.GetPersistentID());
            }

            __instance.Lighthed = true;
            return false;
        }
    }
}
