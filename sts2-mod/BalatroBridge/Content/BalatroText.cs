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
        // Balatro's rarity badge, in its colour.
        if (relic is JokerRelic) text += "\n" + Rarity(relic.Item.Rarity);
        if (relic is JokerRelic { IsMutable: true } joker)
        {
            // In Jimbo's runs they're his jokers; anyone else's only play in Deal In's hands.
            text += Jimbo.JimboRun.IsJimbo(joker.Owner)
                ? "\n[color=#9aa0a6]Right-click to sell or move.[/color]"
                : "\n[color=#9aa0a6]Only affects [gold]Deal In[/gold].[/color]";
        }
        return text;
    }

    // Balatro's rarity names in its rarity colours (globals.lua G.C.RARITY).
    private static string Rarity(int rarity) => rarity switch
    {
        1 => "[color=#009DFF]Common[/color]",
        2 => "[color=#4BC292]Uncommon[/color]",
        3 => "[color=#FE5F55]Rare[/color]",
        _ => "[color=#B26CBB]Legendary[/color]",
    };

    // Jimbo's starting relic: what the stake adds, then the rules his whole run is played by.
    private static string StakeText(string key, string balatro)
    {
        string stake = key == "stake_white" ? "Base difficulty." : balatro;
        return stake + "\n\nEnemy HP equals the blind's score." +
               "\nPlaying a hand deals its score as damage." +
               "\nScoring Hearts heal [blue]1[/blue], Diamonds give [blue]3[/blue] [gold]Block[/gold], Clubs apply [blue]1[/blue] [gold]Weak[/gold], Spades apply [blue]1[/blue] [gold]Vulnerable[/gold]." +
               "\nDiscarding gives [gold]Block[/gold] equal to the cards' chips." +
               "\nOnce the blind's hands run out, enemies gain [gold]Strength[/gold] each turn.";
    }

    public static string For(ConsumablePotion potion)
    {
        string text = BalatroCatalog.Text(potion.BalatroKey) ?? Fallback(potion.Item);
        // How Jimbo's belt works: cards that change playing cards work on the ones selected in hand.
        if (potion.IsMutable && Jimbo.JimboRun.IsJimbo(potion.Owner)) text += "\n[color=#9aa0a6]Select cards first if it changes cards. Discard to sell.[/color]";
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
