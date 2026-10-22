using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Jimbo;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace BalatroBridge.Content;

/// <summary>
/// A relic that is a Balatro item: a joker, a voucher, a deck or a stake. Event rarity keeps it
/// out of StS2's own relic rolls; Balatro decides who has them. Its icon is the item's Balatro
/// art and its text is Balatro's own (see <see cref="BalatroCatalog"/>): the loc description is
/// "{Text}", filled in by <see cref="BalatroText"/> from the live Balatro run when there is one.
/// </summary>
public abstract class BalatroRelic : RelicModel
{
    internal BalatroItem Item => BalatroContent.ItemFor(GetType());

    /// <summary>Balatro's key, e.g. "j_joker".</summary>
    public string BalatroKey => Item.Key;

    /// <summary>The text Balatro last gave for this very card (current values), if any.</summary>
    internal string? LiveText { get; set; }

    public override RelicRarity Rarity => RelicRarity.Event;

    public override bool IsAllowedInShops => false;

    public override string PackedIconPath => BalatroIcons.PathFor(Item, BalatroIcons.Kind.Small);

    protected override string PackedIconOutlinePath => BalatroIcons.PathFor(Item, BalatroIcons.Kind.Outline);

    protected override string BigIconPath => BalatroIcons.PathFor(Item, BalatroIcons.Kind.Big);
}

/// <summary>
/// A Balatro joker. In Jimbo's runs Balatro's jokers are the source of truth and this relic
/// mirrors the joker card <see cref="CardId"/>; with Deal In (other characters) the relic is the
/// source and Balatro creates the joker.
/// </summary>
public abstract class JokerRelic : BalatroRelic
{
    private int _cardId;

    /// <summary>The Balatro card (sort_id) this relic mirrors in a Jimbo run; 0 if none.</summary>
    [SavedProperty]
    public int CardId
    {
        get => _cardId;
        set
        {
            AssertMutable();
            _cardId = value;
        }
    }

    public override bool IsStackable => false;

    /// <summary>
    /// The joker's changing number as its text writes it ("+7", "X2.2", "$4"), shown on the relic's
    /// counter (see JimboRelicCounter); null for jokers whose numbers never change.
    /// </summary>
    internal string? LiveValue { get; private set; }

    public override bool ShowCounter => LiveValue != null;

    public override int DisplayAmount => int.TryParse(new string((LiveValue ?? "").Where(char.IsDigit).ToArray()), out int n) ? n : 0;

    /// <summary>The loc entry of a face-down joker's title (ModText adds it).</summary>
    internal const string FaceDownEntry = "BALATRO_FACE_DOWN_JOKER";

    /// <summary>
    /// Flipped face down (Amber Acorn): the relic hides which joker it is, as Balatro's card does,
    /// until the fight ends. It still scores.
    /// </summary>
    internal bool FaceDown { get; set; }

    public override LocString Title => FaceDown ? new LocString("relics", FaceDownEntry + ".title") : base.Title;

    internal void SetLive(string? value)
    {
        if (value == LiveValue) return;
        LiveValue = value;
        InvokeDisplayAmountChanged();
    }

    // Mr. Bones in Jimbo's runs: Balatro's "prevents death" happens in StS2, as Lizard Tail's does
    // (see JimboCombat.MrBonesWouldSave). Every other joker lets death be.
    public override bool ShouldDieLate(Creature creature) =>
        creature != Owner?.Creature || !JimboCombat.MrBonesWouldSave(this);

    public override async Task AfterPreventingDeath(Creature creature)
    {
        Flash();
        await JimboCombat.SavedByMrBones(Owner!, creature);
    }
}

/// <summary>A Balatro voucher Jimbo has redeemed: a permanent rule for the rest of the run.</summary>
public abstract class VoucherRelic : BalatroRelic;

/// <summary>The Balatro deck Jimbo plays with, chosen from the Dealer at the start of the run.</summary>
public abstract class DeckRelic : BalatroRelic;

/// <summary>
/// Jimbo's starting relic: the Balatro stake his run is played at (from the ascension level). It
/// also carries the rules that turn an StS2 run into a Balatro run, as StS2 hooks.
/// </summary>
public abstract class StakeRelic : BalatroRelic
{
    private string _skippedTag = "";

    public override RelicRarity Rarity => RelicRarity.Starter;

    /// <summary>The tag Jimbo took for the blind he skipped last (shown at its map node).</summary>
    [SavedProperty]
    public string SkippedTag
    {
        get => _skippedTag;
        set
        {
            AssertMutable();
            _skippedTag = value;
        }
    }

    public override ActMap ModifyGeneratedMap(IRunState runState, ActMap map, int actIndex) =>
        JimboRun.IsJimbo(Owner) ? new JimboActMap(runState, actIndex) : map;

    public override Task BeforeCombatStart() => JimboRun.IsJimbo(Owner) ? JimboCombat.Begin(Owner) : Task.CompletedTask;

    public override Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player) =>
        player == Owner && JimboRun.IsJimbo(Owner) ? JimboCombat.TurnStart(Owner, choiceContext) : Task.CompletedTask;

    public override Task AfterCreatureAddedToCombat(Creature creature) =>
        JimboRun.IsJimbo(Owner) ? JimboCombat.CreatureAdded(creature) : Task.CompletedTask;

    // Status cards an enemy gives Jimbo join his Balatro deck, and act at the end of his turn.
    public override Task AfterCardEnteredCombat(CardModel card) =>
        JimboRun.IsJimbo(Owner) ? JimboStatuses.Entered(Owner, card) : Task.CompletedTask;

    public override Task BeforeSideTurnEnd(PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants) =>
        side == CombatSide.Player && JimboRun.IsJimbo(Owner) ? JimboStatuses.TurnEnd(Owner, choiceContext) : Task.CompletedTask;

    public override Task AfterCombatVictory(CombatRoom room) =>
        JimboRun.IsJimbo(Owner) ? JimboCombat.Won(Owner, room) : Task.CompletedTask;

    // The map waits for the cash-out to be collected.
    public override bool ShouldProceedToNextMapPoint() => !(JimboRun.IsJimbo(Owner) && JimboCombat.CashingOut);

    public override bool TryModifyRewards(Player player, List<Reward> rewards, AbstractRoom? room)
    {
        // Jimbo's rewards are Balatro's: cashing out, the shop, packs and tags.
        if (player != Owner || !JimboRun.IsJimbo(Owner) || rewards.Count == 0) return false;
        rewards.Clear();
        return true;
    }

    public override decimal ModifyMaxEnergy(Player player, decimal amount) =>
        player == Owner && JimboRun.IsJimbo(Owner) ? 0 : amount;

    // Jimbo has no StS2 cards to draw; his hand is Balatro's.
    public override bool ShouldDraw(Player player, bool fromHandDraw) => player != Owner || !JimboRun.IsJimbo(Owner);

    public override decimal ModifyHandDraw(Player player, decimal count) =>
        player == Owner && JimboRun.IsJimbo(Owner) ? 0 : count;

    // Enemy Block grows with enemy HP (see JimboScaling).
    public override decimal ModifyBlockMultiplicative(Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay) =>
        target.IsEnemy && JimboRun.IsJimbo(Owner) && Jimbo.JimboScaling.Scale is decimal scale ? scale : 1m;

    // Throwing a consumable's potion away sells the card.
    public override Task AfterPotionDiscarded(PotionModel potion) =>
        JimboRun.IsJimbo(Owner) && potion.Owner == Owner ? JimboConsumables.Discarded(potion) : Task.CompletedTask;
}
