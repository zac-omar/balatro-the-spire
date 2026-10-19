using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.TopBar;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's deck, as Balatro's deck view shows it: every playing card in the run, a row per suit
/// in rank order, with the ones out of the draw pile (in hand, played or discarded this fight)
/// dimmed. Beside it, Balatro's Run Info: every poker hand's level, chips × mult and plays. The
/// top bar's deck button opens it (the shop's Run Info button too); a click or Escape closes it.
/// </summary>
internal sealed class JimboDeckScreen
{
    private static readonly Vector2 StageSize = new(1920, 1080);
    private static readonly string[] Suits = ["Spades", "Hearts", "Clubs", "Diamonds"];
    private const int Layer = 59;

    private static JimboDeckScreen? _open;

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly Control _stage;
    private readonly Control _cards;
    private readonly CardTip _tip;

    private JimboDeckScreen(int layer)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 screen = tree.Root.GetVisibleRect().Size;
        _layer = new CanvasLayer { Layer = layer };
        _root = new Control { Size = screen, MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All };
        _layer.AddChild(_root);
        var shade = new ColorRect { Size = screen, Color = new Color(0.02f, 0.03f, 0.05f, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(shade);
        shade.CreateTween().TweenProperty(shade, "color:a", 0.9f, 0.2 / TableFx.Speed);
        float scale = Math.Min(screen.X / StageSize.X, screen.Y / StageSize.Y);
        _stage = new Control
        {
            Size = StageSize,
            Scale = new Vector2(scale, scale),
            Position = (screen - StageSize * scale) / 2,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.AddChild(_stage);
        _cards = new Control { Size = StageSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        _stage.AddChild(_cards);
        _tip = new CardTip(_stage, StageSize);
        _root.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true } or InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                _root.AcceptEvent();
                Close();
            }
        };
        tree.Root.AddChild(_layer);
        _root.GrabFocus();
    }

    /// <summary>Opens the deck view (or closes it if it's open), on <paramref name="layer"/> (above the shop, from the shop).</summary>
    public static async Task Toggle(Player player, int layer = Layer)
    {
        if (_open != null)
        {
            _open.Close();
            return;
        }
        var screen = new JimboDeckScreen(layer);
        _open = screen;
        try
        {
            DeckListing deck = await JimboRun.Session().JimboDeck(JimboRun.LinkFor(player));
            screen.Show(deck);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: couldn't get the deck: {e.Message}");
            screen._stage.AddChild(TableStyle.Text("Balatro isn't answering...", 34, StsColors.red, new Rect2(460, 500, 1000, 60)));
        }
    }

    private void Show(DeckListing listing)
    {
        var art = BalatroArt.Shared;
        art.Learn(listing.Atlases, listing.TextureScale);
        int left = listing.Cards.Count(c => c.Pile == "deck");
        _stage.AddChild(TableStyle.Text("Jimbo's Deck", 52, StsColors.gold, new Rect2(CardsX - 500, 40, 1000, 64), TableStyle.Title, 12));
        _stage.AddChild(TableStyle.Text($"{left} of {listing.Cards.Count} cards left to draw", 24, StsColors.cream, new Rect2(CardsX - 500, 104, 1000, 34), TableStyle.Regular, 6));
        ShowHands(listing.Hands);

        // A row per suit; stone cards (no suit) go in a last row.
        var rows = Suits.Select(s => listing.Cards.Where(c => !c.Stone && c.Suit == s).ToList()).ToList();
        rows.Add(listing.Cards.Where(c => c.Stone || !Suits.Contains(c.Suit)).ToList());
        float y = 250;
        TableFx.Sound("cardFan2", 1f, 0.6f);
        foreach (List<TableCard> row in rows.Where(r => r.Count > 0))
        {
            List<TableCard> sorted = row.OrderByDescending(c => RankValue(c.Rank)).ThenBy(c => c.Id).ToList();
            float spacing = Math.Min(82, CardsWidth / Math.Max(sorted.Count, 1));
            float x0 = CardsX - spacing * (sorted.Count - 1) / 2f;
            for (int i = 0; i < sorted.Count; i++)
            {
                TableCard card = sorted[i] with { Debuffed = false, FaceDown = false };
                var view = new CardView(card, art, null, 0.72f);
                // Hover a card for what it does; clicks still fall through to close the view.
                view.Root.MouseFilter = Control.MouseFilterEnum.Pass;
                view.Root.Position = new Vector2(x0 + i * spacing, y) - view.Root.Size / 2;
                if (sorted[i].Pile != "deck") view.Root.Modulate = new Color(0.45f, 0.45f, 0.5f, 0.8f);
                Vector2 home = view.Root.Position;
                view.Root.MouseEntered += () =>
                {
                    view.Root.MoveToFront();
                    view.Root.Position = home - new Vector2(0, 18);
                    TableFx.HoverSound();
                    _tip.Show(view);
                };
                view.Root.MouseExited += () =>
                {
                    view.Root.Position = home;
                    _tip.Hide(view);
                };
                _cards.AddChild(view.Root);
            }
            y += 168;
        }
        _stage.AddChild(TableStyle.Text("Click anywhere to close", 20, new Color(StsColors.cream, 0.5f), new Rect2(CardsX - 500, 1030, 1000, 30), TableStyle.Regular, 4));
    }

