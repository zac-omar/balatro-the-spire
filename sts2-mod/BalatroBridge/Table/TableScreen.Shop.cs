using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Content;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Table;

internal enum ShopChoice
{
    Buy,
    Reroll,
    Leave,
}

/// <summary>What the player did in the shop; <see cref="CardId"/> is the card bought.</summary>
internal sealed record ShopAction(ShopChoice Choice, int CardId = 0);

// The shop half of the table: after a beaten blind, Balatro's shop opens on the same overlay.
// Its jokers are joker relics; buying one obtains the relic in StS2.
internal sealed partial class TableScreen
{
    private const float ShopCardY = 540;
    private const float ShopCardScale = 1.15f;
    private const float ShopSpacing = 330;
    private const string ShopHint = "1-3 buy  ·  R reroll  ·  Enter next round";
    private static readonly Color ShopRed = new("FE5F55");
    private static readonly Color RerollGreen = new("4BC292");
    private static readonly Vector2 ShopStatusPosition = new(560, 958);

    private readonly List<Control> _shopNodes = new();
    private readonly List<CardView> _forSale = new();
    private readonly List<Button> _buyButtons = new();
    private TaskCompletionSource<ShopAction>? _shopAction;
    private Button? _reroll;
    private Button? _leave;
    private bool _canBuy;

    private bool InShop => _shopNodes.Count > 0;

    /// <summary>
    /// Turns the table into the shop: the scored hand and its numbers clear away and the jokers
    /// for sale are dealt in. Without <paramref name="canBuy"/> the shop is shown but nothing sells.
    /// </summary>
    public async Task OpenShop(TableSnapshot table, bool canBuy)
    {
        _canBuy = canBuy;
        _acceptingInput = false;
        foreach (CardView view in _played.Concat(_hand))
        {
            Tween t = view.Root.CreateTween().SetParallel();
            t.TweenProperty(view.Root, "position:y", view.Root.Position.Y + 500, 0.3 / Speed).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            t.TweenProperty(view.Root, "modulate:a", 0f, 0.3 / Speed);
            t.Chain().TweenCallback(Callable.From(view.Root.QueueFree));
        }
        foreach (Control revealed in _revealed) Fade(revealed, 0f, 0.25f);
        BalatroSound("cardSlide2", 1f, 0.5f);
        await Wait(0.32f);
        _played.Clear();
        _hand.Clear();
        foreach (Control revealed in _revealed) revealed.QueueFree();
        _revealed.Clear();

        SetHandUiVisible(false);
        SetStatus("", StsColors.cream);
        BalatroSound("cardFan2", 1f, 0.7f);
        await ShowShop(table, dealIn: true);
    }

    /// <summary>Waits for the player to buy, reroll or move on.</summary>
    public Task<ShopAction> NextShopAction()
    {
        _shopAction = new TaskCompletionSource<ShopAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        RefreshShopButtons();
        return _shopAction.Task;
    }

    /// <summary>Lays out the shop from a snapshot (after opening, a reroll or a purchase).</summary>
    public async Task ShowShop(TableSnapshot table, bool dealIn)
    {
        if (table.Shop is not ShopSnapshot shop) return;
        _art.Learn(table);
        ApplyInfo(table);
        ClearShop();

        _blindName.Text = "Shop";
        _blindName.LabelSettings.FontColor = ShopRed;
        _blindGoal.Text = shop.NextBlind is NextBlind next
            ? $"Next: {next.Name} ({TableText.Number(next.Chips ?? 0)})"
            : "";
        _blindChip.Texture = null;
        _roundCaption.Text = "Cashed out";
        _roundScore.Text = "+$" + TableText.Number(shop.CashOut);
        _status.Position = ShopStatusPosition;

        AddShop(TableStyle.Text("SHOP", 52, ShopRed, new Rect2(660, 286, 600, 60), TableStyle.Title, 12));

        float mid = (shop.Cards.Count - 1) / 2f;
        for (int i = 0; i < shop.Cards.Count; i++)
        {
            TableCard card = shop.Cards[i];
            float x = 960 + (i - mid) * ShopSpacing;
            AddForSale(card, i, new Vector2(x, ShopCardY), dealIn ? i * 0.1f : 0f);
        }
        if (shop.Cards.Count == 0)
        {
            string empty = shop.Unseen == 0 ? "Every joker relic is yours!" : "Sold out";
            AddShop(TableStyle.Text(empty, 40, StsColors.cream, new Rect2(560, ShopCardY - 30, 800, 60)));
        }

        _leave = TableStyle.Button("Next Round", ShopRed, new Rect2(112, 852, 250, 92), 32);
        _leave.Pressed += () => Choose(new ShopAction(ShopChoice.Leave));
        _reroll = TableStyle.Button("Reroll $" + TableText.Number(shop.RerollCost), RerollGreen, new Rect2(1558, 852, 250, 92), 32);
        _reroll.Pressed += () => Choose(new ShopAction(ShopChoice.Reroll));
        AddShop(_leave);
        AddShop(_reroll);
        _hint.Text = ShopHint;
        RefreshShopButtons();

        if (dealIn)
        {
            for (int i = 0; i < shop.Cards.Count; i++)
            {
                BalatroSound("card1", 0.9f + 0.1f * i, 0.6f);
                await Wait(0.1f);
            }
            await Wait(0.3f);
        }
    }

