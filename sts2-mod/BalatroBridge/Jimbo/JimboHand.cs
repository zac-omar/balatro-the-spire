using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's hand in a fight: the Balatro table docked along the bottom of StS2's combat screen,
/// where StS2's own hand, energy and piles would be. Pick up to five cards, then Play Hand to hit
/// the targeted enemy with the score, or Discard to throw them away for Block. Each turn brings
/// its own hands and discards; End Turn is StS2's, drawn over the felt. Jokers score from the relic bar and
/// consumables are used from the potion belt, as relics and potions.
/// Click an enemy to aim at it. This class only presents and collects input; the fight's rules
/// live in <see cref="JimboCombat"/>. Layout is in a 1920x1080 stage scaled to the screen.
/// </summary>
internal sealed class JimboHand
{
    private static readonly Vector2 StageSize = new(1920, 1080);
    private const float BandTop = 748;
    private const float HandY = 958;
    private const float CardScale = 0.78f;
    private const float HandX = 900;
    private const float PlayY = 470;
    private static readonly Vector2 DeckPosition = new(1990, 990);

    // The felt band: Balatro's swirl, fading out at its top edge into the fight.
    private const string BandShaderCode = """
        shader_type canvas_item;
        uniform vec4 colour_1 : source_color = vec4(0.24, 0.42, 0.35, 1.0);
        uniform vec4 colour_2 : source_color = vec4(0.06, 0.08, 0.11, 1.0);
        void fragment() {
            vec2 cell = floor(UV * vec2(384.0, 66.0)) / vec2(384.0, 66.0);
            vec2 uv = (cell - vec2(0.5, 0.9)) * vec2(5.8, 1.0);
            float r = length(uv);
            float a = atan(uv.y, uv.x) + TIME * 0.06 + r * (2.5 + sin(TIME * 0.11)) * 0.4;
            float swirl = sin(a * 3.0 + r * 3.0 - TIME * 0.35) * 0.5 + 0.5;
            vec3 col = mix(colour_2.rgb, colour_1.rgb, smoothstep(0.2, 0.8, swirl) * 0.8);
            float edge = smoothstep(0.0, 0.32, UV.y);
            COLOR = vec4(col, 0.93 * edge);
        }
        """;

    private readonly Control _root;
    private readonly Control _stage;
    private readonly ShaderMaterial _feltMaterial;
    private readonly Control _cards;
    private readonly BalatroArt _art = BalatroArt.Shared;

    private readonly TextureRect _blindChip;
    private readonly Label _blindName;
    private readonly Label _turn;
    private readonly RichTextLabel _blindText;
    private readonly Label _blindGoal;
    private readonly Label _hands;
    private readonly Label _discards;
    private readonly Label _money;
    private readonly Label _ante;
    private readonly Label _deck;
    private readonly Label _target;

    private readonly Panel _plate;
    private readonly Label _handName;
    private readonly Label _handLevel;
    private readonly Panel _chipsBox;
    private readonly Panel _multBox;
    private readonly Label _chips;
    private readonly Label _mult;
    private readonly Label _status;

    private readonly Button _play;
    private readonly Button _discard;
    private readonly Button _sortRank;
    private readonly Button _sortSuit;

    private readonly List<CardView> _hand = new();
    private readonly List<CardView> _played = new();
    private readonly List<CardView> _strikers = new();
    private float _fire; // the hand being played is on fire (0 to 1)
    private readonly List<Control> _scoreNodes = new();
    private readonly Dictionary<NCreature, Callable> _clickHandlers = new();
    private TableSnapshot? _table;
    private bool _acceptingInput;
    private int _previewVersion;
    private bool _removed;
    private NCreature? _reticle;
    private readonly CardTip _tip;
    private readonly CardDrag _drag;
    private readonly TurnFuse _fuse;
    private readonly Control? _endTurn;
    private readonly int _endTurnZ;

    /// <summary>The docked hand of the fight in progress, if any.</summary>
    public static JimboHand? Current { get; private set; }

    /// <summary>The player pressed Play Hand (true) or Discard (false) with these cards.</summary>
    public event Action<bool, int[]>? Submitted;

    /// <summary>The player asked to sort the hand by suit (true) or rank.</summary>
    public event Action<bool>? SortRequested;

    /// <summary>The selection changed (consumables may work on these cards).</summary>
    public event Action<int[]>? SelectionChanged;

    /// <summary>The player dragged cards into this order (ids, left to right).</summary>
    public event Action<int[]>? Reordered;

    /// <summary>The enemy Jimbo's hands are aimed at.</summary>
    public Creature? Target { get; private set; }

    public int[] Selected => _hand.Where(c => c.Selected).Select(c => c.Id).ToArray();

    public bool IsAcceptingInput => _acceptingInput && !_removed;

    private static float Speed => TableFx.Speed;