    // The cards sit right of the poker hands.
    private const float CardsX = 1230, CardsWidth = 1280;

    // Balatro's Run Info: a row per poker hand, levelled ones in gold.
    private void ShowHands(IReadOnlyList<PokerHandInfo> hands)
    {
        if (hands.Count == 0) return;
        const float rowHeight = 56;
        var faint = new Color(StsColors.cream, 0.55f);
        Panel panel = TableStyle.Panel(new Rect2(36, 150, 500, 100 + hands.Count * rowHeight), TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 14, 3, 12));
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _stage.AddChild(panel);
        panel.AddChild(TableStyle.Text("Poker Hands", 34, StsColors.gold, new Rect2(0, 12, 500, 44), TableStyle.Title, 8));
        panel.AddChild(TableStyle.Text("level   ·   chips × mult   ·   times played", 17, faint, new Rect2(0, 56, 500, 26), TableStyle.Regular, 4));
        for (int i = 0; i < hands.Count; i++)
        {
            PokerHandInfo hand = hands[i];
            float y = 90 + i * rowHeight;
            Label level = TableStyle.Text("lvl." + hand.Level, 20, hand.Level > 1 ? StsColors.gold : faint, new Rect2(18, y, 72, 44), TableStyle.Bold, 4);
            level.HorizontalAlignment = HorizontalAlignment.Left;
            Label name = TableStyle.Text(hand.Name, 22, StsColors.cream, new Rect2(90, y, 200, 44), TableStyle.Regular, 4);
            name.HorizontalAlignment = HorizontalAlignment.Left;
            panel.AddChild(level);
            panel.AddChild(name);
            panel.AddChild(Box(hand.Chips, TableStyle.Chips, new Rect2(290, y + 5, 84, 34)));
            panel.AddChild(TableStyle.Text("×", 20, StsColors.cream, new Rect2(374, y, 22, 44), TableStyle.Bold, 4));
            panel.AddChild(Box(hand.Mult, TableStyle.Mult, new Rect2(396, y + 5, 62, 34)));
            panel.AddChild(TableStyle.Text("#" + hand.Played, 17, faint, new Rect2(458, y, 36, 44), TableStyle.Regular, 4));
        }
    }

    private static Panel Box(string value, Color colour, Rect2 rect)
    {
        Panel box = TableStyle.Panel(rect, TableStyle.Box(colour, new Color(1, 1, 1, 0.3f), 8, 2, 4));
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddChild(TableStyle.Text(value, 20, Colors.White, new Rect2(0, 0, rect.Size.X, rect.Size.Y), TableStyle.Bold, 4));
        return box;
    }

    private static int RankValue(string? rank) => rank switch
    {
        "Ace" => 14,
        "King" => 13,
        "Queen" => 12,
        "Jack" => 11,
        _ => int.TryParse(rank, out int v) ? v : 0,
    };

    private void Close()
    {
        if (_open == this) _open = null;
        TableFx.Sound("cardSlide2", 1f, 0.4f);
        _root.ReleaseFocus();
        _layer.QueueFree();
    }
}

/// <summary>In Jimbo's runs the top bar's deck button opens his Balatro deck, and counts it.</summary>
[HarmonyPatch(typeof(NTopBarDeckButton), "OnRelease")]
internal static class JimboDeckButton
{
    private static readonly AccessTools.FieldRef<NTopBarDeckButton, MegaCrit.Sts2.addons.mega_text.MegaLabel>? CountLabel = Label();

    private static bool Prefix()
    {
        if (JimboRun.Me() is not Player me) return true;
        TaskHelper.RunSafely(JimboDeckScreen.Toggle(me));
        return false;
    }

    /// <summary>Shows Balatro's deck size on the button.</summary>
    public static void Refresh(TableSnapshot table)
    {
        try
        {
            NTopBarDeckButton? button = NRun.Instance?.GlobalUi.TopBar.Deck;
            if (button == null || CountLabel == null || !GodotObject.IsInstanceValid(button)) return;
            CountLabel(button).SetTextAutoSize(table.DeckTotal.ToString());
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: couldn't update the deck count: {e.Message}");
        }
    }

    private static AccessTools.FieldRef<NTopBarDeckButton, MegaCrit.Sts2.addons.mega_text.MegaLabel>? Label()
    {
        try
        {
            return AccessTools.FieldRefAccess<NTopBarDeckButton, MegaCrit.Sts2.addons.mega_text.MegaLabel>("_countLabel");
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: the deck button's count isn't where expected ({e.Message})");
            return null;
        }
    }
}
