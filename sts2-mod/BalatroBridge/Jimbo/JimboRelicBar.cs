using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's jokers can be sold or moved from the relic bar, as Balatro lets you sell or rearrange
/// jokers at any time (some boss blinds want one sold mid-fight, and jokers score left to right):
/// drag a joker relic to a new place, or right-click it, then Sell, or move it with the arrows.
/// </summary>
internal static class JimboRelicBar
{
    private const string Hooked = "balatro_sell";
    // How far the mouse goes before a press on a joker is a drag: StS2's own buttons treat it the
    // same way (past it, letting go doesn't count as a click, so the relic doesn't open).
    private const float DragThreshold = 14f;
    private static CanvasLayer? _prompt;
    private static NRelicInventoryHolder? _pressed;
    private static Vector2 _pressAt;
    private static CanvasLayer? _ghost;
    private static TextureRect? _ghostIcon;
    private static ColorRect? _marker;
    private static int _dropAt;

    private static readonly AccessTools.FieldRef<NClickableControl, float> IgnoreDragThreshold =
        AccessTools.FieldRefAccess<NClickableControl, float>("_ignoreDragThreshold");

    /// <summary>After each snapshot: makes sure every joker relic in the bar answers right-clicks and drags.</summary>
    public static void Hook(TableSnapshot table)
    {
        NRelicInventory? inventory = NRun.Instance?.GlobalUi.RelicInventory;
        if (inventory == null) return;
        foreach (NRelicInventoryHolder holder in inventory.RelicNodes)
        {
            if (!GodotObject.IsInstanceValid(holder) || holder.HasMeta(Hooked) || holder.Relic?.Model is not JokerRelic) continue;
            holder.SetMeta(Hooked, true);
            IgnoreDragThreshold(holder) = DragThreshold;
            NRelicInventoryHolder h = holder;
            holder.GuiInput += input =>
            {
                if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && h.Relic?.Model is JokerRelic joker) Prompt(h, joker);
                else OnDragInput(h, input);
            };
        }
    }

    // Dragging ------------------------------------------------------------------------------------

    private static void OnDragInput(NRelicInventoryHolder holder, InputEvent input)
    {
        switch (input)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _pressed = holder;
                _pressAt = OnScreen(holder, press.GlobalPosition);
                break;
            case InputEventMouseMotion motion when _pressed == holder:
                Vector2 at = OnScreen(holder, motion.GlobalPosition);
                if (_ghost == null)
                {
                    if (at.DistanceTo(_pressAt) < DragThreshold || holder.Relic?.Model is not JokerRelic joker || JokerHolders().Count < 2) return;
                    Lift(holder, joker);
                }
                Follow(holder, at);
                holder.AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _pressed == holder:
                _pressed = null;
                if (_ghost == null) return;
                Drop(holder);
                holder.AcceptEvent();
                break;
        }
    }

    // A point from the holder's canvas layer, on screen (where the ghost is drawn).
    private static Vector2 OnScreen(CanvasItem item, Vector2 global) => item.GetCanvasTransform() * global;

    private static Vector2 Middle(Control control) => control.GetGlobalTransformWithCanvas() * (control.Size / 2);

    private static List<NRelicInventoryHolder> JokerHolders() =>
        NRun.Instance?.GlobalUi.RelicInventory is NRelicInventory inventory
            ? inventory.RelicNodes.Where(h => GodotObject.IsInstanceValid(h) && h.Relic?.Model is JokerRelic).ToList()
            : [];

    // The joker comes off the bar: a copy of its icon follows the mouse, and a gold mark shows where it would go.
    private static void Lift(NRelicInventoryHolder holder, JokerRelic joker)
    {
        Close();
        _ghost = new CanvasLayer { Layer = 62 };
        Vector2 size = holder.GetGlobalTransformWithCanvas().Scale * holder.Size;
        _ghostIcon = new TextureRect
        {
            Texture = joker.Icon,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Size = size,
            PivotOffset = size / 2,
            Scale = Vector2.One * 1.2f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _marker = new ColorRect { Color = StsColors.gold, Size = new Vector2(4, size.Y), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _ghost.AddChild(_marker);
        _ghost.AddChild(_ghostIcon);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_ghost);
        holder.Modulate = new Color(1, 1, 1, 0.35f);
        // Its tip would sit under the mouse the whole way.
        MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.Remove(holder);
        TableFx.Sound("cardSlide1", 1.1f, 0.5f);
    }

    private static void Follow(NRelicInventoryHolder holder, Vector2 at)
    {
        if (_ghostIcon == null || _marker == null) return;
        float lean = Mathf.Clamp((at.X - (_ghostIcon.Position.X + _ghostIcon.Size.X / 2)) * 0.01f, -0.3f, 0.3f);
        _ghostIcon.Position = at - _ghostIcon.Size / 2;
        _ghostIcon.Rotation = Mathf.Lerp(_ghostIcon.Rotation, lean, 0.4f);

        // Its place among the other jokers: beside the nearest one, on whichever side the mouse is.
        List<NRelicInventoryHolder> others = JokerHolders().Where(h => h != holder).ToList();
        if (others.Count == 0) return;
        NRelicInventoryHolder nearest = others.OrderBy(h => Middle(h).DistanceSquaredTo(at)).First();
        Vector2 centre = Middle(nearest);
        bool after = at.X > centre.X;
        _dropAt = others.IndexOf(nearest) + (after ? 1 : 0);
        Vector2 half = nearest.GetGlobalTransformWithCanvas().Scale * nearest.Size / 2;
        _marker.Visible = true;
        _marker.Size = new Vector2(4, half.Y * 2);
        _marker.Position = new Vector2(centre.X + (after ? half.X : -half.X) - 2, centre.Y - half.Y);
    }

    private static void Drop(NRelicInventoryHolder holder)
    {
        holder.Modulate = Colors.White;
        if (_ghost != null && GodotObject.IsInstanceValid(_ghost)) _ghost.QueueFree();
        _ghost = null;
        _ghostIcon = null;
        _marker = null;
        if (holder.Relic?.Model is not JokerRelic joker || JimboRun.Me() is not Player player) return;
        int from = JokerHolders().IndexOf(holder);
        // Balatro's slot (1-based) once the joker is out of the row.
        int to = _dropAt + 1;
        TableFx.Sound("cardSlide2", 1f, 0.5f);
        if (from >= 0 && to != from + 1) TaskHelper.RunSafely(Move(player, joker, to, reopen: false));
    }

    private static void Prompt(Control anchor, JokerRelic joker)
    {
        Close();
        if (JimboRun.Me() is not Player player || JimboRun.Last?.Jokers.FirstOrDefault(j => j.Id == joker.CardId) is not TableCard card) return;
        TableFx.Sound("cardSlide1");
        _prompt = new CanvasLayer { Layer = 61 };
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 screen = tree.Root.GetVisibleRect().Size;
        var catcher = new Control { Size = screen, MouseFilter = Control.MouseFilterEnum.Stop };
        catcher.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true }) Close();
        };
        _prompt.AddChild(catcher);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 12, 3, 10));
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        column.AddChild(TableStyle.Text(card.Name ?? "Joker", 24, StsColors.gold));
        bool sellable = !card.Eternal;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        Button sell = TableStyle.Button(sellable ? $"Sell ${TableText.Number(card.Sell ?? 0)}" : "Eternal", TableStyle.Money, new Rect2(0, 0, 130, 46), 20);
        sell.CustomMinimumSize = new Vector2(130, 46);
        sell.AddThemeColorOverride("font_color", TableStyle.Ink);
        sell.AddThemeColorOverride("font_hover_color", TableStyle.Ink);
        sell.AddThemeConstantOverride("outline_size", 0);
        sell.Disabled = !sellable;
        sell.Pressed += () =>
        {
            Close();
            TaskHelper.RunSafely(Sell(player, joker));
        };
        Button keep = TableStyle.Button("Keep", new Color(0.32f, 0.36f, 0.42f), new Rect2(0, 0, 100, 46), 20);
        keep.CustomMinimumSize = new Vector2(100, 46);
        keep.Pressed += Close;
        int slot = JimboRun.Last!.Jokers.ToList().FindIndex(j => j.Id == joker.CardId) + 1;
        int count = JimboRun.Last.Jokers.Count;
        row.AddChild(MoveButton("<", slot > 1, () => TaskHelper.RunSafely(Move(player, joker, slot - 1, reopen: true))));
        row.AddChild(sell);
        row.AddChild(keep);
        row.AddChild(MoveButton(">", slot < count, () => TaskHelper.RunSafely(Move(player, joker, slot + 1, reopen: true))));
        column.AddChild(row);
        panel.AddChild(column);
        _prompt.AddChild(panel);
        tree.Root.AddChild(_prompt);
        panel.Size = panel.GetCombinedMinimumSize();
        Vector2 at = anchor.GetGlobalTransformWithCanvas() * new Vector2(0, anchor.Size.Y + 8);
        panel.Position = new Vector2(Math.Clamp(at.X, 8, screen.X - panel.Size.X - 8), Math.Clamp(at.Y, 8, screen.Y - panel.Size.Y - 8));
    }

    private static Button MoveButton(string label, bool enabled, Action pressed)
    {
        Button button = TableStyle.Button(label, new Color(0.32f, 0.36f, 0.42f), new Rect2(0, 0, 46, 46), 24);
        button.CustomMinimumSize = new Vector2(46, 46);
        button.Disabled = !enabled;
        button.Pressed += () =>
        {
            Close();
            pressed();
        };
        return button;
    }

    // Moves the joker to slot `to` (1-based); the relic bar follows. After an arrow, the prompt
    // comes back to move it again.
    private static async Task Move(Player player, JokerRelic joker, int to, bool reopen)
    {
        try
        {
            TableSnapshot table = await JimboRun.Session().JimboMoveJoker(JimboRun.LinkFor(player), joker.CardId, to);
            TableFx.Sound("cardSlide1", 1f, 0.7f);
            await JimboRun.Apply(player, table);
            if (!reopen) return;
            NRelicInventory? inventory = NRun.Instance?.GlobalUi.RelicInventory;
            if (inventory?.RelicNodes.FirstOrDefault(h => h.Relic?.Model == joker) is NRelicInventoryHolder holder) Prompt(holder, joker);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: moving {joker.BalatroKey} failed: {e.Message}");
        }
    }

    private static async Task Sell(Player player, JokerRelic joker)
    {
        try
        {
            TableSnapshot table = await JimboRun.Session().JimboSell(JimboRun.LinkFor(player), joker.CardId);
            TableFx.Sound("coin2", 1f, 0.8f);
            await JimboRun.Apply(player, table);
            // Selling can lift a boss blind's debuff (Verdant Leaf): redraw the hand.
            if (JimboHand.Current is JimboHand hand && table.Hand.Count > 0) await hand.Deal(table);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: selling {joker.BalatroKey} failed: {e.Message}");
        }
    }

    private static void Close()
    {
        if (_prompt != null && GodotObject.IsInstanceValid(_prompt)) _prompt.QueueFree();
        _prompt = null;
    }
}
