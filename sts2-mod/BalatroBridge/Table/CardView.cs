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
    // Balatro's edition shaders; and Balatro's own dissolve (resources/shaders/dissolve.fs), which
    // burns a card in when it appears and away when it goes, its edge in the burn colours.
    private const string EditionShaderCode = """
        shader_type canvas_item;
        uniform int edition = 0;
        uniform float seed = 0.0;
        uniform float dissolve = 0.0;
        uniform vec4 burn_1 : source_color = vec4(0.0);
        uniform vec4 burn_2 : source_color = vec4(0.0);
        uniform vec2 card_size = vec2(142.0, 190.0);
        uniform float hovering = 0.0;
        uniform vec2 mouse = vec2(71.0, 95.0);
        uniform float ambient = 0.0;
        uniform float bob = 0.0;
        uniform int shine = 0;
        varying vec2 local;
        void vertex() {
            local = VERTEX;
            // Balatro's tilt: the corners away from the mouse come forward, the card leans under it.
            // Not hovered, a card with ambient tilt leans gently as an unseen point circles its
            // middle (Card:draw's ambient_tilt), so cards on show seem to breathe.
            vec2 m = mouse;
            float h = hovering;
            if (ambient > 0.0 && hovering < 0.01) {
                float angle = TIME * (1.56 + fract(seed / 1.14212)) + seed / 1.35122;
                m = card_size * (0.5 + 0.1 * vec2(cos(angle), sin(angle)));
                h = ambient * 0.2 * (0.5 + cos(angle));
            }
            if (abs(h) > 0.001) {
                vec2 off = (VERTEX - m) / card_size.y;
                VERTEX = card_size * 0.5 + (VERTEX - card_size * 0.5) * (1.0 + 0.09 * h * dot(off, off));
            }
            // A pack's dealt hand floats, each card on its own beat (Balatro's pack hand).
            VERTEX.y += bob * card_size.y * 0.035 * sin(0.666 * TIME + seed * 3.7);
        }
        // Balatro's voucher and booster shaders: a slow sheen drifting over the art.
        vec4 sheen(vec4 tex) {
            vec2 uv = local / card_size;
            float r = TIME / 28.0 + seed;
            float low = min(tex.r, min(tex.g, tex.b));
            float high = max(tex.r, max(tex.g, tex.b));
            float delta = shine == 2 ? max(high - low, low * 0.7) : high - low;
            float fac = 0.8 + 0.9 * sin(13.0 * uv.x + 5.32 * uv.y + r * 12.0 + cos(r * 5.3 + uv.y * 4.2 - uv.x * 4.0));
            float fac2 = 0.5 + 0.5 * sin(10.0 * uv.x + 2.32 * uv.y + r * 5.0 - cos(r * 2.3 + uv.x * 8.2));
            float fac3 = 0.5 + 0.5 * sin(12.0 * uv.x + 6.32 * uv.y + r * 6.111 + sin(r * 5.3 + uv.y * 3.2));
            float fac4 = 0.5 + 0.5 * sin(4.0 * uv.x + 2.32 * uv.y + r * 8.111 + sin(r * 1.3 + uv.y * 13.2));
            float fac5 = sin(8.0 * uv.x + 5.32 * uv.y + r * 12.0 + cos(r * 5.3 + uv.y * 4.2 - uv.x * 4.0));
            float maxfac = 0.6 * max(max(fac, max(fac2, max(fac3, 0.0))) + (fac + fac2 + fac3 * fac4), 0.0);
            vec3 o = tex.rgb * 0.5 + vec3(0.4, 0.4, 0.8);
            o.r = o.r - delta + delta * maxfac * (0.7 + fac5 * 0.07) - 0.1;
            o.g = o.g - delta + delta * maxfac * (0.7 - fac5 * 0.17) - 0.1;
            o.b = o.b - delta + delta * maxfac * 0.7 - 0.1;
            float a = 0.8 * max(min(1.0, max(0.0, 0.3 * max(low * 0.2, delta) + min(max(maxfac * 0.1, 0.0), 0.4))), 0.0) + 0.15 * maxfac * (0.1 + delta);
            return vec4(mix(tex.rgb, clamp(o, 0.0, 1.0), clamp(a, 0.0, 1.0)), tex.a);
        }
        vec4 burn(vec4 c) {
            float d = (dissolve * dissolve * (3.0 - 2.0 * dissolve)) * 1.02 - 0.01;
            vec2 fuv = floor(local / card_size * vec2(71.0, 95.0)) / 95.0;
            vec2 centred = (fuv - 0.5) * 2.3 * 95.0;
            float t = TIME * 10.0 + 2003.0;
            vec2 f1 = centred + 50.0 * vec2(sin(-t / 143.6340), cos(-t / 99.4324));
            vec2 f2 = centred + 50.0 * vec2(cos(t / 53.1532), cos(t / 61.4532));
            vec2 f3 = centred + 50.0 * vec2(sin(-t / 87.53218), sin(-t / 49.0));
            float field = (1.0 + (cos(length(f1) / 19.483) + sin(length(f2) / 33.155) * cos(f2.y / 15.73)
                + cos(length(f3) / 27.193) * sin(f3.x / 21.92))) / 2.0;
            float res = (0.5 + 0.5 * cos(d / 82.612 + (field - 0.5) * 3.14))
                - (fuv.x > 0.8 ? (fuv.x - 0.8) * (5.0 + 5.0 * dissolve) : 0.0) * dissolve
                - (fuv.y > 0.8 ? (fuv.y - 0.8) * (5.0 + 5.0 * dissolve) : 0.0) * dissolve
                - (fuv.x < 0.2 ? (0.2 - fuv.x) * (5.0 + 5.0 * dissolve) : 0.0) * dissolve
                - (fuv.y < 0.2 ? (0.2 - fuv.y) * (5.0 + 5.0 * dissolve) : 0.0) * dissolve;
            if (c.a > 0.01 && burn_1.a > 0.01 && res < d + 0.8 * (0.5 - abs(d - 0.5)) && res > d) {
                if (res < d + 0.5 * (0.5 - abs(d - 0.5))) {
                    c.rgb = burn_1.rgb;
                } else if (burn_2.a > 0.01) {
                    c.rgb = burn_2.rgb;
                }
            }
            c.a = res > d ? c.a : 0.0;
            return c;
        }
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
            if (shine > 0 && c.a > 0.01) c = sheen(c);
            if (dissolve > 0.001) c = burn(c);
            COLOR = c;
        }
        """;

    private static Shader? _editionShader;
    private static ulong _lastMaterialized;

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
            foreach (SpriteRef sticker in card.StickerSprites()) AddLayer(art.Sprite(sticker), size);
            ApplyEdition(card.Edition);
            if (card.Set is "Voucher" or "Booster") Shading().SetShaderParameter("shine", card.Set == "Voucher" ? 1 : 2);
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
        Shading().SetShaderParameter("edition", mode);
    }

    // The card's material (shared by its layers), made when an edition or a dissolve first needs it.
    private ShaderMaterial Shading()
    {
        if (Root.Material is ShaderMaterial existing) return existing;
        _editionShader ??= new Shader { Code = EditionShaderCode };
        var material = new ShaderMaterial { Shader = _editionShader };
        material.SetShaderParameter("seed", (float)(Id % 97) * 0.37f);
        material.SetShaderParameter("card_size", Root.Size);
        Root.Material = material;
        return material;
    }

    /// <summary>
    /// Balatro's materialize: the card burns in from nothing, its edge in <paramref name="colour"/>
    /// (a joker's rarity, a consumable's set), with motes of it drifting off.
    /// </summary>
    public void Materialize(Color colour, float delay = 0, float seconds = 0.6f, bool silent = false)
    {
        ShaderMaterial material = Shading();
        material.SetShaderParameter("dissolve", 1f);
        material.SetShaderParameter("burn_1", colour);
        material.SetShaderParameter("burn_2", new Color(0, 0, 0, 0));
        _glow.Modulate = new Color(1, 1, 1, 0);
        Tween t = Root.CreateTween();
        t.TweenInterval(delay / TableFx.Speed);
        t.TweenCallback(Callable.From(() =>
        {
            TableFx.Juice(Root, 0.12f);
            TableFx.Motes(Root, Root.Size / 2, Root.Size, colour, seconds);
            // One sound for a batch, as Balatro's G.last_materialized keeps it.
            if (!silent && Time.GetTicksMsec() - _lastMaterialized > 80)
            {
                _lastMaterialized = Time.GetTicksMsec();
                TableFx.Sound("whoosh1", 0.6f + 0.1f * GD.Randf(), 0.3f);
                TableFx.Sound("crumple" + GD.RandRange(1, 5), 1.2f + 0.2f * GD.Randf(), 0.6f);
            }
        }));
        t.TweenMethod(Callable.From<float>(v => material.SetShaderParameter("dissolve", v)), 1f, 0f, seconds / TableFx.Speed);
        t.TweenProperty(_glow, "modulate:a", 1f, 0.1 / TableFx.Speed);
    }

    /// <summary>
    /// Balatro's dissolve: the card burns away, edged in <paramref name="burn"/> (Balatro's black
    /// and orange unless told otherwise), and is freed.
    /// </summary>
    public void Dissolve(Color? burn = null, Color? burn2 = null, float delay = 0, float seconds = 0.7f, bool silent = false)
    {
        ShaderMaterial material = Shading();
        Color one = burn ?? new Color("374244"), two = burn2 ?? (burn == null ? new Color("FDA200") : new Color(0, 0, 0, 0));
        material.SetShaderParameter("burn_1", one);
        material.SetShaderParameter("burn_2", two);
        Root.MouseFilter = Control.MouseFilterEnum.Ignore;
        Tween t = Root.CreateTween();
        t.TweenInterval(delay / TableFx.Speed);
        t.TweenCallback(Callable.From(() =>
        {
            TableFx.Juice(Root, 0.1f);
            _glow.Visible = false;
            TableFx.Motes(Root, Root.Size / 2, Root.Size, two.A > 0 ? two : one, seconds);
            if (!silent)
            {
                TableFx.Sound("whoosh2", 0.9f + 0.2f * GD.Randf(), 0.5f);
                TableFx.Sound("crumple" + GD.RandRange(1, 5), 0.9f + 0.2f * GD.Randf(), 0.5f);
            }
        }));
        t.TweenMethod(Callable.From<float>(v => material.SetShaderParameter("dissolve", v)), 0f, 1f, seconds / TableFx.Speed);
        t.TweenCallback(Callable.From(Root.QueueFree));
    }

    /// <summary>
    /// Balatro's hover tilt: while the mouse is over the card, the card leans away under it as if
    /// pressed, following the mouse across it.
    /// </summary>
    public void EnableTilt()
    {
        ShaderMaterial material = Shading();
        material.SetShaderParameter("ambient", 1f);
        Tween? lean = null;
        void Lean(float to)
        {
            lean?.Kill();
            lean = Root.CreateTween();
            lean.TweenMethod(Callable.From<float>(v => material.SetShaderParameter("hovering", v)),
                (float)material.GetShaderParameter("hovering"), to, 0.15 / TableFx.Speed);
        }
        Root.MouseEntered += () => Lean(1f);
        Root.MouseExited += () => Lean(0f);
        Root.GuiInput += input =>
        {
            if (input is InputEventMouseMotion motion) material.SetShaderParameter("mouse", motion.Position);
        };
    }

    /// <summary>Lets the card float up and down gently, as a pack's dealt hand does in Balatro.</summary>
    public void Float() => Shading().SetShaderParameter("bob", 1f);

    /// <summary>The colour a card burns in with when it appears: its rarity, or its set's.</summary>
    public static Color MaterializeColour(TableCard card) =>
        card.IsJoker && Content.BalatroContent.ItemFor(card.Key) is Content.BalatroItem item
            ? item.Rarity switch { 1 => new Color("009DFF"), 2 => new Color("4BC292"), 3 => new Color("FE5F55"), _ => new Color("B26CBB") }
            : new Color(card.Set switch
            {
                "Tarot" => "A782D1",
                "Planet" => "13AFCE",
                "Spectral" => "4584FA",
                "Voucher" => "FD682B",
                "Booster" => "646EB7",
                _ => "4BC292",
            });

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
