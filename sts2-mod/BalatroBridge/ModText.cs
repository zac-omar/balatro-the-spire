using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BalatroBridge.Content;
using BalatroBridge.Jimbo;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace BalatroBridge;

/// <summary>
/// English text for the mod's cards, relics, potions, character and events. The game only reads
/// mod text from a Godot .pck, so these entries are merged into its loc tables whenever a
/// language's tables are applied (and again when Balatro's catalog arrives). Other languages fall
/// back to them. Balatro items take their names and text from Balatro itself (<see cref="BalatroCatalog"/>).
/// </summary>
[HarmonyPatch(typeof(LocManager), "SetLanguageInternal")]
internal static class ModText
{
    private const string CardTable = "cards";

    private static readonly Dictionary<string, string> Cards = new()
    {
        ["BALATRO_DEAL_IN.title"] = "Deal In",
        ["BALATRO_DEAL_IN.description"] = "Play a hand of [gold]Balatro[/gold].\nDeal damage equal to {Percent:diff()}% of its score.",
        ["BALATRO_DEAL_IN.scored"] = "{hand}! {score} chips!",
        ["BALATRO_DEAL_IN.noBalatro"] = "Balatro isn't answering...",
    };

    private static readonly Dictionary<string, string> Characters = new()
    {
        ["JIMBO.title"] = "Jimbo",
        ["JIMBO.titleObject"] = "Jimbo",
        ["JIMBO.description"] = "Balatro's jester, dealt into the Spire.\nPlays poker hands, buys jokers, and climbs one blind at a time.",
        ["JIMBO.pronounSubject"] = "he",
        ["JIMBO.pronounObject"] = "him",
        ["JIMBO.possessiveAdjective"] = "his",
        ["JIMBO.pronounPossessive"] = "his",
        ["JIMBO.bestiaryQuote"] = "Haven't beaten this blind yet!",
        ["JIMBO.cardsModifierTitle"] = "Jimbo's Cards",
        ["JIMBO.cardsModifierDescription"] = "Jimbo has no cards of his own: he plays Balatro.",
        ["JIMBO.eventDeathPrevention"] = "Not yet! I've still got a hand to play!",
        ["JIMBO.aromaPrinciple"] = "[sine][red]Every hand is a winning hand if you believe![/red][/sine]",
        ["JIMBO.goldMonologue"] = "[sine]Ooh, that's a lot of interest...[/sine]",
        ["JIMBO.banter.alive.endTurnPing"] = "Your turn, pal!",
        ["JIMBO.banter.dead.endTurnPing"] = "...",
    };

    private static readonly Dictionary<string, string> Events = new()
    {
        ["BALATRO_DEALER.title"] = "The Dealer",
        ["BALATRO_DEALER.pages.INITIAL.description"] =
            "A felt table waits where the Spire begins, three decks fanned across it. A pair of gloved hands shuffles them without anyone attached.\n\n[gold]\"Pick a deck, any deck,\"[/gold] says a voice from everywhere. [sine]\"The house always deals fair.\"[/sine]",
        ["BALATRO_DEALER.pages.INITIAL_ACT2.description"] =
            "The felt table is here again, a little higher up. The gloved hands set out three [aqua]vouchers[/aqua] and tap them, one by one.\n\n[gold]\"Halfway up, and still dealing! Take one on the house.\"[/gold]",
        ["BALATRO_DEALER.pages.INITIAL_ACT3.description"] =
            "Near the top of the Spire the table is waiting, and this time the cards on it [jitter]hum[/jitter]. Three [pink]legendary jokers[/pink] look up at you.\n\n[gold]\"For the last stretch,\"[/gold] the voice whispers. [sine]\"Choose your partner.\"[/sine]",
        ["BALATRO_DEALER.pages.INITIAL.options.SHUFFLING.title"] = "Shuffling...",
        ["BALATRO_DEALER.pages.INITIAL.options.SHUFFLING.description"] = "The Dealer is getting the cards ready.",
        ["BALATRO_DEALER.pages.INITIAL.options.PASS.title"] = "Pass",
        ["BALATRO_DEALER.pages.INITIAL.options.PASS.description"] = "Leave the table empty-handed.",
        ["BALATRO_DEALER.pages.DEALT.description"] =
            "The deck slides into your hands, warm from the shuffle. Somewhere above, the first blind is waiting.\n\n[gold]\"Good luck, Jimbo! Don't spend it all in one shop.\"[/gold]",
        ["BALATRO_DEALER.pages.DEALT_ACT2.description"] =
            "The voucher stamps itself into your run with a satisfying [jitter]ka-chunk[/jitter].\n\n[gold]\"Onward and upward!\"[/gold]",
        ["BALATRO_DEALER.pages.DEALT_ACT3.description"] =
            "The joker hops off the felt and onto your shoulder, cackling.\n\n[gold]\"Now go win the whole thing.\"[/gold]",
        ["BALATRO_DEALER.pages.PASSED.description"] = "The gloved hands shrug and sweep the cards away.",
        ["BALATRO_DEALER.pages.EMPTY.description"] = "The Dealer has nothing left to deal you. [sine]\"You've got it all already!\"[/sine]",
        ["BALATRO_DEALER.pages.TROUBLE.description"] = "The cards won't stop shuffling. Balatro isn't answering.",
        ["BALATRO_DEALER.pages.TROUBLE.options.RETRY.title"] = "Deal again",
        ["BALATRO_DEALER.pages.TROUBLE.options.RETRY.description"] = "Ask Balatro to deal once more.",

        ["BALATRO_TAG.title"] = "Skipped Blind",
        ["BALATRO_TAG.pages.INITIAL.description"] =
            "The blind's table is folded away, its chips still warm. In their place sits a little token: the [gold]{Tag}[/gold].\n\n{Text}",
        ["BALATRO_TAG.pages.INITIAL.options.ONWARD.title"] = "Pocket it",
        ["BALATRO_TAG.pages.INITIAL.options.ONWARD.description"] = "On to the next shop.",
        ["BALATRO_TAG.pages.DONE.description"] = "The tag is yours.",

        ["BALATRO_SHOP.title"] = "Shop",
        ["BALATRO_SHOP.pages.INITIAL.description"] = "The shop's sign flickers on. Jokers, planets and packs, all for sale.",
        ["BALATRO_SHOP.pages.INITIAL.options.ENTER.title"] = "Enter the shop",
        ["BALATRO_SHOP.pages.INITIAL.options.ENTER.description"] = "Browse Balatro's shop.",
        ["BALATRO_SHOP.pages.LEFT.description"] = "The shop's sign flickers off behind you.",
        ["BALATRO_SHOP.pages.TROUBLE.description"] = "The shop's shutters are stuck: Balatro isn't answering.",
        ["BALATRO_SHOP.pages.TROUBLE.options.RETRY.title"] = "Try the door again",
        ["BALATRO_SHOP.pages.TROUBLE.options.RETRY.description"] = "Ask Balatro to open the shop.",
        ["BALATRO_SHOP.pages.TROUBLE.options.LEAVE.title"] = "Move on",
        ["BALATRO_SHOP.pages.TROUBLE.options.LEAVE.description"] = "Skip this shop.",
    };

