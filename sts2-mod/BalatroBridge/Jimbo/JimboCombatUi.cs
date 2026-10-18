using System;
using System.Linq;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace BalatroBridge.Jimbo;

/// <summary>
/// In Jimbo's fights StS2's hand, energy orb and draw/discard piles give way to his Balatro hand
/// (<see cref="JimboHand"/>), docked in their place. End Turn, the top bar, intents and hover
/// tips stay StS2's.
/// </summary>
[HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Activate))]
internal static class JimboCombatUi
{
    private static readonly AccessTools.FieldRef<NCombatUi, NCombatPilesContainer>? Piles = Field("_combatPilesContainer");

    private static void Postfix(NCombatUi __instance, CombatState state)
    {
        try
        {
            Player? me = LocalContext.GetMe(state);
            if (me == null || !JimboRun.IsJimbo(me)) return;
            __instance.EnergyCounterContainer.Visible = false;
            __instance.Hand.Visible = false;
            if (Piles != null) Piles(__instance).Visible = false;
            JimboCombat.Dock(__instance, me);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: couldn't dock the hand: {e}");
        }
    }

    private static AccessTools.FieldRef<NCombatUi, NCombatPilesContainer>? Field(string name)
    {
        try
        {
            return AccessTools.FieldRefAccess<NCombatUi, NCombatPilesContainer>(name);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: NCombatUi.{name} not found ({e.Message}); StS2's piles will show in Jimbo's fights");
            return null;
        }
    }
}

/// <summary>A won fight cashes out (Balatro's round summary) instead of offering StS2's rewards.</summary>
[HarmonyPatch(typeof(NCombatUi), "OnCombatWon")]
internal static class JimboCashOutInsteadOfRewards
{
    private static bool Prefix(CombatRoom room)
    {
        Player? me = JimboRun.Me(room.CombatState.RunState);
        if (me == null) return true;
        TaskHelper.RunSafely(JimboCombat.CashOut(me, room));
        return false;
    }
}

/// <summary>StS2's first-fight tutorial explains cards and energy, which Jimbo doesn't have.</summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SeenFtue))]
internal static class JimboSkipsCardTutorials
{
    private static readonly string[] CardTutorials = ["combat_rules_ftue", "combat_reward_ftue", "card_reward_ftue"];

    private static void Postfix(string ftueKey, ref bool __result)
    {
        if (!__result && CardTutorials.Contains(ftueKey) && JimboRun.IsJimboRun(RunManager.Instance.DebugOnlyGetState())) __result = true;
    }
}

/// <summary>
/// Jimbo's runs play Balatro's soundtrack from start to finish (the room's ambience stays):
/// StS2's act music never starts, and Balatro's swells in instead.
/// </summary>
[HarmonyPatch(typeof(NRunMusicController), nameof(NRunMusicController.UpdateMusic))]
internal static class JimboMusicInsteadOfActMusic
{
    private static bool Prefix(NRunMusicController __instance)
    {
        if (!JimboRun.IsJimboRun(RunManager.Instance.DebugOnlyGetState())) return true;
        JimboMusic.Play();
        __instance.UpdateAmbience();
        return false;
    }
}

[HarmonyPatch(typeof(NRunMusicController), nameof(NRunMusicController.PlayCustomMusic))]
internal static class JimboNoEncounterMusic
{
    private static bool Prefix() => !JimboRun.IsJimboRun(RunManager.Instance.DebugOnlyGetState());
}

/// <summary>Balatro's soundtrack for the whole of Jimbo's run.</summary>
internal static class JimboMusic
{
    private static bool _on;

    public static void Play()
    {
        if (_on) return;
        _on = true;
        BalatroAudio.Instance.SetTrack(BalatroAudio.Track.Main);
        BalatroAudio.Instance.FadeMusic(on: true, 2.0);
    }

    public static void SetTrack(BalatroAudio.Track track) => BalatroAudio.Instance.SetTrack(track);

    /// <summary>Each frame: Balatro's music stops once the Jimbo run is over.</summary>
    public static void Update()
    {
        if (_on && !(RunManager.Instance.IsInProgress && JimboRun.IsJimboRun(RunManager.Instance.DebugOnlyGetState())))
        {
            _on = false;
            BalatroAudio.Instance.FadeMusic(on: false, 1.5);
        }
    }
}

/// <summary>
/// A Jimbo fight opens with its blind's name ("Small Blind", "The Goad") where StS2 says
/// "Battle Start", in a Boss Blind's colour, with a gong for a boss.
/// </summary>
[HarmonyPatch(typeof(NCombatStartBanner), "_Ready")]
internal static class JimboBlindBanner
{
    private static void Postfix(NCombatStartBanner __instance)
    {
        if (!JimboRun.IsJimboRun(RunManager.Instance.DebugOnlyGetState()) || JimboRun.Last is not { InFight: true, Blind: TableBlind blind }) return;
        if (__instance.GetNodeOrNull<MegaCrit.Sts2.addons.mega_text.MegaLabel>("Label") is not { } label) return;
        label.SetTextAutoSize(blind.Name);
        if (blind.Boss && blind.Colour is string hex && Color.HtmlIsValid(hex)) label.AddThemeColorOverride("font_color", Color.FromHtml(hex).Lightened(0.25f));
        if (blind.Boss) TableFx.Sound("gong", 1f, 0.5f);
    }
}
