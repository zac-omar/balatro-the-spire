using BalatroBridge.Core;
using Godot;

namespace BalatroBridge.Table;

/// <summary>
/// Balatro's background swirl, drawn by Balatro's own shader (resources/shaders/background.fs,
/// see <see cref="BalatroShaders"/>) in the colours, contrast and spin the hidden Balatro is
/// showing right now (<see cref="BackgroundInfo"/>). Around it, the mod's own touches: an iris
/// that opens out of a point with a gold rim (<c>reveal</c>, <c>origin</c>, <c>rim</c>), a little
/// of the Spire showing through (<c>alpha</c>), a fade at the top edge (<c>top_fade</c>, the
/// fight's felt band) and a wind-up (<c>spin</c>). When the build has no Balatro shaders, the
/// mod's older drawing of the swirl stands in, coloured from the same info.
/// </summary>
internal static class BalatroBackground
{
    // Wraps the translated background.fs: its effect() draws the swirl at screen_coords on a
    // screen of love_ScreenSize; rect_origin and rect_size place this rect on that screen.
    private const string Wrapper = """
        uniform float reveal = 1.0;
        uniform vec2 origin = vec2(0.5, 0.5);
        uniform vec4 rim : source_color = vec4(0.94, 0.78, 0.32, 1.0);
        uniform float alpha = 0.88;
        uniform float top_fade = 0.0;
        uniform vec2 rect_origin = vec2(0.0, 0.0);
        uniform vec2 rect_size = vec2(1920.0, 1080.0);
        void fragment() {
            float glow = 0.0;
            if (reveal < 0.999) {
                vec2 out_of = (UV - origin) * vec2(rect_size.x / rect_size.y, 1.0);
                float wobble = 0.12 * reveal * (1.0 - reveal) * sin(atan(out_of.y, out_of.x) * 7.0 + TIME * 6.0);
                float edge = reveal * 2.1 + wobble - length(out_of);
                if (edge < 0.0) {
                    discard;
                }
                glow = 1.0 - smoothstep(0.0, 0.06, edge);
            }
            vec4 col = effect(vec4(1.0), TEXTURE, UV, rect_origin + UV * rect_size);
            float a = mix(alpha, 1.0, glow);
            if (top_fade > 0.0) {
                a *= smoothstep(0.0, top_fade, UV.y);
            }
            COLOR = vec4(mix(col.rgb, rim.rgb, glow), a);
        }
        """;

    private static Shader? _balatros;
    private static bool _tried;

    /// <summary>Balatro's own background shader with the mod's wrapper, or null without the build's shaders.</summary>
    private static Shader? BalatrosShader()
    {
        if (_tried) return _balatros;
        _tried = true;
        string? library = BalatroShaders.Library("background", "time", "spin_time");
        if (library == null) return null;
        // The wind-up turns the swirl's own spin clock.
        library = library.Replace("uniform float spin_time_offset = 0.0;", "uniform float spin_time_offset = 0.0;\nuniform float spin = 0.0;")
                         .Replace("#define spin_time (TIME * spin_time_rate + spin_time_offset)", "#define spin_time (TIME * spin_time_rate + spin_time_offset + spin * 12.0)");
        _balatros = new Shader { Code = library + Wrapper };
        BridgeMod.LogInfo("table: drawing Balatro's own background shader");
        return _balatros;
    }

    /// <summary>Whether <paramref name="material"/> draws Balatro's own shader (else the mod's stand-in).</summary>
    public static bool IsBalatros(ShaderMaterial material) => _balatros != null && material.Shader == _balatros;

    /// <summary>A material for a rect showing the background, Balatro's shader if there is one, else <paramref name="fallbackCode"/>.</summary>
    public static ShaderMaterial Create(string fallbackCode, Vector2 screen, Vector2? rectOrigin = null, Vector2? rectSize = null)
    {
        Shader? shader = BalatrosShader();
        var material = new ShaderMaterial { Shader = shader ?? new Shader { Code = fallbackCode } };
        if (shader != null)
        {
            material.SetShaderParameter("love_ScreenSize", new Vector4(screen.X, screen.Y, 0, 0));
            material.SetShaderParameter("rect_origin", rectOrigin ?? Vector2.Zero);
            material.SetShaderParameter("rect_size", rectSize ?? screen);
            Set(material, Fallback);
        }
        return material;
    }

    /// <summary>
    /// Until the first snapshot says otherwise: a Small Blind's background, its green (50846E) at
    /// the brightnesses ease_background_colour gives its three colours (0.9, 1.3 and 0.7).
    /// </summary>
    private static readonly BackgroundInfo Fallback = new(
        [0.282f, 0.466f, 0.388f, 1f], [0.408f, 0.673f, 0.561f, 1f], [0.220f, 0.362f, 0.302f, 1f], 1, 0);

    private static Color C(float[] c) => new(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1);

    /// <summary>Shows <paramref name="info"/> now.</summary>
    public static void Set(ShaderMaterial material, BackgroundInfo info)
    {
        if (IsBalatros(material))
        {
            material.SetShaderParameter("colour_1", C(info.Colour1));
            material.SetShaderParameter("colour_2", C(info.Colour2));
            material.SetShaderParameter("colour_3", C(info.Colour3));
            material.SetShaderParameter("contrast", (float)info.Contrast);
            material.SetShaderParameter("spin_amount", (float)info.Spin);
        }
        else
        {
            // The stand-in's two colours: the felt and its shadow.
            material.SetShaderParameter("colour_1", C(info.Colour1));
            material.SetShaderParameter("colour_2", C(info.Colour3).Darkened(0.5f));
        }
    }

    /// <summary>Eases to <paramref name="info"/> over Balatro's 0.6 seconds, on <paramref name="owner"/>'s tween.</summary>
    public static Tween? Ease(ShaderMaterial material, BackgroundInfo? info, Node owner, double seconds = 0.6)
    {
        if (info == null || !GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree()) return null;
        Tween t = owner.CreateTween().SetParallel();
        void Colour(string name, Color to)
        {
            Color from = material.GetShaderParameter(name).VariantType == Variant.Type.Color ? (Color)material.GetShaderParameter(name) : to;
            t.TweenMethod(Callable.From<Color>(c => material.SetShaderParameter(name, c)), from, to, seconds);
        }
        void Number(string name, float to)
        {
            Variant now = material.GetShaderParameter(name);
            float from = now.VariantType == Variant.Type.Float ? (float)now : to;
            t.TweenMethod(Callable.From<float>(v => material.SetShaderParameter(name, v)), from, to, seconds);
        }
        if (IsBalatros(material))
        {
            Colour("colour_1", C(info.Colour1));
            Colour("colour_2", C(info.Colour2));
            Colour("colour_3", C(info.Colour3));
            Number("contrast", (float)info.Contrast);
            Number("spin_amount", (float)info.Spin);
        }
        else
        {
            Colour("colour_1", C(info.Colour1));
            Colour("colour_2", C(info.Colour3).Darkened(0.5f));
        }
        return t;
    }
}
