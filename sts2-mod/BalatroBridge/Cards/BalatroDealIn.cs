using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BalatroBridge.Core;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace BalatroBridge.Cards;

/// <summary>
/// Deal In (1 energy, colorless attack): play your next hand of Balatro, then deal a percentage
/// of its score as damage. The Balatro run carries on between plays, so it grows with you.
/// Card id: BALATRO_DEAL_IN (spawn it with the dev console: card BALATRO_DEAL_IN).
/// </summary>
public sealed class BalatroDealIn : CardModel
{
    private const string PercentVar = "Percent";
    private const decimal MaxDamage = 9999m;

    public BalatroDealIn()
        : base(1, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy)
    {
    }

    protected override IEnumerable<DynamicVar> CanonicalVars => [new IntVar(PercentVar, 10m)];

    // Borrows Jackpot's art until the mod ships its own.
    public override string PortraitPath => ImageHelper.GetImagePath("atlases/card_atlas.sprites/colorless/jackpot.tres");

    protected override string PortraitPngPath => ImageHelper.GetImagePath("packed/card_portraits/colorless/jackpot.png");

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target, "cardPlay.Target");

        // An exception escaping OnPlay stalls StS2's whole action queue (the fight freezes), so
        // anything that goes wrong here fizzles the card instead.
        try
        {
            (ScoredHand? hand, string? problem) = await BalatroTable.PlayHand(cardPlay.Target, Owner, DamageFor);
            if (hand == null)
            {
                BridgeMod.LogError($"Deal In fizzled: {problem}");
                Say(ModText.NoBalatro());
                return;
            }

            Say(ModText.Scored(hand.Hand, hand.Score));
            decimal damage = DamageFor(hand.Score);
            if (damage <= 0) return;
            await DamageCmd.Attack(damage).FromCard(this, cardPlay).Targeting(cardPlay.Target)
                .WithHitFx("vfx/vfx_attack_slash")
                .Execute(choiceContext);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"Deal In failed: {e}");
        }
    }

    protected override void OnUpgrade()
    {
        DynamicVars[PercentVar].UpgradeValueBy(5m);
    }

    private decimal DamageFor(double score)
    {
        if (double.IsNaN(score) || score <= 0) return 0;
        double damage = Math.Floor(score * (double)DynamicVars[PercentVar].BaseValue / 100);
        return damage >= (double)MaxDamage ? MaxDamage : (decimal)damage;
    }

    private void Say(LocString line) => TalkCmd.Play(line, Owner.Creature, VfxColor.White, VfxDuration.Standard);
}
