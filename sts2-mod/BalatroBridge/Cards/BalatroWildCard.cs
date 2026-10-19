using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Jimbo;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace BalatroBridge.Cards;

/// <summary>
/// A Wild Card: in other characters' runs, Jimbo sets up a card table in a ? room and offers
/// <see cref="BalatroDealIn"/>, the card that plays a hand of Balatro. It's the only place to get
/// it. Take it, gamble for a better one (an upgraded copy and a joker, or a bite out of your HP),
/// or ask what the catch is and hear about his house special (a joker too, for gold).
/// </summary>
public sealed class BalatroWildCard : EventModel
{
    private const string Key = "BALATRO_WILD_CARD";
    private const int SpecialCost = 60;
    private const int LosingBite = 8;

    private static readonly string[] Ranks = ["Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Jack", "Queen", "King", "Ace"];
    private static readonly string[] Suits = ["Spades", "Hearts", "Clubs", "Diamonds"];

    protected override IEnumerable<DynamicVar> CanonicalVars =>
        [new GoldVar(SpecialCost), new DamageVar(LosingBite, ValueProp.Unblockable | ValueProp.Unpowered)];

    // Never in Jimbo's own runs, and he deals one table per climb.
    public override bool IsAllowed(IRunState runState) =>
        !JimboRun.IsJimboRun(runState) && !runState.MapPointHistory.SelectMany(act => act).Any(point => point.Rooms.Any(room => room.ModelId == Id));

    protected override IReadOnlyList<EventOption> GenerateInitialOptions() =>
    [
        new EventOption(this, Deal, $"{Key}.pages.INITIAL.options.DEAL", HoverTipFactory.FromCardWithCardHoverTips<BalatroDealIn>()),
        new EventOption(this, DoubleOrNothing, $"{Key}.pages.INITIAL.options.DOUBLE", HoverTipFactory.FromCardWithCardHoverTips<BalatroDealIn>()),
        new EventOption(this, Catch, $"{Key}.pages.INITIAL.options.CATCH"),
        new EventOption(this, Leave, $"{Key}.pages.INITIAL.options.LEAVE"),
    ];

    public override Task AfterEventStarted()
    {
        if (!IsFinished) Speak(InitialDescription);
        return Task.CompletedTask;
    }

    // "What's the catch?": Jimbo explains himself, and lets the house special slip.
    private Task Catch()
    {
        LocString pitch = L10NLookup($"{Key}.pages.CATCH.description");
        bool affordable = Owner!.Gold >= SpecialCost;
        SetEventState(pitch,
        [
            new EventOption(this, Deal, $"{Key}.pages.CATCH.options.DEAL", HoverTipFactory.FromCardWithCardHoverTips<BalatroDealIn>()),
            new EventOption(this, DoubleOrNothing, $"{Key}.pages.CATCH.options.DOUBLE", HoverTipFactory.FromCardWithCardHoverTips<BalatroDealIn>()),
            affordable
                ? new EventOption(this, Special, $"{Key}.pages.CATCH.options.SPECIAL", HoverTipFactory.FromCardWithCardHoverTips<BalatroDealIn>())
                : new EventOption(this, null, $"{Key}.pages.CATCH.options.SPECIAL_LOCKED"),
            new EventOption(this, Leave, $"{Key}.pages.CATCH.options.LEAVE"),
        ]);
        Speak(pitch);
        return Task.CompletedTask;
    }

    private async Task Deal()
    {
        await GiveDealIn(upgraded: false);
        Finish(L10NLookup($"{Key}.pages.DEALT.description"));
    }

