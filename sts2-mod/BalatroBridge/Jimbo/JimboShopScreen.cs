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
    Refresh, // shows the shop as Balatro has it now (after something outside the shop changed it)
}

/// <summary>What the player did in Jimbo's shop: <see cref="CardId"/> is the card it's about, and
/// <see cref="HandCards"/> the cards selected in hand (a tarot from a pack works on them).</summary>
internal sealed record ShopChoiceMade(ShopMove Move, int CardId = 0, int[]? HandCards = null);

/// <summary>
/// Balatro's shop on the card table, for Jimbo: the card slots (jokers, tarots, planets...), the
/// ante's voucher and two booster packs, Reroll and Next Round. Owned jokers and consumables sit
/// along the top: click one to sell it (or use a consumable). Opening a pack turns the table into
/// the pack: pick its cards (tarots and spectrals work on the hand dealt below), or skip.
/// Balatro decides everything; this class shows a snapshot and reports what the player chose.
/// </summary>
internal sealed class JimboShopScreen
{
    private static readonly Vector2 StageSize = new(1920, 1080);
    private const int Layer = 60;
    private const float TopRowY = 182;
    private const float SlotY = 505;
    private const float LowerY = 822;
    private const float HandY = 930;
    private static readonly Color ShopRed = new("FE5F55");
    private static readonly Color RerollGreen = new("4BC292");
    private static readonly Color UseGreen = new("35BD86");

    private readonly CanvasLayer _layer;
    private readonly Control _root;
    private readonly ShaderMaterial _feltMaterial;
    private readonly ColorRect _shade;
    private readonly Control _stage;
    private readonly Control _items;
    private readonly BalatroArt _art = BalatroArt.Shared;
    private readonly Label _title;
    private readonly Label _status;
    private readonly Label _money;
    private readonly Label _ante;
    private readonly Label _turns;
    private readonly Label _deck;
    private readonly Label _nextName;
    private readonly Label _nextGoal;
    private readonly RichTextLabel _nextText;
    private readonly List<Control> _nodes = new();
    private readonly List<CardView> _handCards = new();
    private TaskCompletionSource<ShopChoiceMade>? _choice;
    private TableSnapshot? _table;
    private readonly CardTip _tip;
    private int _selectedOwned;
    private bool _removed;

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

        // Left: the run around the shop, and what comes next.
        var info = TableStyle.Panel(new Rect2(24, 230, 380, 470), TableStyle.Box(TableStyle.PanelFill, TableStyle.PanelEdge, 14, 3, 12));
        _stage.AddChild(info);
        _money = TableStyle.Text("$0", 54, TableStyle.Money, new Rect2(20, 14, 340, 64));
        _ante = TableStyle.Text("", 24, StsColors.cream, new Rect2(20, 80, 340, 32), TableStyle.Regular, 5);
        _turns = TableStyle.Text("", 20, new Color(StsColors.cream, 0.8f), new Rect2(20, 114, 340, 30), TableStyle.Regular, 5);
        _deck = TableStyle.Text("", 20, new Color(StsColors.cream, 0.8f), new Rect2(20, 144, 340, 30), TableStyle.Regular, 5);
        info.AddChild(_money);
        info.AddChild(_ante);
        info.AddChild(_turns);
        info.AddChild(_deck);
        info.AddChild(TableStyle.Text("Up next", 20, new Color(StsColors.cream, 0.55f), new Rect2(20, 196, 340, 28), TableStyle.Regular, 4));
        _nextName = TableStyle.Text("", 30, StsColors.gold, new Rect2(20, 224, 340, 40));
        _nextGoal = TableStyle.Text("", 20, StsColors.cream, new Rect2(20, 264, 340, 30), TableStyle.Regular, 5);
        _nextText = TableStyle.RichText("", 19);
        _nextText.Position = new Vector2(20, 300);
        _nextText.Size = new Vector2(340, 150);
        info.AddChild(_nextName);
        info.AddChild(_nextGoal);
        info.AddChild(_nextText);

