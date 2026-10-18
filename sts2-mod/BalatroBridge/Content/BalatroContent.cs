using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace BalatroBridge.Content;

/// <summary>
/// A Balatro item that is also a StS2 model: <paramref name="Key"/> is Balatro's key ("j_joker",
/// "v_overstock_norm", "b_red", "stake_white", "c_fool"), <paramref name="Set"/> Balatro's set
/// ("Joker", "Voucher", "Back", "Stake", "Tarot", "Planet", "Spectral") and <paramref name="Name"/>
/// its English name in Balatro's data (display names come from the catalog).
/// </summary>
internal sealed record BalatroItem(
    Type Model,
    string Key,
    string Set,
    string Name,
    int Order,
    int Rarity,
    int Cost,
    BalatroSprite Sprite,
    BalatroSprite? Soul)
{
    public bool IsConsumable => Set is "Tarot" or "Planet" or "Spectral";
}

/// <summary>A cell in one of Balatro's 2x sprite sheets ("Jokers", "Tarots", ...).</summary>
internal sealed record BalatroSprite(string Sheet, int Column, int Row, int Width = 142, int Height = 190)
{
    public string Path => $"resources/textures/2x/{Sheet}.png";
}

// The items themselves (BalatroContent.All and one class per item) are generated from Balatro's
// data by tools/generate_balatro_content.py into Generated/BalatroContent.g.cs.
internal static partial class BalatroContent
{
    // Built on first use: All lives in the generated half of this class, and static fields in
    // two halves of a partial class are initialized in no particular order.
    private static Dictionary<Type, BalatroItem>? _byType;
    private static Dictionary<string, BalatroItem>? _byKey;

    private static Dictionary<Type, BalatroItem> ByType => _byType ??= All.ToDictionary(i => i.Model);

    private static Dictionary<string, BalatroItem> ByKey => _byKey ??= All.ToDictionary(i => i.Key);

    public static IEnumerable<BalatroItem> OfSet(string set) => All.Where(i => i.Set == set);

    public static IEnumerable<BalatroItem> Consumables => All.Where(i => i.IsConsumable);

    public static BalatroItem ItemFor(Type model) => ByType[model];

    public static BalatroItem? ItemFor(string? key) => key != null && ByKey.TryGetValue(key, out BalatroItem? item) ? item : null;

    /// <summary>The canonical model (relic or potion) for a Balatro key, or null if it has none.</summary>
    public static T? ModelFor<T>(string? key) where T : AbstractModel =>
        ItemFor(key) is BalatroItem item ? ModelDb.GetByIdOrNull<T>(ModelDb.GetId(item.Model)) : null;

    /// <summary>The stake Balatro plays for an StS2 ascension level: Ascension 0 is White Stake.</summary>
    public static BalatroItem StakeFor(int ascension)
    {
        List<BalatroItem> stakes = OfSet("Stake").OrderBy(s => s.Order).ToList();
        return stakes[Math.Clamp(ascension, 0, stakes.Count - 1)];
    }
}
