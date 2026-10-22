using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Jimbo;

internal enum ShopMove
{
    Buy,
    BuyAndUse,
    Sell,
    Use,
    Reroll,
    Pick,
    SkipPack,
    Leave,
    SkipBlind,
    RaiseBlind, // the Wager: the Boss Blind on deck plays bigger, for a Rare Tag
    MoveJoker, // to slot To (1-based), as dragging a joker in Balatro
    Refresh, // shows the shop as Balatro has it now (after something outside the shop changed it)
}

/// <summary>What the player did in Jimbo's shop: <see cref="CardId"/> is the card it's about,
/// <see cref="HandCards"/> the cards selected in hand (a tarot from a pack works on them), and
/// <see cref="To"/> where a moved joker goes.</summary>
internal sealed record ShopChoiceMade(ShopMove Move, int CardId = 0, int[]? HandCards = null, int To = 0);

/// <summary>
/// Balatro's shop on the card table, for Jimbo, laid out as Balatro's is: the shop sign, then a
/// counter with Next Round and Reroll beside the card slots (jokers, tarots, planets...), and the
/// ante's voucher and the booster packs below. Owned jokers and consumables sit in trays along
/// the top: click one to sell it (or use a consumable), or drag a joker to a new place (jokers
/// score in order, left to right). Opening a pack turns the table into
/// the pack: pick its cards (tarots and spectrals work on the hand dealt below), or skip.
/// Balatro decides everything; this class shows a snapshot and reports what the player chose.
/// </summary>
internal sealed class JimboShopScreen
{
    private static readonly Vector2 StageSize = new(1920, 1080);
    private const int Layer = 60;
    private const float TopRowY = 100; // owned jokers and consumables
    private const float SlotY = 636; // the shop's cards
    private const float LowerY = 920; // the voucher and packs
    private const float PackY = 505; // an open pack's cards
    private const float HandY = 930;
    private const float CounterX = 1110; // the middle of the shop counter
    private static readonly Color ShopRed = new("FE5F55");
    // Balatro's shop slate (G.C.BLACK) and its lighter insets (G.C.L_BLACK).
    private static readonly Color Slate = new("374244");
    private static readonly Color SlateLight = new("4F6367");
    private static readonly Color SignRed = new("EA5C53"); // the shop's G.C.DYN_UI.MAIN: red, a touch of black
    private static readonly Color RerollGreen = new("4BC292");
    private static readonly Color RunInfoOrange = new("E8801C");
    private static readonly Color UseGreen = new("35BD86");
    private static readonly Color BuyOrange = new("FDA200"); // G.C.ORANGE
    private static readonly Color BuyUseOrange = new("FD682B"); // G.C.SECONDARY_SET.Voucher
    private static readonly Color BalatroGreen = new("4BC292"); // G.C.GREEN: redeem, open, sell, select
    private static readonly Color UseRed = new("FE5F55"); // G.C.RED: use

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly ShaderMaterial _feltMaterial;
    private readonly ColorRect _shade;
    private readonly Control _stage;
    private readonly Control _items;
    private readonly BalatroArt _art = BalatroArt.Shared;
    private readonly Label _title;
    private readonly RichTextLabel _status; // rich, so Jimbo's lines keep Balatro's colours
    private readonly Label _money;
    private readonly Label _ante;
    private readonly Label _turns;
    private readonly Label _interest;
    private readonly Label _nextName;
    private readonly TextureRect _nextChip;
    private readonly Panel _bubble;
    private bool _inShop;
    private readonly Label _nextGoal;
    private readonly RichTextLabel _nextText;
    private readonly List<Control> _nodes = new();
    private readonly List<CardView> _handCards = new();
    private readonly List<CardView> _jokerRow = new();
    private readonly CardDrag _handDrag;
    private readonly CardDrag _jokerDrag;
    private TaskCompletionSource<ShopChoiceMade>? _choice;
    private TableSnapshot? _table;
    private readonly CardTip _tip;
    private int _selectedOwned;
    private bool _removed;
    // Every card on show (with the part of the shop it's in), and the same from the layout
    // before: a card that changes place travels there, and the one acted on leaves the way
    // Balatro's does (a bought joker flies to the tray, a sold one shrinks away, a used planet
    // rises and fades, a redeemed voucher glows out).
    private readonly Dictionary<int, (CardView View, string Area)> _shown = new();
    private Dictionary<int, Placed> _before = new();
    private ShopChoiceMade? _lastChoice;
    private bool _effectShown;
    private double? _lastDollars;

    private sealed record Placed(TableCard Card, string Area, Vector2 Centre, Vector2 Size);

    // Where new nodes go: the shop counter while it's being laid out, else the table.
    private Control? _into;

    private static float Speed => TableFx.Speed;