        _title = TableStyle.Text("SHOP", 60, ShopRed, new Rect2(700, 254, 600, 70), TableStyle.Title, 12);
        _stage.AddChild(_title);
        _status = TableStyle.Text("", 26, StsColors.cream, new Rect2(460, 1010, 1000, 40));
        _stage.AddChild(_status);
        _items = new Control { Size = StageSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        _stage.AddChild(_items);

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
        _status.Text = text;
        _status.LabelSettings.FontColor = colour;
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
        foreach (Control node in _nodes)
        {
            if (GodotObject.IsInstanceValid(node)) node.QueueFree();
        }
        _nodes.Clear();
        _ownedButtons.Clear();
        _packButtons.Clear();
        _packViews.Clear();
        _handCards.Clear();
        HideTip();
        _selectedOwned = 0;

        BalatroAudio.Instance.SetTrack(BalatroAudio.Track.Shop);
        _money.Text = "$" + TableText.Number(table.Dollars);
        _ante.Text = $"Ante {table.Ante}/8  ·  Round {table.Round}";
        _turns.Text = $"Hand size {table.HandSize}";
        _deck.Text = $"Deck {table.DeckTotal} cards";
        NextBlind? next = table.Shop?.NextBlind ?? table.NextBlind;
        _nextName.Text = next?.Name ?? "";
        _nextName.LabelSettings.FontColor = next?.Boss == true && next.Colour is string c ? new Color(c).Lightened(0.3f) : StsColors.gold;
        _nextGoal.Text = next?.Chips is double chips ? $"{TableText.Number(chips)} HP  ·  ${TableText.Number(next.Dollars ?? 0)}" : "";
        _nextText.Text = TableStyle.Markup(next?.Text ?? "");

        ShowOwned(table);
        if (table.Pack is PackSnapshot pack) await ShowPack(table, pack, dealIn);
        else if (table.Shop is ShopSnapshot shop) await ShowShop(table, shop, dealIn);
    }

