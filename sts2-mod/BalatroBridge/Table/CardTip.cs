using System;
using BalatroBridge.Content;
using BalatroBridge.Core;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Table;

/// <summary>
/// A card's tooltip on a table stage (the fight's hand, the deck view, the shop, the Deal In
/// table): its name and Balatro's own text for it, beside the card. One shows at a time.
/// </summary>
internal sealed class CardTip
{
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
        if (card.Debuffed && card.Dud == null) text +=(text.Length > 0 ? "\n" : "") + "[red]Debuffed: it won't score.[/red]";
        return text;
    }

    /// <summary>Shows <paramref name="view"/>'s tip, below it (or above it, for cards along the bottom).</summary>
    public void Show(CardView view, bool above = false) =>
        Show(view.Card.Name ?? "", TextFor(view.Card), view.Root.Position, view.Root.Size * view.Root.Scale, view, above);

    public void Show(string title, string? text, Vector2 at, Vector2 size, object? owner = null, bool above = false)
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
        var tip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        tip.AddThemeStyleboxOverride("panel", TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 12, 3, 10));
        tip.AddChild(column);
        _stage.AddChild(tip);
        tip.Size = tip.GetCombinedMinimumSize();
        float x = Math.Clamp(at.X + size.X / 2 - (Width + 20) / 2, 20, _stageSize.X - Width - 40);
        float below = at.Y + size.Y + 14, over = at.Y - tip.Size.Y - 14;
        bool up = above ? over >= 10 : below + tip.Size.Y > _stageSize.Y - 10;
        tip.Position = new Vector2(x, up ? Math.Max(10, over) : below);
        tip.CreateTween().TweenProperty(tip, "modulate:a", 1f, 0.1 / TableFx.Speed);
        _tip = tip;
        _owner = owner;
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
