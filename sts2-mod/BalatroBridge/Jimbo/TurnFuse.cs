using System;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BalatroBridge.Jimbo;

/// <summary>
/// The turns before Overtime, as a lit fuse under StS2's End Turn button, on the felt (above it,
/// the fight's creatures and their health bars can stand anywhere): a notch for each turn the
/// blind's hands last, the spark at this turn, and Overtime waiting at the end. The fuse burns down
/// a notch a turn; on the last safe turn the spark flares and the label warns; when it reaches the
/// end, Overtime goes off: the cap burns red with what the enemies gain, and the word lands big.
/// </summary>
internal sealed class TurnFuse
{
    private const float Left = 1626, Right = 1764, Y = 1022;

    // The middle of StS2's End Turn button at 16:9, which the fuse is laid out under.
    private const float EndTurnX = 1714;
    private static readonly Color Rope = new("D9B26F");
    private static readonly Color RopeEdge = new("3A2A18");
    private static readonly Color Burnt = new("3B3530");
    private static readonly Color Warn = new("FF9A00");

    private readonly Control _stage;
    private readonly Control _root;
    private readonly Line2D _burnt;
    private readonly Control _notches;
    private readonly Node2D _spark;
    private readonly CpuParticles2D _sparks;
    private readonly Panel _cap;
    private readonly Label _capText;
    private readonly Label _label;
    private Tween? _pulse;
    private int _limit = -1;
    private int _turn = -1;
    private Action? _afterSlam;

    public TurnFuse(Control stage)
    {
        _stage = stage;
        _root = new Control { Size = new Vector2(400, 60), Position = new Vector2(0, 0), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        stage.AddChild(_root);

        _root.AddChild(Line(RopeEdge, 15));
        _root.AddChild(Line(Rope, 9));
        _burnt = Line(Burnt, 10);
        _burnt.Points = [new Vector2(Left, Y), new Vector2(Left, Y)];
        _root.AddChild(_burnt);
        _notches = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_notches);

        _cap = TableStyle.Panel(new Rect2(Right + 8, Y - 18, 124, 36), TableStyle.Box(new Color("5A1E1A"), new Color("FE5F55", 0.5f), 9, 2, 4));
        _capText = TableStyle.Text("OVERTIME", 18, new Color(StsColors.cream, 0.7f), new Rect2(0, 0, 124, 36), TableStyle.Bold, 5);
        _cap.AddChild(_capText);
        _root.AddChild(_cap);

        // The spark: a warm glow with embers spitting off it.
        _spark = new Node2D { Position = new Vector2(Left, Y) };
        var glowRamp = new Gradient();
        glowRamp.SetColor(0, new Color(1f, 0.95f, 0.6f, 1f));
        glowRamp.SetColor(1, new Color(1f, 0.5f, 0.1f, 0f));
        var glow = new TextureRect
        {
            Texture = new GradientTexture2D { Gradient = glowRamp, Width = 64, Height = 64, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f) },
            Position = new Vector2(-32, -32),
            Size = new Vector2(64, 64),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _spark.AddChild(glow);
        var emberRamp = new Gradient();
        emberRamp.SetColor(0, new Color(1f, 0.9f, 0.4f, 1f));
        emberRamp.SetColor(1, new Color(1f, 0.3f, 0.05f, 0f));
        _sparks = new CpuParticles2D
        {
            Amount = 18,
            Lifetime = 0.45,
            Direction = Vector2.Up,
            Spread = 160,
            Gravity = new Vector2(0, 160),
            InitialVelocityMin = 40,
            InitialVelocityMax = 110,
            ScaleAmountMin = 2,
            ScaleAmountMax = 4,
            ColorRamp = emberRamp,
        };
        _spark.AddChild(_sparks);
        _root.AddChild(_spark);
        Tween flicker = _spark.CreateTween().SetLoops();
        flicker.TweenProperty(_spark, "scale", Vector2.One * 1.15f, 0.12).SetTrans(Tween.TransitionType.Sine);
        flicker.TweenProperty(_spark, "scale", Vector2.One * 0.9f, 0.14).SetTrans(Tween.TransitionType.Sine);

        // Clear of the Discard and Suit buttons to its left, and the screen's edge to its right.
        _label = TableStyle.Text("", 18, StsColors.cream, new Rect2(Left - 20, Y - 52, 290, 28), TableStyle.Bold, 6);
        _label.HorizontalAlignment = HorizontalAlignment.Left;
        _root.AddChild(_label);
    }

    /// <summary>Runs <paramref name="then"/> once the OVERTIME slam has played (Jimbo's word on it waits for it).</summary>
    public void AfterSlam(Action then) => _afterSlam = then;

    /// <summary>Moves the fuse under End Turn, whose middle is at <paramref name="endTurnX"/> on the stage.</summary>
    public void Under(float endTurnX) => _root.Position = new Vector2(Mathf.Clamp(endTurnX - EndTurnX, -400, 800), 0);

    private static Line2D Line(Color colour, float width) => new()
    {
        Points = [new Vector2(Left, Y), new Vector2(Right, Y)],
        Width = width,
        DefaultColor = colour,
        BeginCapMode = Line2D.LineCapMode.Round,
        EndCapMode = Line2D.LineCapMode.Round,
    };

