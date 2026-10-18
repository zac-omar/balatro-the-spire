using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Core;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// The Dealer: Jimbo's Ancient, waiting at the start of every act. In the first act it deals
/// him a choice of three Balatro decks, and the chosen deck starts his Balatro run. Later acts
/// open with a gift: a voucher (act 2) or a legendary joker (act 3), and Jimbo heals up as he
/// would at any act's Ancient.
/// </summary>
public sealed class BalatroDealer : EventModel
{
    private const string Key = "BALATRO_DEALER";
    private const int Choices = 3;

    protected override IReadOnlyList<EventOption> GenerateInitialOptions() =>
        [new EventOption(this, null, $"{Key}.pages.INITIAL.options.SHUFFLING")];

    public override LocString InitialDescription => L10NLookup($"{Key}.pages.INITIAL{ActSuffix}.description");

    private string ActSuffix => Owner?.RunState.CurrentActIndex switch
    {
        null or 0 => "",
        1 => "_ACT2",
        _ => "_ACT3",
    };

    protected override async Task BeforeEventStarted(bool isPreFinished)
    {
        if (isPreFinished || Owner == null || Owner.RunState.CurrentActIndex == 0) return;
        decimal missing = Owner.Creature.MaxHp - Owner.Creature.CurrentHp;
        if (RunManager.Instance.HasAscension(AscensionLevel.WearyTraveler)) missing *= 0.8m;
        if (missing > 0) await CreatureCmd.Heal(Owner.Creature, missing);
    }

    public override Task AfterEventStarted()
    {
        if (!IsFinished) TaskHelper.RunSafely(Deal());
        return Task.CompletedTask;
    }

    /// <summary>Works out the offers (asking Balatro for later acts' gifts) and shows them.</summary>
    private async Task Deal()
    {
        try
        {
            IReadOnlyList<RelicModel> offers = await Offers();
            if (offers.Count == 0)
            {
                SetEventFinished(L10NLookup($"{Key}.pages.EMPTY.description"));
                return;
            }
            List<EventOption> options = offers.Select(relic => EventOption.FromRelic(relic, this, () => Choose(relic), $"{Key}.pages.INITIAL.options.{relic.Id.Entry}")).ToList();
            if (Owner!.RunState.CurrentActIndex > 0) options.Add(new EventOption(this, Pass, $"{Key}.pages.INITIAL.options.PASS"));
            SetEventState(InitialDescription, options);
            Speak(InitialDescription);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"the Dealer couldn't deal: {e}");
            SetEventState(L10NLookup($"{Key}.pages.TROUBLE.description"), [new EventOption(this, Retry, $"{Key}.pages.TROUBLE.options.RETRY")]);
        }
    }

    private async Task Retry()
    {
        SetEventState(InitialDescription, GenerateInitialOptions());
        // A deck already dealt (Balatro failed to start the run with it) is dealt again.
        if (Owner!.RunState.CurrentActIndex == 0 && Owner.Relics.OfType<DeckRelic>().FirstOrDefault() is DeckRelic dealt)
        {
            await Choose(dealt);
            return;
        }
        await Deal();
    }

    private async Task<IReadOnlyList<RelicModel>> Offers()
    {
        int act = Owner!.RunState.CurrentActIndex;
        if (act == 0)
        {
            // Three of Balatro's decks.
            List<BalatroItem> decks = BalatroContent.OfSet("Back").OrderBy(d => d.Order).ToList();
            Rng.Shuffle(decks);
            return decks.Take(Choices).Select(d => ModelDb.GetById<RelicModel>(ModelDb.GetId(d.Model)).ToMutable()).ToList();
        }
        BalatroSession session = await JimboTable.Connect();
        IReadOnlyList<string> keys = await session.JimboOffers(JimboRun.LinkFor(Owner), act == 1 ? "voucher" : "legendary", Choices);
        return keys.Select(BalatroContent.ModelFor<RelicModel>).OfType<RelicModel>().Select(r => r.ToMutable()).ToList();
    }

    private async Task Choose(RelicModel relic)
    {
        try
        {
            BalatroSession session = await JimboTable.Connect();
            TableSnapshot table;
            if (relic is DeckRelic)
            {
                // The deck is dealt: Jimbo's Balatro run begins with it.
                if (!Owner!.Relics.Contains(relic)) await RelicCmd.Obtain(relic, Owner);
                table = await session.JimboNewRun(JimboRun.LinkFor(Owner!));
            }
            else
            {
                table = await session.JimboGrant(JimboRun.LinkFor(Owner!), ((BalatroRelic)relic).BalatroKey);
            }
            await JimboRun.Apply(Owner!, table);
            LocString dealt = L10NLookup($"{Key}.pages.DEALT{ActSuffix}.description");
            SetEventFinished(dealt);
            Speak(dealt);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"the Dealer's deal failed: {e}");
            SetEventState(L10NLookup($"{Key}.pages.TROUBLE.description"), [new EventOption(this, Retry, $"{Key}.pages.TROUBLE.options.RETRY")]);
        }
    }

    // The Dealer talks as Balatro's Jimbo does, for as long as its line runs (the narration
    // types itself out first, so the voice comes in a moment later).
    private static void Speak(LocString description) => JimboSounds.Say(JimboSounds.Spoken(description.GetFormattedText()), delay: 0.5);

    private Task Pass()
    {
        SetEventFinished(L10NLookup($"{Key}.pages.PASSED.description"));
        return Task.CompletedTask;
    }
}
