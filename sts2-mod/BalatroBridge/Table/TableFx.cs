using System;
using System.Threading.Tasks;
using Godot;

namespace BalatroBridge.Table;

/// <summary>
/// The table's little motions: cards gliding home, juicy pops, fades, shakes, pixel embers and
/// waits, all at StS2's fast-mode speed. Shared by Jimbo's tables (the Deal In table keeps its own).
/// </summary>
internal static class TableFx
{
    public static float Speed => TableStyle.Speed;

    /// <summary>Slides a card to a centre point and rotation.</summary>
    public static void Move(CardView view, Vector2 center, float rotation, float seconds, float delay = 0,
        Tween.TransitionType transition = Tween.TransitionType.Cubic)
    {
        view.MoveTween?.Kill();
        Tween t = view.Root.CreateTween().SetParallel();
        t.TweenProperty(view.Root, "position", center - view.Root.Size / 2, seconds / Speed).SetDelay(delay / Speed)
            .SetTrans(transition).SetEase(Tween.EaseType.Out);
        t.TweenProperty(view.Root, "rotation", rotation, seconds / Speed).SetDelay(delay / Speed)
            .SetTrans(transition).SetEase(Tween.EaseType.Out);
        view.MoveTween = t;
    }

    /// <summary>Balatro's juice: a quick swell that springs back.</summary>
    public static void Juice(Control control, float amount)
    {
        control.Scale = Vector2.One * (1 + amount);
        Tween t = control.CreateTween();
        t.TweenProperty(control, "scale", Vector2.One, 0.25 / Speed).SetTrans(Tween.TransitionType.Elastic).SetEase(Tween.EaseType.Out);
    }

    public static void Fade(CanvasItem item, float alpha, float seconds, float delay = 0)
    {
        item.CreateTween().TweenProperty(item, "modulate:a", alpha, seconds / Speed).SetDelay(delay / Speed);
    }

    public static void Shake(Control stage, Vector2 home, float strength)
    {
        Tween t = stage.CreateTween();
        for (int i = 0; i < 8; i++)
        {
            float falloff = 1f - i / 8f;
            var offset = new Vector2((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0)) * strength * falloff;
            t.TweenProperty(stage, "position", home + offset, 0.035);
        }
        t.TweenProperty(stage, "position", home, 0.05);
    }

    /// <summary>A burst of square, Balatro-style pixel embers.</summary>
    public static void Embers(Node parent, Vector2 at, int amount, Color colour)
    {
        var ramp = new Gradient();
        ramp.SetColor(0, colour.Lightened(0.4f));
        ramp.SetColor(1, new Color(colour.Darkened(0.3f), 0f));
        var embers = new CpuParticles2D
        {
            Position = at,
            Amount = Math.Max(amount, 4),
            Lifetime = 0.9,
            OneShot = true,
            Explosiveness = 0.95f,
            Direction = Vector2.Up,
            Spread = 70,
            Gravity = new Vector2(0, 700),
            InitialVelocityMin = 260,
            InitialVelocityMax = 620,
            ScaleAmountMin = 5,
            ScaleAmountMax = 11,
            ColorRamp = ramp,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 40,
            Emitting = true,
        };
        parent.AddChild(embers);
        embers.Finished += embers.QueueFree;
    }

    /// <summary>A "+30" / "X2 Mult" bubble rising off a point, with Balatro's sound for it.</summary>
    public static void Popup(Node parent, Vector2 at, string text, string kind)
    {
        (Color fill, Color ink, string sound, float volume) = kind switch
        {
            "chips" => (TableStyle.Chips, Colors.White, "chips1", 1f),
            "mult" => (TableStyle.Mult, Colors.White, "multhit1", 1f),
            "xmult" => (TableStyle.Mult.Darkened(0.15f), Colors.White, "multhit2", 0.7f),
            "money" => (TableStyle.Money, TableStyle.Ink, "coin3", 1f),
            "debuff" => (new Color(0.15f, 0.15f, 0.18f), MegaCrit.Sts2.Core.Helpers.StsColors.red, "cancel", 1f),
            "edition" => (TableStyle.Edition, Colors.White, "foil2", 0.3f),
            _ => (TableStyle.Special, Colors.White, "generic1", 1f),
        };
        BalatroAudio.Instance.Play(sound, 0.8f + 0.2f * (0.9f + 0.2f * GD.Randf()), volume);
        if (kind == "xmult") Embers(parent, at, 18, TableStyle.Mult);

        var bubble = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bubble.AddThemeStyleboxOverride("panel", TableStyle.Box(fill, new Color(1, 1, 1, 0.4f), 10, 2, 6));
        bubble.AddChild(TableStyle.Text(text, kind == "xmult" ? 36 : 30, ink));
        parent.AddChild(bubble);
        bubble.Size = bubble.GetCombinedMinimumSize();
        bubble.PivotOffset = bubble.Size / 2;
        bubble.Position = at - bubble.Size / 2 + new Vector2((float)GD.RandRange(-12, 12), 0);
        bubble.Scale = new Vector2(0.3f, 0.3f);

        Tween t = bubble.CreateTween();
        t.TweenProperty(bubble, "scale", Vector2.One, 0.14 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        t.TweenInterval(0.35 / Speed);
        t.SetParallel();
        t.TweenProperty(bubble, "position:y", bubble.Position.Y - 60, 0.5 / Speed);
        t.TweenProperty(bubble, "modulate:a", 0f, 0.5 / Speed);
        t.Chain().TweenCallback(Callable.From(bubble.QueueFree));
    }

    public static Task Wait(float seconds) =>
        Signal(((SceneTree)Engine.GetMainLoop()).CreateTimer(seconds / Speed), SceneTreeTimer.SignalName.Timeout);

    public static Task Finished(Tween tween) => Signal(tween, Tween.SignalName.Finished);

    private static async Task Signal(GodotObject source, StringName signal)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(source, signal);
    }

    /// <summary>Balatro's card hover: a soft paper flick at a slightly random pitch.</summary>
    public static void HoverSound() => BalatroAudio.Instance.Play("paper1", 0.9f + 0.2f * GD.Randf(), 0.35f);

    public static void Sound(string sound, float pitch = 1f, float volume = 1f) => BalatroAudio.Instance.Play(sound, pitch, volume);
}
