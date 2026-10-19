using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BalatroBridge.Core;

/// <summary>
/// High-level conversation with Balatro on top of <see cref="BridgeServer"/>: the hello
/// handshake, request/reply matching, and tracking the latest game state.
/// Like the server, it is single-threaded: call <see cref="Pump"/> from one thread every frame.
/// </summary>
public sealed class BalatroSession
{
    public const string BridgeVersion = "0.3.0";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Getting to a dealt hand can mean clicking through a round's menus (a few seconds at engine speed).</summary>
    public static readonly TimeSpan TableTimeout = TimeSpan.FromSeconds(30);

    private readonly BridgeServer _server;
    private readonly string _hostGameVersion;
    private readonly Action<string> _log;
    private readonly Dictionary<long, PendingRequest> _pending = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextId = 1;

    public BalatroSession(BridgeServer server, string hostGameVersion, Action<string> log)
    {
        _server = server;
        _hostGameVersion = hostGameVersion;
        _log = log;
        _server.Connected += OnConnected;
        _server.Disconnected += OnDisconnected;
        _server.MessageReceived += OnMessage;
    }

    public bool IsConnected => _server.IsConnected;

    /// <summary>True once Balatro's hello has arrived on the current connection.</summary>
    public bool IsReady { get; private set; }

    public string? BalatroVersion { get; private set; }

    public BalatroState? State { get; private set; }

    /// <summary>Raised when Balatro has connected and introduced itself.</summary>
    public event Action? Ready;

    public event Action<string>? Lost;

    public event Action<BalatroState>? StateChanged;

    /// <summary>Raised for Balatro "event" messages: (name, full data).</summary>
    public event Action<string, JsonObject>? GameEvent;

    public void Pump()
    {
        _server.Pump();
        ExpireRequests();
    }

    /// <summary>Sends a one-way message. Returns false if Balatro isn't connected.</summary>
    public bool Send(string type, JsonObject? data = null) =>
        _server.Send(new BridgeMessage { Type = type, Id = _nextId++, Data = data ?? new JsonObject() });

    /// <summary>
    /// Sends a request and completes with Balatro's reply. Faults with <see cref="BalatroBridgeException"/>
    /// on an error reply, <see cref="TimeoutException"/> if no reply arrives in time, or
    /// <see cref="IOException"/> if Balatro is not connected or disconnects first.
    /// </summary>
    public Task<BridgeMessage> Request(string type, JsonObject? data = null, TimeSpan? timeout = null)
    {
        long id = _nextId++;
        var request = new PendingRequest(type, _clock.Elapsed + (timeout ?? DefaultTimeout));
        if (!_server.Send(new BridgeMessage { Type = type, Id = id, Data = data ?? new JsonObject() }))
        {
            return Task.FromException<BridgeMessage>(new IOException("Balatro is not connected"));
        }
        _pending[id] = request;
        return request.Completion.Task;
    }

    public Task<BridgeMessage> Ping() => Request("ping", new JsonObject { ["t"] = _clock.Elapsed.TotalMilliseconds });

    public async Task<BalatroState> GetState() => BalatroState.FromJson((await Request("get_state")).Data);

    public Task<BridgeMessage> StartRun(string? seed = null, int? stake = null)
    {
        var data = new JsonObject();
        if (seed != null) data["seed"] = seed;
        if (stake != null) data["stake"] = stake;
        return Request("start_run", data);
    }

    public Task<BridgeMessage> Quit() => Request("quit");

    /// <summary>
    /// The dealt hand, or the shop if Balatro is in it. Balatro gets itself there first (new run,
    /// round summary, blind select...). With a <paramref name="link"/>, Balatro switches to the
    /// StS2 run's own Balatro run and matches its jokers to the player's joker relics.
    /// </summary>
    public async Task<TableSnapshot> OpenTable(TableLink? link = null) =>
        TableSnapshot.FromJson((await Request("table_open", LinkData(link), TableTimeout)).Data);

    /// <summary>
    /// Names the StS2 run ahead of time (one-way), so Balatro switches to its run and deals a hand
    /// in the background. Returns false if Balatro isn't connected.
    /// </summary>
    public bool LinkTable(TableLink link) => Send("table_link", LinkData(link));

