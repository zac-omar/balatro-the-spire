using System.Collections.Generic;
using System.Linq;
using BalatroBridge.Content;
using BalatroBridge.Core;
using BalatroBridge.Jimbo;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace BalatroBridge.Relics;

/// <summary>
/// The other characters' side of the Balatro jokers: with the Deal In card, the joker relics a
/// player owns are their Balatro jokers (StS2 is the source of truth here), and the Balatro shop
/// on the Deal In table sells any joker as its relic.
/// </summary>
internal static class DealInRelics
{
    /// <summary>Every joker key: what the Deal In shop may sell.</summary>
    public static IReadOnlyList<string> Pool { get; } = BalatroContent.OfSet("Joker").Select(i => i.Key).ToList();

    /// <summary>The canonical relic for a Balatro joker key, or null.</summary>
    public static RelicModel? RelicFor(string? jokerKey) => BalatroContent.ModelFor<RelicModel>(jokerKey);

    /// <summary>
    /// Ties the Deal In table to the player's StS2 run: which run it is and which joker relics the
    /// player owns. Null without a player (e.g. the console's "balatro hand" at the main menu).
    /// </summary>
    public static TableLink? LinkFor(Player? player)
    {
        if (player == null) return null;
        List<string> owned = player.Relics.OfType<JokerRelic>().Select(r => r.BalatroKey).ToList();
        return new TableLink(JimboRun.RunId(player), owned, Pool);
    }
}
