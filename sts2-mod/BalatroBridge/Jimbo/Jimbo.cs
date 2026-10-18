using System;
using System.Collections.Generic;
using System.Linq;
using BalatroBridge.Content;
using Godot;
using MegaCrit.Sts2.Core.Entities.Characters;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo, Balatro's jester, as StS2's fifth character. He has no StS2 cards, energy, rest sites
/// or events: his run is a Balatro run (see docs/JIMBO.md). His art and scenes come from the
/// mod's resource pack (tools/build_mod_pck.py), his text from <see cref="ModText"/>.
/// </summary>
public sealed class Jimbo : CharacterModel
{
    public static readonly Color Red = new("FE5F55");
    public static readonly Color Blue = new("009DFF");

    public override CharacterGender Gender => CharacterGender.Masculine;

    protected override CharacterModel? UnlocksAfterRunAs => null;

    public override Color NameColor => Red;

    public override int StartingHp => 70;

    /// <summary>Balatro's starting $4; StS2's gold is Balatro's money throughout the run.</summary>
    public override int StartingGold => 4;

    public override int MaxEnergy => 0;

    public override CardPoolModel CardPool => ModelDb.CardPool<JimboCardPool>();

    public override RelicPoolModel RelicPool => ModelDb.RelicPool<JimboRelicPool>();

    public override PotionPoolModel PotionPool => ModelDb.PotionPool<JimboPotionPool>();

    public override IEnumerable<CardModel> StartingDeck => Array.Empty<CardModel>();

    /// <summary>White Stake; a run at a higher ascension swaps in its stake (see <see cref="JimboPatches"/>).</summary>
    public override IReadOnlyList<RelicModel> StartingRelics => [ModelDb.Relic<BalatroWhiteStake>()];

    public override float AttackAnimDelay => 0.15f;

    public override float CastAnimDelay => 0.25f;

    public override Color EnergyLabelOutlineColor => new("1B3A5CFF");

    public override Color DialogueColor => new("2B1A3D");

    public override VfxColor SpeechBubbleColor => VfxColor.Red;

    public override Color MapDrawingColor => Red;

    public override Color RemoteTargetingLineColor => Red;

    public override Color RemoteTargetingLineOutline => new("7A1F1AFF");

    // Jimbo's sounds are Balatro's: JimboPatches plays them in place of these FMOD events.
    public override string CharacterSelectSfx => JimboSounds.SelectEvent;

    public override string CharacterTransitionSfx => JimboSounds.TransitionEvent;

    public override List<string> GetArchitectAttackVfx() =>
        ["vfx/vfx_attack_slash", "vfx/vfx_flying_slash", "vfx/vfx_attack_blunt", "vfx/vfx_heavy_blunt", "vfx/vfx_attack_lightning"];
}

/// <summary>Jimbo plays Balatro cards, not StS2 ones: his card pool is empty.</summary>
public sealed class JimboCardPool : CardPoolModel
{
    public override string Title => "jimbo";

    public override string EnergyColorName => "colorless";

    public override string CardFrameMaterialPath => "card_frame_colorless";

    public override Color DeckEntryCardColor => Jimbo.Red;

    public override bool IsColorless => false;

    protected override CardModel[] GenerateAllCards() => [];
}

/// <summary>Every Balatro relic: jokers, vouchers, decks and stakes.</summary>
public sealed class JimboRelicPool : RelicPoolModel
{
    public override string EnergyColorName => "colorless";

    public override Color LabOutlineColor => Jimbo.Red;

    protected override IEnumerable<RelicModel> GenerateAllRelics() =>
        BalatroContent.All.Where(i => typeof(RelicModel).IsAssignableFrom(i.Model))
            .Select(i => ModelDb.GetById<RelicModel>(ModelDb.GetId(i.Model)));
}

/// <summary>Every Balatro consumable: tarots, planets and spectrals.</summary>
public sealed class JimboPotionPool : PotionPoolModel
{
    public override string EnergyColorName => "colorless";

    public override Color LabOutlineColor => Jimbo.Blue;

    protected override IEnumerable<PotionModel> GenerateAllPotions() =>
        BalatroContent.Consumables.Select(i => ModelDb.GetById<PotionModel>(ModelDb.GetId(i.Model)));
}