    private async Task ShowShop(TableSnapshot table, ShopSnapshot shop, bool dealIn)
    {
        _title.Text = "SHOP";
        _title.LabelSettings.FontColor = ShopRed;
        double money = table.Dollars - shop.BankruptAt;

        float spacing = 290;
        float mid = (shop.Cards.Count - 1) / 2f;
        for (int i = 0; i < shop.Cards.Count; i++)
        {
            TableCard card = shop.Cards[i];
            var center = new Vector2(1000 + (i - mid) * spacing, SlotY);
            CardView view = AddCard(card, center, 1.05f, dealIn ? i * 0.08f : -1);
            AddPrice(card, center, view);
            bool affordable = card.Cost is null or <= 0 || card.Cost <= money;
            bool room = card.IsConsumable ? table.Consumables.Count < table.ConsumableSlots
                : !card.IsJoker || table.Jokers.Count < table.JokerSlots || card.Edition == "negative";
            float bottom = center.Y + view.Root.Size.Y / 2;
            if (card.IsConsumable && card.Usable)
            {
                AddButton(affordable ? "Buy" : "Need $" + TableText.Number(card.Cost ?? 0), TableStyle.Money, new Rect2(center.X - 124, bottom + 14, 118, 54),
                    affordable && room, () => Choose(new ShopChoiceMade(ShopMove.Buy, card.Id)), dark: true);
                AddButton("Use", UseGreen, new Rect2(center.X + 6, bottom + 14, 118, 54),
                    affordable, () => Choose(new ShopChoiceMade(ShopMove.BuyAndUse, card.Id)));
            }
            else
            {
                string label = !affordable ? "Need $" + TableText.Number(card.Cost ?? 0) : !room ? "No room" : "Buy";
                AddButton(label, TableStyle.Money, new Rect2(center.X - 80, bottom + 14, 160, 54), affordable && room,
                    () => Choose(new ShopChoiceMade(ShopMove.Buy, card.Id)), dark: true);
            }
        }
        if (shop.Cards.Count == 0) AddNode(TableStyle.Text("Sold out", 40, StsColors.cream, new Rect2(700, SlotY - 30, 600, 60)));

        // Bottom row: the voucher on the left, the packs on the right.
        float x = 640;
        foreach (TableCard voucher in shop.Vouchers)
        {
            var center = new Vector2(x, LowerY);
            CardView view = AddCard(voucher, center, 0.82f, dealIn ? 0.25f : -1);
            AddPrice(voucher, center, view);
            bool affordable = voucher.Cost is null or <= 0 || voucher.Cost <= money;
            AddButton(affordable ? "Redeem" : "Need $" + TableText.Number(voucher.Cost ?? 0), UseGreen,
                new Rect2(center.X - 80, center.Y + view.Root.Size.Y / 2 + 8, 160, 50), affordable, () => Choose(new ShopChoiceMade(ShopMove.Buy, voucher.Id)));
            x += 220;
        }
        float px = 1080;
        foreach (TableCard booster in shop.Boosters)
        {
            var center = new Vector2(px, LowerY);
            CardView view = AddCard(booster, center, 0.82f, dealIn ? 0.3f : -1);
            AddPrice(booster, center, view);
            bool affordable = booster.Cost is null or <= 0 || booster.Cost <= money;
            AddButton(affordable ? "Open" : "Need $" + TableText.Number(booster.Cost ?? 0), new Color("3F8DD8"),
                new Rect2(center.X - 80, center.Y + view.Root.Size.Y / 2 + 8, 160, 50), affordable, () => Choose(new ShopChoiceMade(ShopMove.Buy, booster.Id)));
            px += 210;
        }

        AddButton("Next Round", ShopRed, new Rect2(36, 860, 330, 96), true, () => Choose(new ShopChoiceMade(ShopMove.Leave)), size: 36);
        if (shop.NextBlind?.Tag is TagInfo tag) AddSkip(shop.NextBlind, tag);
        bool canReroll = shop.RerollCost <= 0 || shop.RerollCost <= money;
        AddButton("Reroll $" + TableText.Number(shop.RerollCost), RerollGreen, new Rect2(1560, 860, 330, 96), canReroll,
            () => Choose(new ShopChoiceMade(ShopMove.Reroll)), size: 34);

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
        AddNode(TableStyle.Text($"Choose {pack.Choices}", 26, StsColors.cream, new Rect2(700, 318, 600, 34), TableStyle.Regular, 6));

        float spacing = Math.Min(250, 1300f / Math.Max(pack.Cards.Count, 1));
        float mid = (pack.Cards.Count - 1) / 2f;
        bool needsHand = table.Hand.Count > 0;
        for (int i = 0; i < pack.Cards.Count; i++)
        {
            TableCard card = pack.Cards[i];
            var center = new Vector2(1000 + (i - mid) * spacing, SlotY);
            CardView view = AddCard(card, center, 1.0f, dealIn ? i * 0.07f : -1);
            _packViews[card.Id] = view;
            bool room = !card.IsJoker || table.Jokers.Count < table.JokerSlots || card.Edition == "negative";
            bool usable = !card.IsConsumable || card.Usable;
            string label = card.IsConsumable ? "Use" : "Take";
            // A consumable's button lights up once it works: some need cards selected in hand first.
            Button pick = AddButton(!room ? "No room" : label, UseGreen, new Rect2(center.X - 75, center.Y + view.Root.Size.Y / 2 + 12, 150, 52),
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
                CardView view = AddCard(card, new Vector2(1000 + (i - hmid) * hspacing, HandY), 0.72f, dealIn ? 0.3f + i * 0.04f : -1);
                view.Root.GuiInput += input =>
                {
                    if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
                    ToggleHandCard(view);
                    view.Root.AcceptEvent();
                };
                _handCards.Add(view);
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
        TableFx.Move(view, view.Home + new Vector2(0, select ? -36 : 0), 0, 0.15f);
        TableFx.Sound(select ? "cardSlide1" : "cardSlide2", 1f, select ? 1f : 0.3f);
        TaskHelper.RunSafely(RefreshPackUsable());
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
        CardView? used = effect.Card is TableCard c && _packViews.TryGetValue(c.Id, out CardView? v) && GodotObject.IsInstanceValid(v.Root) ? v : null;
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
            CardView fresh = AddCard(now, view.Center, 0.72f, -1);
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
            _handCards.Add(AddCard(made[i], Slot(row.Count + i), 0.72f, i * 0.08f));
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
        AddNode(TableStyle.Text($"Jokers {table.Jokers.Count}/{table.JokerSlots}", 20, new Color(StsColors.cream, 0.6f), new Rect2(520, 40, 900, 26), TableStyle.Regular, 4));
        float spacing = Math.Min(118, 880f / Math.Max(table.Jokers.Count, 1));
        float mid = (table.Jokers.Count - 1) / 2f;
        for (int i = 0; i < table.Jokers.Count; i++)
        {
            TableCard joker = table.Jokers[i];
            CardView view = AddCard(joker, new Vector2(970 + (i - mid) * spacing, TopRowY - 30), 0.68f, -1);
            view.Root.GuiInput += input =>
            {
                if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
                SelectOwned(joker, view);
                view.Root.AcceptEvent();
            };
        }
        AddNode(TableStyle.Text($"Consumables {table.Consumables.Count}/{table.ConsumableSlots}", 20, new Color(StsColors.cream, 0.6f), new Rect2(1460, 40, 430, 26), TableStyle.Regular, 4));
        float cmid = (table.Consumables.Count - 1) / 2f;
        for (int i = 0; i < table.Consumables.Count; i++)
        {
            TableCard card = table.Consumables[i];
            CardView view = AddCard(card, new Vector2(1675 + (i - cmid) * 112, TopRowY - 30), 0.68f, -1);
            view.Root.GuiInput += input =>
            {
                if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
                SelectOwned(card, view);
                view.Root.AcceptEvent();
            };
        }
    }

    private readonly List<Control> _ownedButtons = new();

    // A selected joker or consumable shows Sell (and Use) under it.
    private void SelectOwned(TableCard card, CardView view)
    {
        if (_choice == null) return;
        // The last selection's buttons go, from the layout too (it frees what it holds).
        foreach (Control b in _ownedButtons)
        {
            _nodes.Remove(b);
            if (GodotObject.IsInstanceValid(b)) b.QueueFree();
        }
        _ownedButtons.Clear();
        if (_selectedOwned == card.Id)
        {
            _selectedOwned = 0;
            TableFx.Sound("cardSlide2", 1f, 0.3f);
            return;
        }
        _selectedOwned = card.Id;
        TableFx.Sound("cardSlide1");
        float x = view.Center.X, y = view.Root.Position.Y + view.Root.Size.Y + 8;
        Button sell = AddButton(card.Eternal ? "Eternal" : $"Sell ${TableText.Number(card.Sell ?? 0)}", TableStyle.Money,
            new Rect2(x - 62, y, 124, 44), !card.Eternal, () => Choose(new ShopChoiceMade(ShopMove.Sell, card.Id)), dark: true, size: 20);
        _ownedButtons.Add(sell);
        if (card.IsConsumable)
        {
            // Tarots that change playing cards need some selected in hand: they're for fights and packs.
            Button use = AddButton(card.Usable ? "Use" : "Use in fight", UseGreen, new Rect2(x - 62, y + 50, 124, 44), card.Usable,
                () => Choose(new ShopChoiceMade(ShopMove.Use, card.Id)), size: 20);
            _ownedButtons.Add(use);
        }
    }

    private CardView AddCard(TableCard card, Vector2 center, float scale, float dealDelay)
    {
        var view = new CardView(card, _art, _table?.Back, scale);
        view.Root.MouseFilter = Control.MouseFilterEnum.Stop;
        view.Home = center;
        view.Root.MouseEntered += () =>
        {
            TableFx.Juice(view.Root, 0.05f);
            TableFx.HoverSound();
            ShowTip(view);
        };
        view.Root.MouseExited += HideTip;
        if (dealDelay >= 0)
        {
            view.Root.Position = center - view.Root.Size / 2 + new Vector2(0, -360);
            view.Root.Modulate = new Color(1, 1, 1, 0);
            TableFx.Move(view, center, 0, 0.4f, dealDelay, Tween.TransitionType.Back);
            view.Root.CreateTween().TweenProperty(view.Root, "modulate:a", 1f, 0.2 / Speed).SetDelay(dealDelay / Speed);
        }
        else
        {
            view.Root.Position = center - view.Root.Size / 2;
        }
        _items.AddChild(view.Root);
        _nodes.Add(view.Root);
        return view;
    }

    private void AddPrice(TableCard card, Vector2 center, CardView view)
    {
        float top = center.Y - view.Root.Size.Y / 2;
        var price = TableStyle.Panel(new Rect2(center.X - 52, top - 48, 104, 42), TableStyle.Box(TableStyle.Money, new Color(1, 1, 1, 0.4f), 10, 2, 6));
        price.AddChild(TableStyle.Text("$" + TableText.Number(card.Cost ?? 0), 28, TableStyle.Ink, new Rect2(0, 0, 104, 42), TableStyle.Bold, 0));
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
        _items.AddChild(node);
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
