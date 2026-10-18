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
/// dimmed. The top bar's deck button opens it; a click or Escape closes it.
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

    private JimboDeckScreen()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 screen = tree.Root.GetVisibleRect().Size;
        _layer = new CanvasLayer { Layer = Layer };
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

    /// <summary>Opens the deck view (or closes it if it's open).</summary>
    public static async Task Toggle(Player player)
    {
        if (_open != null)
        {
            _open.Close();
            return;
        }
        var screen = new JimboDeckScreen();
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
        _stage.AddChild(TableStyle.Text("Jimbo's Deck", 52, StsColors.gold, new Rect2(460, 40, 1000, 64), TableStyle.Title, 12));
        _stage.AddChild(TableStyle.Text($"{left} of {listing.Cards.Count} cards left to draw", 24, StsColors.cream, new Rect2(460, 104, 1000, 34), TableStyle.Regular, 6));

        // A row per suit; stone cards (no suit) go in a last row.
        var rows = Suits.Select(s => listing.Cards.Where(c => !c.Stone && c.Suit == s).ToList()).ToList();
        rows.Add(listing.Cards.Where(c => c.Stone || !Suits.Contains(c.Suit)).ToList());
        float y = 250;
        TableFx.Sound("cardFan2", 1f, 0.6f);
        foreach (List<TableCard> row in rows.Where(r => r.Count > 0))
        {
            List<TableCard> sorted = row.OrderByDescending(c => RankValue(c.Rank)).ThenBy(c => c.Id).ToList();
            float spacing = Math.Min(82, 1640f / Math.Max(sorted.Count, 1));
            float x0 = 960 - spacing * (sorted.Count - 1) / 2f;
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
        _stage.AddChild(TableStyle.Text("Click anywhere to close", 20, new Color(StsColors.cream, 0.5f), new Rect2(460, 1030, 1000, 30), TableStyle.Regular, 4));
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
