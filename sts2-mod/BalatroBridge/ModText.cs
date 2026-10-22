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
        ["BALATRO_DEALER.pages.INITIAL.options.PASS.description"] = "Leave.",
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

        ["BALATRO_WILD_CARD.title"] =
            "A Wild Card",
        ["BALATRO_WILD_CARD.pages.INITIAL.description"] =
            "Between two guttering torches stands a card table that has no business being in the Spire. Behind it, a playing card in a jester's cap riffles a deck with hands you can't quite see.\n\n[gold]\"Well, well! A customer!\"[/gold] [sine]\"Name's Jimbo. I deal a little game called Balatro.\"[/sine]\n\nHe slides a single card across the felt, face up: [gold]Deal In[/gold].",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.DEAL.title"] =
            "\"Deal me in.\"",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.DEAL.description"] =
            "Add [gold]Deal In[/gold] to your [gold]Deck[/gold].",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.DOUBLE.title"] =
            "\"Double or nothing.\"",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.DOUBLE.description"] =
            "50%: Add an [gold]Upgraded[/gold] [gold]Deal In[/gold] to your [gold]Deck[/gold] and obtain a random [gold]Joker[/gold]. Otherwise, take [red]{Damage}[/red] damage.",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.CATCH.title"] =
            "\"What's the catch?\"",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.CATCH.description"] =
            "Ask about the card.",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.LEAVE.title"] =
            "Walk away.",
        ["BALATRO_WILD_CARD.pages.INITIAL.options.LEAVE.description"] =
            "Leave.",
        ["BALATRO_WILD_CARD.pages.CATCH.description"] =
            "Jimbo cackles, a sound like a deck being bridged.\n\n[gold]\"Catch? No catch! Well... one teeny one.\"[/gold] Every time you play that card, you play a hand of [gold]Balatro[/gold]: five cards, [color=#009DFF]chips[/color] times [color=#FE5F55]mult[/color], [color=#FF9A00]jokers[/color] bending every rule. [gold]\"The bigger the hand, the harder it hits. Beat a blind and my shop opens up.\"[/gold]\n\nHe leans over the felt. [sine]\"And between you and me, there's a house special.\"[/sine]",
        ["BALATRO_WILD_CARD.pages.CATCH.options.DEAL.title"] =
            "\"Deal me in.\"",
        ["BALATRO_WILD_CARD.pages.CATCH.options.DEAL.description"] =
            "Add [gold]Deal In[/gold] to your [gold]Deck[/gold].",
        ["BALATRO_WILD_CARD.pages.CATCH.options.DOUBLE.title"] =
            "\"Double or nothing.\"",
        ["BALATRO_WILD_CARD.pages.CATCH.options.DOUBLE.description"] =
            "50%: Add an [gold]Upgraded[/gold] [gold]Deal In[/gold] to your [gold]Deck[/gold] and obtain a random [gold]Joker[/gold]. Otherwise, take [red]{Damage}[/red] damage.",
        ["BALATRO_WILD_CARD.pages.CATCH.options.SPECIAL.title"] =
            "The house special. ({Gold} Gold)",
        ["BALATRO_WILD_CARD.pages.CATCH.options.SPECIAL.description"] =
            "Pay [red]{Gold}[/red] [gold]Gold[/gold]. Add [gold]Deal In[/gold] to your [gold]Deck[/gold] and obtain a random uncommon [gold]Joker[/gold].",
        ["BALATRO_WILD_CARD.pages.CATCH.options.SPECIAL_LOCKED.title"] =
            "The house special. (Locked)",
        ["BALATRO_WILD_CARD.pages.CATCH.options.SPECIAL_LOCKED.description"] =
            "Requires [blue]{Gold}[/blue] [gold]Gold[/gold].",
        ["BALATRO_WILD_CARD.pages.CATCH.options.LEAVE.title"] =
            "Walk away.",
        ["BALATRO_WILD_CARD.pages.CATCH.options.LEAVE.description"] =
            "Leave.",
        ["BALATRO_WILD_CARD.pages.DEALT.description"] =
            "Jimbo flicks the card across the felt and it lands in your deck with a soft [jitter]thwip[/jitter].\n\n[gold]\"Every hand you play with that, you're playing with me. Don't make me look bad!\"[/gold]\n\nWhen you look up, the table is gone. Just a few chips on the floor.",
        ["BALATRO_WILD_CARD.pages.WON.description"] =
            "Two cards hit the felt, face down. Jimbo flips them with a flourish: your [gold]{Yours}[/gold] towers over his [red]{Theirs}[/red].\n\n[gold]\"Beginner's luck!\"[/gold] He slides over [gold]Deal In[/gold], already sharpened, and a little [gold]{Joker}[/gold] hops out of the deck and onto your shoulder. [sine]\"Take him. He likes you.\"[/sine]",
        ["BALATRO_WILD_CARD.pages.LOST.description"] =
            "Two cards hit the felt, face down. Jimbo flips them with a flourish: your [red]{Yours}[/red] against his [gold]{Theirs}[/gold].\n\nThe deck snaps shut on your fingers. [gold]\"Ooh, tough break! The house always wins.\"[/gold]\n\n[sine]\"Well... usually.\"[/sine] And with a wink, he and the table are gone.",
        ["BALATRO_WILD_CARD.pages.SPECIAL.description"] =
            "You count out the gold. Jimbo bites a coin, grins, and deals you [gold]Deal In[/gold] with a [gold]{Joker}[/gold] tucked behind it.\n\n[gold]\"Pleasure doing business! Tell your friends.\"[/gold] [sine]\"Actually, don't.\"[/sine]",
        ["BALATRO_WILD_CARD.pages.LEFT.description"] =
            "Jimbo shrugs. [gold]\"Suit yourself!\"[/gold] He fans the deck one last time and folds himself into it like a bad hand.\n\n[sine]\"The table's always open... somewhere.\"[/sine]",

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
        // What Jimbo says names Balatro's things in Balatro's colours (Mult red, Chips blue...).
        table("characters")?.MergeWith(Jimbo.JimboQuips.Lines.ToDictionary(l => l.Key, l => BalatroTerms.Colour(l.Value)));
        table("events")?.MergeWith(Events.ToDictionary(e => e.Key, e => JimboSpeaks.IsMatch(e.Key) ? BalatroTerms.ColourQuotes(e.Value) : e.Value));
        table("ancients")?.MergeWith(JimboArchitect.Lines.ToDictionary(l => l.Key, l => l.Key.EndsWith(".char") ? BalatroTerms.Colour(l.Value) : l.Value));
    }

    // A Wild Card's pages, where Jimbo's lines are the quoted parts (the options are the player's).
    private static readonly System.Text.RegularExpressions.Regex JimboSpeaks = new(@"^BALATRO_WILD_CARD\.pages\.[A-Z_]+\.description$");
}