    // A card each, face down: the higher card wins.
    private async Task DoubleOrNothing()
    {
        bool won = Rng.NextBool();
        int low = Rng.NextInt(12), high = low + 1 + Rng.NextInt(12 - low);
        string Card(int rank) => $"{Ranks[rank]} of {Suits[Rng.NextInt(Suits.Length)]}";
        string yours = Card(won ? high : low), theirs = Card(won ? low : high);
        LocString result;
        if (won)
        {
            await GiveDealIn(upgraded: true);
            result = L10NLookup($"{Key}.pages.WON.description");
            result.Add("Joker", await GiveJoker(rarity: 1));
        }
        else
        {
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), Owner!.Creature, DynamicVars.Damage, null, null, null);
            result = L10NLookup($"{Key}.pages.LOST.description");
        }
        result.Add("Yours", yours);
        result.Add("Theirs", theirs);
        Finish(result);
    }

    // The house special: the card and an uncommon joker, for gold.
    private async Task Special()
    {
        await PlayerCmd.LoseGold(SpecialCost, Owner!, GoldLossType.Spent);
        await GiveDealIn(upgraded: false);
        LocString sold = L10NLookup($"{Key}.pages.SPECIAL.description");
        sold.Add("Joker", await GiveJoker(rarity: 2));
        Finish(sold);
    }

    private Task Leave()
    {
        Finish(L10NLookup($"{Key}.pages.LEFT.description"));
        return Task.CompletedTask;
    }

    private async Task GiveDealIn(bool upgraded)
    {
        CardModel card = Owner!.RunState.CreateCard<BalatroDealIn>(Owner);
        if (upgraded) CardCmd.Upgrade(card);
        CardCmd.PreviewCardPileAdd(await CardPileCmd.Add(card, PileType.Deck), 2f);
    }

    // A joker of that rarity the player doesn't have yet (it joins Deal In's Balatro run). Returns its name.
    private async Task<string> GiveJoker(int rarity)
    {
        HashSet<string> owned = Owner!.Relics.OfType<JokerRelic>().Select(r => r.BalatroKey).ToHashSet();
        List<BalatroItem> jokers = BalatroContent.OfSet("Joker").Where(j => j.Rarity == rarity && !owned.Contains(j.Key)).OrderBy(j => j.Order).ToList();
        if (jokers.Count == 0 || BalatroContent.ModelFor<RelicModel>(jokers[Rng.NextInt(jokers.Count)].Key) is not RelicModel joker) return "joker";
        RelicModel relic = joker.ToMutable();
        await RelicCmd.Obtain(relic, Owner);
        return relic.Title.GetFormattedText();
    }

    private void Finish(LocString page)
    {
        SetEventFinished(page);
        Speak(page);
    }

    // Jimbo talks as Balatro's Jimbo does, for as long as his line runs.
    private static void Speak(LocString page) => JimboSounds.Say(JimboSounds.Spoken(page.GetFormattedText()), delay: 0.5);
}

/// <summary>
/// Jimbo's table turns up in the first or second act's ? rooms, near the front of the act's
/// events, in some runs (never in Jimbo's own). Its odds come from the run's seed, so a seed
/// always plays out the same.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
internal static class WildCardInTheSpire
{
    private const int ChancePerAct = 40;
    private static readonly AccessTools.FieldRef<ActModel, RoomSet> Rooms = AccessTools.FieldRefAccess<ActModel, RoomSet>("_rooms");

    private static void Postfix(RunManager __instance)
    {
        try
        {
            RunState? state = __instance.DebugOnlyGetState();
            if (state == null || JimboRun.IsJimboRun(state)) return;
            var rng = new Rng(state.Rng.Seed ^ 0x4A494D424FUL); // its own stream, so the rest of the run is untouched
            EventModel table = ModelDb.Event<BalatroWildCard>();
            for (int i = 0; i < Math.Min(2, state.Acts.Count); i++)
            {
                if (rng.NextInt(100) >= ChancePerAct) continue;
                List<EventModel> events = Rooms(state.Acts[i]).events;
                int at = rng.NextInt(Math.Min(3, events.Count + 1));
                events.Insert(at, table);
                BridgeMod.LogInfo($"Jimbo's table is act {i + 1}'s event #{at + 1}");
            }
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"Jimbo's table couldn't join the Spire's events: {e.Message}");
        }
    }
}

/// <summary>The table is one of the Spire's events (StS2's event console command finds it too).</summary>
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.AllEvents), MethodType.Getter)]
internal static class WildCardIsAnEvent
{
    private static void Postfix(ref IEnumerable<EventModel> __result)
    {
        if (!__result.Any(e => e is BalatroWildCard)) __result = __result.Append(ModelDb.Event<BalatroWildCard>());
    }
}
