using System;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's game over screen sums up his Balatro run the way Balatro's own does: the best hand,
/// the most played hand, how far he got, and the cards he played, discarded and bought. It joins
/// StS2's run summary, on the right.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), "OpenSummaryScreen")]
internal static class JimboGameOver
{
    private static readonly Vector2 StageSize = new(1920, 1080);
    private static readonly AccessTools.FieldRef<NGameOverScreen, RunState> RunOf = AccessTools.FieldRefAccess<NGameOverScreen, RunState>("_runState");
    private static readonly Color Green = new("4BC292");

    private static void Postfix(NGameOverScreen __instance)
    {
        if (JimboRun.Me(RunOf(__instance)) is not Player me) return;
        TaskHelper.RunSafely(Show(__instance, me));
    }

    private static async Task Show(NGameOverScreen screen, Player me)
    {
        RunStats stats;
        try
        {
            stats = await JimboRun.Session().JimboStats(JimboRun.LinkFor(me));
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: no run summary from Balatro: {e.Message}");
            return;
        }
        if (!GodotObject.IsInstanceValid(screen)) return;

        Vector2 size = screen.GetViewportRect().Size;
        float scale = Math.Min(size.X / StageSize.X, size.Y / StageSize.Y);
        var stage = new Control
        {
            Size = StageSize,
            Scale = new Vector2(scale, scale),
            Position = (size - StageSize * scale) / 2,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        screen.AddChild(stage);

        const float width = 420, rowHeight = 46;
        string mostPlayed = stats.MostPlayed is string hand ? $"{hand} ({stats.MostPlayedCount})" : "-";
        (string Label, string Value, Color Colour)[] rows =
        [
            ("Most Played Hand", mostPlayed, StsColors.cream),
            ("Furthest Ante", $"{stats.Ante}  ·  Round {stats.Round}", StsColors.cream),
            ("Cards Played", stats.CardsPlayed.ToString(), TableStyle.Chips),
            ("Cards Discarded", stats.CardsDiscarded.ToString(), TableStyle.Mult),
            ("Cards Purchased", stats.CardsPurchased.ToString(), TableStyle.Money),
            ("Times Rerolled", stats.TimesRerolled.ToString(), Green),
        ];
        float height = 190 + rows.Length * rowHeight;
        // Right of StS2's summary, below the defeat quote (which can run the screen's width).
        Panel panel = TableStyle.Panel(new Rect2(StageSize.X - width - 40, Math.Max(360, (StageSize.Y - height) / 2), width, height),
            TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 14, 3, 12));
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        stage.AddChild(panel);
        panel.AddChild(TableStyle.Text("Jimbo's Run", 34, StsColors.gold, new Rect2(0, 12, width, 44), TableStyle.Title, 8));
        panel.AddChild(TableStyle.Text("Best Hand", 20, new Color(StsColors.cream, 0.7f), new Rect2(0, 62, width, 28), TableStyle.Regular, 4));
        panel.AddChild(TableStyle.Text(TableText.Number(stats.BestHand), 52, StsColors.gold, new Rect2(0, 88, width, 64), TableStyle.Title, 10));
        for (int i = 0; i < rows.Length; i++)
        {
            float y = 168 + i * rowHeight;
            Label label = TableStyle.Text(rows[i].Label, 20, StsColors.cream, new Rect2(22, y, 200, 40), TableStyle.Regular, 4);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            Label value = TableStyle.Text(rows[i].Value, 22, rows[i].Colour, new Rect2(200, y, width - 222, 40), TableStyle.Bold, 5);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            panel.AddChild(label);
            panel.AddChild(value);
        }

        // In with StS2's summary, a beat after it starts.
        Tween fade = stage.CreateTween();
        fade.TweenInterval(0.6);
        fade.TweenProperty(stage, "modulate:a", 1f, 0.5);
    }
}