    private JimboHand(Control parent)
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "JimboHand" };
        parent.AddChild(_root);
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        Vector2 screen = parent.GetViewportRect().Size;
        float scale = Math.Min(screen.X / StageSize.X, screen.Y / StageSize.Y);
        _stage = new Control
        {
            Size = StageSize,
            Scale = new Vector2(scale, scale),
            Position = new Vector2((screen.X - StageSize.X * scale) / 2, screen.Y - StageSize.Y * scale),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.AddChild(_stage);
        _tip = new CardTip(_stage, StageSize);
        // Cards are picked by clicking and moved by dragging, as in Balatro (Death copies the rightmost).
        _drag = new CardDrag(_hand, () => IsAcceptingInput, LayoutHand, Toggle, (_, ids) => Reordered?.Invoke(ids));
        _drag.Started += view => _tip.Hide(view);
        // StS2's End Turn button sits where the felt band runs: it goes on top.
        if (parent is NCombatUi ui && GodotObject.IsInstanceValid(ui.EndTurnButton))
        {
            _endTurn = ui.EndTurnButton;
            _endTurnZ = _endTurn.ZIndex;
            _endTurn.ZIndex = Math.Max(_endTurnZ, 1);
        }

        _feltMaterial = new ShaderMaterial { Shader = new Shader { Code = BandShaderCode } };
        var felt = new ColorRect
        {
            Position = new Vector2(-200, BandTop),
            Size = new Vector2(StageSize.X + 400, StageSize.Y - BandTop + 40),
            Material = _feltMaterial,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _stage.AddChild(felt);

        // Left: the blind and this turn's hands and discards.
        var info = TableStyle.Panel(new Rect2(18, 806, 404, 262), TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 14, 3, 10));
        _stage.AddChild(info);
        _blindChip = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(12, 10), Size = new Vector2(60, 60),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        info.AddChild(_blindChip);
        _blindName = TableStyle.Text("", 28, StsColors.gold, new Rect2(80, 6, 316, 36));
        _blindName.HorizontalAlignment = HorizontalAlignment.Left;
        info.AddChild(_blindName);
        _blindGoal = TableStyle.Text("", 19, StsColors.cream, new Rect2(80, 40, 316, 26), TableStyle.Regular, 5);
        _blindGoal.HorizontalAlignment = HorizontalAlignment.Left;
        info.AddChild(_blindGoal);
        _blindText = TableStyle.RichText("", 17, centred: false);
        _blindText.Position = new Vector2(14, 74);
        _blindText.Size = new Vector2(376, 44);
        info.AddChild(_blindText);
        info.AddChild(Counter("Hands", TableStyle.Chips, new Vector2(14, 122), out _hands));
        info.AddChild(Counter("Discards", TableStyle.Mult, new Vector2(140, 122), out _discards));
        _money = TableStyle.Text("$0", 38, TableStyle.Money, new Rect2(266, 122, 128, 52));
        info.AddChild(_money);
        _ante = TableStyle.Text("", 19, StsColors.cream, new Rect2(266, 172, 128, 26), TableStyle.Regular, 5);
        info.AddChild(_ante);
        _turn = TableStyle.Text("", 19, new Color(StsColors.cream, 0.8f), new Rect2(266, 196, 128, 26), TableStyle.Regular, 5);
        info.AddChild(_turn);
        _fuse = new TurnFuse(_stage);
        _deck = TableStyle.Text("", 19, new Color(StsColors.cream, 0.8f), new Rect2(14, 222, 190, 30), TableStyle.Regular, 5);
        _deck.HorizontalAlignment = HorizontalAlignment.Left;
        info.AddChild(_deck);
        _target = TableStyle.Text("", 19, StsColors.red, new Rect2(186, 222, 208, 30), TableStyle.Regular, 5);
        _target.HorizontalAlignment = HorizontalAlignment.Right;
        info.AddChild(_target);

        // Above the hand: which poker hand the selection makes, chips x mult.
        _plate = TableStyle.Panel(new Rect2(HandX - 260, 800, 520, 62), TableStyle.Box(new Color(TableStyle.PanelFill, 0.85f), new Color(1, 1, 1, 0.18f), 12, 2, 6));
        _stage.AddChild(_plate);
        _handName = TableStyle.Text("", 26, StsColors.cream, new Rect2(10, 2, 230, 40));
        _handName.HorizontalAlignment = HorizontalAlignment.Left;
        _handLevel = TableStyle.Text("", 17, StsColors.gold, new Rect2(10, 36, 230, 22), TableStyle.Regular, 4);
        _handLevel.HorizontalAlignment = HorizontalAlignment.Left;
        _chipsBox = TableStyle.Panel(new Rect2(250, 8, 118, 46), TableStyle.Box(TableStyle.Chips, new Color(1, 1, 1, 0.3f), 10, 2, 4));
        _multBox = TableStyle.Panel(new Rect2(394, 8, 118, 46), TableStyle.Box(TableStyle.Mult, new Color(1, 1, 1, 0.3f), 10, 2, 4));
        _chips = TableStyle.Text("0", 30, Colors.White, new Rect2(0, 0, 118, 46));
        _mult = TableStyle.Text("0", 30, Colors.White, new Rect2(0, 0, 118, 46));
        _chipsBox.AddChild(_chips);
        _multBox.AddChild(_mult);
        foreach (Control c in new Control[] { _handName, _handLevel, _chipsBox, _multBox }) _plate.AddChild(c);
        _plate.AddChild(TableStyle.Text("X", 28, StsColors.cream, new Rect2(366, 8, 30, 46)));
        _status = TableStyle.Text("", 22, StsColors.red, new Rect2(HandX - 400, 760, 800, 34));
        _stage.AddChild(_status);

        _cards = new Control { Size = StageSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        _stage.AddChild(_cards);

        // Right, short of StS2's End Turn button: what to do with the selection.
        _play = TableStyle.Button("Play Hand", TableStyle.Chips, new Rect2(1388, 812, 210, 74), 28);
        _discard = TableStyle.Button("Discard", TableStyle.Mult, new Rect2(1388, 896, 210, 74), 28);
        _sortRank = TableStyle.Button("Rank", new Color(0.32f, 0.36f, 0.42f), new Rect2(1388, 982, 100, 44), 20);
        _sortSuit = TableStyle.Button("Suit", new Color(0.32f, 0.36f, 0.42f), new Rect2(1498, 982, 100, 44), 20);
        _play.Pressed += () => Submit(play: true);
        _discard.Pressed += () => Submit(play: false);
        _sortRank.Pressed += () => Sort(bySuit: false);
        _sortSuit.Pressed += () => Sort(bySuit: true);
        foreach (Button b in new[] { _play, _discard, _sortRank, _sortSuit }) _stage.AddChild(b);

        SetStatus("Shuffling the deck...", StsColors.cream);
        ShowPreview(null);
        RefreshButtons();
        _stage.Modulate = new Color(1, 1, 1, 0);
        _stage.Position += new Vector2(0, 60);
        Tween t = _stage.CreateTween().SetParallel();
        t.TweenProperty(_stage, "modulate:a", 1f, 0.35 / Speed);
        t.TweenProperty(_stage, "position:y", _stage.Position.Y - 60, 0.45 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    /// <summary>Docks a fresh hand onto the fight's UI.</summary>
    public static JimboHand Attach(Control combatUi)
    {
        Current?.Remove();
        Current = new JimboHand(combatUi);
        return Current;
    }

    public void SetStatus(string text, Color colour)
    {
        _status.Text = text;
        _status.LabelSettings.FontColor = colour;
    }

    // Dealing ----------------------------------------------------------------------------------

    /// <summary>
    /// Shows the hand and the run around it: new cards are dealt in from the deck, cards that
    /// changed in hand (a tarot's enhancement, a new rank) flip over where they are, and cards
    /// that left (destroyed, say) go.
    /// </summary>
    public async Task Deal(TableSnapshot table, bool deselect = false)
    {
        if (_removed) return;
        _drag.Cancel();
        ApplyInfo(table);
        if (table.Hand.Count > 0) SetStatus("", StsColors.cream);

        var keep = _hand.ToDictionary(c => c.Id);
        _hand.Clear();
        var dealt = new List<CardView>();
        var changed = new List<CardView>();
        foreach (TableCard card in table.Hand)
        {
            if (keep.Remove(card.Id, out CardView? view) && view.Card == card)
            {
                if (deselect) view.SetSelected(false);
                _hand.Add(view);
                continue;
            }
            CardView fresh = CreateCard(card);
            if (view != null)
            {
                // Still in hand, but different now: it changes in place.
                fresh.Root.Position = view.Root.Position;
                fresh.Root.Rotation = view.Root.Rotation;
                if (view.Selected && !deselect) fresh.SetSelected(true);
                view.Root.QueueFree();
                changed.Add(fresh);
            }
            else
            {
                fresh.Root.Position = DeckPosition - fresh.Root.Size / 2;
                fresh.Root.Rotation = 0.6f;
                dealt.Add(fresh);
            }
            _hand.Add(fresh);
        }
        foreach (CardView view in changed)
        {
            TableFx.Juice(view.Root, 0.25f);
            TableFx.Sound("tarot2", 1f + 0.05f * changed.IndexOf(view), 0.4f);
        }
        foreach (CardView gone in keep.Values) Vanish(gone);
        // The Cerulean Bell's card is always picked.
        foreach (CardView view in _hand) if (view.Card.Forced) view.SetSelected(true);
        for (int i = 0; i < _hand.Count; i++) _cards.MoveChild(_hand[i].Root, i);

        LayoutHand();
        for (int i = 0; i < _hand.Count; i++)
        {
            CardView view = _hand[i];
            float delay = dealt.IndexOf(view) is var d and >= 0 ? d * 0.06f : 0f;
            TableFx.Move(view, view.Home, view.HomeRotation, 0.34f, delay, Tween.TransitionType.Back);
        }
        for (int i = 0; i < dealt.Count; i++)
        {
            TableFx.Sound("card1", 0.85f + 0.2f * i / Math.Max(dealt.Count - 1, 1), 0.55f);
            await TableFx.Wait(0.06f);
        }
        if (dealt.Count > 0) await TableFx.Wait(0.2f);
        RefreshButtons();
        await UpdatePreview();
        if (deselect) SelectionChanged?.Invoke(Selected);
    }

    // A card that left the hand outside of a play or discard (destroyed by a tarot, say) burns away.
    private void Vanish(CardView view)
    {
        view.MoveTween?.Kill();
        TableFx.Sound("crumple1", 1f, 0.4f);
        Tween t = view.Root.CreateTween().SetParallel();
        t.TweenProperty(view.Root, "modulate", new Color(1f, 0.4f, 0.2f, 0f), 0.35 / Speed);
        t.TweenProperty(view.Root, "scale", new Vector2(1.15f, 1.15f), 0.35 / Speed);
        t.Chain().TweenCallback(Callable.From(view.Root.QueueFree));
    }

    /// <summary>Updates the counters and blind without touching the cards (a new turn, say).</summary>
    public void ApplyInfo(TableSnapshot table)
    {
        _art.Learn(table);
        _table = table;
        TableBlind? blind = table.Blind;
        Color blindColour = blind?.Colour is string hex ? new Color(hex) : TableStyle.DefaultFelt;
        _feltMaterial.SetShaderParameter("colour_1", blind?.Boss == true ? blindColour.Darkened(0.3f) : TableStyle.DefaultFelt);
        _blindName.Text = blind?.Name ?? "";
        _blindName.LabelSettings.FontColor = blind?.Boss == true ? blindColour.Lightened(0.3f) : StsColors.gold;
        _blindGoal.Text = blind?.Chips is double goal ? $"Blind {TableText.Number(goal)}  ·  Reward ${TableText.Number(blind.Dollars ?? 0)}" : "";
        string rule = blind?.Text is string text && !blind.Disabled ? text : "";
        // Verdant Leaf wants a joker sold: in StS2 that's from the relic bar.
        if (blind is { Key: "bl_final_leaf", Disabled: false }) rule += "\n[gold](Right-click a joker relic to sell it.)[/gold]";
        _blindText.Text = TableStyle.Markup(rule, centred: false);
        _blindChip.Texture = _art.Sprite(blind?.Sprite);
        _hands.Text = table.HandsLeft.ToString();
        _discards.Text = table.DiscardsLeft.ToString();
        _money.Text = "$" + TableText.Number(table.Dollars);
        _ante.Text = $"Ante {table.Ante}/8";
        _deck.Text = $"Deck {table.DeckLeft}/{table.DeckTotal}";
        UpdateTargetLabel();
        RefreshButtons();
    }

    private CardView CreateCard(TableCard card)
    {
        var view = new CardView(card, _art, _table?.Back, CardScale);
        // Hovering a card in hand shows what it does (enhancement, edition, seal), above it.
        view.Root.MouseEntered += () =>
        {
            SetHover(view, true);
            if (_hand.Contains(view) && _drag.Dragging == null) _tip.Show(view, above: true);
        };
        view.Root.MouseExited += () =>
        {
            SetHover(view, false);
            _tip.Hide(view);
        };
        _drag.Attach(view);
        view.Root.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
            ClearSelection();
            view.Root.AcceptEvent();
        };
        _cards.AddChild(view.Root);
        return view;
    }

    private void LayoutHand()
    {
        int n = _hand.Count;
        float spacing = Math.Min(118f, 840f / Math.Max(n, 1));
        float mid = (n - 1) / 2f;
        for (int i = 0; i < n; i++)
        {
            CardView view = _hand[i];
            float offset = i - mid;
            float lift = view.Selected ? 42 : view.Hovered ? 14 : 0;
            view.Home = new Vector2(HandX + offset * spacing, HandY + offset * offset * 2.6f - lift);
            view.HomeRotation = Mathf.DegToRad(offset * 2.2f) * (view.Selected ? 0.4f : 1f);
        }
    }

    private void Relayout()
    {
        LayoutHand();
        foreach (CardView view in _hand)
        {
            if (view != _drag.Dragging) TableFx.Move(view, view.Home, view.HomeRotation, 0.16f);
        }
    }

    // Input ------------------------------------------------------------------------------------

    /// <summary>Lets the player pick cards and act (their turn), or not (busy, or the enemies' turn).</summary>
    public void SetInputEnabled(bool on)
    {
        _acceptingInput = on;
        if (!on) foreach (CardView view in _hand) view.Hovered = false;
        RefreshButtons();
    }

    private void SetHover(CardView view, bool hovered)
    {
        if (_drag.Dragging != null) return;
        if (!_hand.Contains(view) || !_acceptingInput)
        {
            view.Hovered = false;
            return;
        }
        view.Hovered = hovered;
        if (hovered) TableFx.HoverSound();
        Relayout();
    }

    private void Toggle(CardView view)
    {
        if (!_acceptingInput || !_hand.Contains(view) || _table == null) return;
        if (view.Selected && view.Card.Forced)
        {
            // The Cerulean Bell won't let it go.
            TableFx.Juice(view.Root, 0.08f);
            TableFx.Sound("cancel", 1f, 0.5f);
            SetStatus("The Cerulean Bell forces this card!", CardView.Cerulean);
            return;
        }
        if (!view.Selected && Selected.Length >= _table.SelectLimit)
        {
            TableFx.Juice(view.Root, 0.08f);
            TableFx.Sound("cancel", 1f, 0.5f);
            return;
        }
        view.SetSelected(!view.Selected);
        TableFx.Sound(view.Selected ? "cardSlide1" : "cardSlide2", 1f, view.Selected ? 1f : 0.3f);
        Relayout();
        RefreshButtons();
        _ = UpdatePreview();
        SelectionChanged?.Invoke(Selected);
    }

    private void ClearSelection()
    {
        if (!_acceptingInput || !_hand.Any(c => c.Selected && !c.Card.Forced)) return;
        TableFx.Sound("cardSlide2", 1f, 0.3f);
        foreach (CardView view in _hand) view.SetSelected(view.Card.Forced);
        Relayout();
        RefreshButtons();
        _ = UpdatePreview();
        SelectionChanged?.Invoke(Selected);
    }

    private void Submit(bool play)
    {
        if (!_acceptingInput || _table == null) return;
        int[] ids = Selected;
        if (ids.Length == 0) return;
        if (play ? _table.HandsLeft <= 0 : _table.DiscardsLeft <= 0) return;
        TableFx.Sound("button");
        SetInputEnabled(false);
        Submitted?.Invoke(play, ids);
    }

    private void Sort(bool bySuit)
    {
        if (!_acceptingInput) return;
        TableFx.Sound("button");
        SortRequested?.Invoke(bySuit);
    }

    /// <summary>Puts the hand in the order Balatro sorted it.</summary>
    public void Reorder(TableSnapshot sorted)
    {
        var byId = _hand.ToDictionary(c => c.Id);
        _hand.Clear();
        foreach (TableCard card in sorted.Hand)
        {
            if (byId.TryGetValue(card.Id, out CardView? view)) _hand.Add(view);
        }
        for (int i = 0; i < _hand.Count; i++) _cards.MoveChild(_hand[i].Root, i);
        TableFx.Sound("paper1", 1f, 0.6f);
        Relayout();
    }

    private void RefreshButtons()
    {
        bool any = Selected.Length > 0;
        int hands = _table?.HandsLeft ?? 0, discards = _table?.DiscardsLeft ?? 0;
        _play.Disabled = !_acceptingInput || !any || hands <= 0;
        _discard.Disabled = !_acceptingInput || !any || discards <= 0;
        _sortRank.Disabled = _sortSuit.Disabled = !_acceptingInput;
        _play.Text = hands > 0 ? "Play Hand" : "No hands";
        _discard.Text = discards > 0 ? "Discard" : "No discards";
    }

    private async Task UpdatePreview()
    {
        int version = ++_previewVersion;
        int[] ids = Selected;
        if (ids.Length == 0 || _table == null)
        {
            ShowPreview(null);
            return;
        }
        try
        {
            HandPreview preview = await JimboRun.Session().Preview(ids);
            if (version == _previewVersion && !_removed) ShowPreview(preview);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: preview failed: {e.Message}");
        }
    }

    private void ShowPreview(HandPreview? preview)
    {
        if (preview == null || preview.IsEmpty)
        {
            _handName.Text = _table?.Hand.Count > 0 ? "Pick up to " + (_table?.SelectLimit ?? 5) : "";
            _handName.LabelSettings.FontColor = new Color(StsColors.cream, 0.6f);
            _handLevel.Text = "";
            _chips.Text = "0";
            _mult.Text = "0";
            ClearBossLine();
            return;
        }
        bool changed = _handName.Text != preview.Hand;
        _handName.Text = preview.Hand;
        _handName.LabelSettings.FontColor = StsColors.cream;
        _handLevel.Text = preview.Level.Length > 0 ? "lvl." + preview.Level : "";
        _chips.Text = preview.Chips;
        _mult.Text = preview.Mult;
        // As Balatro does: a hand the boss would refuse says so before it's played ("Hand will not
        // score" and the boss's rule) and the blind's chip twitches; a hand it would punish says how.
        if (preview.BossLine is string line)
        {
            if (line != _bossLine)
            {
                TableFx.Juice(_blindChip, 0.15f);
                TableFx.Sound("chips1", 0.55f + 0.1f * GD.Randf(), 0.3f);
            }
            _bossLine = line;
            SetStatus(line, preview.Allowed ? StsColors.gold : StsColors.red);
        }
        else ClearBossLine();
        if (changed) TableFx.Juice(_handName, 0.12f);
    }

    private string? _bossLine;

    private void ClearBossLine()
    {
        if (_bossLine != null && _status.Text == _bossLine) SetStatus("", StsColors.cream);
        _bossLine = null;
    }

    // Targeting --------------------------------------------------------------------------------

    /// <summary>Aims at an enemy (or the first one standing) and lets enemies be clicked to aim at them.</summary>
    public void SetTarget(Creature? target)
    {
        Target = target;
        _reticle?.HideMultiselectReticle();
        _reticle = null;
        if (target != null && NCombatRoom.Instance?.GetCreatureNode(target) is NCreature node && GodotObject.IsInstanceValid(node))
        {
            node.ShowMultiselectReticle();
            _reticle = node;
            ListenForClicks(node);
        }
        UpdateTargetLabel();
    }

    /// <summary>Makes every enemy on screen clickable as a target.</summary>
    public void ListenForClicks(IEnumerable<Creature> enemies)
    {
        foreach (Creature enemy in enemies)
        {
            if (NCombatRoom.Instance?.GetCreatureNode(enemy) is NCreature node && GodotObject.IsInstanceValid(node)) ListenForClicks(node);
        }
    }

    private void ListenForClicks(NCreature node)
    {
        if (_clickHandlers.ContainsKey(node)) return;
        Callable handler = Callable.From<InputEvent>(input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && node.Entity is { IsAlive: true } enemy && enemy != Target)
            {
                TableFx.Sound("cardSlide1", 1.1f, 0.6f);
                SetTarget(enemy);
            }
        });
        node.Hitbox.Connect(Control.SignalName.GuiInput, handler);
        _clickHandlers[node] = handler;
    }

    private void UpdateTargetLabel() => _target.Text = Target is { IsAlive: true } t ? "Aim: " + t.Name : "";

    // Discarding -------------------------------------------------------------------------------

    public async Task ThrowAway(int[] ids)
    {
        int i = 0;
        foreach (CardView view in _hand.Where(c => ids.Contains(c.Id)).ToList())
        {
            TableFx.Sound("card1", 0.75f + 0.05f * i, 0.5f);
            _hand.Remove(view);
            Tween t = view.Root.CreateTween().SetParallel();
            float delay = i++ * 0.04f;
            t.TweenProperty(view.Root, "position", new Vector2(2100, 900 + i * 10), 0.3 / Speed).SetDelay(delay / Speed)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            t.TweenProperty(view.Root, "rotation", 0.9f, 0.3 / Speed).SetDelay(delay / Speed);
            t.Chain().TweenCallback(Callable.From(view.Root.QueueFree));
        }
        Relayout();
        ShowPreview(null);
        await TableFx.Wait(0.32f);
    }

    /// <summary>The discarded cards' chips, shown as the Block they became.</summary>
    /// <summary>The turn against the blind's hands ("Turn 2/4"), or overtime once they're spent.</summary>
    /// <summary>The turn, and how near Overtime is: the fuse over the panel burns down a notch a turn.</summary>
    public void ShowTurn(int turn, int limit, int strength)
    {
        _turn.Text = limit > 0 ? $"Turn {turn}" : "";
        _turn.LabelSettings.FontColor = turn > limit && limit > 0 ? StsColors.red : new Color(StsColors.cream, 0.8f);
        _fuse.Show(turn, limit, strength);
    }

    public void ShowBlock(double chips)
    {
        if (_removed) return;
        var label = TableStyle.Text($"+{TableText.Number(chips)} Block", 40, StsColors.blue, new Rect2(560, 640, 800, 56));
        _stage.AddChild(label);
        TableFx.Juice(label, 0.25f);
        Tween t = label.CreateTween();
        t.TweenInterval(0.6 / Speed);
        t.SetParallel();
        t.TweenProperty(label, "position:y", 590f, 0.5 / Speed);
        t.TweenProperty(label, "modulate:a", 0f, 0.5 / Speed);
        t.Chain().TweenCallback(Callable.From(label.QueueFree));
    }

    // Playing and scoring ----------------------------------------------------------------------

    /// <summary>Moves the chosen cards up into the fight while Balatro scores them.</summary>
    public async Task MoveToPlay(int[] ids)
    {
        _tip.Hide();
        _played.Clear();
        _played.AddRange(_hand.Where(c => ids.Contains(c.Id)));
        foreach (CardView view in _played)
        {
            _hand.Remove(view);
            view.SetSelected(false);
            view.Hovered = false;
        }
        LayoutPlayRow();
        foreach (CardView view in _played) TableFx.Move(view, view.Home, 0, 0.3f, 0, Tween.TransitionType.Back);
        foreach (CardView view in _hand)
        {
            TableFx.Move(view, view.Home + new Vector2(0, 30), view.HomeRotation, 0.3f);
            TableFx.Fade(view.Root, 0.6f, 0.3f);
        }
        for (int i = 0; i < _played.Count; i++) TableFx.Sound("card1", 0.9f + 0.05f * i, 0.6f);
        await TableFx.Wait(0.32f);
    }

    private void LayoutPlayRow()
    {
        float spacing = 150;
        float mid = (_played.Count - 1) / 2f;
        for (int i = 0; i < _played.Count; i++) _played[i].Home = new Vector2(960 + (i - mid) * spacing, PlayY);
    }

    /// <summary>
    /// Replays Balatro's scoring over the fight, then shows the score the target is hit for. A
    /// <paramref name="fire"/> hand (see <see cref="JimboFire"/>) burns as it counts up, chips blue
    /// and mult red, and its total goes up in flames.
    /// </summary>
    public async Task Score(ScoredHand scored, decimal damage, float fire = 0)
    {
        var byId = _played.ToDictionary(c => c.Id);
        List<CardView> ordered = scored.Played.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (ordered.Count == _played.Count)
        {
            _played.Clear();
            _played.AddRange(ordered);
            LayoutPlayRow();
        }
        _strikers.Clear();
        _strikers.AddRange(_played.Where(v => scored.Scoring.Contains(v.Id)));
        if (_strikers.Count == 0) _strikers.AddRange(_played);
        foreach (CardView view in _played)
        {
            bool scores = scored.Scoring.Contains(view.Id);
            TableFx.Move(view, view.Home + new Vector2(0, scores ? -24 : 16), 0, 0.2f);
            if (!scores) TableFx.Fade(view.Root, 0.6f, 0.2f);
        }

        // The score plate rises over the fight, under the played cards.
        var plate = TableStyle.Panel(new Rect2(620, PlayY + 120, 680, 92), TableStyle.Box(new Color(TableStyle.PanelFill, 0.9f), TableStyle.PanelEdge, 14, 3, 10));
        var hand = TableStyle.Text(scored.Hand, 30, StsColors.cream, new Rect2(14, 4, 300, 84));
        hand.HorizontalAlignment = HorizontalAlignment.Left;
        var chipsBox = TableStyle.Panel(new Rect2(330, 14, 150, 64), TableStyle.Box(TableStyle.Chips, new Color(1, 1, 1, 0.35f), 12, 2, 6));
        var multBox = TableStyle.Panel(new Rect2(516, 14, 150, 64), TableStyle.Box(TableStyle.Mult, new Color(1, 1, 1, 0.35f), 12, 2, 6));
        Label chips = TableStyle.Text("0", 40, Colors.White, new Rect2(0, 0, 150, 64));
        Label mult = TableStyle.Text("0", 40, Colors.White, new Rect2(0, 0, 150, 64));
        chipsBox.AddChild(chips);
        multBox.AddChild(mult);
        plate.AddChild(hand);
        plate.AddChild(chipsBox);
        plate.AddChild(multBox);
        plate.AddChild(TableStyle.Text("X", 36, StsColors.cream, new Rect2(480, 14, 36, 64)));
        plate.Modulate = new Color(1, 1, 1, 0);
        _stage.AddChild(plate);
        _scoreNodes.Add(plate);
        _fire = fire;
        ColorRect? chipsFire = null, multFire = null;
        if (fire > 0)
        {
            // In front of the plate, behind the chips and mult.
            chipsFire = Flame.Create(new Rect2(310, -150, 190, 228), Flame.Chips);
            multFire = Flame.Create(new Rect2(496, -150, 190, 228), Flame.Mult);
            foreach (ColorRect flame in new[] { chipsFire, multFire })
            {
                plate.AddChild(flame);
                plate.MoveChild(flame, 0);
            }
        }
        TableFx.Fade(plate, 1f, 0.15f);
        await TableFx.Wait(0.2f);

        float step = Math.Clamp(3.0f / Math.Max(scored.Trace.Count, 1), 0.12f, 0.4f);
        int counted = 0;
        foreach (TraceStep traceStep in scored.Trace)
        {
            // The fire builds as the hand counts up.
            if (chipsFire != null && multFire != null)
            {
                float burn = fire * (0.25f + 0.75f * ++counted / scored.Trace.Count);
                Flame.Heat(chipsFire, burn, 0.15);
                Flame.Heat(multFire, burn, 0.15);
            }
            switch (traceStep)
            {
                case CounterStep counter:
                    if (!string.IsNullOrEmpty(counter.Hand))
                    {
                        hand.Text = counter.Hand + (string.IsNullOrEmpty(counter.Level) ? "" : $"  lvl.{counter.Level}");
                        TableFx.Juice(hand, 0.12f);
                    }
                    if (counter.Chips != null && counter.Chips != chips.Text)
                    {
                        chips.Text = counter.Chips;
                        TableFx.Juice(chipsBox, 0.14f);
                    }
                    if (counter.Mult != null && counter.Mult != mult.Text)
                    {
                        mult.Text = counter.Mult;
                        TableFx.Juice(multBox, 0.14f);
                    }
                    break;
                case PopupStep popup:
                    ShowPopup(popup);
                    await TableFx.Wait(step);
                    break;
            }
        }
        await TableFx.Wait(0.25f);

        // Chips and mult slam together into the total.
        Tween slam = plate.CreateTween().SetParallel();
        slam.TweenProperty(chipsBox, "position:x", 423f, 0.15 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
        slam.TweenProperty(multBox, "position:x", 423f, 0.15 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
        await TableFx.Finished(slam);
        chipsBox.Visible = multBox.Visible = false;
        if (chipsFire != null && multFire != null)
        {
            // The two fires merge into one blaze behind the total.
            Flame.Heat(chipsFire, 0f, 0.15);
            Flame.Heat(multFire, 0f, 0.15);
            ColorRect blaze = Flame.Create(new Rect2(230, -254, 520, 360), Flame.Fire);
            plate.AddChild(blaze);
            plate.MoveChild(blaze, 0);
            Flame.Heat(blaze, fire * 1.3f, 0.12).Chain().TweenMethod(
                Callable.From<float>(h => ((ShaderMaterial)blaze.Material).SetShaderParameter("heat", h)), fire * 1.3f, fire, 0.4 / Speed);
            TableFx.Sound("explosion_release1", 1f, 0.45f + 0.35f * fire);
        }
        var total = TableStyle.Text(TableText.Number(scored.Score), 84, StsColors.gold, new Rect2(300, -24, 380, 130), TableStyle.Title, 14);
        plate.AddChild(total);
        total.Scale = Vector2.Zero;
        Tween pop = total.CreateTween();
        pop.TweenProperty(total, "scale", new Vector2(1.25f, 1.25f), 0.13 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        pop.TweenProperty(total, "scale", Vector2.One, 0.16 / Speed);
        TableStyle.Sfx("ui/cards/card_impact_into_multi", 1f);
        float heat = (float)Math.Clamp(Math.Log10(Math.Max(scored.Score, 1)), 1, 8);
        TableFx.Embers(_stage, new Vector2(960, PlayY + 166), (int)(heat * 12), StsColors.gold);
        await TableFx.Wait(0.35f);
    }

    // Scoring popups land on the card that scored, a card held in hand, or the joker's relic.
    private void ShowPopup(PopupStep popup)
    {
        Vector2? at = popup.Area switch
        {
            "play" => _played.FirstOrDefault(c => c.Id == popup.CardId) is CardView p ? p.Center + new Vector2(0, -p.Root.Size.Y / 2 - 26) : null,
            "hand" => _hand.FirstOrDefault(c => c.Id == popup.CardId) is CardView h ? h.Center + new Vector2(0, -h.Root.Size.Y / 2 - 20) : null,
            "jokers" => JokerRelicPoint(popup.CardId),
            _ => null,
        };
        CardView? card = _played.Concat(_hand).FirstOrDefault(c => c.Id == popup.CardId);
        if (card != null) TableFx.Juice(card.Root, 0.15f);
        TableFx.Popup(_stage, at ?? new Vector2(960, PlayY - 140), popup.Text, popup.Kind);
    }

    /// <summary>Where a joker's relic sits in the relic bar, in stage space; it flashes as it scores.</summary>
    private Vector2? JokerRelicPoint(int cardId)
    {
        try
        {
            NRelicInventory? inventory = NRun.Instance?.GlobalUi.RelicInventory;
            NRelicInventoryHolder? holder = inventory?.RelicNodes.FirstOrDefault(h => h.Relic?.Model is JokerRelic j && j.CardId == cardId);
            if (holder == null || !GodotObject.IsInstanceValid(holder)) return null;
            ((JokerRelic)holder.Relic.Model).Flash();
            Vector2 screen = holder.GetGlobalTransformWithCanvas() * (holder.Size / 2);
            return _stage.GetGlobalTransformWithCanvas().AffineInverse() * screen + new Vector2(0, 70);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: couldn't find the joker's relic: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// One strike of the volley: the scoring card <paramref name="cardId"/> flies into
    /// <paramref name="target"/> (with no card, the whole play flies in one throw).
    /// </summary>
    public async Task Strike(int? cardId, Creature target)
    {
        if (_removed) return;
        List<CardView> flying = cardId is int id ? _strikers.Where(v => v.Id == id).ToList() : _strikers.ToList();
        if (flying.Count == 0) return;
        int index = _strikers.Count - _strikers.Count(v => _played.Contains(v));
        Vector2 into = new(1400, 420);
        if (NCombatRoom.Instance?.GetCreatureNode(target) is NCreature node && GodotObject.IsInstanceValid(node))
        {
            Vector2 screen = node.Hitbox.GetGlobalTransformWithCanvas() * (node.Hitbox.Size / 2);
            into = _stage.GetGlobalTransformWithCanvas().AffineInverse() * screen;
        }
        // Later cards fly faster, so a five-card volley rattles off like Balatro's scoring.
        float time = Math.Max(0.12f, 0.24f - 0.03f * index);
        if (_fire > 0)
        {
            // A hand on fire throws burning cards.
            foreach (CardView view in flying)
            {
                Vector2 size = view.Root.Size;
                ColorRect trail = Flame.Create(new Rect2(-size.X * 0.25f, -size.Y * 0.55f, size.X * 1.5f, size.Y * 1.55f), Flame.Fire);
                view.Root.AddChild(trail);
                view.Root.MoveChild(trail, 0);
                Flame.Heat(trail, _fire, 0.1);
            }
        }
        TableFx.Sound("card3", 0.9f + 0.07f * index, 0.6f);
        TableFx.Sound("whoosh2", 1f + 0.05f * index, 0.3f);
        Tween t = _stage.CreateTween().SetParallel();
        foreach (CardView view in flying)
        {
            view.MoveTween?.Kill();
            t.TweenProperty(view.Root, "position", into - view.Root.Size / 2, time / Speed).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            t.TweenProperty(view.Root, "scale", new Vector2(0.35f, 0.35f), time / Speed);
            t.TweenProperty(view.Root, "modulate:a", 0f, 0.08 / Speed).SetDelay((time - 0.06f) / Speed);
        }
        await TableFx.Finished(t);
        foreach (CardView view in flying)
        {
            _played.Remove(view);
            view.Root.QueueFree();
        }
    }

    /// <summary>After the volley: the cards that didn't score and the score plate fade, and the hand comes back.</summary>
    public async Task ClearPlay()
    {
        if (_removed) return;
        Tween t = _stage.CreateTween().SetParallel();
        foreach (CardView view in _played) t.TweenProperty(view.Root, "modulate:a", 0f, 0.25 / Speed);
        foreach (Control node in _scoreNodes) t.TweenProperty(node, "modulate:a", 0f, 0.3 / Speed);
        await TableFx.Finished(t);
        foreach (CardView view in _played) view.Root.QueueFree();
        _played.Clear();
        _strikers.Clear();
        _fire = 0;
        foreach (Control node in _scoreNodes) node.QueueFree();
        _scoreNodes.Clear();
        foreach (CardView view in _hand) TableFx.Fade(view.Root, 1f, 0.2f);
        Relayout();
    }

    // Leaving ----------------------------------------------------------------------------------

    /// <summary>Slides the table away (the fight is over).</summary>
    public async Task Close()
    {
        if (_removed) return;
        SetInputEnabled(false);
        Tween t = _stage.CreateTween().SetParallel();
        t.TweenProperty(_stage, "modulate:a", 0f, 0.3 / Speed);
        t.TweenProperty(_stage, "position:y", _stage.Position.Y + 80, 0.35 / Speed).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        await TableFx.Finished(t);
        Remove();
    }

    public void Remove()
    {
        if (_removed) return;
        _removed = true;
        _reticle?.HideMultiselectReticle();
        foreach ((NCreature node, Callable handler) in _clickHandlers)
        {
            if (GodotObject.IsInstanceValid(node) && node.Hitbox.IsConnected(Control.SignalName.GuiInput, handler))
            {
                node.Hitbox.Disconnect(Control.SignalName.GuiInput, handler);
            }
        }
        _clickHandlers.Clear();
        if (_endTurn != null && GodotObject.IsInstanceValid(_endTurn)) _endTurn.ZIndex = _endTurnZ;
        if (GodotObject.IsInstanceValid(_root)) _root.QueueFree();
        if (Current == this) Current = null;
    }

    private static Control Counter(string title, Color colour, Vector2 at, out Label value)
    {
        var box = TableStyle.Panel(new Rect2(at, new Vector2(116, 92)), TableStyle.Box(new Color(colour, 0.9f), new Color(1, 1, 1, 0.3f), 12, 2, 6));
        box.AddChild(TableStyle.Text(title, 18, Colors.White, new Rect2(0, 4, 116, 26), TableStyle.Regular, 5));
        value = TableStyle.Text("0", 42, Colors.White, new Rect2(0, 28, 116, 58));
        box.AddChild(value);
        return box;
    }
}
