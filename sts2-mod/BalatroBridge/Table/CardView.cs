using BalatroBridge.Core;
using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Table;

/// <summary>
/// One Balatro card or joker on StS2's table: Balatro's layered sprites (enhancement, rank/suit,
/// seal), an edition shimmer, and selection state. A plain wrapper around stock Godot nodes.
/// </summary>
internal sealed class CardView
{
    public static readonly Vector2 FullSize = new(142, 190);

    /// <summary>The Cerulean Bell's blue (its boss colour in Balatro).</summary>
    public static readonly Color Cerulean = new("009CFD");

    // Foil, holographic and polychrome shimmer, and negative's inverted colours, in the spirit of
    // Balatro's edition shaders.
    private const string EditionShaderCode = """
        shader_type canvas_item;
        uniform int edition = 0;
        uniform float seed = 0.0;
        void fragment() {
            // Godot's COLOR comes in already multiplied by the texture: sampling it again would
            // square the art's colours (yellows go orange).
            vec4 c = COLOR;
            float t = TIME * 0.7 + seed;
            float band = sin((UV.x * 4.0 + UV.y * 6.0) * 3.14159 + t * 2.2) * 0.5 + 0.5;
            if (edition == 1) {
                c.rgb += vec3(0.25, 0.4, 0.75) * pow(band, 4.0) * 0.9;
            } else if (edition == 2) {
                c.rgb += mix(vec3(0.7, 0.15, 0.2), vec3(0.15, 0.7, 0.45), band) * pow(band, 2.0) * 0.4;
            } else if (edition == 3) {
                vec3 hue = 0.5 + 0.5 * cos(6.28318 * (band * 0.8 + t * 0.1 + vec3(0.0, 0.33, 0.67)));
                c.rgb = mix(c.rgb, c.rgb * hue * 1.7, 0.5);
            } else if (edition == 4) {
                c.rgb = vec3(1.0) - c.rgb;
                c.rgb = mix(c.rgb, vec3(c.r * 0.5, c.g * 0.4, c.b * 0.9 + 0.1), 0.4);
            }
            COLOR = c;
        }
        """;

    private static Shader? _editionShader;

    private readonly Panel _glow;
    private readonly System.Collections.Generic.List<TextureRect> _layers = new();

    public CardView(TableCard card, BalatroArt art, SpriteRef? back, float scale = 1f)
    {
        Card = card;
        Vector2 size = FullSize * scale;
        Root = new Control
        {
            Size = size,
            PivotOffset = size / 2,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };

        // The Cerulean Bell's card glows its colour: it's picked, and it stays picked.
        Color edge = card.Forced ? Cerulean : StsColors.gold;
        var glow = TableStyle.Box(new Color(edge, 0.12f), edge, 12, 4);
        glow.ShadowColor = new Color(edge, 0.55f);
        glow.ShadowSize = 18;
        glow.ShadowOffset = Vector2.Zero;
        _glow = TableStyle.Panel(new Rect2(-6, -6, size.X + 12, size.Y + 12), glow);
        _glow.Visible = false;
        Root.AddChild(_glow);

        bool faceDown = card.FaceDown;
        AddLayer(art.Sprite(faceDown ? back : card.Center), size);
        if (!faceDown)
        {
            if (!card.Stone) AddLayer(art.Sprite(card.Front), size);
            AddLayer(art.Sprite(card.SealSprite), size);
            AddLayer(art.Sprite(card.Soul), size);
            ApplyEdition(card.Edition);
        }
        if (card.Dud is int dud) AddStatusArt(card, dud, size);
        else if (card.Debuffed) AddDebuffMark(size);
    }

    /// <summary>The art for a status card in a Balatro deck (see Jimbo's JimboStatuses), by its ref.</summary>
    public static Func<int, Texture2D?>? StatusArt { get; set; }

    public TableCard Card { get; }

    public int Id => Card.Id;

    public Control Root { get; }

    public bool Selected { get; private set; }

    public bool Hovered { get; set; }

    /// <summary>Where layout wants this card's centre, and its resting rotation.</summary>
    public Vector2 Home { get; set; }

    public float HomeRotation { get; set; }

    public Tween? MoveTween { get; set; }

    public Vector2 Center => Root.Position + Root.Size / 2;

    public void SetSelected(bool selected)
    {
        Selected = selected;
        _glow.Visible = selected;
    }

    private void AddLayer(Texture2D? texture, Vector2 size)
    {
        if (texture == null) return;
        // ExpandMode goes first: until it is IgnoreSize, the texture's own size is a minimum and a
        // smaller Size (a scaled-down joker, say) would be ignored.
        var layer = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Texture = texture,
            Size = size,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            UseParentMaterial = true,
        };
        _layers.Add(layer);
        Root.AddChild(layer);
    }

    private void ApplyEdition(string? edition)
    {
        int mode = edition switch { "foil" => 1, "holo" => 2, "polychrome" => 3, "negative" => 4, _ => 0 };
        if (mode == 0) return;
        _editionShader ??= new Shader { Code = EditionShaderCode };
        var material = new ShaderMaterial { Shader = _editionShader };
        material.SetShaderParameter("edition", mode);
        material.SetShaderParameter("seed", (float)(Id % 97) * 0.37f);
        Root.Material = material;
    }

    // A status card the enemy put in Jimbo's deck: a dead stone card with StS2's art and name on it.
    private void AddStatusArt(TableCard card, int dud, Vector2 size)
    {
        foreach (TextureRect layer in _layers) layer.Modulate = new Color(0.5f, 0.45f, 0.5f);
        if (StatusArt?.Invoke(dud) is Texture2D art)
        {
            var window = new Control
            {
                Position = new Vector2(size.X * 0.09f, size.Y * 0.1f),
                Size = new Vector2(size.X * 0.82f, size.Y * 0.52f),
                ClipContents = true,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            window.AddChild(new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                Texture = art,
                Size = window.Size,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            Root.AddChild(window);
        }
        Label name = TableStyle.Text(card.Name ?? "Status", Math.Max(10, (int)(size.Y * 0.11f)), StsColors.red,
            new Rect2(0, size.Y * 0.66f, size.X, size.Y * 0.2f));
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Root.AddChild(name);
    }

    private void AddDebuffMark(Vector2 size)
    {
        foreach (TextureRect layer in _layers) layer.Modulate = new Color(0.6f, 0.6f, 0.65f);
        foreach (float angle in new[] { 0.95f, -0.95f })
        {
            var bar = new ColorRect
            {
                Color = new Color(StsColors.red, 0.85f),
                Size = new Vector2(size.Y * 0.95f, 12),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            bar.PivotOffset = bar.Size / 2;
            bar.Position = size / 2 - bar.Size / 2;
            bar.Rotation = angle;
            Root.AddChild(bar);
        }
    }
}