    private static readonly Dictionary<string, string> Flavors = new()
    {
        ["Voucher"] = "A voucher: a rule for the rest of the run.",
        ["Back"] = "The deck Jimbo plays with.",
        ["Stake"] = "The stake Jimbo's run is played at.",
    };

    public static LocString Scored(string hand, double score)
    {
        var line = new LocString(CardTable, "BALATRO_DEAL_IN.scored");
        line.Add("hand", hand);
        line.Add("score", double.IsFinite(score) ? score.ToString("N0", CultureInfo.CurrentCulture) : "∞");
        return line;
    }

    public static LocString NoBalatro() => new(CardTable, "BALATRO_DEAL_IN.noBalatro");

    private static string JokerRarity(int rarity) => rarity switch
    {
        1 => "A common joker.",
        2 => "An uncommon joker.",
        3 => "A rare joker.",
        _ => "A legendary joker.",
    };

    // Every Balatro item: its name (Balatro's own, once the catalog is in) and "{Text}", which
    // BalatroText fills in.
    private static (Dictionary<string, string> Relics, Dictionary<string, string> Potions) ItemEntries()
    {
        var relics = new Dictionary<string, string>();
        var potions = new Dictionary<string, string>();
        foreach (BalatroItem item in BalatroContent.All)
        {
            string entry = ModelDb.GetEntry(item.Model);
            string name = BalatroCatalog.Name(item.Key) ?? item.Name;
            if (item.IsConsumable)
            {
                potions[entry + ".title"] = name;
                potions[entry + ".description"] = "{" + BalatroText.Variable + "}";
                continue;
            }
            relics[entry + ".title"] = name;
            relics[entry + ".description"] = "{" + BalatroText.Variable + "}";
            relics[entry + ".flavor"] = item.Set == "Joker" ? JokerRarity(item.Rarity) : Flavors.GetValueOrDefault(item.Set, "");
        }
        return (relics, potions);
    }

    private static void Prefix(Dictionary<string, LocTable> tables) => Merge(name => tables.TryGetValue(name, out LocTable? t) ? t : null);

    /// <summary>Merges the text into the current language's tables (Balatro's catalog has just arrived).</summary>
    public static void Refresh()
    {
        if (LocManager.Instance == null) return;
        Merge(name =>
        {
            try
            {
                return LocManager.Instance.GetTable(name);
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        });
    }

    /// <summary>Sets one entry of a loc table to text worked out while playing (a hover tip's title, say).</summary>
    public static void SetLive(string table, string key, string text) =>
        LocManager.Instance?.GetTable(table).MergeWith(new Dictionary<string, string> { [key] = text });

    private static void Merge(System.Func<string, LocTable?> table)
    {
        (Dictionary<string, string> relics, Dictionary<string, string> potions) = ItemEntries();
        table(CardTable)?.MergeWith(Cards);
        table("relics")?.MergeWith(relics);
        table("potions")?.MergeWith(potions);
        table("characters")?.MergeWith(Characters);
        table("characters")?.MergeWith(Jimbo.JimboQuips.Lines.ToDictionary(l => l.Key, l => l.Value));
        table("events")?.MergeWith(Events);
        table("ancients")?.MergeWith(JimboArchitect.Lines);
    }
}
