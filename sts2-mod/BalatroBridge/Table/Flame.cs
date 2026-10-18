using Godot;

namespace BalatroBridge.Table;

/// <summary>
/// A burning panel: rising, flickering fire in chunky pixels like Balatro's art, brightest at the
/// base and the middle, licking up and thinning out at the top. <c>heat</c> (0 to about 1.5) sets
/// how tall and how bright it burns; tween it to flare up or die down.
/// </summary>
internal static class Flame
{
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

    public static readonly (Color Core, Color Edge) Fire = (new Color(1f, 0.95f, 0.65f), new Color(0.95f, 0.25f, 0.08f));
    public static readonly (Color Core, Color Edge) Chips = (new Color(0.85f, 0.97f, 1f), new Color(0.1f, 0.45f, 1f));
    public static readonly (Color Core, Color Edge) Mult = (new Color(1f, 0.93f, 0.7f), new Color(1f, 0.18f, 0.12f));

    /// <summary>A panel of fire over <paramref name="rect"/> (it burns upward from the rect's bottom edge), unlit until heated.</summary>
    public static ColorRect Create(Rect2 rect, (Color Core, Color Edge) colours, bool glow = false)
    {
        var material = new ShaderMaterial { Shader = glow ? Glow : Normal };
        material.SetShaderParameter("core_colour", colours.Core);
        material.SetShaderParameter("edge_colour", colours.Edge);
        material.SetShaderParameter("seed", GD.Randf() * 50f);
        material.SetShaderParameter("pixels", new Vector2(Mathf.Max(12f, rect.Size.X / 5f), Mathf.Max(16f, rect.Size.Y / 5f)));
        material.SetShaderParameter("heat", 0f);
        return new ColorRect { Position = rect.Position, Size = rect.Size, Material = material, MouseFilter = Control.MouseFilterEnum.Ignore };
    }

    /// <summary>Tweens a flame's heat (flaring up, settling, dying down).</summary>
    public static Tween Heat(ColorRect flame, float to, double seconds)
    {
        var material = (ShaderMaterial)flame.Material;
        float from = (float)material.GetShaderParameter("heat");
        Tween t = flame.CreateTween();
        t.TweenMethod(Callable.From<float>(h => material.SetShaderParameter("heat", h)), from, to, seconds / TableFx.Speed);
        return t;
    }
}
