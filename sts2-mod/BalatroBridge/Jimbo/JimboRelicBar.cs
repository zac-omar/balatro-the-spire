using System;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's jokers can be sold from the relic bar, as Balatro lets you sell a joker at any time
/// (some boss blinds want one sold mid-fight): right-click a joker relic, then Sell.
/// </summary>
internal static class JimboRelicBar
{
    private const string Hooked = "balatro_sell";
    private static CanvasLayer? _prompt;

    /// <summary>After each snapshot: makes sure every joker relic in the bar answers right-clicks.</summary>
    public static void Hook(TableSnapshot table)
    {
        NRelicInventory? inventory = NRun.Instance?.GlobalUi.RelicInventory;
        if (inventory == null) return;
        foreach (NRelicInventoryHolder holder in inventory.RelicNodes)
        {
            if (!GodotObject.IsInstanceValid(holder) || holder.HasMeta(Hooked) || holder.Relic?.Model is not JokerRelic) continue;
            holder.SetMeta(Hooked, true);
            NRelicInventoryHolder h = holder;
            holder.GuiInput += input =>
            {
                if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && h.Relic?.Model is JokerRelic joker) Prompt(h, joker);
            };
        }
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
        row.AddChild(sell);
        row.AddChild(keep);
        column.AddChild(row);
        panel.AddChild(column);
        _prompt.AddChild(panel);
        tree.Root.AddChild(_prompt);
        panel.Size = panel.GetCombinedMinimumSize();
        Vector2 at = anchor.GetGlobalTransformWithCanvas() * new Vector2(0, anchor.Size.Y + 8);
        panel.Position = new Vector2(Math.Clamp(at.X, 8, screen.X - panel.Size.X - 8), Math.Clamp(at.Y, 8, screen.Y - panel.Size.Y - 8));
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