    /// <summary>Buys a card from the shop; the reply's <see cref="TableSnapshot.BoughtKey"/> names it.</summary>
    public async Task<TableSnapshot> BuyFromShop(int cardId) =>
        TableSnapshot.FromJson((await Request("table_shop_buy", new JsonObject { ["card"] = cardId }, TableTimeout)).Data);

    public async Task<TableSnapshot> RerollShop() =>
        TableSnapshot.FromJson((await Request("table_shop_reroll", timeout: TableTimeout)).Data);

    /// <summary>Leaves the shop. Balatro deals the next hand in the background.</summary>
    public Task<BridgeMessage> LeaveShop() => Request("table_shop_leave");

    /// <summary>Which poker hand the given cards make.</summary>
    public async Task<HandPreview> Preview(IEnumerable<int> cardIds) =>
        HandPreview.FromJson((await Request("table_preview", Cards(cardIds))).Data);

    /// <summary>Discards the given cards and completes with the refilled hand.</summary>
    public async Task<TableSnapshot> Discard(IEnumerable<int> cardIds) =>
        TableSnapshot.FromJson((await Request("table_discard", Cards(cardIds), TableTimeout)).Data);

    /// <summary>Plays the given cards and completes with the score and its scoring sequence.</summary>
    public async Task<ScoredHand> PlayCards(IEnumerable<int> cardIds) =>
        ScoredHand.FromJson((await Request("table_play", Cards(cardIds), TableTimeout)).Data);

    public async Task<TableSnapshot> SortHand(bool bySuit) =>
        TableSnapshot.FromJson((await Request("table_sort", new JsonObject { ["by"] = bySuit ? "suit" : "rank" })).Data);

    // Jimbo (bridge/jimbo.lua): every request names the run, so Balatro is always in the right one.

    /// <summary>Jimbo's step timeout: a little longer than Balatro's own, so its reason arrives first.</summary>
    public static readonly TimeSpan JimboTimeout = TimeSpan.FromSeconds(36);

    /// <summary>Names the run (one-way) so Balatro loads it in the background.</summary>
    public bool JimboLink(JimboLink link) => Send("jimbo_link", link.ToJson());

    public async Task<TableSnapshot> JimboState(JimboLink link) => await Table("jimbo_state", link);

    /// <summary>A fresh Balatro run (the Dealer dealt Jimbo his deck), at its first blind select.</summary>
    public async Task<TableSnapshot> JimboNewRun(JimboLink link) => await Table("jimbo_new_run", link);

    /// <summary>Plays the blind on deck; <paramref name="kind"/> ("Small", "Big", "Boss") is what StS2 expects.</summary>
    public async Task<TableSnapshot> JimboBlind(JimboLink link, string kind) => await Table("jimbo_blind", link, new JsonObject { ["kind"] = kind });

    public async Task<TableSnapshot> JimboTurn(JimboLink link) => await Table("jimbo_turn", link);

    /// <summary>The fight is won: Balatro beats the blind and adds up the cash-out.</summary>
    public async Task<CashOut> JimboWin(JimboLink link) =>
        CashOut.FromJson((await Request("jimbo_win", link.ToJson(), JimboTimeout)).Data);

    public async Task<TableSnapshot> JimboCashOut(JimboLink link) => await Table("jimbo_cash_out", link);

    /// <summary>Buys a shop card, voucher or pack (a pack opens). With <paramref name="use"/>, a consumable is used at once.</summary>
    public async Task<TableSnapshot> JimboBuy(JimboLink link, int cardId, bool use = false) =>
        await Table("jimbo_buy", link, new JsonObject { ["card"] = cardId, ["use"] = use });

    public async Task<TableSnapshot> JimboSell(JimboLink link, int cardId) => await Table("jimbo_sell", link, new JsonObject { ["card"] = cardId });

    /// <summary>Uses a consumable, on the given cards in hand when it needs them.</summary>
    public async Task<TableSnapshot> JimboUse(JimboLink link, int cardId, IEnumerable<int> handCards) =>
        await Table("jimbo_use", link, new JsonObject { ["card"] = cardId, ["cards"] = Ids(handCards) });

    /// <summary>Takes a card from the open pack (a tarot or spectral works on the given cards in hand).</summary>
    public async Task<TableSnapshot> JimboPick(JimboLink link, int cardId, IEnumerable<int> handCards) =>
        await Table("jimbo_pick", link, new JsonObject { ["card"] = cardId, ["cards"] = Ids(handCards) });

