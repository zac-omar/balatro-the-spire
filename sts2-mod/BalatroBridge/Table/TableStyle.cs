using System;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace BalatroBridge.Table;

/// <summary>
/// Fonts, colours, widgets and sounds for the Balatro table. Everything comes from StS2 itself
/// (its fonts, palette and sound events) except the scoring colours, which stay Balatro's so
/// chips and mult read the way Balatro players expect.
/// </summary>
internal static class TableStyle
{
    public static readonly Color Chips = new("009DFF");
    public static readonly Color Mult = new("FE5F55");
    public static readonly Color Money = new("F3B958");
    public static readonly Color Special = new("FF9A00");
    public static readonly Color Edition = new("8D7BFF");
    public static readonly Color Ink = new(0.07f, 0.05f, 0.04f);
    public static readonly Color PanelFill = new(0.05f, 0.08f, 0.11f, 0.92f);
    public static readonly Color PanelEdge = new(StsColors.gold, 0.6f);
    public static readonly Color DefaultFelt = new("3E6B5A");

    public static readonly Font Bold = LoadFont("res://fonts/kreon_bold.ttf");
    public static readonly Font Regular = LoadFont("res://fonts/kreon_regular.ttf");
    public static readonly Font Title = LoadFont("res://fonts/spectral_bold.ttf");

    /// <summary>Animation speed multiplier, following StS2's fast-mode setting (normal until prefs load).</summary>
    public static float Speed => SaveManager.Instance?.PrefsSave?.FastMode switch
    {
        FastModeType.Fast => 1.6f,
        FastModeType.Instant => 3f,
        _ => 1f,
    };

    public static Label Text(string text, int size, Color colour, Font? font = null, int outline = 8)
    {
        return new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = new LabelSettings
            {
                Font = font ?? Bold,
                FontSize = size,
                FontColor = colour,
                OutlineSize = outline,
                OutlineColor = Ink,
                ShadowSize = 1,
                ShadowColor = new Color(0, 0, 0, 0.45f),
                ShadowOffset = new Vector2(2, 3),
            },
        };
    }

    /// <summary>A label of fixed size whose text stays centred in it.</summary>
    public static Label Text(string text, int size, Color colour, Rect2 rect, Font? font = null, int outline = 8)
    {
        Label label = Text(text, size, colour, font, outline);
        label.Position = rect.Position;
        label.Size = rect.Size;
        label.PivotOffset = rect.Size / 2;
        return label;
    }

    /// <summary>
    /// Wrapping text in StS2's markup style: [gold], [blue], [red] and its other colour spans
    /// become colours. It grows to fit its text; give it a width with CustomMinimumSize or Size.
    /// </summary>
    public static RichTextLabel RichText(string text, int size, bool centred = true)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = Markup(text, centred),
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontOverride("normal_font", Regular);
        label.AddThemeFontSizeOverride("normal_font_size", size);
        label.AddThemeColorOverride("default_color", StsColors.cream);
        label.AddThemeConstantOverride("outline_size", 6);
        label.AddThemeColorOverride("font_outline_color", Ink);
        return label;
    }

    /// <summary>StS2 markup (and Balatro's text, which the bridge writes in it) as plain BBCode.</summary>
    public static string Markup(string text, bool centred = true)
    {
        string bbcode = Regex.Replace(text, @"\[(gold|blue|red|green|orange|purple|aqua|pink)\]", m => $"[color=#{TagColour(m.Groups[1].Value).ToHtml(false)}]");
        bbcode = Regex.Replace(bbcode, @"\[/(gold|blue|red|green|orange|purple|aqua|pink)\]", "[/color]");
        return centred ? "[center]" + bbcode + "[/center]" : bbcode;
    }

    private static Color TagColour(string tag) => tag switch
    {
        "gold" => StsColors.gold,
        "blue" => Chips,
        "green" => new Color("4BC292"),
        "orange" => Special,
        "purple" => new Color("A782D1"),
        "aqua" => new Color("13AFCE"),
        "pink" => new Color("F06BC0"),
        _ => Mult,
    };

    public static StyleBoxFlat Box(Color fill, Color? edge = null, int radius = 12, int edgeWidth = 0, int shadow = 0)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = edge ?? Colors.Transparent,
            ShadowColor = new Color(0, 0, 0, 0.45f),
            ShadowSize = shadow,
            ShadowOffset = new Vector2(0, 4),
        };
        box.SetCornerRadiusAll(radius);
        box.SetBorderWidthAll(edgeWidth);
        box.SetContentMarginAll(10);
        return box;
    }

    public static Panel Panel(Rect2 rect, StyleBoxFlat box)
    {
        var panel = new Panel
        {
            Position = rect.Position,
            Size = rect.Size,
            PivotOffset = rect.Size / 2,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", box);
        return panel;
    }

    public static Button Button(string text, Color colour, Rect2 rect, int fontSize = 30)
    {
        var button = new Button
        {
            Text = text,
            Position = rect.Position,
            Size = rect.Size,
            PivotOffset = rect.Size / 2,
            FocusMode = Control.FocusModeEnum.None,
        };
        button.AddThemeFontOverride("font", Bold);
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeConstantOverride("outline_size", 7);
        button.AddThemeColorOverride("font_outline_color", Ink);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", StsColors.cream);
        button.AddThemeColorOverride("font_disabled_color", new Color(StsColors.cream, 0.35f));
        button.AddThemeStyleboxOverride("normal", Box(colour, new Color(StsColors.cream, 0.55f), 14, 3, 8));
        button.AddThemeStyleboxOverride("hover", Box(colour.Lightened(0.18f), StsColors.cream, 14, 3, 14));
        button.AddThemeStyleboxOverride("pressed", Box(colour.Darkened(0.2f), StsColors.cream, 14, 3, 4));
        button.AddThemeStyleboxOverride("disabled", Box(new Color(0.22f, 0.22f, 0.25f, 0.92f), new Color(1, 1, 1, 0.12f), 14, 3, 4));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        return button;
    }

    /// <summary>Plays one of StS2's own sound events, e.g. "ui/clicks/ui_click".</summary>
    public static void Sfx(string name, float volume = 1f)
    {
        try
        {
            SfxCmd.Play("event:/sfx/" + name, volume);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"sound {name} failed: {e.Message}");
        }
    }

    private static Font LoadFont(string path) =>
        ResourceLoader.Exists(path) ? ResourceLoader.Load<Font>(path) : ThemeDB.FallbackFont;
}