    /// <summary>Shows turn <paramref name="turn"/> of a blind whose hands last <paramref name="limit"/> turns; past them, the enemies gain <paramref name="strength"/>.</summary>
    public void Show(int turn, int limit, int strength)
    {
        if (limit <= 0)
        {
            _root.Visible = false;
            return;
        }
        _root.Visible = true;
        if (limit != _limit) Notch(limit);
        int left = limit - turn + 1;
        float x = Left + (Right - Left) * Math.Clamp((turn - 1) / (float)limit, 0f, 1f);
        bool moved = _turn >= 0 && turn != _turn;
        _turn = turn;

        Tween burn = _root.CreateTween().SetParallel();
        double seconds = moved ? 0.7 / TableFx.Speed : 0.01;
        burn.TweenProperty(_spark, "position", new Vector2(x, Y), seconds).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        burn.TweenMethod(Callable.From<float>(v => _burnt.Points = [new Vector2(Left, Y), new Vector2(v, Y)]), _burnt.Points[1].X, x, seconds);
        if (moved && left >= 1) TableFx.Sound("whoosh1", 1.6f, 0.25f);

        _pulse?.Kill();
        _cap.Modulate = Colors.White;
        if (left >= 2)
        {
            _spark.Visible = true;
            _sparks.Emitting = true;
            _sparks.Amount = 18;
            Label($"Overtime in {left} turns", StsColors.cream);
            Cap(false);
        }
        else if (left == 1)
        {
            // The last safe turn: the spark flares, the label and the cap warn.
            _spark.Visible = true;
            _sparks.Emitting = true;
            _sparks.Amount = 30;
            Label("Last turn before Overtime!", Warn);
            TableFx.Juice(_label, 0.25f);
            Cap(false);
            _pulse = _cap.CreateTween().SetLoops();
            _pulse.TweenProperty(_cap, "modulate", new Color(1.5f, 1.2f, 1f), 0.35);
            _pulse.TweenProperty(_cap, "modulate", Colors.White, 0.35);
        }
        else
        {
            // Overtime: the fuse is spent and the cap burns red.
            _spark.Visible = false;
            _sparks.Emitting = false;
            _burnt.Points = [new Vector2(Left, Y), new Vector2(Right, Y)];
            Label($"Overtime: enemies +{strength} Strength", new Color("FE5F55"));
            Cap(true);
            _pulse = _cap.CreateTween().SetLoops();
            _pulse.TweenProperty(_cap, "modulate", new Color(1.4f, 1.1f, 1.1f), 0.3);
            _pulse.TweenProperty(_cap, "modulate", Colors.White, 0.3);
            TableFx.Juice(_cap, 0.2f);
            if (left == 0) TaskHelper.RunSafely(GoOffAfterBanner());
        }
    }

    private void Notch(int limit)
    {
        _limit = limit;
        foreach (Node n in _notches.GetChildren()) n.QueueFree();
        for (int i = 1; i < limit; i++)
        {
            float x = Left + (Right - Left) * i / limit;
            _notches.AddChild(new ColorRect { Position = new Vector2(x - 1.5f, Y - 8), Size = new Vector2(3, 16), Color = RopeEdge, MouseFilter = Control.MouseFilterEnum.Ignore });
        }
    }

    private void Label(string text, Color colour)
    {
        _label.Text = text;
        _label.LabelSettings.FontColor = colour;
    }

    private void Cap(bool lit)
    {
        _cap.AddThemeStyleboxOverride("panel", lit
            ? TableStyle.Box(new Color("FE5F55"), new Color("FFD1CC"), 9, 2, 12)
            : TableStyle.Box(new Color("5A1E1A"), new Color("FE5F55", 0.5f), 9, 2, 4));
        _capText.LabelSettings.FontColor = lit ? Colors.White : new Color(StsColors.cream, 0.7f);
    }

    // OVERTIME lands once StS2's "Player Turn" banner has gone (about two seconds), not on it.
    private async Task GoOffAfterBanner()
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        ulong deadline = Time.GetTicksMsec() + 3000;
        do
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (!GodotObject.IsInstanceValid(_stage) || !_stage.IsInsideTree()) return;
        }
        while (Time.GetTicksMsec() < deadline && Banner());
        BridgeMod.LogInfo("jimbo: Overtime begins");
        GoOff();
    }

    // StS2 puts its turn banner straight on the combat room.
    private static bool Banner() =>
        NCombatRoom.Instance is NCombatRoom room && GodotObject.IsInstanceValid(room) && room.GetChildren().Any(c => c is NPlayerTurnBanner);

    // The fuse reaches the end: a burst at the cap, and OVERTIME slams down over the table.
    private void GoOff()
    {
        TableFx.Embers(_root, new Vector2(Right + 62, Y), 30, new Color("FE5F55"));
        TableFx.Juice(_cap, 0.4f);
        Label word = TableStyle.Text("OVERTIME", 120, new Color("FE5F55"), new Rect2(460, 300, 1000, 150), TableStyle.Title, 18);
        word.Scale = Vector2.One * 2.2f;
        word.Modulate = new Color(1, 1, 1, 0);
        word.Rotation = -0.06f;
        _stage.AddChild(word);
        Tween t = word.CreateTween();
        t.SetParallel();
        t.TweenProperty(word, "scale", Vector2.One, 0.35 / TableFx.Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        t.TweenProperty(word, "modulate:a", 1f, 0.15 / TableFx.Speed);
        t.TweenProperty(word, "rotation", 0.02f, 0.35 / TableFx.Speed);
        t.Chain().TweenCallback(Callable.From(() => TableFx.Embers(_stage, new Vector2(960, 380), 40, new Color("FE5F55"))));
        t.Chain().TweenInterval(1.0 / TableFx.Speed);
        t.Chain().TweenProperty(word, "modulate:a", 0f, 0.4 / TableFx.Speed);
        t.Chain().TweenCallback(Callable.From(word.QueueFree));
        t.Chain().TweenCallback(Callable.From(() =>
        {
            Action? then = _afterSlam;
            _afterSlam = null;
            then?.Invoke();
        }));
    }
}
