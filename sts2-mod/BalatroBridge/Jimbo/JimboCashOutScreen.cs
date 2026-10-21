using System;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Balatro's round summary, over the won fight: the blind's reward, leftover hands, interest and
/// jokers' payouts counted up one line at a time, then Cash Out. It takes StS2's reward screen's
/// place after every one of Jimbo's fights.
/// </summary>
internal sealed class JimboCashOutScreen
{
    private static readonly Vector2 StageSize = new(1920, 1080);
    private const int Layer = 60;

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly Control _stage;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private JimboCashOutScreen()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 screen = tree.Root.GetVisibleRect().Size;
        _layer = new CanvasLayer { Layer = Layer };
        _root = new Control { Size = screen, MouseFilter = Control.MouseFilterEnum.Stop };
        _layer.AddChild(_root);
        var shade = new ColorRect { Size = screen, Color = new Color(0, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(shade);
        shade.CreateTween().TweenProperty(shade, "color:a", 0.45f, 0.3 / TableFx.Speed);
        float scale = Math.Min(screen.X / StageSize.X, screen.Y / StageSize.Y);
        _stage = new Control
        {
            Size = StageSize,
            Scale = new Vector2(scale, scale),
            Position = (screen - StageSize * scale) / 2,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.AddChild(_stage);
        tree.Root.AddChild(_layer);
    }

    /// <summary>Counts the cash-out up and waits for the player to collect it.</summary>
    public static async Task Show(CashOut cash)
    {
        var screen = new JimboCashOutScreen();
        try
        {
            await screen.Run(cash);
        }
        finally
        {
            screen._layer.QueueFree();
        }
    }

    private async Task Run(CashOut cash)
    {
        // What the round's end paid (Gold Cards held, Rental) comes first, as Balatro pays it first.
        var lines = cash.Paid.Select(p => (Label: p.Count > 1 ? $"{p.Label} x{p.Count}" : p.Label, p.Dollars))
            .Concat(cash.Rows.Select(r => (r.Label, r.Dollars))).ToList();
        int rows = lines.Count;
        float notesHeight = cash.Notes.Count > 0 ? 12 + cash.Notes.Count * 30 : 0;
        float height = 230 + rows * 58 + notesHeight;
        float top = 540 - height / 2;
        var panel = TableStyle.Panel(new Rect2(660, top, 600, height), TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 18, 3, 16));
        _stage.AddChild(panel);
        panel.Scale = new Vector2(0.8f, 0.8f);
        panel.Modulate = new Color(1, 1, 1, 0);
        Tween open = panel.CreateTween().SetParallel();
        open.TweenProperty(panel, "scale", Vector2.One, 0.3 / TableFx.Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        open.TweenProperty(panel, "modulate:a", 1f, 0.2 / TableFx.Speed);
        TableFx.Sound("cardFan2", 1f, 0.6f);
        panel.AddChild(TableStyle.Text("Blind defeated!", 40, StsColors.gold, new Rect2(0, 18, 600, 52), TableStyle.Title, 10));
        await TableFx.Wait(0.3f);

        float pitch = 0.95f;
        for (int i = 0; i < rows; i++)
        {
            (string text, double amount) = lines[i];
            float y = 84 + i * 58;
            Label label = TableStyle.Text(text, 26, StsColors.cream, new Rect2(30, y, 380, 48), TableStyle.Regular, 6);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            // Balatro counts small payouts in dollar signs; a charge (Rental) is a red minus.
            string money = amount is > 0 and <= 8 && amount == Math.Floor(amount)
                ? new string('$', (int)amount)
                : amount < 0 ? "-$" + TableText.Number(-amount) : "$" + TableText.Number(amount);
            Label dollars = TableStyle.Text(money, 30, amount < 0 ? TableStyle.Mult : TableStyle.Money, new Rect2(390, y, 180, 48));
            dollars.HorizontalAlignment = HorizontalAlignment.Right;
            panel.AddChild(label);
            panel.AddChild(dollars);
            TableFx.Juice(dollars, 0.3f);
            TableFx.Sound("coin" + (1 + i % 3), pitch, 0.8f);
            pitch += 0.06f;
            await TableFx.Wait(0.28f);
        }

        // What jokers and cards said as the round ended ("Gros Michel: Extinct!").
        for (int i = 0; i < cash.Notes.Count; i++)
        {
            float y = 84 + rows * 58 + 6 + i * 30;
            Label note = TableStyle.Text(cash.Notes[i], 20, new Color(StsColors.cream, 0.75f), new Rect2(30, y, 540, 30), TableStyle.Regular, 4);
            note.HorizontalAlignment = HorizontalAlignment.Left;
            panel.AddChild(note);
        }

        double total = cash.Total + cash.PaidTotal;
        Button collect = TableStyle.Button($"Cash Out: ${TableText.Number(total)}", new Color("F3B958").Darkened(0.15f), new Rect2(110, height - 112, 380, 88), 34);
        collect.AddThemeColorOverride("font_color", TableStyle.Ink);
        collect.AddThemeColorOverride("font_hover_color", TableStyle.Ink);
        collect.AddThemeConstantOverride("outline_size", 0);
        collect.Pressed += () =>
        {
            TableFx.Sound("coin1", 1f, 0.9f);
            TableFx.Sound("button");
            TableFx.Embers(_stage, new Vector2(960, top + height - 70), 30, TableStyle.Money);
            _done.TrySetResult();
        };
        panel.AddChild(collect);
        TableFx.Juice(collect, 0.15f);
        await _done.Task;
        Tween close = _stage.CreateTween().SetParallel();
        close.TweenProperty(panel, "modulate:a", 0f, 0.25 / TableFx.Speed);
        close.TweenProperty(panel, "position:y", panel.Position.Y - 60, 0.3 / TableFx.Speed);
        await TableFx.Finished(close);
    }
}
