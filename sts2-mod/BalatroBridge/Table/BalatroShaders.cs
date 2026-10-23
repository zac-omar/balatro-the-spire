using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BalatroBridge.Table;

/// <summary>
/// Balatro's own shaders (its background swirl, its score flames), read from the bridged build's
/// folder (tools/build_balatro.py extracts resources/shaders there) and turned from LÖVE's GLSL
/// into Godot's shading language, so StS2 draws Balatro's effects exactly as Balatro does without
/// any of them living in this repo. A shader becomes a library: its uniforms and its
/// <c>effect(colour, tex_in, texture_coords, screen_coords)</c>; each use adds a fragment() that
/// calls it. Null when the build has no shaders (an older build_balatro.py) or a shader won't
/// translate, so callers keep a drawing of their own to fall back on.
/// </summary>
internal static class BalatroShaders
{
    private static readonly ConcurrentDictionary<string, string?> Libraries = new();

    /// <summary>
    /// The shader <paramref name="name"/> ("background") as Godot shader code without a fragment(),
    /// or null. The uniforms named in <paramref name="clocks"/> become clocks instead: each is
    /// TIME times a uniform <c>NAME_rate</c> (1) plus a uniform <c>NAME_offset</c> (0), so they run
    /// on their own and can still be wound forward or slowed down.
    /// </summary>
    public static string? Library(string name, params string[] clocks) =>
        Libraries.GetOrAdd(name + "|" + string.Join(",", clocks), _ => Load(name, clocks));

    private static string? Load(string name, string[] clocks)
    {
        try
        {
            string root = Path.GetDirectoryName(BridgeMod.Config.BalatroExe ?? "") ?? "";
            string path = Path.Combine(root, "resources", "shaders", name + ".fs");
            if (!File.Exists(path))
            {
                BridgeMod.LogInfo($"Balatro shader missing: {path} (rebuild with tools/build_balatro.py); using the mod's own drawing");
                return null;
            }
            return Translate(File.ReadAllText(path), clocks);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"Balatro shader {name} couldn't be read: {e.Message}");
            return null;
        }
    }

    /// <summary>LÖVE's pixel-shader GLSL to Godot's: the same maths, Godot's words for it.</summary>
    public static string Translate(string love, IReadOnlyCollection<string> clocks)
    {
        string s = love.Replace("\r\n", "\n");
        // The precision preamble (GLSL ES only) and the vertex-shader part (the card tilt, which
        // the mod's card shader does its own way).
        s = Regex.Replace(s, @"#if\s+defined\(VERTEX\).*?#endif", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"#ifdef\s+VERTEX.*?#endif", "", RegexOptions.Singleline);
        s = s.Replace("MY_HIGHP_OR_MEDIUMP", "");
        s = Regex.Replace(s, @"\bextern\b", "uniform");
        s = Regex.Replace(s, @"\bnumber\b", "float");
        // LÖVE's texture lookup; "texture" itself is a function name in Godot.
        s = Regex.Replace(s, @"Texel\(\s*texture\s*,", "Texel(tex_in,");
        s = Regex.Replace(s, @"\bImage\s+texture\b", "sampler2D tex_in");
        s = s.Replace("Texel(", "texture(");
        // Godot wants its float literals whole: "2." is "2.0", ".5" is "0.5".
        s = Regex.Replace(s, @"(?<![\w.])(\d+)\.(?![\d\w])", "$1.0");
        s = Regex.Replace(s, @"(?<![\w.\d])\.(\d)", "0.$1");
        // Clocks: the uniform goes, a macro takes its name.
        var header = new List<string> { "shader_type canvas_item;", "uniform vec4 love_ScreenSize = vec4(1920.0, 1080.0, 0.0, 0.0);" };
        foreach (string clock in clocks)
        {
            s = Regex.Replace(s, @"uniform\s+\w+\s+" + Regex.Escape(clock) + @"\s*;", "");
            header.Add($"uniform float {clock}_rate = 1.0;");
            header.Add($"uniform float {clock}_offset = 0.0;");
            header.Add($"#define {clock} (TIME * {clock}_rate + {clock}_offset)");
        }
        return string.Join("\n", header) + "\n" + s.Trim() + "\n";
    }
}