    public async Task<TableSnapshot> JimboSkipPack(JimboLink link) => await Table("jimbo_skip_pack", link);

    public async Task<HighlightResult> JimboHighlight(JimboLink link, IEnumerable<int> handCards) =>
        HighlightResult.FromJson((await Request("jimbo_highlight", With(link, new JsonObject { ["cards"] = Ids(handCards) }))).Data);

    public async Task<TableSnapshot> JimboMoveJoker(JimboLink link, int cardId, int to) =>
        await Table("jimbo_move_joker", link, new JsonObject { ["card"] = cardId, ["to"] = to });

    /// <summary>StS2 changed Jimbo's money (a thief, say); Balatro's follows.</summary>
    public bool JimboDollars(JimboLink link, int delta) => Send("jimbo_dollars", With(link, new JsonObject { ["delta"] = delta }));

    public async Task<DeckListing> JimboDeck(JimboLink link) =>
        DeckListing.FromJson((await Request("jimbo_deck", link.ToJson(), JimboTimeout)).Data);

    /// <summary>The run in numbers (best hand, most played hand, cards played...), for the game over screen.</summary>
    public async Task<RunStats> JimboStats(JimboLink link) =>
        RunStats.FromJson((await Request("jimbo_stats", link.ToJson(), JimboTimeout)).Data);

    /// <summary>What the Dealer may offer: "voucher" (what the shop could stock next) or "legendary" jokers.</summary>
    public async Task<IReadOnlyList<string>> JimboOffers(JimboLink link, string kind, int count) =>
        (await Request("jimbo_offers", With(link, new JsonObject { ["kind"] = kind, ["count"] = count }), JimboTimeout)).Data["keys"]
            is JsonArray keys ? keys.Select(BalatroState.Str).OfType<string>().ToList() : [];

    /// <summary>Leaves the shop and skips the next Small or Big Blind for its tag (a pack tag opens its pack).</summary>
    public async Task<TableSnapshot> JimboSkip(JimboLink link) => await Table("jimbo_skip", link);

    /// <summary>Opens the shop a skipped blind owes Jimbo; otherwise the run as it is.</summary>
    public async Task<TableSnapshot> JimboOpenShop(JimboLink link) => await Table("jimbo_open_shop", link);

    /// <summary>Gives Jimbo a voucher, joker or consumable for free (the Dealer's gift).</summary>
    public async Task<TableSnapshot> JimboGrant(JimboLink link, string key) => await Table("jimbo_grant", link, new JsonObject { ["key"] = key });

    /// <summary>Status cards join Jimbo's Balatro deck for the fight (<paramref name="add"/>), or burn away (<paramref name="remove"/>).</summary>
    public async Task<TableSnapshot> JimboDuds(JimboLink link, JsonArray add, IEnumerable<int> remove) =>
        await Table("jimbo_duds", link, new JsonObject { ["add"] = add, ["remove"] = new JsonArray(remove.Select(r => (JsonNode?)r).ToArray()) });

    /// <summary>Every Balatro item's name and text (see <see cref="BalatroCatalog"/>).</summary>
    public async Task<JsonObject> Catalog() => (await Request("jimbo_catalog", timeout: TimeSpan.FromSeconds(20))).Data;

    private async Task<TableSnapshot> Table(string type, JimboLink link, JsonObject? extra = null) =>
        TableSnapshot.FromJson((await Request(type, With(link, extra), JimboTimeout)).Data);

    private static JsonObject With(JimboLink link, JsonObject? extra)
    {
        JsonObject data = link.ToJson();
        if (extra != null)
        {
            foreach ((string key, JsonNode? value) in extra.ToList())
            {
                extra.Remove(key);
                data[key] = value;
            }
        }
        return data;
    }

    private static JsonArray Ids(IEnumerable<int> ids) => new(ids.Select(id => (JsonNode)id).ToArray());

    private static JsonObject Cards(IEnumerable<int> cardIds) =>
        new() { ["cards"] = new JsonArray(cardIds.Select(id => (JsonNode)id).ToArray()) };

    private static JsonObject LinkData(TableLink? link) => link == null ? new JsonObject() : new JsonObject
    {
        ["run"] = link.Run,
        ["jokers"] = Keys(link.Jokers),
        ["pool"] = Keys(link.Pool),
    };

    private static JsonArray Keys(IEnumerable<string> keys) => new(keys.Select(k => (JsonNode)k).ToArray());

