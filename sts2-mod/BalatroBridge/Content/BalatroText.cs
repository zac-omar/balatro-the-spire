using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace BalatroBridge.Content;

/// <summary>
/// Balatro relics' and potions' descriptions are "{Text}" in StS2's loc tables (see
/// <see cref="ModText"/>); these fill Text in with Balatro's own words: the live card's when
/// Balatro has described it in this run (a joker's current mult, say), else the catalog's.
/// </summary>
internal static class BalatroText
{
    public const string Variable = "Text";

    public static string For(BalatroRelic relic)
    {
        string text = (relic.IsMutable ? relic.LiveText : null) ?? BalatroCatalog.Text(relic.BalatroKey) ?? Fallback(relic.Item);
        if (relic is StakeRelic) text = StakeText(relic.BalatroKey, text);
        // Jimbo's jokers can be sold from the relic bar (JimboRelicBar).
        if (relic is JokerRelic { IsMutable: true } joker && Jimbo.JimboRun.IsJimbo(joker.Owner)) text += "\n[color=#9aa0a6]Right-click to sell or move.[/color]";
        return text;
    }

    // Jimbo's starting relic: what the stake adds, then the rules his whole run is played by.
    private static string StakeText(string key, string balatro)
    {
        string stake = key == "stake_white" ? "No handicaps. Higher Ascensions play harder stakes." : balatro;
        return stake + "\n\n[gold]Enemy HP[/gold] is the blind's score." +
               "\n[blue]Play[/blue]: each scoring card strikes. [red]Hearts[/red] heal, [orange]Diamonds[/orange] Block, [blue]Clubs[/blue] Weak, [purple]Spades[/purple] Vulnerable." +
               "\n[red]Discard[/red]: Block equal to the cards' chips (with no discards, played cards give it)." +
               "\n[red]Overtime[/red]: once the blind's hands are spent, enemies gain more Strength every turn.";
    }

    public static string For(ConsumablePotion potion)
    {
        string text = BalatroCatalog.Text(potion.BalatroKey) ?? Fallback(potion.Item);
        // How Jimbo's belt works: cards that change playing cards work on the ones selected in hand.
        if (potion.IsMutable && Jimbo.JimboRun.IsJimbo(potion.Owner)) text += "\n[color=#9aa0a6]Select cards in hand first if it changes cards. Discard it to sell it.[/color]";
        return text;
    }

    private static string Fallback(BalatroItem item) => item.Set switch
    {
        "Joker" => "A Balatro joker.",
        "Voucher" => "A Balatro voucher.",
        "Back" => "A Balatro deck.",
        "Stake" => "A Balatro stake.",
        _ => $"A Balatro {item.Set.ToLowerInvariant()} card.",
    };
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicDescription), MethodType.Getter)]
internal static class BalatroRelicDescription
{
    private static void Postfix(RelicModel __instance, LocString __result)
    {
        if (__instance is BalatroRelic relic) __result.Add(BalatroText.Variable, BalatroText.For(relic));
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicEventDescription), MethodType.Getter)]
internal static class BalatroRelicEventDescription
{
    private static void Postfix(RelicModel __instance, LocString __result)
    {
        if (__instance is BalatroRelic relic) __result.Add(BalatroText.Variable, BalatroText.For(relic));
    }
}

[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.DynamicDescription), MethodType.Getter)]
internal static class BalatroPotionDescription
{
    private static void Postfix(PotionModel __instance, LocString __result)
    {
        if (__instance is ConsumablePotion potion) __result.Add(BalatroText.Variable, BalatroText.For(potion));
    }
}