    /// <summary>The bought joker flies up into the joker row, then the shop is laid out again.</summary>
    public async Task Bought(TableSnapshot table, int cardId)
    {
        CardView? view = _forSale.FirstOrDefault(c => c.Id == cardId);
        if (view != null)
        {
            view.MoveTween?.Kill();
            Tween t = view.Root.CreateTween().SetParallel();
            t.TweenProperty(view.Root, "position", new Vector2(960, 205) - view.Root.Size / 2, 0.35 / Speed)
                .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
            t.TweenProperty(view.Root, "scale", new Vector2(0.72f, 0.72f) / ShopCardScale, 0.35 / Speed);
            await ToSignal(t);
        }
        BalatroSound("coin1", 1f, 0.8f);
        BalatroSound("card1", 1f, 0.6f);
        string name = BalatroCatalog.Name(table.BoughtKey) ?? "Joker";
        await ShowShop(table, dealIn: false);
        Embers(new Vector2(960, 205), 30, TableStyle.Money);
        SetStatus($"{name} joins your relics!", TableStyle.Money);
    }

    /// <summary>Takes the shop down and brings the hand controls back.</summary>
    public async Task CloseShop()
    {
        _shopAction?.TrySetCanceled();
        _shopAction = null;
        foreach (Control node in _shopNodes) Fade(node, 0f, 0.2f);
        await Wait(0.22f);
        ClearShop();
        _status.Position = StatusPosition;
        SetStatus("", StsColors.cream);
        _hint.Text = HandHint;
        SetHandUiVisible(true);
    }

    private void AddForSale(TableCard card, int index, Vector2 center, float delay)
    {
        var view = new CardView(card, _art, null, ShopCardScale);
        view.Root.MouseFilter = Control.MouseFilterEnum.Pass;
        view.Root.MouseEntered += () =>
        {
            Juice(view.Root, 0.05f);
            HoverSound();
        };
        view.Home = center;
        view.Root.Position = center - view.Root.Size / 2 + new Vector2(0, -420);
        view.Root.Modulate = new Color(1, 1, 1, 0);
        _stage.AddChild(view.Root);
        _forSale.Add(view);
        _shopNodes.Add(view.Root);
        Move(view, center, 0, 0.4f, delay, Tween.TransitionType.Back);
        view.Root.CreateTween().TweenProperty(view.Root, "modulate:a", 1f, 0.2 / Speed).SetDelay(delay / Speed);

        float top = center.Y - view.Root.Size.Y / 2, bottom = center.Y + view.Root.Size.Y / 2;
        var price = TableStyle.Panel(new Rect2(center.X - 55, top - 52, 110, 46), TableStyle.Box(TableStyle.Money, new Color(1, 1, 1, 0.4f), 10, 2, 6));
        price.AddChild(TableStyle.Text("$" + TableText.Number(card.Cost ?? 0), 30, TableStyle.Ink, new Rect2(0, 0, 110, 46), TableStyle.Bold, 0));
        AddShop(price);

        AddShop(TableStyle.Text(card.Name ?? "Joker", 30, StsColors.cream, new Rect2(center.X - 150, bottom + 8, 300, 40)));
        if ((card.Text ?? BalatroCatalog.Text(card.Key)) is string text)
        {
            RichTextLabel description = TableStyle.RichText(text, 20);
            description.Position = new Vector2(center.X - 150, bottom + 50);
            description.Size = new Vector2(300, 96);
            AddShop(description);
        }

        var buy = TableStyle.Button("Buy", TableStyle.Money, new Rect2(center.X - 80, bottom + 150, 160, 56), 28);
        buy.AddThemeColorOverride("font_color", TableStyle.Ink);
        buy.AddThemeColorOverride("font_hover_color", TableStyle.Ink);
        buy.AddThemeConstantOverride("outline_size", 0);
        buy.SetMeta("card", card.Id);
        buy.Pressed += () => Choose(new ShopAction(ShopChoice.Buy, card.Id));
        _buyButtons.Add(buy);
        AddShop(buy);
    }

