using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Core;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BalatroBridge.Table;

/// <summary>What the player chose to do with the selected cards.</summary>
internal sealed record TableAction(bool Play, int[] CardIds);

/// <summary>
/// The Balatro table drawn inside StS2: a full-screen overlay over the fight where the player
/// picks cards, discards, and plays a hand, then watches it score. After a beaten blind it turns
/// into Balatro's shop (TableScreen.Shop.cs). Balatro (running hidden) decides everything; this
/// class only presents it and collects input. Card handling sounds are Balatro's own, played by
/// the hidden engine; everything else sounds like StS2.
/// The table opens as a swirling iris out of the player's hand and closes into the target, as
/// the played hand flies at it. All layout is in a 1920x1080 "stage" scaled to fit the window.
/// </summary>
internal sealed partial class TableScreen
{
    private static readonly Vector2 StageSize = new(1920, 1080);
    private static readonly Vector2 DeckPosition = new(2080, 960);
    private const float HandY = 900;
    private const float PlayY = 615;
    private const int Layer = 60;
    private static readonly Vector2 StatusPosition = new(560, 492);

    // Balatro's pixelated swirl. `reveal` opens it as an iris from `origin` (0..1 across the
    // screen) with a wobbling gold rim, and `spin` winds the swirl up while it opens or closes.
    internal const string FeltShader = """
        shader_type canvas_item;
        uniform vec4 colour_1 : source_color = vec4(0.24, 0.42, 0.35, 1.0);
        uniform vec4 colour_2 : source_color = vec4(0.07, 0.10, 0.13, 1.0);
        uniform vec4 rim : source_color = vec4(0.94, 0.78, 0.32, 1.0);
        uniform float reveal = 1.0;
        uniform vec2 origin = vec2(0.5, 0.5);
        uniform float spin = 0.0;
        void fragment() {
            vec2 cell = floor(UV * vec2(384.0, 216.0)) / vec2(384.0, 216.0);
            vec2 out_of = (cell - origin) * vec2(1.78, 1.0);
            float wobble = 0.12 * reveal * (1.0 - reveal) * sin(atan(out_of.y, out_of.x) * 7.0 + TIME * 6.0);
            float edge = reveal * 2.1 + wobble - length(out_of);
            if (edge < 0.0) {
                discard;
            }
            vec2 uv = (cell - 0.5) * vec2(1.78, 1.0);
            float r = length(uv);
            float a = atan(uv.y, uv.x) + TIME * 0.06 + r * (2.5 + sin(TIME * 0.11)) + spin * (1.6 - r);
            float swirl = sin(a * 3.0 + r * 9.0 - TIME * 0.35) * 0.5 + 0.5;
            vec3 col = mix(colour_2.rgb, colour_1.rgb, smoothstep(0.2, 0.8, swirl));
            col *= 0.5 + 0.5 * smoothstep(1.25, 0.15, r);
            float glow = (1.0 - smoothstep(0.0, 0.06, edge)) * step(reveal, 0.999);
            COLOR = vec4(mix(col, rim.rgb, glow), mix(0.88, 1.0, glow));
        }
        """;

    // Where the iris opens from: the bottom of the screen, where StS2's hand is.
    private static readonly Vector2 OpenOrigin = new(0.5f, 1.05f);

    // Where it closes into when the target isn't on screen: StS2's enemy side.
    private static readonly Vector2 DefaultTargetOrigin = new(0.72f, 0.45f);
    private const float ShadeAlpha = 0.45f;

    private readonly BalatroSession _session;
    private readonly Creature? _target;
    private readonly BalatroArt _art = BalatroArt.Shared;
    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly ColorRect _shade;
    private readonly ColorRect _felt;
    private readonly ShaderMaterial _feltMaterial;
    private readonly Control _stage;
    private readonly Control _cards;

    private readonly Label _handName;
    private readonly Label _handLevel;
    private readonly Panel _chipsBox;
    private readonly Panel _multBox;
    private readonly Label _chips;
    private readonly Label _mult;
    private readonly Label _times;
    private readonly Label _status;
    private readonly Label _blindName;
    private readonly Label _blindGoal;
    private readonly TextureRect _blindChip;
    private readonly Label _roundCaption;
    private readonly Label _roundScore;
    private readonly Label _hands;
    private readonly Label _discards;
    private readonly Label _money;
    private readonly Label _ante;
    private readonly Label _deck;
    private readonly Label? _targetHp;
    private readonly Button _play;
    private readonly Button _discard;
    private readonly Button _sortRank;
    private readonly Button _sortSuit;
    private readonly Label _hint;

    private readonly List<CardView> _hand = new();
    private readonly List<CardView> _played = new();
    private readonly List<CardView> _jokers = new();
    private readonly List<Control> _revealed = new(); // the score and damage shown after a hand
    private TableSnapshot? _table;
    private CardTip? _tip;
    private Vector2 _stageHome;
    private bool _removed;
    private TaskCompletionSource<TableAction>? _action;
    private int _previewVersion;
    private bool _acceptingInput;

