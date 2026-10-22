using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace BalatroBridge.Core;

// Typed views of the dealer's replies (balatro-mod/bridge/dealer.lua and jimbo.lua,
// protocol/PROTOCOL.md).

/// <summary>A sprite cell in one of Balatro's texture atlases.</summary>
public sealed record SpriteRef(string Atlas, int X, int Y)
{
    public static SpriteRef? FromJson(JsonNode? node) =>
        node is JsonObject o && BalatroState.Str(o["atlas"]) is string atlas
            ? new SpriteRef(atlas, BalatroState.Int(o["x"]) ?? 0, BalatroState.Int(o["y"]) ?? 0)
            : null;
}

/// <summary>An atlas image (path relative to the Balatro folder) and its cell size at 1x scale.</summary>
public sealed record AtlasInfo(string Path, int CellWidth, int CellHeight);

public sealed record TableCard(
    int Id,
    string? Name,
    string? Rank,
    string? Suit,
    double? Chips,
    string? Enhancement,
    string? Edition,
    string? Seal,
    bool Debuffed,
    bool FaceDown,
    bool Stone,
    SpriteRef? Center,
    SpriteRef? Front,
    SpriteRef? SealSprite,
    SpriteRef? Soul,
    string? Key,
    double? Cost)
{
    /// <summary>Balatro's own text for the card (current values), in StS2 markup.</summary>
    public string? Text { get; init; }

    /// <summary>For a joker whose number changes: that number now, as its text writes it ("+7", "X2.2").</summary>
    public string? Live { get; init; }

    /// <summary>Balatro's set: "Joker", "Tarot", "Planet", "Spectral", "Voucher", "Booster", "Default", "Enhanced".</summary>
    public string? Set { get; init; }

    public double? Sell { get; init; }

    /// <summary>For consumables: whether Balatro would let it be used right now.</summary>
    public bool Usable { get; init; }

    public bool Eternal { get; init; }

    /// <summary>Rounds a perishable joker has left; null if it isn't perishable.</summary>
    public int? Perishable { get; init; }

    public bool Rental { get; init; }

    /// <summary>In a deck listing: "deck", "hand", "discard" or "play".</summary>
    public string? Pile { get; init; }

    /// <summary>
    /// A playing card's suits as Balatro counts them ("Hearts,Diamonds"): Wild Cards are all four,
    /// Stone Cards none. Kept as text so cards compare by value (an unchanged card isn't re-dealt).
    /// </summary>
    public string Suits { get; init; } = "";

    public bool CountsAs(string suit) => Suits.Split(',').Contains(suit);

    /// <summary>For a status card the enemy put in Jimbo's deck (a dud): which one, as StS2 knows it.</summary>
    public int? Dud { get; init; }

    public bool IsJoker => Set == "Joker";

    public bool IsConsumable => Set is "Tarot" or "Planet" or "Spectral";

    public bool IsPlayingCard => Set is "Default" or "Enhanced" || (Set == null && Rank != null);

    public static TableCard FromJson(JsonObject c) => new(
        BalatroState.Int(c["id"]) ?? 0,
        BalatroState.Str(c["name"]),
        BalatroState.Str(c["rank"]),
        BalatroState.Str(c["suit"]),
        BalatroState.Num(c["chips"]),
        BalatroState.Str(c["enhancement"]),
        BalatroState.Str(c["edition"]),
        BalatroState.Str(c["seal"]),
        BalatroState.Bool(c["debuff"]),
        BalatroState.Str(c["facing"]) == "back",
        BalatroState.Bool(c["stone"]),
        SpriteRef.FromJson(c["center"]),
        SpriteRef.FromJson(c["front"]),
        SpriteRef.FromJson(c["seal_sprite"]),
        SpriteRef.FromJson(c["soul"]),
        BalatroState.Str(c["key"]),
        BalatroState.Num(c["cost"]))
    {
        Text = BalatroState.Str(c["text"]),
        Live = BalatroState.Str(c["live"]),
        Set = BalatroState.Str(c["set"]),
        Sell = BalatroState.Num(c["sell"]),
        Usable = BalatroState.Bool(c["usable"]),
        Eternal = BalatroState.Bool(c["eternal"]),
        Perishable = BalatroState.Int(c["perishable"]),
        Rental = BalatroState.Bool(c["rental"]),
        Pile = BalatroState.Str(c["pile"]),
        Dud = BalatroState.Int(c["dud"]),
        Suits = c["suits"] is JsonArray suits ? string.Join(",", suits.Select(BalatroState.Str).OfType<string>()) : "",
    };

    // Balatro encodes an empty list as {} since Lua can't tell the two apart.
    internal static IReadOnlyList<TableCard> List(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>().Select(FromJson).ToList() : [];
}

/// <summary>A blind: its name, chip target and look. Boss blinds have an effect <see cref="Text"/>.</summary>
public sealed record TableBlind(string Name, double? Chips, bool Boss, string? Colour, SpriteRef? Sprite)
{
    public string? Key { get; init; }

    public string? Text { get; init; }

    public double? Dollars { get; init; }

    public bool Disabled { get; init; }

    public static TableBlind? FromJson(JsonNode? node) => node is JsonObject b
        ? new TableBlind(BalatroState.Str(b["name"]) ?? "Blind", BalatroState.Num(b["chips"]), BalatroState.Bool(b["boss"]),
            BalatroState.Str(b["colour"]), SpriteRef.FromJson(b["sprite"]))
        {
            Key = BalatroState.Str(b["key"]),
            Text = BalatroState.Str(b["text"]),
            Dollars = BalatroState.Num(b["dollars"]),
            Disabled = BalatroState.Bool(b["disabled"]),
        }
        : null;
}

/// <summary>A Balatro tag: what skipping a Small or Big Blind gives.</summary>
public sealed record TagInfo(string Key, string Name, string? Text, SpriteRef? Sprite)
{
    public static TagInfo? FromJson(JsonNode? node) => node is JsonObject t && BalatroState.Str(t["key"]) is string key
        ? new TagInfo(key, BalatroState.Str(t["name"]) ?? key, BalatroState.Str(t["text"]), SpriteRef.FromJson(t["sprite"]))
        : null;
}

/// <summary>The blind that comes next: <see cref="Kind"/> is "Small", "Big" or "Boss".</summary>
public sealed record NextBlind(string Name, double? Chips, bool Boss)
{
    /// <summary>The tag for skipping it (Small and Big Blinds only).</summary>
    public TagInfo? Tag { get; init; }

    public string? Key { get; init; }

    public string? Kind { get; init; }

    public string? Text { get; init; }

    public string? Colour { get; init; }

    public SpriteRef? Sprite { get; init; }

    public double? Dollars { get; init; }

    /// <summary>The Wager: this Boss Blind can be raised from the shop.</summary>
    public bool Raisable { get; init; }

    /// <summary>The Wager: this Boss Blind was raised (its chips already show it).</summary>
    public bool Raised { get; init; }

    public static NextBlind? FromJson(JsonNode? node) => node is JsonObject b
        ? new NextBlind(BalatroState.Str(b["name"]) ?? "Blind", BalatroState.Num(b["chips"]), BalatroState.Bool(b["boss"]))
        {
            Key = BalatroState.Str(b["key"]),
            Kind = BalatroState.Str(b["kind"]),
            Text = BalatroState.Str(b["text"]),
            Colour = BalatroState.Str(b["colour"]),
            Sprite = SpriteRef.FromJson(b["sprite"]),
            Dollars = BalatroState.Num(b["dollars"]),
            Tag = TagInfo.FromJson(b["tag"]),
            Raisable = BalatroState.Bool(b["raisable"]),
            Raised = BalatroState.Bool(b["raised"]),
        }
        : null;
}

/// <summary>
/// The road ahead, for Jimbo's map: the current ante's three blinds (its Boss Blind already
/// chosen) and each ante's base chips (Small Blind; Big is x1.5, Boss x2 or more).
/// </summary>
public sealed record RoadInfo(int Ante, NextBlind? Small, NextBlind? Big, NextBlind? Boss, IReadOnlyList<double> Antes)
{
    public static RoadInfo? FromJson(JsonNode? node) => node is JsonObject r
        ? new RoadInfo(BalatroState.Int(r["ante"]) ?? 1, NextBlind.FromJson(r["small"]), NextBlind.FromJson(r["big"]), NextBlind.FromJson(r["boss"]),
            r["antes"] is JsonArray antes ? antes.Select(BalatroState.Num).Select(a => a ?? 0).ToList() : [])
        : null;
}

/// <summary>
/// A tarot or spectral just used from a pack on the cards it dealt: the card, and the hand before
/// and after (taken just before the pack closed), so the effect can be shown.
/// </summary>
public sealed record PackEffect(TableCard? Card, IReadOnlyList<TableCard> Before, IReadOnlyList<TableCard> After)
{
    public static PackEffect? FromJson(JsonNode? node) => node is JsonObject e
        ? new PackEffect(e["card"] is JsonObject c ? TableCard.FromJson(c) : null, TableCard.List(e["before"]), TableCard.List(e["after"]))
        : null;
}

/// <summary>An open booster pack: <see cref="Kind"/> is "tarot", "planet", "spectral", "standard" or "buffoon".</summary>
public sealed record PackSnapshot(string? Kind, int Choices, IReadOnlyList<TableCard> Cards)
{
    public static PackSnapshot? FromJson(JsonNode? node) => node is JsonObject p
        ? new PackSnapshot(BalatroState.Str(p["kind"]), BalatroState.Int(p["choices"]) ?? 1, TableCard.List(p["cards"]))
        : null;
}

/// <summary>Hands and discards each StS2 turn gets in a Jimbo fight.</summary>
public sealed record PerTurn(int Hands, int Discards, int Turns)
{
    public static PerTurn? FromJson(JsonNode? node) => node is JsonObject t
        ? new PerTurn(BalatroState.Int(t["hands"]) ?? 1, BalatroState.Int(t["discards"]) ?? 0, BalatroState.Int(t["turns"]) ?? 4)
        : null;
}

/// <summary>
/// Balatro's shop after a beaten blind. For Deal In only its joker slots are offered and, given a
/// relic pool, they only hold joker relics the player doesn't own yet. <see cref="Unseen"/> counts
/// the pool's jokers that are neither owned nor on sale (null without a pool). Jimbo's shop is
/// Balatro's whole shop: the card slots, the voucher and the booster packs.
/// </summary>
public sealed record ShopSnapshot(
    IReadOnlyList<TableCard> Cards,
    IReadOnlyList<TableCard> Vouchers,
    IReadOnlyList<TableCard> Boosters,
    double RerollCost,
    int? Unseen,
    double CashOut,
    double BankruptAt,
    int JokerSlots,
    NextBlind? NextBlind)
{
    public static ShopSnapshot? FromJson(JsonNode? node) => node is JsonObject s
        ? new ShopSnapshot(
            TableCard.List(s["cards"]),
            TableCard.List(s["vouchers"]),
            TableCard.List(s["boosters"]),
            BalatroState.Num(s["reroll_cost"]) ?? 0,
            BalatroState.Int(s["unseen"]),
            BalatroState.Num(s["cash_out"]) ?? 0,
            BalatroState.Num(s["bankrupt_at"]) ?? 0,
            BalatroState.Int(s["joker_slots"]) ?? 5,
            NextBlind.FromJson(s["next_blind"]))
        : null;
}

/// <summary>
/// The dealt hand and everything around it, as StS2 draws it. While Balatro is in the shop,
/// <see cref="Shop"/> is set and the hand is empty. <see cref="BoughtKey"/> is the joker a
/// table_shop_buy just bought.
/// </summary>
public sealed record TableSnapshot(
    IReadOnlyList<TableCard> Hand,
    IReadOnlyList<TableCard> Jokers,
    int HandsLeft,
    int DiscardsLeft,
    int SelectLimit,
    double RoundChips,
    TableBlind? Blind,
    double Dollars,
    int Ante,
    int Round,
    int DeckLeft,
    int DeckTotal,
    SpriteRef? Back,
    int TextureScale,
    IReadOnlyDictionary<string, AtlasInfo> Atlases,
    ShopSnapshot? Shop,
    string? BoughtKey)
{
    // Jimbo's runs report more about the run (bridge/jimbo.lua); Deal In leaves these empty.

    /// <summary>"hand", "shop", "blind_select", "round_eval", "pack", "game_over" or "other".</summary>
    public string? State { get; init; }

    public IReadOnlyList<TableCard> Consumables { get; init; } = [];

    public int ConsumableSlots { get; init; } = 2;

    public int JokerSlots { get; init; } = 5;

    public int HandSize { get; init; } = 8;

    public IReadOnlyList<string> Vouchers { get; init; } = [];

    public string? DeckKey { get; init; }

    public int Stake { get; init; } = 1;

    public PerTurn? PerTurn { get; init; }

    public bool InFight { get; init; }

    /// <summary>Cards in the discard pile.</summary>
    public int DiscardLeft { get; init; }

    public PackSnapshot? Pack { get; init; }

    public NextBlind? NextBlind { get; init; }

    /// <summary>Jimbo's road ahead (see <see cref="RoadInfo"/>).</summary>
    public RoadInfo? Road { get; init; }

    /// <summary>After a pick from a pack: what the card did to the dealt hand.</summary>
    public PackEffect? PackEffect { get; init; }

    public bool WonRun { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>After a discard: the chips on the cards thrown away (Jimbo's block).</summary>
    public double DiscardedChips { get; init; }

    /// <summary>Every card on the table (hand, jokers, consumables, shop, pack).</summary>
    public IEnumerable<TableCard> AllCards => Hand.Concat(Jokers).Concat(Consumables)
        .Concat(Shop?.Cards ?? []).Concat(Shop?.Vouchers ?? []).Concat(Shop?.Boosters ?? []).Concat(Pack?.Cards ?? []);

    public static TableSnapshot FromJson(JsonObject t)
    {
        var atlases = new Dictionary<string, AtlasInfo>();
        if (t["atlases"] is JsonObject a)
        {
            foreach ((string name, JsonNode? info) in a)
            {
                if (info is JsonObject i && BalatroState.Str(i["path"]) is string path)
                {
                    atlases[name] = new AtlasInfo(path, BalatroState.Int(i["px"]) ?? 71, BalatroState.Int(i["py"]) ?? 95);
                }
            }
        }
        return new TableSnapshot(
            TableCard.List(t["hand"]),
            TableCard.List(t["jokers"]),
            BalatroState.Int(t["hands_left"]) ?? 0,
            BalatroState.Int(t["discards_left"]) ?? 0,
            BalatroState.Int(t["select_limit"]) ?? 5,
            BalatroState.Num(t["round_chips"]) ?? 0,
            TableBlind.FromJson(t["blind"]),
            BalatroState.Num(t["dollars"]) ?? 0,
            BalatroState.Int(t["ante"]) ?? 1,
            BalatroState.Int(t["round"]) ?? 1,
            BalatroState.Int(t["deck_left"]) ?? 0,
            BalatroState.Int(t["deck_total"]) ?? 0,
            SpriteRef.FromJson(t["back"]),
            BalatroState.Int(t["texture_scale"]) ?? 2,
            atlases,
            ShopSnapshot.FromJson(t["shop"]),
            t["bought"] is JsonObject bought ? BalatroState.Str(bought["key"]) : null)
        {
            State = BalatroState.Str(t["state"]),
            Consumables = TableCard.List(t["consumables"]),
            ConsumableSlots = BalatroState.Int(t["consumable_slots"]) ?? 2,
            JokerSlots = BalatroState.Int(t["joker_slots"]) ?? 5,
            HandSize = BalatroState.Int(t["hand_size"]) ?? 8,
            Vouchers = t["vouchers"] is JsonArray v ? v.Select(BalatroState.Str).OfType<string>().ToList() : [],
            DeckKey = BalatroState.Str(t["deck_key"]),
            Stake = BalatroState.Int(t["stake"]) ?? 1,
            PerTurn = PerTurn.FromJson(t["per_turn"]),
            InFight = BalatroState.Bool(t["in_fight"]),
            DiscardLeft = BalatroState.Int(t["discard_left"]) ?? 0,
            Pack = PackSnapshot.FromJson(t["pack"]),
            NextBlind = NextBlind.FromJson(t["next_blind"]),
            Road = RoadInfo.FromJson(t["road"]),
            PackEffect = PackEffect.FromJson(t["pack_effect"]),
            WonRun = BalatroState.Bool(t["won_run"]),
            Tags = t["tags"] is JsonArray g ? g.OfType<JsonObject>().Select(o => BalatroState.Str(o["key"])).OfType<string>().ToList() : [],
            DiscardedChips = BalatroState.Num(t["discarded_chips"]) ?? 0,
        };
    }
}

/// <summary>Which poker hand the selected cards make, before jokers.</summary>
public sealed record HandPreview(string Hand, string Level, string Chips, string Mult, bool Allowed)
{
    public bool IsEmpty => Hand.Length == 0;

    public static HandPreview FromJson(JsonObject p) => new(
        BalatroState.Str(p["hand"]) ?? "",
        TableText.Display(p["level"]),
        TableText.Display(p["chips"]),
        TableText.Display(p["mult"]),
        p["allowed"] is not JsonValue v || !v.TryGetValue(out bool allowed) || allowed);
}

/// <summary>After selecting cards in Jimbo's hand: the poker hand, and which consumables can be used on them.</summary>
public sealed record HighlightResult(HandPreview Preview, IReadOnlyList<int> Usable, IReadOnlyList<int> PackUsable)
{
    public static HighlightResult FromJson(JsonObject h) => new(
        h["preview"] is JsonObject p ? HandPreview.FromJson(p) : new HandPreview("", "", "0", "0", true),
        h["usable"] is JsonArray u ? u.Select(BalatroState.Int).OfType<int>().ToList() : [],
        h["pack_usable"] is JsonArray pu ? pu.Select(BalatroState.Int).OfType<int>().ToList() : []);
}

/// <summary>One line of a cash-out: a blind's reward, leftover hands, interest, a joker's payout.</summary>
public sealed record CashOutRow(string Name, string Label, double Dollars);

/// <summary>Money a won fight's end of round paid before the cash-out: Gold Cards held, Rental.</summary>
public sealed record CashOutPaid(string Label, double Dollars, int Count);

/// <summary>A won fight's cash-out, as Balatro added it up, and the run after it.</summary>
public sealed record CashOut(IReadOnlyList<CashOutRow> Rows, double Total, TableSnapshot Table)
{
    /// <summary>What the round's end paid before the cash-out; already in the table's dollars.</summary>
    public IReadOnlyList<CashOutPaid> Paid { get; init; } = [];

    /// <summary>What jokers and cards said as the round ended ("Gros Michel: Extinct!").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public double PaidTotal => Paid.Sum(p => p.Dollars);

    public static CashOut FromJson(JsonObject c) => new(
        c["rows"] is JsonArray rows
            ? rows.OfType<JsonObject>().Select(r => new CashOutRow(BalatroState.Str(r["name"]) ?? "", BalatroState.Str(r["label"]) ?? "",
                BalatroState.Num(r["dollars"]) ?? 0)).ToList()
            : [],
        BalatroState.Num(c["total"]) ?? 0,
        c["table"] is JsonObject t ? TableSnapshot.FromJson(t) : throw new System.FormatException("cash-out without the table"))
    {
        Paid = c["paid"] is JsonArray paid
            ? paid.OfType<JsonObject>().Select(p => new CashOutPaid(BalatroState.Str(p["label"]) ?? "", BalatroState.Num(p["dollars"]) ?? 0,
                BalatroState.Int(p["count"]) ?? 1)).ToList()
            : [],
        Notes = c["notes"] is JsonArray notes ? notes.Select(BalatroState.Str).OfType<string>().ToList() : [],
    };
}

/// <summary>Every playing card in Jimbo's run, for the deck view.</summary>
public sealed record DeckListing(IReadOnlyList<TableCard> Cards, IReadOnlyList<PokerHandInfo> Hands, IReadOnlyDictionary<string, AtlasInfo> Atlases, int TextureScale)
{
    public static DeckListing FromJson(JsonObject d)
    {
        var atlases = new Dictionary<string, AtlasInfo>();
        if (d["atlases"] is JsonObject a)
        {
            foreach ((string name, JsonNode? info) in a)
            {
                if (info is JsonObject i && BalatroState.Str(i["path"]) is string path)
                {
                    atlases[name] = new AtlasInfo(path, BalatroState.Int(i["px"]) ?? 71, BalatroState.Int(i["py"]) ?? 95);
                }
            }
        }
        return new DeckListing(TableCard.List(d["cards"]), PokerHandInfo.List(d["hands"]), atlases, BalatroState.Int(d["texture_scale"]) ?? 2);
    }
}

/// <summary>A Balatro run in numbers, as Balatro's game over screen shows it.</summary>
public sealed record RunStats(int Ante, int Round, double BestHand, string? MostPlayed, int MostPlayedCount,
    int CardsPlayed, int CardsDiscarded, int CardsPurchased, int TimesRerolled, int Jokers)
{
    public static RunStats FromJson(JsonObject d) => new(
        BalatroState.Int(d["ante"]) ?? 1, BalatroState.Int(d["round"]) ?? 0, BalatroState.Num(d["best_hand"]) ?? 0,
        BalatroState.Str(d["most_played"]), BalatroState.Int(d["most_played_count"]) ?? 0,
        BalatroState.Int(d["cards_played"]) ?? 0, BalatroState.Int(d["cards_discarded"]) ?? 0,
        BalatroState.Int(d["cards_purchased"]) ?? 0, BalatroState.Int(d["times_rerolled"]) ?? 0, BalatroState.Int(d["jokers"]) ?? 0);
}

/// <summary>A poker hand's standing in the run, as Balatro's Run Info shows it.</summary>
public sealed record PokerHandInfo(string Key, string Name, int Level, string Chips, string Mult, int Played)
{
    public static IReadOnlyList<PokerHandInfo> List(JsonNode? node) =>
        node is JsonArray hands
            ? hands.OfType<JsonObject>().Select(h => new PokerHandInfo(
                BalatroState.Str(h["key"]) ?? "", BalatroState.Str(h["name"]) ?? "", BalatroState.Int(h["level"]) ?? 1,
                TableText.Display(h["chips"]), TableText.Display(h["mult"]), BalatroState.Int(h["played"]) ?? 0)).ToList()
            : [];
}

/// <summary>One step of Balatro's scoring sequence, replayed by StS2's scoring animation.</summary>
public abstract record TraceStep;

/// <summary>The chips/mult counters (or the hand name) changed. Null fields didn't change.</summary>
public sealed record CounterStep(string? Chips, string? Mult, string? Hand, string? Level) : TraceStep;

/// <summary>A "+30" / "+4 Mult" / "X2 Mult" style popup over a card or joker.</summary>
public sealed record PopupStep(int CardId, string Area, string Text, string Kind) : TraceStep;

public sealed record ScoredHand(
    double Score,
    string Hand,
    double Chips,
    double Mult,
    IReadOnlyList<int> Played,
    IReadOnlyList<int> Scoring,
    IReadOnlyList<TraceStep> Trace,
    double RoundChips,
    double? Target,
    bool Defeated,
    bool Failed)
{
    public static ScoredHand FromJson(JsonObject s)
    {
        var trace = new List<TraceStep>();
        if (s["trace"] is JsonArray steps)
        {
            foreach (JsonObject step in steps.OfType<JsonObject>())
            {
                switch (BalatroState.Str(step["step"]))
                {
                    case "counter":
                        trace.Add(new CounterStep(TableText.DisplayOrNull(step["chips"]), TableText.DisplayOrNull(step["mult"]),
                            BalatroState.Str(step["hand"]), TableText.DisplayOrNull(step["level"])));
                        break;
                    case "popup":
                        trace.Add(new PopupStep(BalatroState.Int(step["card"]) ?? 0, BalatroState.Str(step["area"]) ?? "other",
                            BalatroState.Str(step["text"]) ?? "", BalatroState.Str(step["kind"]) ?? "special"));
                        break;
                }
            }
        }
        return new ScoredHand(
            BalatroState.Num(s["score"]) ?? 0,
            BalatroState.Str(s["hand"]) ?? "Hand",
            BalatroState.Num(s["chips"]) ?? 0,
            BalatroState.Num(s["mult"]) ?? 0,
            Ids(s["played"]),
            Ids(s["scoring"]),
            trace,
            BalatroState.Num(s["round_chips"]) ?? 0,
            BalatroState.Num(s["target"]),
            BalatroState.Bool(s["defeated"]),
            BalatroState.Bool(s["failed"]));
    }

    private static IReadOnlyList<int> Ids(JsonNode? node) =>
        node is JsonArray array ? array.Select(BalatroState.Int).OfType<int>().ToList() : [];
}

/// <summary>Number formatting shared by the table models and the StS2 table screen.</summary>
public static class TableText
{
    /// <summary>Balatro-style: grouped digits, scientific notation once numbers get silly.</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "naneinf" : "-naneinf";
        if (System.Math.Abs(value) >= 1e11) return value.ToString("0.###e+0", CultureInfo.InvariantCulture);
        return System.Math.Floor(value) == value
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);
    }

    public static string Display(JsonNode? node) => DisplayOrNull(node) ?? "";

    /// <summary>Numbers are formatted; strings (Balatro sends "?" for face-down cards) pass through.</summary>
    public static string? DisplayOrNull(JsonNode? node) =>
        BalatroState.Str(node) == null && BalatroState.Num(node) is double d ? Number(d) : BalatroState.Str(node);
}
