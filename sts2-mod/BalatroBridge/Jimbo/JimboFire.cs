using System;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BalatroBridge.Jimbo;

/// <summary>
/// A hand that dwarfs what it's thrown at catches fire, as a hand that beats the blind does in
/// Balatro: the score burns as it counts up (see <see cref="JimboHand.Score"/>), and Jimbo goes up
/// in flames for the volley, fire roaring up behind and through him and embers flying, with a
/// crackle under it all. The hotter the hand, the bigger the blaze.
/// </summary>
internal static class JimboFire
{
    private const string Name = "BalatroFire";
    private static AudioStreamPlayer? _crackle;

    /// <summary>
    /// How hot a hand is against its target, 0 to 1: nothing under one and a half times the
    /// target's HP (Block included), full blaze from six times.
    /// </summary>
    public static float Heat(decimal score, Creature? target)
    {
        if (target is not { IsAlive: true }) return 0f;
        decimal toughness = target.CurrentHp + target.Block;
        if (toughness <= 0) return 0f;
        double ratio = (double)(score / toughness);
        return ratio < 1.5 ? 0f : (float)Math.Clamp(0.35 + (ratio - 1.5) / 4.5 * 0.65, 0.35, 1.0);
    }

    /// <summary>Jimbo bursts into flame at <paramref name="heat"/>, until <see cref="Douse"/>.</summary>
    public static void Ignite(Player player, float heat)
    {
        if (NCombatRoom.Instance?.GetCreatureNode(player.Creature) is not NCreature node || !GodotObject.IsInstanceValid(node)) return;
        Douse(node, quickly: true);
        // His card, in his node's own space.
        Rect2 card = node.GetGlobalTransformWithCanvas().AffineInverse() * node.Hitbox.GetGlobalRect();
        var fire = new Control { Name = Name, MouseFilter = Control.MouseFilterEnum.Ignore };
        node.AddChild(fire);

        // Behind him, a tall blaze from his feet; through him, a lighter glow; embers flying up.
        float width = card.Size.X * 2.1f, height = card.Size.Y * 2.3f;
        var bottom = new Vector2(card.GetCenter().X, card.End.Y + card.Size.Y * 0.08f);
        ColorRect back = Flame.Create(new Rect2(bottom.X - width / 2, bottom.Y - height, width, height), Flame.Fire);
        ColorRect front = Flame.Create(new Rect2(bottom.X - width * 0.35f, bottom.Y - height * 0.6f, width * 0.7f, height * 0.6f), Flame.Fire, glow: true);
        front.Modulate = new Color(1, 1, 1, 0.55f);
        node.AddChild(back);
        node.MoveChild(back, 0);
        back.Name = Name + "Back";
        fire.AddChild(front);

        var embers = new CpuParticles2D
        {
            Position = card.GetCenter() + new Vector2(0, card.Size.Y * 0.2f),
            Amount = (int)(20 + 40 * heat),
            Lifetime = 1.2,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            EmissionRectExtents = new Vector2(card.Size.X * 0.55f, card.Size.Y * 0.3f),
            Direction = Vector2.Up,
            Spread = 28,
            Gravity = new Vector2(0, -160),
            InitialVelocityMin = 90,
            InitialVelocityMax = 260,
            ScaleAmountMin = 3,
            ScaleAmountMax = 7,
            ColorRamp = new Gradient
            {
                Offsets = [0f, 0.4f, 1f],
                Colors = [new Color(1f, 0.95f, 0.6f), new Color(1f, 0.45f, 0.1f), new Color(0.8f, 0.1f, 0.05f, 0f)],
            },
        };
        fire.AddChild(embers);

        // It flares past its heat, then settles at it.
        Tween flare = back.CreateTween();
        flare.TweenMethod(Callable.From<float>(h => ((ShaderMaterial)back.Material).SetShaderParameter("heat", h)), 0f, heat * 1.3f, 0.18 / TableFx.Speed);
        flare.TweenMethod(Callable.From<float>(h => ((ShaderMaterial)back.Material).SetShaderParameter("heat", h)), heat * 1.3f, heat, 0.4 / TableFx.Speed);
        Flame.Heat(front, heat, 0.3);
        if (node.Visuals.FindChild("Body", recursive: true, owned: false) is Node2D body)
        {
            Tween pulse = body.CreateTween();
            pulse.TweenProperty(body, "scale", body.Scale * 1.12f, 0.08);
            pulse.TweenProperty(body, "scale", body.Scale, 0.25).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }

        TableFx.Sound("whoosh_long", 1.3f, 0.4f); // (the score's blaze already went up with a bang)
        BalatroAudio.StopLoop(_crackle, 0.2);
        _crackle = BalatroAudio.Instance.StartLoop("ambientFire2", 0.35f + 0.45f * heat);
    }

    /// <summary>The fire dies down (after the volley, or as the fight ends).</summary>
    public static void Douse(Player player)
    {
        if (NCombatRoom.Instance?.GetCreatureNode(player.Creature) is NCreature node && GodotObject.IsInstanceValid(node)) Douse(node, quickly: false);
        BalatroAudio.StopLoop(_crackle, 0.8);
        _crackle = null;
    }

    private static void Douse(NCreature node, bool quickly)
    {
        double seconds = quickly ? 0.05 : 0.6;
        foreach (string name in new[] { Name, Name + "Back" })
        {
            if (node.GetNodeOrNull<Control>(name) is not Control part) continue;
            part.Name = name + "Out"; // a new fire can take the name straight away
            Tween t = part.CreateTween();
            t.TweenProperty(part, "modulate:a", 0f, seconds / TableFx.Speed);
            t.TweenCallback(Callable.From(part.QueueFree));
            foreach (Node child in part.GetChildren())
            {
                if (child is CpuParticles2D embers) embers.Emitting = false;
            }
        }
    }
}
