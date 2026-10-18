using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Status and curse cards an enemy gives Jimbo (Wound, Burn, Slimed, Dazed...) join his Balatro
/// deck for the fight, where they're drawn, clog his hand and cycle like any card: each is a dud
/// in Balatro (a debuffed Stone Card showing the StS2 card) that never scores. StS2 keeps the
/// card's own rules: one in his hand when his turn ends does what it does in StS2 (Burn burns),
/// an Ethereal one then burns away, and an Exhaust one is gone once discarded or played. They all
/// leave his deck when the fight does. Balatro's side is in balatro-mod/bridge/jimbo.lua.
/// </summary>
internal static class JimboStatuses
{
    private static readonly Dictionary<int, CardModel> Cards = new();

    /// <summary>How many times a status card has acted at the end of a turn (for the audit).</summary>
    public static int Acted { get; private set; }
    private static int _next;

    /// <summary>A new fight: no status cards yet.</summary>
    public static void Reset()
    {
        Cards.Clear();
        CardView.StatusArt = Art;
    }

    private static Texture2D? Art(int dud) =>
        Cards.TryGetValue(dud, out CardModel? card) && ResourceLoader.Exists(card.PortraitPath) ? card.Portrait : null;

    /// <summary>A card entered Jimbo's StS2 piles mid-fight: a status or curse joins his Balatro deck.</summary>
    public static async Task Entered(Player player, CardModel card)
    {
        if (card.Owner != player || card.Type is not (CardType.Status or CardType.Curse) || JimboCombat.HpScale == null) return;
        try
        {
            int dud = ++_next;
            Cards[dud] = card;
            PileType pile = card.Pile?.Type ?? PileType.Draw;
            // Its StS2 copy waits in the discard pile: Jimbo's real hand is Balatro's.
            if (pile == PileType.Hand) await CardPileCmd.Add(card, PileType.Discard);
            var spec = new JsonObject
            {
                ["ref"] = dud,
                ["name"] = card.Title,
                ["text"] = Describe(card),
                ["pile"] = pile switch { PileType.Hand => "hand", PileType.Discard => "discard", _ => "draw" },
                ["exhaust"] = card.Keywords.Contains(CardKeyword.Exhaust),
            };
            TableSnapshot table = await JimboRun.Session().JimboDuds(JimboRun.LinkFor(player), new JsonArray(spec), []);
            await JimboRun.Apply(player, table);
            if (JimboHand.Current is JimboHand hand) await hand.Deal(table);
            BridgeMod.LogInfo($"jimbo: {card.Title} joined the deck ({spec["pile"]})");
            JimboQuips.Maybe(player, JimboQuips.Moment.Status);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: {card.Title} couldn't join the Balatro deck: {e.Message}");
        }
    }

    /// <summary>Jimbo's turn ends: status cards in his hand act, and Ethereal ones burn away.</summary>
    public static async Task TurnEnd(Player player, PlayerChoiceContext choiceContext)
    {
        if (Cards.Count == 0 || JimboRun.Last is not { InFight: true } table) return;
        var burned = new List<int>();
        foreach (int dud in table.Hand.Select(c => c.Dud).OfType<int>().Where(Cards.ContainsKey).ToList())
        {
            CardModel card = Cards[dud];
            if (ActsAtTurnEnd(card))
            {
                BridgeMod.LogInfo($"jimbo: {card.Title} acts at the end of the turn");
                Acted++;
                await card.OnTurnEndInHandWrapper(choiceContext);
            }
            if (card.Keywords.Contains(CardKeyword.Ethereal)) burned.Add(dud);
        }
        if (burned.Count == 0) return;
        try
        {
            TableSnapshot after = await JimboRun.Session().JimboDuds(JimboRun.LinkFor(player), [], burned);
            await JimboRun.Apply(player, after);
            if (JimboHand.Current is JimboHand hand) await hand.Deal(after);
            foreach (int dud in burned)
            {
                if (Cards.Remove(dud, out CardModel? card) && card.Pile != null) await CardCmd.Exhaust(choiceContext, card, causedByEthereal: true);
            }
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: ethereal cards didn't burn away: {e.Message}");
        }
    }

    // Whether the card does something of its own at the end of the turn in hand (Burn, Wither...).
    private static bool ActsAtTurnEnd(CardModel card) =>
        card.GetType().GetMethod("OnTurnEndInHand", BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType != typeof(CardModel);

    // What the dud's tooltip says: StS2's own words where they still apply, then how it lives in Jimbo's deck.
    private static string Describe(CardModel card)
    {
        var lines = new List<string>();
        if (ActsAtTurnEnd(card)) lines.Add(card.GetDescriptionForPile(PileType.Hand));
        lines.Add("[color=#9aa0a6]A status card: it never scores, and takes up room in hand.[/color]");
        if (card.Keywords.Contains(CardKeyword.Ethereal)) lines.Add("Burns away at the end of your turn.");
        if (card.Keywords.Contains(CardKeyword.Exhaust)) lines.Add("Gone once discarded or played.");
        lines.Add("[color=#9aa0a6]Leaves your deck when the fight ends.[/color]");
        return string.Join("\n", lines);
    }
}