    private void OnConnected()
    {
        // A new connection can replace an old one without a disconnect in between.
        FailAll(new IOException("Balatro reconnected before replying"));
        IsReady = false;
        BalatroVersion = null;
        State = null;
        _server.Send(new BridgeMessage
        {
            Type = "hello",
            Id = _nextId++,
            Data = new JsonObject
            {
                ["game"] = "sts2",
                ["game_version"] = _hostGameVersion,
                ["bridge_version"] = BridgeVersion,
                ["protocol"] = BridgeMessage.ProtocolVersion,
            },
        });
    }

    private void OnDisconnected(string reason)
    {
        bool wasReady = IsReady;
        IsReady = false;
        FailAll(new IOException($"Balatro disconnected: {reason}"));
        _log($"Balatro disconnected: {reason}");
        if (wasReady) Lost?.Invoke(reason);
    }

    private void OnMessage(BridgeMessage message)
    {
        if (message.Re is long re && _pending.Remove(re, out PendingRequest? request))
        {
            if (message.Type == "error")
            {
                string error = BalatroState.Str(message.Data["message"]) ?? "unknown error";
                request.Completion.TrySetException(new BalatroBridgeException($"Balatro rejected '{request.Type}': {error}"));
            }
            else
            {
                request.Completion.TrySetResult(message);
            }
        }

        switch (message.Type)
        {
            case "hello":
                BalatroVersion = BalatroState.Str(message.Data["game_version"]);
                int? protocol = BalatroState.Int(message.Data["protocol"]);
                if (protocol != BridgeMessage.ProtocolVersion)
                {
                    _log($"WARNING: Balatro speaks bridge protocol {protocol}, this mod speaks {BridgeMessage.ProtocolVersion}");
                }
                IsReady = true;
                _log($"Balatro {BalatroVersion} connected (bridge {BalatroState.Str(message.Data["bridge_version"])})");
                Ready?.Invoke();
                break;
            case "state":
                State = BalatroState.FromJson(message.Data);
                StateChanged?.Invoke(State);
                break;
            case "event":
                GameEvent?.Invoke(BalatroState.Str(message.Data["name"]) ?? "?", message.Data);
                break;
            case "error" when message.Re == null:
                _log($"Balatro reported an error: {BalatroState.Str(message.Data["message"])}");
                break;
        }
    }

    private void ExpireRequests()
    {
        if (_pending.Count == 0) return;
        TimeSpan now = _clock.Elapsed;
        foreach (long id in _pending.Where(p => p.Value.Deadline <= now).Select(p => p.Key).ToList())
        {
            _pending.Remove(id, out PendingRequest? request);
            request!.Completion.TrySetException(new TimeoutException($"Balatro did not answer '{request.Type}' in time"));
        }
    }

    private void FailAll(Exception error)
    {
        List<PendingRequest> failed = _pending.Values.ToList();
        _pending.Clear();
        foreach (PendingRequest request in failed) request.Completion.TrySetException(error);
    }

    private sealed class PendingRequest(string type, TimeSpan deadline)
    {
        public string Type { get; } = type;
        public TimeSpan Deadline { get; } = deadline;
        public TaskCompletionSource<BridgeMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class BalatroBridgeException(string message) : Exception(message);

/// <summary>
/// Ties the table to a StS2 run: <see cref="Run"/> identifies the run, <see cref="Jokers"/> are
/// the Balatro joker keys of the relics the player owns, and <see cref="Pool"/> is every joker
/// key that exists as a relic (what the shop may sell).
/// </summary>
public sealed record TableLink(string Run, IReadOnlyList<string> Jokers, IReadOnlyList<string> Pool);

/// <summary>
/// Ties Balatro to one of Jimbo's StS2 runs: <see cref="Run"/> identifies it, and a fresh Balatro
/// run for it plays <see cref="Deck"/> (null until the Dealer has dealt it) at <see cref="Stake"/>
/// on <see cref="Seed"/>.
/// </summary>
public sealed record JimboLink(string Run, string? Deck, int Stake, string Seed)
{
    public JsonObject ToJson()
    {
        var data = new JsonObject { ["mode"] = "jimbo", ["run"] = Run, ["stake"] = Stake, ["seed"] = Seed };
        if (Deck != null) data["deck"] = Deck;
        return data;
    }
}
