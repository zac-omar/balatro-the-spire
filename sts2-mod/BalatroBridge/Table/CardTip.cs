using System;
using System.Collections.Generic;
using BalatroBridge.Content;
using BalatroBridge.Core;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Table;

/// <summary>
/// A card's tooltip on a table stage (the fight's hand, the deck view, the shop, the Deal In
/// table): its name, Balatro's own text for it and Balatro's badges (rarity, set, edition, seal,
/// stickers), by the card. One shows at a time.
/// </summary>
internal sealed class CardTip
{
    /// <summary>Where a tip goes: under the card, over it, or to its side (left if there's room).</summary>
    public enum Place { Below, Above, Beside }

    private const float Width = 360;

    private readonly Control _stage;
    private readonly Vector2 _stageSize;
    private Control? _tip;
    private object? _owner;

    public CardTip(Control stage, Vector2 stageSize)
    {
        _stage = stage;
        _stageSize = stageSize;
    }

    /// <summary>What a card does, in Balatro's words: its live text, else the catalog's; a plain card's chips.</summary>
    public static string TextFor(TableCard card)
    {
        string text = card.Text ?? BalatroCatalog.Text(card.Key) ?? (card.IsPlayingCard ? $"[blue]+{TableText.Number(card.Chips ?? 0)}[/blue] chips" : "");
        if (card.Debuffed && card.Dud == null) text +=(text.Length > 0 ? "\n" : "") + "[red]Debuffed: doesn't score.[/red]";
        // The stake stickers' rules, as Balatro's extra boxes give them.
        if (card.Eternal) text += "\n[color=#C75985]Eternal[/color]: can't be sold or destroyed.";
        if (card.Perishable is int rounds) text += $"\n[color=#4F5DA1]Perishable[/color]: debuffed in [gold]{rounds}[/gold] round{(rounds == 1 ? "" : "s")}.";
        if (card.Rental) text += "\n[color=#B18F43]Rental[/color]: costs [gold]$3[/gold] at the end of each round.";
        return text;
    }

    /// <summary>Balatro's badges for a card: its rarity or set, edition, seal and stickers, as (label, colour).</summary>
    public static List<(string Label, Color Colour)> Badges(TableCard card)
    {
        var badges = new List<(string, Color)>();
        if (card.IsJoker && BalatroContent.ItemFor(card.Key) is BalatroItem item)
        {
            badges.Add(item.Rarity switch
            {
                1 => ("Common", new Color("009DFF")),
                2 => ("Uncommon", new Color("4BC292")),
                3 => ("Rare", new Color("FE5F55")),
                _ => ("Legendary", new Color("B26CBB")),
            });
        }
        else if (card.Set switch { "Tarot" => "A782D1", "Planet" => "13AFCE", "Spectral" => "4584FA", "Voucher" => "FD682B", _ => null } is string hex)
        {
            badges.Add((card.Set!, new Color(hex)));
        }
        if (card.Edition switch { "foil" => "Foil", "holo" => "Holographic", "polychrome" => "Polychrome", "negative" => "Negative", _ => null } is string edition)
        {
            badges.Add((edition, new Color("4CA893")));
        }
        if (card.Seal is string seal)
        {
            badges.Add((seal + " Seal", seal switch { "Gold" => new Color("EAC058"), "Red" => new Color("FE5F55"), "Blue" => new Color("009DFF"), _ => new Color("8867A5") }));
        }
        if (card.Eternal) badges.Add(("Eternal", new Color("C75985")));
        if (card.Perishable != null) badges.Add(("Perishable", new Color("4F5DA1")));
        if (card.Rental) badges.Add(("Rental", new Color("B18F43")));
        return badges;
    }

    /// <summary>Shows <paramref name="view"/>'s tip, below it (or above it, for cards along the bottom).</summary>
    public void Show(CardView view, bool above = false) => Show(view, above ? Place.Above : Place.Below);

    public void Show(CardView view, Place place) =>
        Show(view.Card.Name ?? "", TextFor(view.Card), view.Root.Position, view.Root.Size * view.Root.Scale, view, place, Badges(view.Card));

    public void Show(string title, string? text, Vector2 at, Vector2 size, object? owner = null, bool above = false) =>
        Show(title, text, at, size, owner, above ? Place.Above : Place.Below, null);

    public void Show(string title, string? text, Vector2 at, Vector2 size, object? owner, Place place, List<(string Label, Color Colour)>? badges)
    {
        Hide();
        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 4);
        Label name = TableStyle.Text(title, 28, StsColors.gold);
        name.CustomMinimumSize = new Vector2(Width, 38);
        column.AddChild(name);
        if (!string.IsNullOrEmpty(text))
        {
            RichTextLabel body = TableStyle.RichText(text, 20);
            body.CustomMinimumSize = new Vector2(Width, 0);
            column.AddChild(body);
        }
        if (badges is { Count: > 0 })
        {
            var row = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = FlowContainer.AlignmentMode.Center, CustomMinimumSize = new Vector2(Width, 0) };
            row.AddThemeConstantOverride("h_separation", 6);
            row.AddThemeConstantOverride("v_separation", 4);
            foreach ((string label, Color colour) in badges) row.AddChild(Badge(label, colour));
            column.AddChild(row);
        }
        var tip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0), ZIndex = 100 };
        tip.AddThemeStyleboxOverride("panel", TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 12, 3, 10));
        tip.AddChild(column);
        _stage.AddChild(tip);
        tip.Size = tip.GetCombinedMinimumSize();
        if (place == Place.Beside)
        {
            // Balatro shows a shop card's box to its left; to its right when there's no room.
            float left = at.X - tip.Size.X - 16, right = at.X + size.X + 16;
            float y = Math.Clamp(at.Y + size.Y / 2 - tip.Size.Y / 2, 10, _stageSize.Y - tip.Size.Y - 10);
            tip.Position = new Vector2(left >= 10 ? left : Math.Min(right, _stageSize.X - tip.Size.X - 10), y);
        }
        else
        {
            float x = Math.Clamp(at.X + size.X / 2 - (Width + 20) / 2, 20, _stageSize.X - Width - 40);
            float below = at.Y + size.Y + 14, over = at.Y - tip.Size.Y - 14;
            bool up = place == Place.Above ? over >= 10 : below + tip.Size.Y > _stageSize.Y - 10;
            tip.Position = new Vector2(x, up ? Math.Max(10, over) : below);
        }
        tip.CreateTween().TweenProperty(tip, "modulate:a", 1f, 0.1 / TableFx.Speed);
        _tip = tip;
        _owner = owner;
    }

    // One of Balatro's badges: a rounded pill in the badge's colour, its name in white.
    private static Control Badge(string label, Color colour)
    {
        var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        StyleBoxFlat box = TableStyle.Box(colour, colour.Darkened(0.35f), 10, 0);
        box.BorderWidthBottom = 3;
        box.SetContentMarginAll(3);
        box.ContentMarginLeft = box.ContentMarginRight = 12;
        pill.AddThemeStyleboxOverride("panel", box);
        pill.AddChild(TableStyle.Text(label, 17, Colors.White, TableStyle.Bold, 5));
        return pill;
    }

    /// <summary>Hides the tip (only if it belongs to <paramref name="owner"/>, when given).</summary>
    public void Hide(object? owner = null)
    {
        if (_tip == null || (owner != null && owner != _owner)) return;
        if (GodotObject.IsInstanceValid(_tip)) _tip.QueueFree();
        _tip = null;
        _owner = null;
    }
}
