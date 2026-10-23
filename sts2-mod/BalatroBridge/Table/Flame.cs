using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Godot;

namespace BalatroBridge.Table;

/// <summary>
/// A burning panel: Balatro's own score flames (resources/shaders/flame.fs, see
/// <see cref="BalatroShaders"/>), the fire that rises off its Chips and Mult when a hand beats the
/// blind. <c>heat</c> (0 to about 1.5) sets how tall and how bright it burns; tween it to flare up
/// or die down. Its colour is the flame's own, its tips that colour mixed with yellow as Balatro's
/// flame_handler mixes them. Without the build's shaders, the mod's own drawing of a pixel fire
/// (below) stands in.
/// </summary>
internal static class Flame
{
    // Balatro's flame grows with its "amount" (a hand's log-5 score past 100, 0 to 10): heat 1 is a
    // big hand's 3.
    private const float AmountPerHeat = 3f;

    private const string BalatroWrapper = """
        void fragment() {
            vec4 c = effect(COLOR, TEXTURE, UV, FRAGCOORD.xy);
            COLOR = vec4(c.rgb, c.a * COLOR.a);
        }
        """;

    private static Shader? _balatroNormal;
    private static Shader? _balatroGlow;
    private static bool _tried;

    private static Shader? Balatros(bool glow)
    {
        if (!_tried)
        {
            _tried = true;
            if (BalatroShaders.Library("flame", "time") is string library)
            {
                // The flame's amount follows the mod's heat; its clock runs faster as it burns hotter.
                library = Regex.Replace(library, @"uniform\s+float\s+amount\s*;",
                    "uniform float heat = 0.0;\nuniform float amount_scale = " + AmountPerHeat.ToString("0.0###", CultureInfo.InvariantCulture) + ";\n#define amount (heat * amount_scale)");
                string code = library + BalatroWrapper;
                _balatroNormal = new Shader { Code = code };
                _balatroGlow = new Shader { Code = code.Replace("shader_type canvas_item;", "shader_type canvas_item;\nrender_mode blend_add;") };
                BridgeMod.LogInfo("table: drawing Balatro's own flame shader");
            }
        }
        return glow ? _balatroGlow : _balatroNormal;
    }

    // The tip of a Balatro flame: its colour half mixed with yellow, lifted and squared (flame_handler's lick).
    private static Color Lick(Color c)
    {
        float Channel(float v, float yellow) => Math.Clamp(MathF.Pow(v * 0.5f + yellow * 0.5f + 0.1f, 2), 0.1f, 1f);
        return new Color(Channel(c.R, 1), Channel(c.G, 1), Channel(c.B, 0));
    }

    private const string Body = """
        uniform float heat = 1.0;
        uniform vec4 core_colour : source_color = vec4(1.0, 0.95, 0.65, 1.0);
        uniform vec4 edge_colour : source_color = vec4(0.95, 0.25, 0.08, 1.0);
        uniform vec2 pixels = vec2(40.0, 56.0);
        uniform float seed = 0.0;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p) {
            vec2 i = floor(p);
            vec2 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
        }
        float fbm(vec2 p) {
            float v = 0.0;
            float a = 0.5;
            for (int i = 0; i < 5; i++) { v += a * noise(p); p = p * 2.03 + 1.7; a *= 0.5; }
            return v;
        }
        void fragment() {
            vec2 uv = floor(UV * pixels) / pixels;
            float up = 1.0 - uv.y;
            float across = abs(uv.x - 0.5) * 2.0;
            vec2 p = vec2(uv.x * 4.0 + seed, up * 3.0 - TIME * 2.2);
            float n = fbm(p + vec2(fbm(p * 0.6 + TIME * 0.3), 0.0));
            float body = 1.0 - up / (0.4 + 0.65 * heat) - across * across * 0.9;
            float f = clamp(body + (n - 0.5) * 1.1, 0.0, 1.0);
            float a = smoothstep(0.08, 0.3, f) * min(heat * 1.6, 1.0);
            vec3 c = mix(edge_colour.rgb, core_colour.rgb, smoothstep(0.35, 0.85, f));
            COLOR = vec4(c, a * COLOR.a);
        }
        """;

