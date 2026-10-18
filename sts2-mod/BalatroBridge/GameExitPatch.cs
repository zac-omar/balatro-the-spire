using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace BalatroBridge;

/// <summary>Closes Balatro as soon as the player quits StS2 from its menus, before StS2 saves and exits.</summary>
[HarmonyPatch(typeof(NGame), nameof(NGame.Quit))]
internal static class GameExitPatch
{
    private static void Prefix() => BridgeMod.Shutdown("player quit the game");
}