    private TableScreen(BalatroSession session, Creature? target)
    {
        _session = session;
        _target = target;
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 screen = tree.Root.GetVisibleRect().Size;

        _layer = new CanvasLayer { Layer = Layer };
        _root = new Control
        {
            Size = screen,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All,
        };
        _layer.AddChild(_root);

        _shade = new ColorRect { Size = screen, Color = new Color(0, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_shade);
        _feltMaterial = new ShaderMaterial { Shader = new Shader { Code = FeltShader } };
        _feltMaterial.SetShaderParameter("reveal", 0f);
        _felt = new ColorRect { Size = screen, Material = _feltMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_felt);

        float scale = Math.Min(screen.X / StageSize.X, screen.Y / StageSize.Y);
        _stage = new Control
        {
            Size = StageSize,
            Scale = new Vector2(scale, scale),
            Position = (screen - StageSize * scale) / 2,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        _stageHome = _stage.Position;
        _root.AddChild(_stage);
        _tip = new CardTip(_stage, StageSize);

        // Title.
        _stage.AddChild(TableStyle.Text("DEAL IN", 46, StsColors.gold, new Rect2(660, 28, 600, 60), TableStyle.Title, 10));
        _stage.AddChild(TableStyle.Text("Play a hand of Balatro", 24, new Color(StsColors.cream, 0.8f), new Rect2(660, 84, 600, 32), TableStyle.Regular, 6));

        // Score plate: hand name, then chips x mult.
        _handName = TableStyle.Text("", 48, StsColors.cream, new Rect2(560, 300, 800, 64));
        _handLevel = TableStyle.Text("", 24, StsColors.gold, new Rect2(560, 352, 800, 30), TableStyle.Regular, 6);
        _chipsBox = TableStyle.Panel(new Rect2(700, 392, 220, 96), TableStyle.Box(TableStyle.Chips, new Color(1, 1, 1, 0.35f), 14, 3, 10));
        _multBox = TableStyle.Panel(new Rect2(1000, 392, 220, 96), TableStyle.Box(TableStyle.Mult, new Color(1, 1, 1, 0.35f), 14, 3, 10));
        _chips = TableStyle.Text("0", 58, Colors.White, new Rect2(0, 0, 220, 96));
        _mult = TableStyle.Text("0", 58, Colors.White, new Rect2(0, 0, 220, 96));
        _chipsBox.AddChild(_chips);
        _multBox.AddChild(_mult);
        _times = TableStyle.Text("X", 52, StsColors.cream, new Rect2(920, 392, 80, 96));
        _status = TableStyle.Text("", 30, StsColors.red, new Rect2(StatusPosition, new Vector2(800, 40)));
        foreach (Control c in new Control[] { _handName, _handLevel, _chipsBox, _multBox, _times, _status }) _stage.AddChild(c);

        // Left: the Balatro run around this hand.
        var info = TableStyle.Panel(new Rect2(36, 250, 360, 470), TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 14, 3, 12));
        _stage.AddChild(info);
        _blindChip = new TextureRect
        {
            Position = new Vector2(20, 18), Size = new Vector2(68, 68),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        info.AddChild(_blindChip);
        _blindName = TableStyle.Text("", 32, StsColors.gold, new Rect2(96, 14, 250, 44));
        _blindName.HorizontalAlignment = HorizontalAlignment.Left;
        _blindGoal = TableStyle.Text("", 22, StsColors.cream, new Rect2(96, 54, 250, 30), TableStyle.Regular, 5);
        _blindGoal.HorizontalAlignment = HorizontalAlignment.Left;
        info.AddChild(_blindName);
        info.AddChild(_blindGoal);
        _roundCaption = TableStyle.Text("Round score", 22, new Color(StsColors.cream, 0.75f), new Rect2(20, 104, 320, 28), TableStyle.Regular, 5);
        info.AddChild(_roundCaption);
        _roundScore = TableStyle.Text("0", 44, StsColors.cream, new Rect2(20, 130, 320, 52));
        info.AddChild(_roundScore);
        info.AddChild(Counter("Hands", TableStyle.Chips, new Vector2(24, 200), out _hands));
        info.AddChild(Counter("Discards", TableStyle.Mult, new Vector2(186, 200), out _discards));
        _money = TableStyle.Text("$0", 42, TableStyle.Money, new Rect2(20, 318, 320, 50));
        _ante = TableStyle.Text("", 24, StsColors.cream, new Rect2(20, 370, 160, 32), TableStyle.Regular, 5);
        _deck = TableStyle.Text("", 24, StsColors.cream, new Rect2(180, 370, 160, 32), TableStyle.Regular, 5);
        info.AddChild(_money);
        info.AddChild(_ante);
        info.AddChild(_deck);
        info.AddChild(TableStyle.Text("Balatro run", 20, new Color(StsColors.cream, 0.5f), new Rect2(20, 418, 320, 30), TableStyle.Regular, 4));

        // Right: who the hand is aimed at.
        if (target != null)
        {
            var aim = TableStyle.Panel(new Rect2(1524, 250, 360, 190), TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 14, 3, 12));
            _stage.AddChild(aim);
            aim.AddChild(TableStyle.Text("Target", 22, new Color(StsColors.cream, 0.75f), new Rect2(20, 14, 320, 28), TableStyle.Regular, 5));
            aim.AddChild(TableStyle.Text(target.Name, 34, StsColors.cream, new Rect2(20, 44, 320, 48)));
            _targetHp = TableStyle.Text("", 30, StsColors.red, new Rect2(20, 100, 320, 40));
            aim.AddChild(_targetHp);
            aim.AddChild(TableStyle.Text("Hand score becomes damage", 18, new Color(StsColors.cream, 0.55f), new Rect2(20, 146, 320, 26), TableStyle.Regular, 4));
            UpdateTarget();
        }

        _cards = new Control { Size = StageSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        _stage.AddChild(_cards);

        _play = TableStyle.Button("Play Hand", TableStyle.Chips, new Rect2(112, 852, 250, 92), 34);
        _discard = TableStyle.Button("Discard", TableStyle.Mult, new Rect2(1558, 852, 250, 92), 34);
        _sortRank = TableStyle.Button("Rank", new Color(0.32f, 0.36f, 0.42f), new Rect2(830, 1010, 120, 50), 24);
        _sortSuit = TableStyle.Button("Suit", new Color(0.32f, 0.36f, 0.42f), new Rect2(970, 1010, 120, 50), 24);
        _play.Pressed += () => Submit(play: true);
        _discard.Pressed += () => Submit(play: false);
        _sortRank.Pressed += () => _ = Sort(bySuit: false);
        _sortSuit.Pressed += () => _ = Sort(bySuit: true);
        foreach (Button b in new[] { _play, _discard, _sortRank, _sortSuit }) _stage.AddChild(b);
        _hint = TableStyle.Text(HandHint, 18, new Color(StsColors.cream, 0.45f), new Rect2(36, 1030, 760, 30), TableStyle.Regular, 4);
        _hint.HorizontalAlignment = HorizontalAlignment.Left;
        _stage.AddChild(_hint);

        _root.GuiInput += OnRootInput;
        tree.Root.AddChild(_layer);
        _root.GrabFocus();
        RefreshButtons();
    }

    private const string HandHint = "Click or 1-8 to pick cards  ·  Enter play  ·  D discard  ·  right click clears";

    private static float Speed => TableStyle.Speed;

    private int SelectedCount => _hand.Count(c => c.Selected);

    public static TableScreen Open(BalatroSession session, Creature? target)
    {
        var screen = new TableScreen(session, target);
        screen.SetStatus("Shuffling the deck...", StsColors.cream);
        screen.OpenIris();
        return screen;
    }

    // The felt swirls open out of the hand; the table's contents rise in as it passes.
    private void OpenIris()
    {
        _feltMaterial.SetShaderParameter("origin", OpenOrigin);
        Tween t = _root.CreateTween().SetParallel();
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("reveal", v)), 0f, 1f, 0.65 / Speed)
            .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("spin", v)), 5f, 0f, 1.1 / Speed)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(_shade, "color:a", ShadeAlpha, 0.45 / Speed);
        _stage.Position = _stageHome + new Vector2(0, 40);
        t.TweenProperty(_stage, "modulate:a", 1f, 0.3 / Speed).SetDelay(0.2 / Speed);
        t.TweenProperty(_stage, "position", _stageHome, 0.45 / Speed).SetDelay(0.2 / Speed)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        BalatroSound("cardFan2", 1f, 0.8f);
        BalatroSound("whoosh1", 1.1f, 0.35f);
    }

    public void SetStatus(string text, Color colour)
    {
        _status.Text = text;
        _status.LabelSettings.FontColor = colour;
    }

    // Dealing -------------------------------------------------------------------------------------

    /// <summary>Shows a freshly dealt (or refilled) hand. Cards already on the table stay put.</summary>
    public async Task Deal(TableSnapshot table)
    {
        _art.Learn(table);
        ApplyInfo(table);
        SetStatus("", StsColors.cream);

        var keep = _hand.ToDictionary(c => c.Id);
        _hand.Clear();
        var dealt = new List<CardView>();
        foreach (TableCard card in table.Hand)
        {
            if (keep.Remove(card.Id, out CardView? view) && view.Card == card)
            {
                _hand.Add(view);
                continue;
            }
            view?.Root.QueueFree();
            view = CreateCard(card, 1f);
            view.Root.Position = DeckPosition - view.Root.Size / 2;
            view.Root.Rotation = 0.6f;
            _hand.Add(view);
            dealt.Add(view);
        }
        foreach (CardView stale in keep.Values) stale.Root.QueueFree();
        // The Cerulean Bell's card is always picked.
        foreach (CardView view in _hand) if (view.Card.Forced) view.SetSelected(true);
        for (int i = 0; i < _hand.Count; i++) _cards.MoveChild(_hand[i].Root, i);

        LayoutHand();
        for (int i = 0; i < _hand.Count; i++)
        {
            CardView view = _hand[i];
            float delay = dealt.IndexOf(view) is var d and >= 0 ? d * 0.07f : 0f;
            Move(view, view.Home, view.HomeRotation, 0.38f, delay, Tween.TransitionType.Back);
        }
        for (int i = 0; i < dealt.Count; i++)
        {
            BalatroSound("card1", 0.85f + 0.2f * i / Math.Max(dealt.Count - 1, 1), 0.6f);
            await Wait(0.07f);
        }
        await Wait(0.3f);
        ShowPreview(null);
    }

    private CardView CreateCard(TableCard card, float scale)
    {
        var view = new CardView(card, _art, _table?.Back, scale);
        view.Root.MouseEntered += () => SetHover(view, true);
        view.Root.MouseExited += () => SetHover(view, false);
        view.Root.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true } click) return;
            if (click.ButtonIndex == MouseButton.Left) Toggle(view);
            else if (click.ButtonIndex == MouseButton.Right) ClearSelection();
            view.Root.AcceptEvent();
        };
        _cards.AddChild(view.Root);
        return view;
    }

    private void LayoutHand()
    {
        int n = _hand.Count;
        float spacing = Math.Min(150f, 1150f / Math.Max(n, 1));
        float mid = (n - 1) / 2f;
        for (int i = 0; i < n; i++)
        {
            CardView view = _hand[i];
            float offset = i - mid;
            float lift = view.Selected ? 52 : view.Hovered ? 16 : 0;
            view.Home = new Vector2(960 + offset * spacing, HandY + offset * offset * 3.2f - lift);
            view.HomeRotation = Mathf.DegToRad(offset * 2.4f) * (view.Selected ? 0.4f : 1f);
        }
    }

    private void Relayout()
    {
        LayoutHand();
        foreach (CardView view in _hand) Move(view, view.Home, view.HomeRotation, 0.16f);
    }

    // Input ---------------------------------------------------------------------------------------

    /// <summary>Waits for the player to play or discard the selected cards.</summary>
    public Task<TableAction> NextAction()
    {
        _action = new TaskCompletionSource<TableAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptingInput = true;
        RefreshButtons();
        return _action.Task;
    }

    private void OnRootInput(InputEvent input)
    {
        // Every key is swallowed so StS2's own hotkeys (end turn, pause...) can't fire underneath.
        if (input is InputEventKey key)
        {
            if (key.Pressed && !key.Echo && !OnShopKey(key.Keycode)) OnKey(key.Keycode);
            _root.AcceptEvent();
        }
        else if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            ClearSelection();
        }
    }

    private void OnKey(Key key)
    {
        if (key >= Key.Key1 && key <= Key.Key9)
        {
            int index = (int)(key - Key.Key1);
            if (index < _hand.Count) Toggle(_hand[index]);
        }
        else if (key is Key.Enter or Key.KpEnter or Key.Space) Submit(play: true);
        else if (key == Key.D) Submit(play: false);
        else if (key == Key.Escape) ClearSelection();
    }

    private void SetHover(CardView view, bool hovered)
    {
        if (!_hand.Contains(view) || !_acceptingInput)
        {
            view.Hovered = false;
            return;
        }
        view.Hovered = hovered;
        if (view.Hovered) HoverSound();
        Relayout();
    }

    private void Toggle(CardView view)
    {
        if (!_acceptingInput || !_hand.Contains(view) || _table == null) return;
        if (view.Selected && view.Card.Forced)
        {
            // The Cerulean Bell won't let it go.
            Juice(view.Root, 0.08f);
            BalatroSound("cancel", 1f, 0.5f);
            SetStatus("The Cerulean Bell forces this card!", CardView.Cerulean);
            return;
        }
        if (!view.Selected && SelectedCount >= _table.SelectLimit)
        {
            Juice(view.Root, 0.08f);
            BalatroSound("cancel", 1f, 0.5f);
            return;
        }
        view.SetSelected(!view.Selected);
        if (view.Selected) BalatroSound("cardSlide1");
        else BalatroSound("cardSlide2", 1f, 0.3f);
        Relayout();
        RefreshButtons();
        _ = UpdatePreview();
    }

    private void ClearSelection()
    {
        if (!_acceptingInput || !_hand.Any(c => c.Selected && !c.Card.Forced)) return;
        BalatroSound("cardSlide2", 1f, 0.3f);
        foreach (CardView view in _hand) view.SetSelected(view.Card.Forced);
        Relayout();
        RefreshButtons();
        _ = UpdatePreview();
    }

    private void Submit(bool play)
    {
        if (!_acceptingInput || _action == null || _table == null) return;
        int[] ids = _hand.Where(c => c.Selected).Select(c => c.Id).ToArray();
        if (ids.Length == 0 || (!play && _table.DiscardsLeft <= 0)) return;
        BalatroSound("button");
        _acceptingInput = false;
        foreach (CardView view in _hand) view.Hovered = false;
        RefreshButtons();
        TaskCompletionSource<TableAction> action = _action;
        _action = null;
        action.TrySetResult(new TableAction(play, ids));
    }

    private async Task Sort(bool bySuit)
    {
        if (!_acceptingInput) return;
        BalatroSound("button");
        _acceptingInput = false;
        RefreshButtons();
        try
        {
            TableSnapshot sorted = await _session.SortHand(bySuit);
            var byId = _hand.ToDictionary(c => c.Id);
            _hand.Clear();
            foreach (TableCard card in sorted.Hand)
            {
                if (byId.TryGetValue(card.Id, out CardView? view)) _hand.Add(view);
            }
            for (int i = 0; i < _hand.Count; i++) _cards.MoveChild(_hand[i].Root, i);
            BalatroSound("paper1", 1f, 0.6f);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"sort failed: {e.Message}");
        }
        _acceptingInput = _action != null;
        Relayout();
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool any = SelectedCount > 0;
        _play.Disabled = !_acceptingInput || !any;
        _discard.Disabled = !_acceptingInput || !any || (_table?.DiscardsLeft ?? 0) <= 0;
        _sortRank.Disabled = !_acceptingInput;
        _sortSuit.Disabled = !_acceptingInput;
    }

    private async Task UpdatePreview()
    {
        int version = ++_previewVersion;
        int[] ids = _hand.Where(c => c.Selected).Select(c => c.Id).ToArray();
        if (ids.Length == 0)
        {
            ShowPreview(null);
            return;
        }
        try
        {
            HandPreview preview = await _session.Preview(ids);
            if (version == _previewVersion && _acceptingInput) ShowPreview(preview);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"preview failed: {e.Message}");
        }
    }

    private void ShowPreview(HandPreview? preview)
    {
        if (preview == null || preview.IsEmpty)
        {
            _handName.Text = "Select up to " + (_table?.SelectLimit ?? 5) + " cards";
            _handName.LabelSettings.FontColor = new Color(StsColors.cream, 0.6f);
            _handLevel.Text = "";
            _chips.Text = "0";
            _mult.Text = "0";
            SetStatus("", StsColors.cream);
            return;
        }
        bool changed = _handName.Text != preview.Hand;
        _handName.Text = preview.Hand;
        _handName.LabelSettings.FontColor = StsColors.cream;
        _handLevel.Text = preview.Level.Length > 0 ? "lvl." + preview.Level : "";
        _chips.Text = preview.Chips;
        _mult.Text = preview.Mult;
        SetStatus(preview.Allowed ? "" : "Not allowed by the boss blind!", StsColors.red);
        if (changed) Juice(_handName, 0.12f);
    }

    // Discarding ----------------------------------------------------------------------------------

    public async Task ThrowAway(int[] ids)
    {
        int i = 0;
        foreach (CardView view in _hand.Where(c => ids.Contains(c.Id)).ToList())
        {
            BalatroSound("card1", 0.75f + 0.05f * i, 0.5f);
            _hand.Remove(view);
            Tween t = view.Root.CreateTween().SetParallel();
            float delay = i++ * 0.04f;
            t.TweenProperty(view.Root, "position", new Vector2(2100, 560 + i * 30), 0.32 / Speed).SetDelay(delay / Speed)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            t.TweenProperty(view.Root, "rotation", 0.9f, 0.32 / Speed).SetDelay(delay / Speed);
            t.Chain().TweenCallback(Callable.From(view.Root.QueueFree));
        }
        Relayout();
        ShowPreview(null);
        await Wait(0.35f);
    }

    // Playing and scoring -------------------------------------------------------------------------

    /// <summary>Moves the chosen cards up to the play row while Balatro scores them.</summary>
    public async Task MoveToPlay(int[] ids)
    {
        _played.Clear();
        _played.AddRange(_hand.Where(c => ids.Contains(c.Id)));
        foreach (CardView view in _played)
        {
            _hand.Remove(view);
            view.SetSelected(false);
            view.Hovered = false;
        }
        LayoutPlayRow();
        foreach (CardView view in _played) Move(view, view.Home, 0, 0.3f, 0, Tween.TransitionType.Back);
        foreach (CardView view in _hand)
        {
            Move(view, view.Home + new Vector2(0, 40), view.HomeRotation, 0.3f);
            Fade(view.Root, 0.55f, 0.3f);
        }
        for (int i = 0; i < _played.Count; i++) BalatroSound("card1", 0.9f + 0.05f * i, 0.6f);
        await Wait(0.35f);
    }

    private void LayoutPlayRow()
    {
        float spacing = 160;
        float mid = (_played.Count - 1) / 2f;
        for (int i = 0; i < _played.Count; i++) _played[i].Home = new Vector2(960 + (i - mid) * spacing, PlayY);
    }

    /// <summary>Replays Balatro's scoring step by step, then reveals the score and the damage it deals.</summary>
    public async Task Score(ScoredHand scored, decimal damage)
    {
        // Balatro may have reordered the played cards; follow its order.
        var byId = _played.ToDictionary(c => c.Id);
        List<CardView> ordered = scored.Played.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (ordered.Count == _played.Count)
        {
            _played.Clear();
            _played.AddRange(ordered);
            LayoutPlayRow();
        }
        foreach (CardView view in _played)
        {
            bool scores = scored.Scoring.Contains(view.Id);
            Move(view, view.Home + new Vector2(0, scores ? -28 : 18), 0, 0.2f);
            if (!scores) Fade(view.Root, 0.6f, 0.2f);
        }
        await Wait(0.25f);

        float step = Math.Clamp(3.2f / Math.Max(scored.Trace.Count, 1), 0.13f, 0.42f);
        foreach (TraceStep traceStep in scored.Trace)
        {
            switch (traceStep)
            {
                case CounterStep counter:
                    ApplyCounter(counter);
                    break;
                case PopupStep popup:
                    ShowPopup(popup);
                    await Wait(step);
                    break;
            }
        }
        await Wait(0.3f);
        await RevealTotal(scored, damage);
    }

    private void ApplyCounter(CounterStep counter)
    {
        if (!string.IsNullOrEmpty(counter.Hand))
        {
            _handName.Text = counter.Hand;
            _handName.LabelSettings.FontColor = StsColors.cream;
            _handLevel.Text = string.IsNullOrEmpty(counter.Level) ? "" : "lvl." + counter.Level;
            Juice(_handName, 0.15f);
        }
        if (counter.Chips != null && counter.Chips != _chips.Text)
        {
            _chips.Text = counter.Chips;
            Juice(_chipsBox, 0.14f);
        }
        if (counter.Mult != null && counter.Mult != _mult.Text)
        {
            _mult.Text = counter.Mult;
            Juice(_multBox, 0.14f);
        }
    }

    private void ShowPopup(PopupStep popup)
    {
        CardView? card = popup.Area switch
        {
            "play" => _played.FirstOrDefault(c => c.Id == popup.CardId),
            "hand" => _hand.FirstOrDefault(c => c.Id == popup.CardId),
            "jokers" => _jokers.FirstOrDefault(c => c.Id == popup.CardId),
            _ => null,
        };
        Vector2 at = card != null ? card.Center + new Vector2(0, -card.Root.Size.Y / 2 - 30) : new Vector2(960, 270);
        if (card != null) Juice(card.Root, 0.16f);

        // Balatro's own scoring sounds, at its pitch wobble and volumes.
        (Color fill, Color ink, string sound, float volume) = popup.Kind switch
        {
            "chips" => (TableStyle.Chips, Colors.White, "chips1", 1f),
            "mult" => (TableStyle.Mult, Colors.White, "multhit1", 1f),
            "xmult" => (TableStyle.Mult.Darkened(0.15f), Colors.White, "multhit2", 0.7f),
            "money" => (TableStyle.Money, TableStyle.Ink, "coin3", 1f),
            "debuff" => (new Color(0.15f, 0.15f, 0.18f), StsColors.red, "cancel", 1f),
            "edition" => (TableStyle.Edition, Colors.White, "foil2", 0.3f),
            _ => (TableStyle.Special, Colors.White, "generic1", 1f),
        };
        BalatroSound(sound, 0.8f + 0.2f * (0.9f + 0.2f * GD.Randf()), volume);
        if (popup.Kind == "xmult") Embers(at, 18, TableStyle.Mult);

        var bubble = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bubble.AddThemeStyleboxOverride("panel", TableStyle.Box(fill, new Color(1, 1, 1, 0.4f), 10, 2, 6));
        bubble.AddChild(TableStyle.Text(popup.Text, popup.Kind == "xmult" ? 40 : 34, ink));
        _stage.AddChild(bubble);
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

    private async Task RevealTotal(ScoredHand scored, decimal damage)
    {
        // Chips and mult slam together into the total.
        Tween slam = _root.CreateTween().SetParallel();
        slam.TweenProperty(_chipsBox, "position:x", 850f, 0.16 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
        slam.TweenProperty(_multBox, "position:x", 850f, 0.16 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
        slam.TweenProperty(_times, "modulate:a", 0f, 0.1 / Speed);
        await ToSignal(slam);
        _chipsBox.Visible = _multBox.Visible = false;

        var total = TableStyle.Text(TableText.Number(scored.Score), 104, StsColors.gold, new Rect2(460, 372, 1000, 130), TableStyle.Title, 16);
        _stage.AddChild(total);
        _revealed.Add(total);
        total.Scale = Vector2.Zero;
        Tween pop = total.CreateTween();
        pop.TweenProperty(total, "scale", new Vector2(1.3f, 1.3f), 0.14 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        pop.TweenProperty(total, "scale", Vector2.One, 0.18 / Speed);
        TableStyle.Sfx("ui/cards/card_impact_into_multi", 1f);
        float heat = (float)Math.Clamp(Math.Log10(Math.Max(scored.Score, 1)), 1, 7);
        Shake(heat * 4f);
        Embers(new Vector2(960, 437), (int)(heat * 14), StsColors.gold);
        SetStatus("", StsColors.cream);
        await Wait(0.45f);

        string line = damage > 0 ? $"{damage:N0} damage" : "No damage";
        bool lethal = _target != null && damage > 0 && damage >= _target.CurrentHp + _target.Block;
        var hit = TableStyle.Text(lethal ? line + "  ·  LETHAL" : line, 46, lethal ? StsColors.gold : StsColors.red, new Rect2(460, 500, 1000, 60));
        _stage.AddChild(hit);
        _revealed.Add(hit);
        Juice(hit, 0.25f);
        TableStyle.Sfx(damage > 0 ? "characters/attack_fire" : "debuff", 0.7f);

        string? note = scored.Defeated ? $"Blind defeated! ({TableText.Number(scored.RoundChips)}/{TableText.Number(scored.Target ?? 0)})  ·  The shop is open"
            : scored.Failed ? "Out of hands: the blind starts over" : null;
        if (note != null)
        {
            SetStatus(note, scored.Defeated ? TableStyle.Money : StsColors.red);
            _status.Position = new Vector2(560, 562);
            if (scored.Defeated) TableStyle.Sfx("ui/gold/gold_2", 0.8f);
            else BalatroSound("cancel", 0.8f, 0.8f);
        }
        _roundScore.Text = TableText.Number(scored.RoundChips);
        await Wait(1.1f);
    }

    // Closing -------------------------------------------------------------------------------------

    public async Task Close()
    {
        _acceptingInput = false;
        _action?.TrySetCanceled();
        HideTip();
        Vector2 into = TargetOrigin();
        Vector2 strike = (into * _root.Size - _stage.Position) / _stage.Scale; // the target, in stage space
        _feltMaterial.SetShaderParameter("origin", into);
        BalatroSound("whoosh2", 1f, 0.45f);

        Tween t = _root.CreateTween().SetParallel();
        foreach (CardView view in _played)
        {
            // The played hand flies at the target as the attack lands.
            t.TweenProperty(view.Root, "position", strike - view.Root.Size / 2, 0.35 / Speed)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            t.TweenProperty(view.Root, "scale", new Vector2(0.4f, 0.4f), 0.35 / Speed);
        }
        foreach (Node child in _stage.GetChildren())
        {
            if (child != _cards && child is CanvasItem item) t.TweenProperty(item, "modulate:a", 0f, 0.2 / Speed);
        }
        foreach (CardView view in _hand) t.TweenProperty(view.Root, "modulate:a", 0f, 0.2 / Speed);
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("reveal", v)), 1f, 0f, 0.5 / Speed)
            .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.In);
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("spin", v)), 0f, -4f, 0.5 / Speed)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        t.TweenProperty(_shade, "color:a", 0f, 0.5 / Speed);
        await ToSignal(t);
        Remove();
    }

    /// <summary>Where the target stands, from 0 to 1 across the screen (StS2's enemy side if unknown).</summary>
    private Vector2 TargetOrigin()
    {
        try
        {
            if (_target != null && NCombatRoom.Instance?.GetCreatureNode(_target) is NCreature node && GodotObject.IsInstanceValid(node))
            {
                Vector2 centre = node.Hitbox.GetGlobalTransformWithCanvas() * (node.Hitbox.Size / 2);
                Vector2 uv = centre / _root.Size;
                if (uv.X is > 0 and < 1 && uv.Y is > 0 and < 1) return uv;
            }
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"table: couldn't find the target on screen: {e.Message}");
        }
        return DefaultTargetOrigin;
    }

    /// <summary>Takes the table off the screen immediately.</summary>
    public void Remove()
    {
        if (_removed) return;
        _removed = true;
        _root.ReleaseFocus();
        _layer.QueueFree();
    }

    // Info panel ----------------------------------------------------------------------------------

    private void ApplyInfo(TableSnapshot table)
    {
        _table = table;
        BalatroAudio.Instance.SetTrack(table.Shop != null ? BalatroAudio.Track.Shop
            : table.Blind?.Boss == true ? BalatroAudio.Track.Boss : BalatroAudio.Track.Main);
        TableBlind? blind = table.Blind;
        Color blindColour = blind?.Colour is string hex ? new Color(hex) : TableStyle.DefaultFelt;
        _feltMaterial.SetShaderParameter("colour_1", blind?.Boss == true ? blindColour.Darkened(0.25f) : TableStyle.DefaultFelt);
        _feltMaterial.SetShaderParameter("colour_2", new Color(0.06f, 0.08f, 0.11f));

        _blindName.Text = blind?.Name ?? "";
        _blindName.LabelSettings.FontColor = blind?.Boss == true ? blindColour.Lightened(0.3f) : StsColors.gold;
        _blindGoal.Text = blind?.Chips is double goal ? $"Score at least {TableText.Number(goal)}" : "";
        _blindChip.Texture = _art.Sprite(blind?.Sprite);
        _roundCaption.Text = "Round score";
        _roundScore.Text = TableText.Number(table.RoundChips);
        _hands.Text = table.HandsLeft.ToString();
        _discards.Text = table.DiscardsLeft.ToString();
        _money.Text = "$" + TableText.Number(table.Dollars);
        _ante.Text = $"Ante {table.Ante}/8";
        _deck.Text = $"Deck {table.DeckLeft}/{table.DeckTotal}";
        UpdateTarget();
        ShowJokers(table.Jokers);
        RefreshButtons();
    }

    private void ShowJokers(IReadOnlyList<TableCard> jokers)
    {
        foreach (CardView view in _jokers) view.Root.QueueFree();
        _jokers.Clear();
        const float scale = 0.72f;
        float spacing = 116;
        float mid = (jokers.Count - 1) / 2f;
        HideTip();
        for (int i = 0; i < jokers.Count; i++)
        {
            var view = new CardView(jokers[i], _art, null, scale);
            view.Root.Position = new Vector2(960 + (i - mid) * spacing, 205) - view.Root.Size / 2;
            view.Root.MouseEntered += () =>
            {
                Juice(view.Root, 0.06f);
                HoverSound();
                ShowTip(view);
            };
            view.Root.MouseExited += () => HideTip(view);
            _stage.AddChild(view.Root);
            _jokers.Add(view);
        }
    }

    /// <summary>A joker's name, what it does, and its edition, under the joker.</summary>
    private void ShowTip(CardView view) => _tip?.Show(view);

    /// <summary>Hides the tooltip (only if it belongs to <paramref name="owner"/>, when given).</summary>
    private void HideTip(CardView? owner = null) => _tip?.Hide(owner);

    /// <summary>Plays one of Balatro's own sounds (e.g. "card1").</summary>
    private static void BalatroSound(string sound, float pitch = 1f, float volume = 1f) => BalatroAudio.Instance.Play(sound, pitch, volume);

    /// <summary>The sounds the table plays, loaded ahead of time (see BridgeMod).</summary>
    internal static readonly string[] Sounds =
    [
        "paper1", "card1", "cardSlide1", "cardSlide2", "cardFan2", "whoosh1", "whoosh2", "button", "cancel",
        "chips1", "multhit1", "multhit2", "coin1", "coin2", "coin3", "foil2", "generic1", "other1",
    ];

    // Balatro's card hover: a soft paper flick at a slightly random pitch.
    private void HoverSound() => BalatroSound("paper1", 0.9f + 0.2f * GD.Randf(), 0.35f);

    private void UpdateTarget()
    {
        if (_target == null || _targetHp == null) return;
        _targetHp.Text = _target.Block > 0 ? $"{_target.CurrentHp}/{_target.MaxHp} HP  +{_target.Block} block" : $"{_target.CurrentHp}/{_target.MaxHp} HP";
    }

    private static Control Counter(string title, Color colour, Vector2 at, out Label value)
    {
        var box = TableStyle.Panel(new Rect2(at, new Vector2(150, 100)), TableStyle.Box(new Color(colour, 0.9f), new Color(1, 1, 1, 0.3f), 12, 2, 6));
        box.AddChild(TableStyle.Text(title, 22, Colors.White, new Rect2(0, 6, 150, 30), TableStyle.Regular, 5));
        value = TableStyle.Text("0", 46, Colors.White, new Rect2(0, 34, 150, 60));
        box.AddChild(value);
        return box;
    }

    // Effects -------------------------------------------------------------------------------------

    private void Move(CardView view, Vector2 center, float rotation, float seconds, float delay = 0,
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

    private static void Juice(Control control, float amount)
    {
        control.Scale = Vector2.One * (1 + amount);
        Tween t = control.CreateTween();
        t.TweenProperty(control, "scale", Vector2.One, 0.25 / Speed).SetTrans(Tween.TransitionType.Elastic).SetEase(Tween.EaseType.Out);
    }

    private static void Fade(Control control, float alpha, float seconds)
    {
        control.CreateTween().TweenProperty(control, "modulate:a", alpha, seconds / Speed);
    }

    private void Shake(float strength)
    {
        Vector2 home = _stage.Position;
        Tween t = _stage.CreateTween();
        for (int i = 0; i < 8; i++)
        {
            float falloff = 1f - i / 8f;
            var offset = new Vector2((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0)) * strength * falloff;
            t.TweenProperty(_stage, "position", home + offset, 0.035);
        }
        t.TweenProperty(_stage, "position", home, 0.05);
    }

    /// <summary>A burst of square, Balatro-style pixel embers.</summary>
    private void Embers(Vector2 at, int amount, Color colour)
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
        _stage.AddChild(embers);
        embers.Finished += embers.QueueFree;
    }

    private Task Wait(float seconds) => ToSignal(((SceneTree)Engine.GetMainLoop()).CreateTimer(seconds / Speed), SceneTreeTimer.SignalName.Timeout);

    private Task ToSignal(Tween tween) => ToSignal(tween, Tween.SignalName.Finished);

    private async Task ToSignal(GodotObject source, StringName signal) => await _root.ToSignal(source, signal);
}