    private static Shader? _normal;
    private static Shader? _glow;

    // Behind things, plain; in front of them, added on as light so what's under it shows through.
    private static Shader Normal => _normal ??= new Shader { Code = "shader_type canvas_item;\n" + Body };
    private static Shader Glow => _glow ??= new Shader { Code = "shader_type canvas_item;\nrender_mode blend_add;\n" + Body };

    // Each flame's colour (Balatro's: its Chips blue and Mult red, and an orange for Jimbo's own
    // blaze), and the stand-in's core and edge for it.
    public static readonly (Color Core, Color Edge) Fire = (new Color(1f, 0.95f, 0.65f), new Color(0.95f, 0.25f, 0.08f));
    public static readonly (Color Core, Color Edge) Chips = (new Color(0.85f, 0.97f, 1f), TableStyle.Chips);
    public static readonly (Color Core, Color Edge) Mult = (new Color(1f, 0.93f, 0.7f), TableStyle.Mult);

    /// <summary>
    /// A panel of fire over <paramref name="rect"/> (it burns upward from the rect's bottom edge),
    /// unlit until heated. <paramref name="strength"/> makes each degree of heat burn harder (a
    /// body going up in flames rather than a score).
    /// </summary>
    public static ColorRect Create(Rect2 rect, (Color Core, Color Edge) colours, bool glow = false, float strength = 1f)
    {
        ShaderMaterial material;
        if (Balatros(glow) is Shader balatros)
        {
            material = new ShaderMaterial { Shader = balatros };
            material.SetShaderParameter("colour_1", colours.Edge);
            material.SetShaderParameter("colour_2", Lick(colours.Edge));
            // The flame is drawn for Balatro's square sprite, which sits on the bottom of what
            // burns: a wider rect gets a square flame in its middle, a taller one a square flame
            // at its foot, rather than a stretched one. Each flame flickers on its own.
            float wide = rect.Size.X / Mathf.Max(rect.Size.Y, 1f), tall = 1f / Mathf.Max(wide, 0.0001f);
            material.SetShaderParameter("texture_details", wide >= 1f
                ? new Vector4((wide - 1f) / 2f, 0, 1f / wide, 1)
                : new Vector4(0, tall - 1f, 1, 1f / tall));
            material.SetShaderParameter("image_details", new Vector2(1, 1));
            material.SetShaderParameter("id", GD.Randf() * 1000f);
            material.SetShaderParameter("time_offset", GD.Randf() * 100f);
            material.SetShaderParameter("amount_scale", AmountPerHeat * strength);
        }
        else
        {
            material = new ShaderMaterial { Shader = glow ? Glow : Normal };
            material.SetShaderParameter("core_colour", colours.Core);
            material.SetShaderParameter("edge_colour", colours.Edge);
            material.SetShaderParameter("seed", GD.Randf() * 50f);
            material.SetShaderParameter("pixels", new Vector2(Mathf.Max(12f, rect.Size.X / 5f), Mathf.Max(16f, rect.Size.Y / 5f)));
        }
        material.SetShaderParameter("heat", 0f);
        return new ColorRect { Position = rect.Position, Size = rect.Size, Material = material, MouseFilter = Control.MouseFilterEnum.Ignore };
    }

    /// <summary>Tweens a flame's heat (flaring up, settling, dying down).</summary>
    public static Tween Heat(ColorRect flame, float to, double seconds)
    {
        var material = (ShaderMaterial)flame.Material;
        float from = (float)material.GetShaderParameter("heat");
        Tween t = flame.CreateTween();
        t.TweenMethod(Callable.From<float>(h =>
        {
            material.SetShaderParameter("heat", h);
            // Balatro's flame clock speeds up with its intensity (flame_handler's timer).
            Variant scale = material.GetShaderParameter("amount_scale");
            material.SetShaderParameter("time_rate", 1f + 0.2f * h * (scale.VariantType == Variant.Type.Float ? (float)scale : AmountPerHeat));
        }), from, to, seconds / TableFx.Speed);
        return t;
    }
}