    private JimboShopScreen()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 screen = tree.Root.GetVisibleRect().Size;
        _layer = new CanvasLayer { Layer = Layer };
        _root = new Control { Size = screen, MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All };
        _layer.AddChild(_root);
        // The room behind goes dark, so the shop reads as its own place.
        _shade = new ColorRect { Size = screen, Color = new Color(0.02f, 0.03f, 0.05f, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_shade);
        _feltMaterial = new ShaderMaterial { Shader = new Shader { Code = TableScreen.FeltShader } };
        _feltMaterial.SetShaderParameter("reveal", 0f);
        _feltMaterial.SetShaderParameter("origin", new Vector2(0.5f, 0.5f));
        _feltMaterial.SetShaderParameter("colour_1", new Color("3B5D7A"));
        _root.AddChild(new ColorRect { Size = screen, Material = _feltMaterial, MouseFilter = Control.MouseFilterEnum.Ignore });

        float scale = Math.Min(screen.X / StageSize.X, screen.Y / StageSize.Y);
        _stage = new Control
        {
            Size = StageSize,
            Scale = new Vector2(scale, scale),
            Position = (screen - StageSize * scale) / 2,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        _root.AddChild(_stage);
        _tip = new CardTip(_stage, StageSize);
        // As in Balatro, cards are clicked to pick them and dragged to move them: the pack's hand
        // (Death copies the rightmost selected card), and the jokers (they score left to right).
        _handDrag = new CardDrag(_handCards, () => _choice != null, LayoutPackHand, ToggleHandCard, (_, ids) => TaskHelper.RunSafely(ReorderHand(ids)));
        _handDrag.Started += _ => HideTip();
        _jokerDrag = new CardDrag(_jokerRow, () => _choice != null, LayoutJokers, view => SelectOwned(view.Card, view), MoveJokerTo);
        _jokerDrag.Started += _ =>
        {
            HideTip();
            ClearOwnedButtons();
            _selectedOwned = 0;
        };

        // Left: the run around the shop, and what comes next.
        var info = TableStyle.Panel(new Rect2(24, 230, 380, 420), SlateBox(Slate, 14, 12));
        _stage.AddChild(info);
        _money = TableStyle.Text("$0", 54, TableStyle.Money, new Rect2(20, 14, 340, 64));
        _ante = TableStyle.Text("", 24, StsColors.cream, new Rect2(20, 80, 340, 32), TableStyle.Regular, 5);
        _turns = TableStyle.Text("", 20, new Color(StsColors.cream, 0.8f), new Rect2(20, 114, 340, 30), TableStyle.Regular, 5);
        _interest = TableStyle.Text("", 20, TableStyle.Money, new Rect2(20, 144, 340, 30), TableStyle.Regular, 5);
        info.AddChild(_money);
        info.AddChild(_ante);
        info.AddChild(_turns);
        info.AddChild(_interest);
        info.AddChild(TableStyle.Panel(new Rect2(12, 188, 356, 212), SlateBox(SlateLight, 12, 0)));
        info.AddChild(TableStyle.Text("Up next", 20, new Color(StsColors.cream, 0.6f), new Rect2(20, 192, 340, 28), TableStyle.Regular, 4));
        _nextChip = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(30, 224),
            Size = new Vector2(64, 64),
            PivotOffset = new Vector2(32, 32),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        info.AddChild(_nextChip);
        // The blind's chip rocks gently, as chips do on Balatro's blind select.
        Tween rock = _nextChip.CreateTween().SetLoops();
        rock.TweenProperty(_nextChip, "rotation", 0.12f, 1.4).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        rock.TweenProperty(_nextChip, "rotation", -0.12f, 1.4).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        _nextName = TableStyle.Text("", 30, StsColors.gold, new Rect2(100, 222, 256, 40));
        _nextGoal = TableStyle.Text("", 20, StsColors.cream, new Rect2(100, 260, 256, 30), TableStyle.Regular, 5);
        _nextText = TableStyle.RichText("", 19);
        _nextText.Position = new Vector2(24, 298);
        _nextText.Size = new Vector2(332, 96);
        info.AddChild(_nextName);
        info.AddChild(_nextGoal);
        info.AddChild(_nextText);

        _title = TableStyle.Text("SHOP", 60, ShopRed, new Rect2(700, 254, 600, 70), TableStyle.Title, 12);
        // In the shop, Jimbo's lines (and what went wrong) show in a bubble under the run's panel;
        // over an open pack they run along the bottom.
        _bubble = TableStyle.Panel(new Rect2(24, 836, 380, 150), SlateBox(Slate, 14, 10));
        _bubble.Visible = false;
        _stage.AddChild(_bubble);
        _status = TableStyle.RichText("", 26);
        _status.Position = new Vector2(460, 1010);
        _status.Size = new Vector2(1000, 40);
        _status.CustomMinimumSize = new Vector2(1000, 0);
        _stage.AddChild(_status);
        _items = new Control { Size = StageSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        _stage.AddChild(_items);
        // An open pack's name, over its board.
        _stage.AddChild(_title);

        _root.GuiInput += OnRootInput;
        tree.Root.AddChild(_layer);
        _root.GrabFocus();
    }

    public static JimboShopScreen Open()
    {
        var screen = new JimboShopScreen();
        screen.OpenIris();
        return screen;
    }

    private void OpenIris()
    {
        Tween t = _root.CreateTween().SetParallel();
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("reveal", v)), 0f, 1f, 0.6 / Speed)
            .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("spin", v)), 5f, 0f, 1.0 / Speed)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(_stage, "modulate:a", 1f, 0.3 / Speed).SetDelay(0.2 / Speed);
        t.TweenProperty(_shade, "color:a", 0.94f, 0.4 / Speed);
        TableFx.Sound("cardFan2", 1f, 0.8f);
        TableFx.Sound("whoosh1", 1.1f, 0.35f);
    }

    public void SetStatus(string text, Color colour)
    {
        _status.Text = TableStyle.Markup(text);
        _status.AddThemeColorOverride("default_color", colour);
        _status.AddThemeFontSizeOverride("normal_font_size", _inShop ? 22 : 26);
        float width = _inShop ? 348 : 1000;
        _status.Position = _inShop ? new Vector2(40, 856) : new Vector2(460, 1010);
        _status.CustomMinimumSize = new Vector2(width, 0);
        _status.Size = new Vector2(width, 0);
        bool speaking = _inShop && text.Length > 0;
        if (speaking && !_bubble.Visible) TableFx.Juice(_bubble, 0.05f);
        _bubble.Visible = speaking;
        // The bubble fits what's said.
        if (speaking) _bubble.Size = new Vector2(380, Math.Max(64, _status.GetContentHeight() + 36));
    }

    // Balatro's panels: flat slate with a darker lip along the bottom edge, and a soft shadow.
    private static StyleBoxFlat SlateBox(Color fill, int radius, int shadow)
    {
        StyleBoxFlat box = TableStyle.Box(fill, fill.Darkened(0.35f), radius, 0, shadow);
        box.BorderWidthBottom = 6;
        return box;
    }

    /// <summary>Waits for the player's next move in the shop (or the open pack).</summary>
    public Task<ShopChoiceMade> NextChoice()
    {
        _choice = new TaskCompletionSource<ShopChoiceMade>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _choice.Task;
    }

    public void Choose(ShopChoiceMade choice)
    {
        if (_choice == null) return;
        TaskCompletionSource<ShopChoiceMade> pending = _choice;
        _choice = null;
        _lastChoice = choice;
        TableFx.Sound("button");
        HideTip();
        pending.TrySetResult(choice);
    }

    // Laying the table out -----------------------------------------------------------------------

    /// <summary>Lays out the shop, or the open pack, from a snapshot.</summary>
    public async Task Show(TableSnapshot table, bool dealIn)
    {
        if (_removed) return;
        _table = table;
        _art.Learn(table);
        _before = _shown.Where(s => GodotObject.IsInstanceValid(s.Value.View.Root))
            .ToDictionary(s => s.Key, s => new Placed(s.Value.View.Card, s.Value.Area, s.Value.View.Center, s.Value.View.Root.Size));
        _shown.Clear();
        foreach (Control node in _nodes)
        {
            if (GodotObject.IsInstanceValid(node)) node.QueueFree();
        }
        _nodes.Clear();
        _ownedButtons.Clear();
        _ownedUse = null;
        _usableNow.Clear();
        _packButtons.Clear();
        _packViews.Clear();
        _handDrag.Cancel();
        _jokerDrag.Cancel();
        _handCards.Clear();
        _jokerRow.Clear();
        HideTip();
        _selectedOwned = 0;

        SetMood(table);
        _money.Text = "$" + TableText.Number(table.Dollars);
        if (_lastDollars is double was && was != table.Dollars) MoneyPop(table.Dollars - was);
        _lastDollars = table.Dollars;
        _ante.Text = $"Ante {table.Ante}/8  ·  Round {table.Round}";
        _turns.Text = $"Hand size {table.HandSize}  ·  Deck {table.DeckTotal}";
        // What the money held now earns at the next cash-out (Balatro pays $1 per $5, up to a cap).
        _interest.Text = table.Interest is InterestRule interest
            ? $"Interest +${TableText.Number(interest.For(table.Dollars))}" + (interest.Maxed(table.Dollars) ? "  (max)" : $"  ·  max at ${TableText.Number(interest.Cap)}")
            : "No interest";
        _interest.LabelSettings.FontColor = table.Interest == null ? new Color(StsColors.cream, 0.5f) : TableStyle.Money;
        NextBlind? next = table.Shop?.NextBlind ?? table.NextBlind;
        _nextName.Text = next?.Name ?? "";
        _nextName.LabelSettings.FontColor = next?.Boss == true && next.Colour is string c ? new Color(c).Lightened(0.3f) : StsColors.gold;
        _nextChip.Texture = _art.Sprite(next?.Sprite);
        _nextGoal.Text = next?.Chips is double chips ? $"{TableText.Number(chips)} HP  ·  ${TableText.Number(next.Dollars ?? 0)}" : "";
        _nextText.Text = TableStyle.Markup(next?.Text ?? "");

        bool wasShop = _inShop;
        _inShop = table.Pack == null && table.Shop != null;
        _title.Visible = !_inShop;
        // The shop's bubble and the pack's line along the bottom don't carry over to each other.
        if (wasShop != _inShop) SetStatus("", StsColors.cream);
        ShowOwned(table);
        ShowTags(table);
        Task layout = table.Pack is PackSnapshot pack ? ShowPack(table, pack, dealIn)
            : table.Shop is ShopSnapshot shop ? ShowShop(table, shop, dealIn, drop: !wasShop)
            : Task.CompletedTask;
        SeeOff();
        _effectShown = false;
        await layout;
    }

    // Balatro's room changes with where you are (ease_background_colour_blind): the shop's swirl
    // takes the colour of the blind just beaten, and each pack has its own colours, music layer
    // and sparkle (an Arcana pack's glitter, a Celestial pack's stars and meteors).
    private string _mood = "";
    private Control? _ambience;
    private Tween? _moodTween;

    private void SetMood(TableSnapshot table)
    {
        string kind = table.Pack?.Kind ?? "shop";
        Color main, accent;
        BalatroAudio.Track track = BalatroAudio.Track.Pack;
        switch (kind)
        {
            case "tarot":
                (main, accent) = (new Color("8867A5"), new Color("2C3536"));
                break;
            case "spectral":
                (main, accent) = (new Color("4584FA"), new Color("2C3536"));
                break;
            case "planet":
                (main, accent) = (new Color("374244"), new Color("0E1213"));
                track = BalatroAudio.Track.Celestial;
                break;
            case "standard":
                (main, accent) = (new Color("2C3536"), new Color("FE5F55"));
                break;
            case "buffoon":
                (main, accent) = (new Color("FF9A00"), new Color("374244"));
                break;
            default:
                // The blind just beaten: Small and Big Blinds' green, or a boss's own colour.
                main = table.Blind is { Boss: true, Colour: string hex }
                    ? new Color(hex).Lerp(Slate, 0.7f).Lightened(0.1f)
                    : new Color("50846E");
                accent = new Color(0.07f, 0.10f, 0.13f);
                track = BalatroAudio.Track.Shop;
                break;
        }
        BalatroAudio.Instance.SetTrack(track);
        if (kind == _mood) return;
        bool first = _mood.Length == 0;
        _mood = kind;
        _moodTween?.Kill();
        if (first)
        {
            // The room opens already in the right colours.
            _feltMaterial.SetShaderParameter("colour_1", main);
            _feltMaterial.SetShaderParameter("colour_2", accent);
        }
        _moodTween = _root.CreateTween().SetParallel();
        Color fromMain = (Color)_feltMaterial.GetShaderParameter("colour_1"), fromAccent = (Color)_feltMaterial.GetShaderParameter("colour_2");
        _moodTween.TweenMethod(Callable.From<Color>(c => _feltMaterial.SetShaderParameter("colour_1", c)), fromMain, main, 0.6 / Speed);
        _moodTween.TweenMethod(Callable.From<Color>(c => _feltMaterial.SetShaderParameter("colour_2", c)), fromAccent, accent, 0.6 / Speed);

        if (_ambience != null && GodotObject.IsInstanceValid(_ambience))
        {
            Control old = _ambience;
            old.CreateTween().TweenProperty(old, "modulate:a", 0f, 0.5 / Speed).Finished += old.QueueFree;
        }
        _ambience = null;
        Color[]? sparkle = kind switch
        {
            "tarot" => [Colors.White, new Color("8867A5").Lightened(0.4f), new Color("8867A5").Lightened(0.2f), new Color("EAC058").Lightened(0.2f)],
            "spectral" => [Colors.White, new Color("EAC058").Lightened(0.2f)],
            "planet" => [Colors.White, new Color("A7D6E0"), new Color("FDDCA0")],
            _ => null,
        };
        if (sparkle == null) return;
        _ambience = new Control { Size = StageSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        _stage.AddChild(_ambience);
        _stage.MoveChild(_ambience, 0);
        bool stars = kind == "planet";
        foreach (Color colour in sparkle)
        {
            var motes = new CpuParticles2D
            {
                Position = StageSize / 2,
                Amount = stars ? 26 : 18,
                Lifetime = stars ? 6 : kind == "spectral" ? 3 : 1.4,
                Preprocess = stars ? 6 : 2,
                Direction = Vector2.Up,
                Spread = 180,
                Gravity = Vector2.Zero,
                InitialVelocityMin = stars ? 2 : 10,
                InitialVelocityMax = stars ? 8 : kind == "spectral" ? 25 : 60,
                ScaleAmountMin = stars ? 3 : 4,
                ScaleAmountMax = stars ? 6 : 8,
                Color = colour,
                EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
                EmissionRectExtents = StageSize / 2,
            };
            var twinkle = new Gradient();
            twinkle.SetColor(0, new Color(1, 1, 1, 0));
            twinkle.AddPoint(0.3f, Colors.White);
            twinkle.SetColor(twinkle.GetPointCount() - 1, new Color(1, 1, 1, 0));
            motes.ColorRamp = twinkle;
            _ambience.AddChild(motes);
        }
        if (stars)
        {
            // Now and then a meteor streaks across.
            var streak = new Gradient();
            streak.SetColor(0, new Color(1, 1, 1, 0));
            streak.SetColor(1, Colors.White);
            var meteors = new CpuParticles2D
            {
                Position = new Vector2(StageSize.X / 2, 0),
                Amount = 2,
                Lifetime = 1.6,
                Direction = new Vector2(-1, 0.55f),
                Spread = 8,
                Gravity = Vector2.Zero,
                InitialVelocityMin = 900,
                InitialVelocityMax = 1300,
                Texture = new GradientTexture2D { Gradient = streak, Width = 4, Height = 46, FillFrom = new Vector2(0, 1), FillTo = Vector2.Zero },
                ParticleFlagAlignY = true,
                EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
                EmissionRectExtents = new Vector2(StageSize.X / 2, 10),
            };
            _ambience.AddChild(meteors);
        }
        _ambience.Modulate = new Color(1, 1, 1, 0);
        _ambience.CreateTween().TweenProperty(_ambience, "modulate:a", 1f, 0.6 / Speed);
    }

    // The tags waiting to be used, as Balatro lines them up: their art, their words on hover.
    private void ShowTags(TableSnapshot table)
    {
        if (table.OwnedTags.Count == 0) return;
        AddNode(TableStyle.Text("Tags", 18, new Color(StsColors.cream, 0.6f), new Rect2(36, 660, 64, 44), TableStyle.Regular, 4));
        for (int i = 0; i < Math.Min(table.OwnedTags.Count, 6); i++)
        {
            TagInfo tag = table.OwnedTags[i];
            var at = new Vector2(104 + i * 48, 660);
            var icon = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture = _art.Sprite(tag.Sprite),
                Position = at,
                Size = new Vector2(44, 44),
                PivotOffset = new Vector2(22, 22),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            icon.MouseEntered += () =>
            {
                TableFx.Juice(icon, 0.1f);
                TableFx.HoverSound();
                ShowTip(tag.Name, tag.Text, at, icon.Size);
            };
            icon.MouseExited += HideTip;
            AddNode(icon);
        }
    }

    // Balatro's money pop: the change, over the money, red when spent and gold when earned.
    private void MoneyPop(double change)
    {
        bool spent = change < 0;
        var pop = TableStyle.Panel(new Rect2(84, 238, 260, 76), TableStyle.Box(spent ? UseRed : TableStyle.Money, null, 14, 0, 8));
        pop.AddChild(TableStyle.Text((spent ? "-$" : "+$") + TableText.Number(Math.Abs(change)), 46, Colors.White, new Rect2(0, 0, 260, 76), TableStyle.Bold, 8));
        pop.Scale = new Vector2(0.4f, 0.4f);
        _stage.AddChild(pop);
        Tween t = pop.CreateTween();
        t.TweenProperty(pop, "scale", Vector2.One, 0.18 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        t.TweenInterval(0.7 / Speed);
        t.TweenProperty(pop, "modulate:a", 0f, 0.3 / Speed);
        t.TweenCallback(Callable.From(pop.QueueFree));
        TableFx.Juice(_money, 0.15f);
    }

    // The cards that left with the last move, seen off where they were: the one the move was
    // about goes Balatro's way (sold: a gold burn; used or redeemed: it rises, glows and burns
    // away; a pack: it bursts open); the rest (a rerolled shop, a closed pack's cards) burn away.
    private void SeeOff()
    {
        bool quiet = false;
        foreach ((int id, Placed was) in _before)
        {
            if (_shown.ContainsKey(id) || was.Area == "hand") continue;
            bool actedOn = _lastChoice?.CardId == id;
            // A card used from a pack on dealt cards has had its moment already (ShowPackEffect).
            if (actedOn && _effectShown) continue;
            var ghost = new CardView(was.Card, _art, _table?.Back, was.Size.X / CardView.FullSize.X);
            ghost.Root.MouseFilter = Control.MouseFilterEnum.Ignore;
            ghost.Root.Position = was.Centre - ghost.Root.Size / 2;
            ghost.Root.ZIndex = 20;
            _items.AddChild(ghost.Root);
            _nodes.Add(ghost.Root);
            if (!actedOn)
            {
                ghost.Dissolve(seconds: 0.4f, silent: quiet);
                quiet = true;
                continue;
            }
            switch (_lastChoice!.Move)
            {
                case ShopMove.Sell:
                    // Balatro: coin2, a juice, and a gold burn.
                    ghost.Dissolve(new Color("EAC058"), seconds: 0.6f);
                    break;
                case ShopMove.Buy when was.Area == "booster":
                {
                    // Opened: the pack goes to the middle of the table and bursts open.
                    Tween t = ghost.Root.CreateTween();
                    t.TweenProperty(ghost.Root, "position", new Vector2(1000, 505) - ghost.Root.Size / 2, 0.25 / Speed).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    t.Parallel().TweenProperty(ghost.Root, "scale", Vector2.One * 1.4f, 0.25 / Speed);
                    t.TweenCallback(Callable.From(() =>
                    {
                        TableFx.Sound("explosion_release1", 1f, 0.35f);
                        TableFx.Embers(_items, new Vector2(1000, 505), 26, StsColors.gold);
                        ghost.Dissolve(new Color("FFFFFF"), CardView.MaterializeColour(was.Card), seconds: 0.35f, silent: true);
                    }));
                    break;
                }
                default:
                {
                    // Used or redeemed: it rises to the middle, glows and swells, then burns away.
                    ghost.SetSelected(true);
                    Tween t = ghost.Root.CreateTween();
                    t.TweenProperty(ghost.Root, "position", new Vector2(CounterX, 330) - ghost.Root.Size / 2, 0.3 / Speed).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    t.Parallel().TweenProperty(ghost.Root, "scale", Vector2.One * 1.25f, 0.3 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                    t.TweenCallback(Callable.From(() => TableFx.Sound(was.Area == "voucher" ? "coin3" : "tarot1", 1f, 0.6f)));
                    t.TweenInterval(0.25 / Speed);
                    t.TweenCallback(Callable.From(() => ghost.Dissolve()));
                    break;
                }
            }
        }
    }

    private async Task ShowShop(TableSnapshot table, ShopSnapshot shop, bool dealIn, bool drop)
    {
        double money = table.Dollars - shop.BankruptAt;
        AddSign(drop);
        var counter = new Control { Size = StageSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddNode(counter);
        _into = counter;

        // The counter: Next Round and Reroll on its left, the cards in an inset beside them, the
        // voucher and the packs in their own insets below (Balatro's G.UIDEF.shop).
        AddNode(TableStyle.Panel(new Rect2(CounterX - 570, 452, 1140, 612), SlateBox(Slate, 18, 16)));
        AddNode(TableStyle.Panel(new Rect2(CounterX - 304, 470, 854, 320), SlateBox(SlateLight, 14, 0)));
        Button next = AddButton("Next\nRound", ShopRed, new Rect2(CounterX - 550, 470, 230, 150), true, () => Choose(new ShopChoiceMade(ShopMove.Leave)), size: 36);
        bool canReroll = shop.RerollCost <= 0 || shop.RerollCost <= money;
        Button reroll = AddButton("Reroll\n$" + TableText.Number(shop.RerollCost), RerollGreen, new Rect2(CounterX - 550, 634, 230, 150), canReroll,
            () => Choose(new ShopChoiceMade(ShopMove.Reroll)), size: 36);
        next.AddThemeConstantOverride("line_spacing", -6);
        reroll.AddThemeConstantOverride("line_spacing", -6);

        float spacing = Math.Min(270, 800f / Math.Max(shop.Cards.Count, 1));
        float mid = (shop.Cards.Count - 1) / 2f;
        for (int i = 0; i < shop.Cards.Count; i++)
        {
            TableCard card = shop.Cards[i];
            var center = new Vector2(CounterX + 123 + (i - mid) * spacing, SlotY);
            CardView view = AddCard(card, center, 1.0f, dealIn ? i * 0.08f : -1, "shop");
            AddPrice(card, center, view);
            bool affordable = card.Cost is null or <= 0 || card.Cost <= money;
            bool room = card.IsConsumable ? table.Consumables.Count < table.ConsumableSlots
                : !card.IsJoker || table.Jokers.Count < table.JokerSlots || card.Edition == "negative";
            float bottom = center.Y + view.Root.Size.Y / 2;
            // The buttons share the card's width of the counter, so neighbours never overlap.
            float room2 = Math.Min(220, spacing - 14);
            if (card.IsConsumable && card.Usable)
            {
                float half = (room2 - 4) / 2;
                AddButton(affordable ? "Buy" : "Need $" + TableText.Number(card.Cost ?? 0), BuyOrange, new Rect2(center.X - half - 2, bottom + 8, half, 46),
                    affordable && room, () => Choose(new ShopChoiceMade(ShopMove.Buy, card.Id)), size: half < 100 ? 20 : 24);
                AddButton("Use", BuyUseOrange, new Rect2(center.X + 2, bottom + 8, half, 46),
                    affordable, () => Choose(new ShopChoiceMade(ShopMove.BuyAndUse, card.Id)), size: 24);
            }
            else
            {
                string label = !affordable ? "Need $" + TableText.Number(card.Cost ?? 0) : !room ? "No room" : "Buy";
                float width = Math.Min(148, room2);
                AddButton(label, BuyOrange, new Rect2(center.X - width / 2, bottom + 8, width, 46), affordable && room,
                    () => Choose(new ShopChoiceMade(ShopMove.Buy, card.Id)), size: 24);
            }
        }
        if (shop.Cards.Count == 0) AddNode(TableStyle.Text("Sold out", 40, new Color(StsColors.cream, 0.6f), new Rect2(CounterX - 177, SlotY - 30, 600, 60)));

        // The ante's voucher, in a dark inset with Balatro's sideways label.
        AddNode(TableStyle.Panel(new Rect2(CounterX - 550, 806, 330, 240), SlateBox(SlateLight, 14, 0)));
        AddNode(TableStyle.Panel(new Rect2(CounterX - 538, 816, 306, 216), TableStyle.Box(Slate.Darkened(0.15f), null, 12)));
        var voucherLabel = TableStyle.Text($"ANTE {table.Ante} VOUCHER", 18, SlateLight.Lightened(0.15f), new Rect2(0, 0, 210, 26), TableStyle.Bold, 0);
        voucherLabel.Position = new Vector2(CounterX - 517, 924) - voucherLabel.Size / 2;
        voucherLabel.Rotation = -Mathf.Pi / 2;
        AddNode(voucherLabel);
        float vspacing = Math.Min(140, 240f / Math.Max(shop.Vouchers.Count, 1));
        float vmid = (shop.Vouchers.Count - 1) / 2f;
        for (int i = 0; i < shop.Vouchers.Count; i++)
        {
            TableCard voucher = shop.Vouchers[i];
            var center = new Vector2(CounterX - 368 + (i - vmid) * vspacing, LowerY);
            CardView view = AddCard(voucher, center, 0.76f, dealIn ? 0.25f : -1, "voucher");
            AddPrice(voucher, center, view);
            bool affordable = voucher.Cost is null or <= 0 || voucher.Cost <= money;
            AddButton(affordable ? "Redeem" : "Need $" + TableText.Number(voucher.Cost ?? 0), BalatroGreen,
                new Rect2(center.X - 70, center.Y + view.Root.Size.Y / 2 + 6, 140, 40), affordable, () => Choose(new ShopChoiceMade(ShopMove.Buy, voucher.Id)), size: 22);
        }

        // The booster packs.
        AddNode(TableStyle.Panel(new Rect2(CounterX - 204, 806, 754, 240), SlateBox(SlateLight, 14, 0)));
        float pspacing = Math.Min(220, 680f / Math.Max(shop.Boosters.Count, 1));
        float pmid = (shop.Boosters.Count - 1) / 2f;
        for (int i = 0; i < shop.Boosters.Count; i++)
        {
            TableCard booster = shop.Boosters[i];
            var center = new Vector2(CounterX + 173 + (i - pmid) * pspacing, LowerY);
            CardView view = AddCard(booster, center, 0.8f, dealIn ? 0.3f + i * 0.06f : -1, "booster");
            AddPrice(booster, center, view);
            bool affordable = booster.Cost is null or <= 0 || booster.Cost <= money;
            AddButton(affordable ? "Open" : "Need $" + TableText.Number(booster.Cost ?? 0), BalatroGreen,
                new Rect2(center.X - 70, center.Y + view.Root.Size.Y / 2 + 6, 140, 40), affordable, () => Choose(new ShopChoiceMade(ShopMove.Buy, booster.Id)), size: 22);
        }

        _into = null;
        AddButton("Run Info", RunInfoOrange, new Rect2(24, 152, 380, 62), true, () =>
        {
            if (JimboRun.Me() is { } me) TaskHelper.RunSafely(JimboDeckScreen.Toggle(me, Layer + 1));
        }, size: 28);
        if (drop)
        {
            // Balatro's shop rises into place as it opens.
            counter.Position = new Vector2(0, 640);
            counter.CreateTween().TweenProperty(counter, "position", Vector2.Zero, 0.5 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
        if (shop.NextBlind?.Tag is TagInfo tag) AddSkip(shop.NextBlind, tag);
        else if (shop.NextBlind is { Boss: true } boss && (boss.Raisable || boss.Raised)) AddRaise(boss);

        if (dealIn)
        {
            for (int i = 0; i < shop.Cards.Count; i++)
            {
                TableFx.Sound("card1", 0.9f + 0.1f * i, 0.6f);
                await TableFx.Wait(0.08f);
            }
            await TableFx.Wait(0.25f);
        }
    }

    // Balatro's shop sign: its lit-up marquee (the game's own animation) over "Improve your run!",
    // on a slate board. It drops in when the shop opens, as Balatro's does.
    private void AddSign(bool drop)
    {
        var board = TableStyle.Panel(new Rect2(CounterX - 190, 216, 380, 222), SlateBox(SignRed, 16, 14));
        board.AddChild(TableStyle.Panel(new Rect2(10, 10, 360, 196), TableStyle.Box(Slate.Darkened(0.25f), null, 12)));
        AtlasTexture? strip = _art.Strip("ShopSignAnimation.png", 113, 57);
        if (strip != null)
        {
            float frameWidth = strip.Region.Size.X;
            var sign = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture = strip,
                Position = new Vector2(20, 14),
                Size = new Vector2(340, 171),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            board.AddChild(sign);
            // The marquee's lights chase round, four frames to the loop.
            Tween lights = sign.CreateTween().SetLoops();
            lights.TweenMethod(Callable.From<float>(f => strip.Region = strip.Region with { Position = new Vector2((int)f % 4 * frameWidth, 0) }), 0f, 4f, 0.9);
        }
        else
        {
            board.AddChild(TableStyle.Text("SHOP", 64, ShopRed, new Rect2(0, 30, 380, 120), TableStyle.Title, 12));
        }
        Label motto = TableStyle.Text("Improve your run!", 26, StsColors.gold.Lightened(0.3f), new Rect2(0, 180, 380, 34), TableStyle.Bold, 6);
        board.AddChild(motto);
        Tween bob = motto.CreateTween().SetLoops();
        bob.TweenProperty(motto, "rotation", 0.03f, 1.1).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        bob.TweenProperty(motto, "rotation", -0.03f, 1.1).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        AddNode(board);
        if (!drop) return;
        Vector2 home = board.Position;
        board.Position = home - new Vector2(0, 420);
        board.CreateTween().TweenProperty(board, "position", home, 0.55 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        TableFx.Sound("whoosh1", 0.9f, 0.3f);
    }

    private async Task ShowPack(TableSnapshot table, PackSnapshot pack, bool dealIn)
    {
        _title.Text = PackTitle(pack.Kind);
        _title.LabelSettings.FontColor = pack.Kind switch
        {
            "tarot" => new Color("A782D1"),
            "planet" => new Color("13AFCE"),
            "spectral" => TableStyle.Chips,
            "buffoon" => ShopRed,
            _ => StsColors.gold,
        };
        // The pack's name and how many to take, on a slate board (Balatro's pack header).
        AddNode(TableStyle.Panel(new Rect2(680, 240, 640, 136), SlateBox(Slate, 16, 12)));
        AddNode(TableStyle.Text($"Choose {pack.Choices}", 26, StsColors.cream, new Rect2(700, 326, 600, 34), TableStyle.Regular, 6));

        float spacing = Math.Min(250, 1300f / Math.Max(pack.Cards.Count, 1));
        float mid = (pack.Cards.Count - 1) / 2f;
        bool needsHand = table.Hand.Count > 0;
        for (int i = 0; i < pack.Cards.Count; i++)
        {
            TableCard card = pack.Cards[i];
            var center = new Vector2(1000 + (i - mid) * spacing, PackY);
            CardView view = AddCard(card, center, 1.0f, dealIn ? i * 0.07f : -1, "pack");
            _packViews[card.Id] = view;
            bool room = !card.IsJoker || table.Jokers.Count < table.JokerSlots || card.Edition == "negative";
            bool usable = !card.IsConsumable || card.Usable;
            string label = card.IsConsumable ? "Use" : "Take";
            // A consumable's button lights up once it works: some need cards selected in hand first.
            Button pick = AddButton(!room ? "No room" : label, card.IsConsumable ? UseRed : BalatroGreen, new Rect2(center.X - 75, center.Y + view.Root.Size.Y / 2 + 12, 150, 52),
                room && usable, () => Choose(new ShopChoiceMade(ShopMove.Pick, card.Id, SelectedHand())));
            if (card.IsConsumable) _packButtons[card.Id] = pick;
        }

        if (needsHand)
        {
            // Tarots and spectrals work on cards in hand: Balatro dealt some to pick from.
            float hspacing = Math.Min(120, 960f / Math.Max(table.Hand.Count, 1));
            float hmid = (table.Hand.Count - 1) / 2f;
            for (int i = 0; i < table.Hand.Count; i++)
            {
                TableCard card = table.Hand[i];
                CardView view = AddCard(card, new Vector2(1000 + (i - hmid) * hspacing, HandY), 0.72f, dealIn ? 0.3f + i * 0.04f : -1, "hand");
                _handDrag.Attach(view);
                view.Float();
                _handCards.Add(view);
            }
            // Into the fan, each in turn.
            LayoutPackHand();
            for (int i = 0; i < _handCards.Count; i++)
            {
                CardView view = _handCards[i];
                if (dealIn) TableFx.Move(view, view.Home, view.HomeRotation, 0.4f, 0.3f + i * 0.04f, Tween.TransitionType.Back);
                else
                {
                    view.Root.Position = view.Home - view.Root.Size / 2;
                    view.Root.Rotation = view.HomeRotation;
                }
            }
            SetStatus("Select cards for the tarot, then use it", new Color(StsColors.cream, 0.7f));
        }
        else
        {
            SetStatus("", StsColors.cream);
        }
        AddButton("Skip", new Color(0.32f, 0.36f, 0.42f), new Rect2(1560, 860, 330, 96), true, () => Choose(new ShopChoiceMade(ShopMove.SkipPack)), size: 34);
        if (dealIn)
        {
            TableFx.Sound("cardFan2", 1f, 0.7f);
            await TableFx.Wait(0.4f);
        }
    }

    // Balatro's blind select comes right after the shop: a Small or Big Blind can be skipped for
    // its tag, with no fight and no cash-out (the next shop still opens).
    private void AddSkip(NextBlind next, TagInfo tag)
    {
        var icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _art.Sprite(tag.Sprite),
            Position = new Vector2(40, 716), Size = new Vector2(66, 66),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        icon.MouseEntered += () =>
        {
            TableFx.Juice(icon, 0.1f);
            TableFx.HoverSound();
            ShowTip(tag.Name, tag.Text, new Vector2(40, 716), new Vector2(66, 66));
        };
        icon.MouseExited += HideTip;
        AddNode(icon);
        AddButton($"Skip {next.Name}", new Color(0.32f, 0.36f, 0.42f), new Rect2(116, 718, 270, 62), true,
            () => Choose(new ShopChoiceMade(ShopMove.SkipBlind)), size: 22);
        AddNode(TableStyle.Text("for " + tag.Name, 18, new Color(StsColors.cream, 0.7f), new Rect2(116, 782, 270, 26), TableStyle.Regular, 4));
    }

    // The Wager, in the skip's place: the Boss Blind on deck can be raised (its chips x1.5) for a
    // Rare Tag if it's beaten. Small and Big Blinds can be skipped; Boss Blinds can be raised.
    private const double RaiseFactor = 1.5;

    private void AddRaise(NextBlind boss)
    {
        var icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _art.Sprite(boss.Sprite),
            Position = new Vector2(40, 716), Size = new Vector2(66, 66),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        double chips = boss.Chips ?? 0;
        string tip = boss.Raised
            ? $"Raised to [red]{TableText.Number(chips)}[/red] chips. Beat it for a [gold]Rare Tag[/gold]."
            : $"[red]{TableText.Number(chips)}[/red] chips, or [red]{TableText.Number(chips * RaiseFactor)}[/red] raised. Beat it raised for a [gold]Rare Tag[/gold].";
        icon.MouseEntered += () =>
        {
            TableFx.Juice(icon, 0.1f);
            TableFx.HoverSound();
            ShowTip(boss.Name, tip, new Vector2(40, 716), new Vector2(66, 66));
        };
        icon.MouseExited += HideTip;
        AddNode(icon);
        if (boss.Raised)
        {
            AddNode(TableStyle.Text("Raised!", 30, ShopRed, new Rect2(116, 718, 270, 62), TableStyle.Title, 8));
            AddNode(TableStyle.Text(TableText.Number(chips) + " chips", 18, new Color(StsColors.cream, 0.7f), new Rect2(116, 782, 270, 26), TableStyle.Regular, 4));
            return;
        }
        AddButton($"Raise: x{RaiseFactor:0.#}", ShopRed, new Rect2(116, 718, 270, 62), true,
            () => Choose(new ShopChoiceMade(ShopMove.RaiseBlind)), size: 24);
        AddNode(TableStyle.Text("for a Rare Tag", 18, new Color(StsColors.cream, 0.7f), new Rect2(116, 782, 270, 26), TableStyle.Regular, 4));
    }

    private static string PackTitle(string? kind) => kind switch
    {
        "tarot" => "ARCANA PACK",
        "planet" => "CELESTIAL PACK",
        "spectral" => "SPECTRAL PACK",
        "standard" => "STANDARD PACK",
        "buffoon" => "BUFFOON PACK",
        _ => "PACK",
    };

    private void ToggleHandCard(CardView view)
    {
        if (_choice == null) return;
        bool select = !view.Selected;
        if (select && _handCards.Count(c => c.Selected) >= 5)
        {
            TableFx.Sound("cancel", 1f, 0.5f);
            return;
        }
        view.SetSelected(select);
        LayoutPackHand();
        TableFx.Move(view, view.Home, view.HomeRotation, 0.15f);
        TableFx.Sound(select ? "cardSlide1" : "cardSlide2", 1f, select ? 1f : 0.3f);
        TaskHelper.RunSafely(RefreshPackUsable());
    }

    // The pack's hand along the bottom, fanned on an arc as Balatro deals it (align_cards for a
    // pack); selected cards stand up.
    private void LayoutPackHand()
    {
        int n = _handCards.Count;
        float spacing = Math.Min(120, 960f / Math.Max(n, 1));
        float mid = (n - 1) / 2f;
        for (int i = 0; i < n; i++)
        {
            CardView view = _handCards[i];
            float k = i + 1;
            float arc = MathF.Pow(1.3f * (-n / 2f + k - 0.5f) / Math.Max(n, 1), 2) * 50;
            view.Home = new Vector2(1000 + (i - mid) * spacing, HandY + arc - (view.Selected ? 36 : 0));
            view.HomeRotation = 0.4f * (-n / 2f - 0.5f + k) / Math.Max(n, 1) * (view.Selected ? 0.5f : 1f);
        }
    }

    // Dragged cards move in Balatro's hand too.
    private static async Task ReorderHand(int[] ids)
    {
        try
        {
            await JimboRun.Session().ReorderHand(ids);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: reordering the pack's hand failed: {e.Message}");
        }
    }

    // Asks Balatro which of the pack's cards work on the selected hand cards.
    private async Task RefreshPackUsable()
    {
        if (_table?.Pack == null || JimboRun.Me() is not { } player) return;
        try
        {
            HighlightResult result = await JimboRun.Session().JimboHighlight(JimboRun.LinkFor(player), SelectedHand());
            foreach ((int id, Button button) in _packButtons)
            {
                if (GodotObject.IsInstanceValid(button)) button.Disabled = !result.PackUsable.Contains(id);
            }
            // The consumable slots work on the pack's hand too.
            _usableNow = result.Usable.ToHashSet();
            if (_ownedUse is (int ownedId, Button ownedButton) && GodotObject.IsInstanceValid(ownedButton)) ownedButton.Disabled = !_usableNow.Contains(ownedId);
            SetStatus(result.PackUsable.Count > 0 ? "" : "Select cards for the tarot, then use it", new Color(StsColors.cream, 0.7f));
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: pack highlight failed: {e.Message}");
        }
    }

    private readonly Dictionary<int, Button> _packButtons = new();
    private readonly Dictionary<int, CardView> _packViews = new();

    /// <summary>
    /// A tarot or spectral was just used from the open pack: it rises and fades, and the dealt
    /// cards show what it did (changed ones flip over, destroyed ones burn, new ones slide in)
    /// before the shop moves on.
    /// </summary>
    public async Task ShowPackEffect(PackEffect effect)
    {
        if (_removed) return;
        HideTip();
        _effectShown = true;
        // The card used: one of the pack's, or one from the consumable slots.
        CardView? used = effect.Card is TableCard c && (_packViews.TryGetValue(c.Id, out CardView? v) || _shown.TryGetValue(c.Id, out var s) && (v = s.View) != null)
            && GodotObject.IsInstanceValid(v.Root) ? v : null;
        if (effect.Card?.Name is string name) SetStatus(name, StsColors.gold);
        if (used != null)
        {
            // The card glows and swells where it is; the rest of the pack steps back.
            used.Root.MoveToFront();
            used.SetSelected(true);
            TableFx.Sound("tarot1", 1f, 0.8f);
            foreach (CardView other in _packViews.Values.Where(view => view != used && GodotObject.IsInstanceValid(view.Root)))
            {
                TableFx.Fade(other.Root, 0.35f, 0.25f);
            }
            Tween swell = used.Root.CreateTween();
            swell.TweenProperty(used.Root, "scale", Vector2.One * 1.15f, 0.3 / Speed).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            await TableFx.Finished(swell);
            TableFx.Juice(used.Root, 0.2f);
        }
        await TableFx.Wait(0.25f);

        var after = effect.After.ToDictionary(card => card.Id);
        var before = _handCards.Select(view => view.Id).ToHashSet();
        int changed = 0;
        foreach (CardView view in _handCards.ToList())
        {
            if (!GodotObject.IsInstanceValid(view.Root)) continue;
            if (!after.TryGetValue(view.Id, out TableCard? now))
            {
                // Destroyed (The Hanged Man, Immolate...): it burns away.
                TableFx.Sound("crumple1", 1f, 0.5f);
                Tween burn = view.Root.CreateTween().SetParallel();
                burn.TweenProperty(view.Root, "modulate", new Color(1f, 0.4f, 0.2f, 0f), 0.4 / Speed);
                burn.TweenProperty(view.Root, "scale", Vector2.One * 1.15f, 0.4 / Speed);
                continue;
            }
            if (now == view.Card) continue;
            // Changed (an enhancement, a seal, a suit, a rank): it flips over where it is.
            CardView fresh = AddCard(now, view.Center, 0.72f, -1, "hand");
            fresh.Root.Position = view.Root.Position;
            view.Root.QueueFree();
            _handCards[_handCards.IndexOf(view)] = fresh;
            TableFx.Juice(fresh.Root, 0.3f);
            TableFx.Sound("tarot2", 0.9f + 0.08f * changed++, 0.6f);
            await TableFx.Wait(0.12f);
        }
        // New cards (Familiar, Grim, Incantation, Cryptid make some): they join the row, and the
        // row closes up around the gaps and spreads to fit.
        List<TableCard> made = effect.After.Where(card => !before.Contains(card.Id)).ToList();
        List<CardView> row = _handCards.Where(view => after.ContainsKey(view.Id) && GodotObject.IsInstanceValid(view.Root)).ToList();
        int count = row.Count + made.Count;
        float spacing = Math.Min(120, 960f / Math.Max(count, 1));
        Vector2 Slot(int i) => new(1000 + (i - (count - 1) / 2f) * spacing, HandY);
        for (int i = 0; i < row.Count; i++) TableFx.Move(row[i], Slot(i), 0, 0.3f);
        for (int i = 0; i < made.Count; i++)
        {
            _handCards.Add(AddCard(made[i], Slot(row.Count + i), 0.72f, i * 0.08f, "hand"));
            TableFx.Sound("card1", 0.9f + 0.05f * i, 0.6f);
        }
        if (used != null) used.Root.CreateTween().TweenProperty(used.Root, "modulate:a", 0f, 0.4 / Speed);
        await TableFx.Wait(changed + made.Count > 0 ? 1.0f : 0.6f);
        SetStatus("", StsColors.cream);
    }

    private int[] SelectedHand() => _handCards.Where(c => c.Selected).Select(c => c.Id).ToArray();

    // Owned jokers (top middle) and consumables (top right): click to sell or use.
    private void ShowOwned(TableSnapshot table)
    {
        _ownedViews.Clear();
        AddNode(TableStyle.Panel(new Rect2(470, 16, 1000, 186), TableStyle.Box(new Color(Slate, 0.72f), new Color(SlateLight, 0.8f), 16, 2)));
        AddNode(TableStyle.Text($"Jokers  {table.Jokers.Count}/{table.JokerSlots}", 18, new Color(StsColors.cream, 0.7f), new Rect2(470, 172, 1000, 26), TableStyle.Regular, 4));
        foreach (TableCard joker in table.Jokers)
        {
            CardView view = AddCard(joker, JokerHome(_jokerRow.Count, table.Jokers.Count), 0.68f, -1, "joker");
            _ownedViews[joker.Id] = view;
            _jokerRow.Add(view);
            _jokerDrag.Attach(view);
        }
        LayoutJokers();
        AddNode(TableStyle.Panel(new Rect2(1490, 16, 406, 186), TableStyle.Box(new Color(Slate, 0.72f), new Color(SlateLight, 0.8f), 16, 2)));
        AddNode(TableStyle.Text($"Consumables  {table.Consumables.Count}/{table.ConsumableSlots}", 18, new Color(StsColors.cream, 0.7f), new Rect2(1490, 172, 406, 26), TableStyle.Regular, 4));
        float cspacing = Math.Min(112, 340f / Math.Max(table.Consumables.Count, 1));
        float cmid = (table.Consumables.Count - 1) / 2f;
        for (int i = 0; i < table.Consumables.Count; i++)
        {
            TableCard card = table.Consumables[i];
            CardView view = AddCard(card, new Vector2(1693 + (i - cmid) * cspacing, TopRowY - 8), 0.68f, -1, "consumable");
            view.Root.GuiInput += input =>
            {
                if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
                SelectOwned(card, view);
                view.Root.AcceptEvent();
            };
        }
    }

    private readonly List<Control> _ownedButtons = new();
    // The selected consumable's Use button, and what Balatro says works on the cards selected now.
    private (int Id, Button Button)? _ownedUse;
    private HashSet<int> _usableNow = new();
    private readonly Dictionary<int, CardView> _ownedViews = new();

    private void LayoutJokers()
    {
        for (int i = 0; i < _jokerRow.Count; i++)
        {
            _jokerRow[i].Home = JokerHome(i, _jokerRow.Count);
            _jokerRow[i].HomeRotation = 0;
        }
    }

    // The middle of the i-th of n jokers in the tray.
    private static Vector2 JokerHome(int i, int n) => new(970 + (i - (n - 1) / 2f) * Math.Min(118, 880f / Math.Max(n, 1)), TopRowY - 8);

    // A joker dragged to a new place: Balatro moves it there (jokers score left to right).
    private void MoveJokerTo(CardView view, int[] order)
    {
        int to = Array.IndexOf(order, view.Id) + 1;
        if (to >= 1) Choose(new ShopChoiceMade(ShopMove.MoveJoker, view.Id, To: to));
    }

    private void ClearOwnedButtons()
    {
        // The layout drops them too (it frees what it holds).
        foreach (Control b in _ownedButtons)
        {
            _nodes.Remove(b);
            if (GodotObject.IsInstanceValid(b)) b.QueueFree();
        }
        _ownedButtons.Clear();
    }

    // A selected joker or consumable shows Sell (and Use) under it, in Balatro's colours. Jokers
    // are moved by dragging them.
    private void SelectOwned(TableCard card, CardView view)
    {
        if (_choice == null) return;
        // The last selection's buttons go.
        ClearOwnedButtons();
        if (_selectedOwned == card.Id)
        {
            _selectedOwned = 0;
            TableFx.Sound("cardSlide2", 1f, 0.3f);
            return;
        }
        _selectedOwned = card.Id;
        TableFx.Sound("cardSlide1");
        float x = view.Center.X, y = view.Root.Position.Y + view.Root.Size.Y + 8;
        Button sell = AddButton(card.Eternal ? "Eternal" : $"Sell ${TableText.Number(card.Sell ?? 0)}", BalatroGreen,
            new Rect2(x - 62, y, 124, 44), !card.Eternal, () => Choose(new ShopChoiceMade(ShopMove.Sell, card.Id)), size: 20);
        _ownedButtons.Add(sell);
        if (card.IsConsumable)
        {
            // Tarots that change playing cards need some selected in hand: in a fight, or on the
            // hand an Arcana or Spectral pack deals (Balatro lets the slots be used on it too).
            bool packHand = _handCards.Count > 0;
            bool usable = packHand ? _usableNow.Contains(card.Id) || card.Usable : card.Usable;
            Button use = AddButton(usable || packHand ? "Use" : "Use in fight", UseRed, new Rect2(x - 62, y + 50, 124, 44), usable,
                () => Choose(new ShopChoiceMade(ShopMove.Use, card.Id, SelectedHand())), size: 20);
            _ownedButtons.Add(use);
            _ownedUse = (card.Id, use);
        }
    }

    private CardView AddCard(TableCard card, Vector2 center, float scale, float dealDelay, string area)
    {
        var view = new CardView(card, _art, _table?.Back, scale);
        view.Root.MouseFilter = Control.MouseFilterEnum.Stop;
        view.Home = center;
        view.EnableTilt();
        // Balatro's info box: beside the wares on the counter and in packs, under the trays' cards,
        // over the pack's hand.
        CardTip.Place place = area switch { "joker" or "consumable" => CardTip.Place.Below, "hand" => CardTip.Place.Above, _ => CardTip.Place.Beside };
        view.Root.MouseEntered += () =>
        {
            if (_handDrag.Dragging != null || _jokerDrag.Dragging != null) return;
            TableFx.Juice(view.Root, 0.05f);
            TableFx.HoverSound();
            _tip.Show(view, place);
        };
        view.Root.MouseExited += HideTip;
        AddNode(view.Root);
        _shown[card.Id] = (view, area);
        if (_before.ContainsKey(card.Id)) Arrive(view, center, area);
        else if (dealDelay >= 0 && area == "hand")
        {
            // The pack's hand is dealt from the deck, below.
            view.Root.Position = center - view.Root.Size / 2 + new Vector2(0, 360);
            view.Root.Modulate = new Color(1, 1, 1, 0);
            TableFx.Move(view, center, 0, 0.4f, dealDelay, Tween.TransitionType.Back);
            view.Root.CreateTween().TweenProperty(view.Root, "modulate:a", 1f, 0.2 / Speed).SetDelay(dealDelay / Speed);
        }
        else
        {
            view.Root.Position = center - view.Root.Size / 2;
            // New wares burn in where they stand, in their rarity's or set's colour (Balatro's materialize).
            if (dealDelay >= 0) view.Materialize(CardView.MaterializeColour(card), dealDelay);
        }
        return view;
    }

    // A card that was on show before comes from where it was: across the table if it changed
    // place (bought, taken from a pack, moved along the tray), else it simply stays put.
    private void Arrive(CardView view, Vector2 center, string area)
    {
        view.Root.Position = center - view.Root.Size / 2;
        if (!_before.TryGetValue(view.Id, out Placed? was) || was.Centre.DistanceTo(center) < 2) return;
        bool moved = was.Area != area;
        view.Root.Position = was.Centre - view.Root.Size / 2;
        view.Root.Scale = was.Size / view.Root.Size;
        view.Root.ZIndex = moved ? 10 : 0;
        Tween t = view.Root.CreateTween().SetParallel();
        double seconds = (moved ? 0.45 : 0.22) / Speed;
        t.TweenProperty(view.Root, "position", center - view.Root.Size / 2, seconds).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(view.Root, "scale", Vector2.One, seconds).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.Chain().TweenCallback(Callable.From(() =>
        {
            view.Root.ZIndex = 0;
            if (moved) TableFx.Juice(view.Root, 0.12f);
        }));
    }

    private void AddPrice(TableCard card, Vector2 center, CardView view)
    {
        float top = center.Y - view.Root.Size.Y / 2;
        var price = TableStyle.Panel(new Rect2(center.X - 46, top - 40, 92, 44), SlateBox(Slate.Darkened(0.2f), 10, 6));
        price.AddChild(TableStyle.Text("$" + TableText.Number(card.Cost ?? 0), 28, TableStyle.Money, new Rect2(0, 0, 92, 38), TableStyle.Bold, 6));
        AddNode(price);
    }

    private Button AddButton(string text, Color colour, Rect2 rect, bool enabled, Action pressed, bool dark = false, int size = 26)
    {
        Button button = TableStyle.Button(text, colour, rect, size);
        if (dark)
        {
            button.AddThemeColorOverride("font_color", TableStyle.Ink);
            button.AddThemeColorOverride("font_hover_color", TableStyle.Ink);
            button.AddThemeConstantOverride("outline_size", 0);
        }
        button.Disabled = !enabled;
        button.Pressed += pressed;
        AddNode(button);
        return button;
    }

    private void AddNode(Control node)
    {
        (_into ?? _items).AddChild(node);
        _nodes.Add(node);
    }

    // A card's name and Balatro's text for it, beside the card.
    private void ShowTip(CardView view) => _tip.Show(view);

    private void ShowTip(string name, string? text, Vector2 at, Vector2 size) => _tip.Show(name, text, at, size);

    private void HideTip() => _tip.Hide();

    private void OnRootInput(InputEvent input)
    {
        // Keys are swallowed so StS2's own hotkeys can't fire underneath the table.
        if (input is InputEventKey key)
        {
            if (key.Pressed && !key.Echo && key.Keycode is Key.Escape && _table?.Pack == null) Choose(new ShopChoiceMade(ShopMove.Leave));
            _root.AcceptEvent();
        }
    }

    // Closing ----------------------------------------------------------------------------------

    public async Task Close()
    {
        if (_removed) return;
        _choice?.TrySetCanceled();
        HideTip();
        TableFx.Sound("whoosh2", 1f, 0.45f);
        Tween t = _root.CreateTween().SetParallel();
        t.TweenProperty(_stage, "modulate:a", 0f, 0.2 / Speed);
        t.TweenProperty(_shade, "color:a", 0f, 0.45 / Speed);
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("reveal", v)), 1f, 0f, 0.45 / Speed)
            .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.In);
        t.TweenMethod(Callable.From<float>(v => _feltMaterial.SetShaderParameter("spin", v)), 0f, -4f, 0.45 / Speed)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        await TableFx.Finished(t);
        Remove();
    }

    public void Remove()
    {
        if (_removed) return;
        _removed = true;
        _root.ReleaseFocus();
        _layer.QueueFree();
    }
}