    private void AddShop(Control node)
    {
        _stage.AddChild(node);
        _shopNodes.Add(node);
    }

    private void ClearShop()
    {
        foreach (Control node in _shopNodes) node.QueueFree();
        _shopNodes.Clear();
        _forSale.Clear();
        _buyButtons.Clear();
        _reroll = _leave = null;
    }

    private void RefreshShopButtons()
    {
        if (_table?.Shop is not ShopSnapshot shop) return;
        bool waiting = _shopAction != null;
        double money = _table.Dollars - shop.BankruptAt;
        foreach (Button buy in _buyButtons)
        {
            int id = buy.GetMeta("card").AsInt32();
            TableCard? card = shop.Cards.FirstOrDefault(c => c.Id == id);
            double cost = card?.Cost ?? 0;
            int slots = shop.JokerSlots + (card?.Edition == "negative" ? 1 : 0);
            bool room = _table.Jokers.Count < slots;
            bool affordable = cost <= 0 || cost <= money;
            buy.Text = !_canBuy ? "—" : !room ? "No room" : affordable ? "Buy" : "Need $" + TableText.Number(cost);
            buy.Disabled = !waiting || !_canBuy || !room || !affordable;
        }
        if (_reroll != null)
        {
            bool anyLeft = shop.Unseen is not 0;
            _reroll.Disabled = !waiting || !_canBuy || !anyLeft || (shop.RerollCost > 0 && shop.RerollCost > money);
        }
        if (_leave != null) _leave.Disabled = !waiting;
    }

    private void Choose(ShopAction action)
    {
        if (_shopAction == null) return;
        if (action.Choice == ShopChoice.Buy && _buyButtons.FirstOrDefault(b => b.GetMeta("card").AsInt32() == action.CardId) is { Disabled: true }) return;
        if (action.Choice == ShopChoice.Reroll && _reroll is { Disabled: true }) return;
        TaskCompletionSource<ShopAction> pending = _shopAction;
        _shopAction = null;
        RefreshShopButtons();
        BalatroSound("button");
        if (action.Choice == ShopChoice.Reroll)
        {
            BalatroSound("coin2");
            BalatroSound("other1");
        }
        pending.TrySetResult(action);
    }

    /// <summary>Shop hotkeys. Returns false when the key isn't the shop's (so the hand's keys apply).</summary>
    private bool OnShopKey(Key key)
    {
        if (!InShop) return false;
        if (key >= Key.Key1 && key <= Key.Key9)
        {
            int index = (int)(key - Key.Key1);
            if (index < _forSale.Count) Choose(new ShopAction(ShopChoice.Buy, _forSale[index].Id));
        }
        else if (key == Key.R) Choose(new ShopAction(ShopChoice.Reroll));
        else if (key is Key.Enter or Key.KpEnter or Key.Space) Choose(new ShopAction(ShopChoice.Leave));
        return true;
    }

    private void SetHandUiVisible(bool visible)
    {
        foreach (Control c in new Control[] { _handName, _handLevel, _times, _play, _discard, _sortRank, _sortSuit })
        {
            c.Visible = visible;
        }
        _chipsBox.Visible = _multBox.Visible = visible;
        if (visible)
        {
            // RevealTotal slid the chips and mult boxes together; put them back.
            _chipsBox.Position = new Vector2(700, 392);
            _multBox.Position = new Vector2(1000, 392);
            _times.Modulate = Colors.White;
        }
    }
}
