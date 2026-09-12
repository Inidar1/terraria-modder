using System.Collections.Generic;
using HarmonyLib;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.IO;
using Terraria.UI;
using TerrariaModder.Core.IO;

namespace TerrariaModder.Core.UI
{
    /// <summary>Uses Terraria's existing report page for failed player sidecars.</summary>
    internal static class PlayerLoadFailureUI
    {
        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(UICharacterListItem), "PlayGame"),
                prefix: new HarmonyMethod(typeof(PlayerLoadFailureUI), nameof(PlayPrefix)));
            harmony.Patch(AccessTools.Method(typeof(UICharacterListItem), "PlayMouseOver"),
                postfix: new HarmonyMethod(typeof(PlayerLoadFailureUI), nameof(HoverPostfix)));
        }
        private static void HoverPostfix(PlayerFileData ____data, UIText ____buttonLabel)
        {
            if (PlayerSaveContributors.GetLoadErrors(____data.Player).Count > 0)
                ____buttonLabel.SetText("Equipment recovery details");
        }
        private static bool PlayPrefix(UIMouseEvent evt, UIElement listeningElement, PlayerFileData ____data)
        {
            var errors = PlayerSaveContributors.GetLoadErrors(____data.Player);
            if (errors.Count == 0 || listeningElement != evt.Target) return true;
            var reporter = new GeneralIssueReporter();
            reporter.AddReport("Cannot load equipment for " + ____data.Name + ".\n\n"
                + "This character is unavailable because mod equipment data could not be read. Your existing character files and equipment sidecars have been preserved.\n\n"
                + "Keep a copy of the character file and all of its sidecars and backups before attempting recovery. Repair the affected sidecar, or restore a matching character-and-sidecar backup set. Do not delete a sidecar to bypass this error.\n\n"
                + "After repair, return to the title screen and open character selection again to reload the files. If you need help, include the failure details below.\n\n"
                + string.Join("\n\n", errors));
            Main.MenuUI.SetState(new UIReportsPage(Main.MenuUI.CurrentState, Main.menuMode,
                new List<IProvideReports> { reporter }));
            return false;
        }
    }
}
