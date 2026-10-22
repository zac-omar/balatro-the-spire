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

    // Balatro's edition shaders (resources/shaders/foil.fs, holo.fs, polychrome.fs, negative.fs and
    // negative_shine.fs), driven as Card:draw drives them: a slow clock plus the card's tilt, and the
    // real clock. Holographic, polychrome and negative redraw the card; foil and negative's shine lie
    // over it. And Balatro's own dissolve (dissolve.fs), which burns a card in when it appears and
    // away when it goes, its edge in the burn colours.
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
        // Balatro's colour helpers (the edition shaders' HSL and RGB).
        float hue_part(float s, float t, float h) {
            float hs = mod(h, 1.0) * 6.0;
            if (hs < 1.0) return (t - s) * hs + s;
            if (hs < 3.0) return t;
            if (hs < 4.0) return (t - s) * (4.0 - hs) + s;
            return s;
        }
        vec4 to_rgb(vec4 c) {
            if (c.y < 0.0001) return vec4(vec3(c.z), c.a);
            float t = (c.z < 0.5) ? c.y * c.z + c.z : -c.y * c.z + (c.y + c.z);
            float s = 2.0 * c.z - t;
            return vec4(hue_part(s, t, c.x + 1.0 / 3.0), hue_part(s, t, c.x), hue_part(s, t, c.x - 1.0 / 3.0), c.w);
        }
        vec4 to_hsl(vec4 c) {
            float low = min(c.r, min(c.g, c.b));
            float high = max(c.r, max(c.g, c.b));
            float delta = high - low;
            float sum = high + low;
            vec4 hsl = vec4(0.0, 0.0, 0.5 * sum, c.a);
            if (delta == 0.0) return hsl;
            hsl.y = (hsl.z < 0.5) ? delta / sum : delta / (2.0 - sum);
            if (high == c.r) hsl.x = (c.g - c.b) / delta;
            else if (high == c.g) hsl.x = (c.b - c.r) / delta + 2.0;
            else hsl.x = (c.r - c.g) / delta + 4.0;
            hsl.x = mod(hsl.x / 6.0, 1.0);
            return hsl;
        }
        // The swirling field holo and polychrome shift their hue by.
        float field_at(vec2 p, float t) {
            vec2 f1 = p + 50.0 * vec2(sin(-t / 143.6340), cos(-t / 99.4324));
            vec2 f2 = p + 50.0 * vec2(cos(t / 53.1532), cos(t / 61.4532));
            vec2 f3 = p + 50.0 * vec2(sin(-t / 87.53218), sin(-t / 49.0));
            return (1.0 + (cos(length(f1) / 19.483) + sin(length(f2) / 33.155) * cos(f2.y / 15.73)
                + cos(length(f3) / 27.193) * sin(f3.x / 21.92))) / 2.0;
        }
        // Card:draw's tilt amount: where the mouse is on a hovered card, or the ambient lean.
        float tilt_amount() {
            if (hovering > 0.01) return abs(mouse.y / card_size.y + mouse.x / card_size.x - 1.0) * 0.3;
            if (ambient > 0.0) {
                float angle = TIME * (1.56 + fract(seed / 1.14212)) + seed / 1.35122;
                return ambient * (0.5 + cos(angle)) * 0.3;
            }
            return 0.0;
        }
        vec4 edition_fx(vec4 c) {
            vec2 grid = vec2(71.0, 95.0);  // the art's pixels, which the shimmer keeps to
            vec2 uv = local / card_size;
            vec2 fuv = floor(uv * grid) / grid;
            // send_to_shader: the slow clock plus the tilt, and the real clock; and the card's own constant.
            vec2 fx = vec2(TIME / 28.0 + tilt_amount() + seed, TIME);
            float own = mod(123.33412 * (seed * 10.0 + 12.5) / 1.14212, 3000.0);
            float low = min(c.r, min(c.g, c.b));
            float high = max(c.r, max(c.g, c.b));
            if (edition == 1) {
                // Foil: a silver-blue layer over the card, swirling rings and a sweeping beam.
                vec2 adj = uv - 0.5;
                adj.x *= grid.x / grid.y;
                float delta = min(high, max(0.5, 1.0 - low));
                float fac = max(min(2.0 * sin((length(90.0 * adj) + fx.x * 2.0) + 3.0 * (1.0 + 0.8 * cos(length(113.1121 * adj) - fx.x * 3.121))) - 1.0 - max(5.0 - length(90.0 * adj), 0.0), 1.0), 0.0);
                vec2 rot = vec2(cos(fx.x * 0.1221), sin(fx.x * 0.3512));
                float angle = dot(rot, adj) / (length(rot) * max(length(adj), 0.0001));
                float fac2 = max(min(5.0 * cos(fx.y * 0.3 + angle * 3.14 * (2.2 + 0.9 * sin(fx.x * 1.65 + 0.2 * fx.y))) - 4.0 - max(2.0 - length(20.0 * adj), 0.0), 1.0), 0.0);
                float fac3 = 0.3 * max(min(2.0 * sin(fx.x * 5.0 + uv.x * 3.0 + 3.0 * (1.0 + 0.5 * cos(fx.x * 7.0))) - 1.0, 1.0), -1.0);
                float fac4 = 0.3 * max(min(2.0 * sin(fx.x * 6.66 + uv.y * 3.8 + 3.0 * (1.0 + 0.5 * cos(fx.x * 3.414))) - 1.0, 1.0), -1.0);
                float maxfac = max(max(fac, max(fac2, max(fac3, max(fac4, 0.0)))) + 2.2 * (fac + fac2 + fac3 + fac4), 0.0);
                vec3 o = vec3(c.r - delta + delta * maxfac * 0.3, c.g - delta + delta * maxfac * 0.3, c.b + delta * maxfac * 1.9);
                float a = min(1.0, 0.3 + 0.9 * min(0.5, maxfac * 0.1));
                c.rgb = mix(c.rgb, clamp(o, 0.0, 1.0), a);
            } else if (edition == 2) {
                // Holographic: the card redrawn lighter, its hue shifting over a swirl and a fine grid.
                vec4 hsl = to_hsl(0.5 * c + 0.5 * vec4(0.0, 0.0, 1.0, c.a));
                float t = fx.y * 7.221 + own;
                float res = 0.5 + 0.5 * cos(fx.x * 2.612 + (field_at((fuv - 0.5) * 250.0, t) - 0.5) * 3.14);
                float delta = 0.2 + 0.3 * (high - low) + 0.1 * high;
                float gs = 0.79;
                float fac = 0.5 * max(max(max(0.0, 7.0 * abs(cos(uv.x * gs * 20.0)) - 6.0), max(0.0, 7.0 * cos(uv.y * gs * 45.0 + uv.x * gs * 20.0) - 6.0)),
                    max(0.0, 7.0 * cos(uv.y * gs * 45.0 - uv.x * gs * 20.0) - 6.0));
                hsl.x = hsl.x + res + fac;
                hsl.y = hsl.y * 1.3;
                hsl.z = hsl.z * 0.6 + 0.4;
                c.rgb = clamp((1.0 - delta) * c.rgb + delta * to_rgb(hsl).rgb * vec3(0.9, 0.8, 1.2), 0.0, 1.0);
            } else if (edition == 3) {
                // Polychrome: the card's hue rolls through the rainbow on a slow swirl.
                float sf = 1.0 - max(0.0, 0.05 * (1.1 - (high - low)));
                vec4 hsl = to_hsl(vec4(c.r * sf, c.g * sf, c.b, c.a));
                float t = fx.y * 2.221 + own;
                float res = 0.5 + 0.5 * cos(fx.x * 2.612 + (field_at((fuv - 0.5) * 50.0, t) - 0.5) * 3.14);
                hsl.x = hsl.x + res + fx.y * 0.04;
                hsl.y = min(0.6, hsl.y + 0.5);
                c.rgb = to_rgb(hsl).rgb;
            } else if (edition == 4) {
                // Negative: lightness inverted and hue turned, tinted grey-teal; then its shine on top.
                vec4 sat = to_hsl(c);
                sat.z = 1.0 - sat.z;
                sat.x = -sat.x + 0.2;
                vec3 neg = clamp(to_rgb(sat).rgb + 0.8 * vec3(79.0, 99.0, 103.0) / 255.0, 0.0, 1.0);
                float r = fx.x;
                float delta = high - low - 0.1;
                float fac = 0.8 + 0.9 * sin(11.0 * uv.x + 4.32 * uv.y + r * 12.0 + cos(r * 5.3 + uv.y * 4.2 - uv.x * 4.0));
                float fac2 = 0.5 + 0.5 * sin(8.0 * uv.x + 2.32 * uv.y + r * 5.0 - cos(r * 2.3 + uv.x * 8.2));
                float fac3 = 0.5 + 0.5 * sin(10.0 * uv.x + 5.32 * uv.y + r * 6.111 + sin(r * 5.3 + uv.y * 3.2));
                float fac4 = 0.5 + 0.5 * sin(3.0 * uv.x + 2.32 * uv.y + r * 8.111 + sin(r * 1.3 + uv.y * 11.2));
                float fac5 = sin(0.9 * 16.0 * uv.x + 5.32 * uv.y + r * 12.0 + cos(r * 5.3 + uv.y * 4.2 - uv.x * 4.0));
                float maxfac = 0.7 * max(max(fac, max(fac2, max(fac3, 0.0))) + (fac + fac2 + fac3 * fac4), 0.0);
                vec3 s = c.rgb * 0.5 + vec3(0.4, 0.4, 0.8);
                s.r = s.r - delta + delta * maxfac * (0.7 + fac5 * 0.27) - 0.1;
                s.g = s.g - delta + delta * maxfac * (0.7 - fac5 * 0.27) - 0.1;
                s.b = s.b - delta + delta * maxfac * 0.7 - 0.1;
                float a = 0.5 * max(min(1.0, max(0.0, 0.3 * max(low * 0.2, delta) + min(max(maxfac * 0.1, 0.0), 0.4))), 0.0) + 0.15 * maxfac * (0.1 + delta);
                c.rgb = mix(neg, clamp(s, 0.0, 1.0), clamp(a, 0.0, 1.0));
            }
            return c;
        }
        void fragment() {
            // Godot's COLOR comes in already multiplied by the texture: sampling it again would
            // square the art's colours (yellows go orange).
            vec4 c = COLOR;
            if (edition > 0 && c.a > 0.01) c = edition_fx(c);
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
