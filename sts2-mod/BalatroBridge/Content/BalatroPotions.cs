using System.Threading.Tasks;
using BalatroBridge.Jimbo;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace BalatroBridge.Content;

/// <summary>
/// A Balatro consumable (tarot, planet or spectral card) in Jimbo's potion belt, which holds
/// Balatro's consumable slots. Balatro decides everything: whether it can be used right now (a
/// tarot that changes cards needs those cards selected in hand), what it does, and when one is
/// created or used up; the belt follows. Event rarity keeps it out of StS2's potion rolls.
/// </summary>
public abstract class ConsumablePotion : PotionModel
{
    internal BalatroItem Item => BalatroContent.ItemFor(GetType());

    public string BalatroKey => Item.Key;

    public override PotionRarity Rarity => PotionRarity.Event;

    public override PotionUsage Usage => PotionUsage.AnyTime;

    public override TargetType TargetType => TargetType.Self;

    public override bool CanBeGeneratedInCombat => false;

    public override bool PassesCustomUsabilityCheck => JimboConsumables.CanUse(this);

    /// <summary>Where StS2 looks for the belt icon; <see cref="BalatroIcons"/> puts the card's art there.</summary>
    internal string ImageResourcePath => ImageHelper.GetImagePath("atlases/potion_atlas.sprites/" + Id.Entry.ToLowerInvariant() + ".tres");

    protected override Task OnUse(PlayerChoiceContext choiceContext, Creature? target) => JimboConsumables.Use(this);
}

public abstract class TarotPotion : ConsumablePotion;

public abstract class PlanetPotion : ConsumablePotion;

public abstract class SpectralPotion : ConsumablePotion;
