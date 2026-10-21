using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's run as Balatro sees it. StS2 shows Balatro's run back to the player: jokers, vouchers
/// and the deck are relics, consumables are potions, and Balatro's money is StS2's gold.
/// <see cref="Apply"/> brings all of those in line with a Balatro snapshot; every Jimbo step
/// passes its reply through it. Call from the main thread.
/// </summary>
internal static class JimboRun
{
    // StS2 keeps a run's start time private; with the seed it tells two runs on one seed apart.
    private static readonly AccessTools.FieldRef<RunManager, long>? StartTime = TryStartTime();

    private static bool _applying;

    public static bool IsJimbo(Player? player) => player?.Character is Jimbo;

    /// <summary>True for a run Jimbo plays (Jimbo is single-player only).</summary>
    public static bool IsJimboRun(IRunState? state) => state != null && state.Players.Count == 1 && IsJimbo(state.Players[0]);

    /// <summary>The run's Jimbo, if this is a Jimbo run.</summary>
    public static Player? Me(IRunState? state) => IsJimboRun(state) ? state!.Players[0] : null;

    public static Player? Me() => Me(RunManager.Instance.DebugOnlyGetState());

    /// <summary>The latest word from Balatro on this run.</summary>
    public static TableSnapshot? Last { get; private set; }

    /// <summary>Raised after <see cref="Apply"/> takes in a new snapshot.</summary>
    public static event Action<TableSnapshot>? Updated = table =>
    {
        JimboDeckButton.Refresh(table);
        JimboRelicBar.Hook(table);
        JimboMapChips.Refresh(table);
    };

    /// <summary>True while Apply is changing relics and potions (so those changes aren't sent back to Balatro).</summary>
    public static bool IsApplying => _applying;

    /// <summary>The StS2 run's id, shared with Deal In: its seed and start time.</summary>
    public static string RunId(Player player)
    {
        long start = StartTime != null ? StartTime(RunManager.Instance) : 0;
        return $"{player.RunState.Rng.StringSeed}-{start}";
    }

    /// <summary>What Balatro needs to find (or start) Jimbo's Balatro run.</summary>
    public static JimboLink LinkFor(Player player)
    {
        string? deck = player.Relics.OfType<DeckRelic>().FirstOrDefault()?.BalatroKey;
        StakeRelic? stake = player.Relics.OfType<StakeRelic>().FirstOrDefault();
        int level = stake != null ? stake.Item.Order : 1;
        return new JimboLink(RunId(player), deck, level, BalatroSeed(player.RunState.Rng.StringSeed));
    }

    /// <summary>Balatro seeds are up to 8 letters and digits; StS2's seed gives the same shuffles every time.</summary>
    public static string BalatroSeed(string sts2Seed)
    {
        var seed = new StringBuilder();
        foreach (char c in sts2Seed.ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) && c != '0') seed.Append(c);
            if (seed.Length == 8) break;
        }
        return seed.Length > 0 ? seed.ToString() : "JIMBO";
    }

    /// <summary>The session, or null with a reason if Balatro isn't there.</summary>
    public static BalatroSession Session() =>
        BridgeMod.Session is { IsReady: true } session ? session : throw new BalatroBridgeException("Balatro isn't connected");

    /// <summary>Takes in a snapshot: relics, potions and gold follow Balatro.</summary>
    public static async Task Apply(Player player, TableSnapshot table)
    {
        Last = table;
        if (!IsJimbo(player)) return;
        _applying = true;
        try
        {
            if (player.Gold != (int)table.Dollars) player.Gold = (int)Math.Max(table.Dollars, -999);
            _gold = player.Gold;
            await SyncJokers(player, table);
            await SyncSingles<VoucherRelic>(player, table.Vouchers.ToHashSet());
            if (table.DeckKey != null) await SyncSingles<DeckRelic>(player, [table.DeckKey]);
            await JimboConsumables.Sync(player, table);
            await JimboCombat.FollowBlind(player, table);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: couldn't bring the run in line with Balatro: {e}");
        }
        finally
        {
            _applying = false;
        }
        Updated?.Invoke(table);
    }

    // Each Balatro joker card is one relic (two copies of a joker are two relics).
    private static async Task SyncJokers(Player player, TableSnapshot table)
    {
        var wanted = table.Jokers.Where(j => j.Key != null).ToDictionary(j => j.Id);
        foreach (JokerRelic relic in player.Relics.OfType<JokerRelic>().ToList())
        {
            if (wanted.TryGetValue(relic.CardId, out TableCard? card) && card.Key == relic.BalatroKey) continue;
            BridgeMod.LogInfo($"jimbo: lost {relic.BalatroKey}");
            await RelicCmd.Remove(relic);
        }
        var have = player.Relics.OfType<JokerRelic>().ToDictionary(r => r.CardId);
        foreach (TableCard card in table.Jokers)
        {
            if (!have.TryGetValue(card.Id, out JokerRelic? relic))
            {
                if (BalatroContent.ModelFor<RelicModel>(card.Key) is not RelicModel canonical) continue;
                relic = (JokerRelic)canonical.ToMutable();
                relic.CardId = card.Id;
                relic.LiveText = card.Text;
                BridgeMod.LogInfo($"jimbo: gained {card.Key}");
                await RelicCmd.Obtain(relic, player);
            }
            relic.LiveText = card.Text;
            relic.Status = card.Debuffed ? RelicStatus.Disabled : RelicStatus.Normal;
        }
        OrderJokers(player, table);
    }

    private static readonly AccessTools.FieldRef<Player, List<RelicModel>> RelicList = AccessTools.FieldRefAccess<Player, List<RelicModel>>("_relics");
    private static readonly AccessTools.FieldRef<NRelicInventory, List<NRelicInventoryHolder>> BarNodes =
        AccessTools.FieldRefAccess<NRelicInventory, List<NRelicInventoryHolder>>("_relicNodes");

    // Jokers score left to right, so the relic bar shows them in Balatro's order: the joker relics
    // keep their places among the other relics, and which joker sits in each follows Balatro.
    private static void OrderJokers(Player player, TableSnapshot table)
    {
        List<RelicModel> relics = RelicList(player);
        List<int> order = table.Jokers.Select(j => j.Id).ToList();
        List<int> slots = Enumerable.Range(0, relics.Count).Where(i => relics[i] is JokerRelic).ToList();
        List<RelicModel> jokers = slots.Select(i => relics[i])
            .OrderBy(r => order.IndexOf(((JokerRelic)r).CardId) is int at && at >= 0 ? at : int.MaxValue).ToList();
        if (slots.Select(i => relics[i]).SequenceEqual(jokers)) return;
        for (int k = 0; k < slots.Count; k++) relics[slots[k]] = jokers[k];

        if (NRun.Instance?.GlobalUi.RelicInventory is not NRelicInventory bar || !Godot.GodotObject.IsInstanceValid(bar)) return;
        List<NRelicInventoryHolder> nodes = BarNodes(bar);
        var byRelic = nodes.Where(n => n.Relic?.Model != null).ToDictionary(n => n.Relic.Model);
        List<NRelicInventoryHolder> sorted = relics.Where(byRelic.ContainsKey).Select(r => byRelic[r]).ToList();
        if (sorted.Count != nodes.Count) return;
        nodes.Clear();
        nodes.AddRange(sorted);
        for (int i = 0; i < sorted.Count; i++) bar.MoveChild(sorted[i], i);
    }

    // Vouchers and the deck: one relic per key.
    private static async Task SyncSingles<T>(Player player, ICollection<string> keys) where T : BalatroRelic
    {
        foreach (T relic in player.Relics.OfType<T>().ToList())
        {
            if (keys.Contains(relic.BalatroKey)) continue;
            BridgeMod.LogInfo($"jimbo: lost {relic.BalatroKey}");
            await RelicCmd.Remove(relic);
        }
        var have = player.Relics.OfType<T>().Select(r => r.BalatroKey).ToHashSet();
        foreach (string key in keys)
        {
            if (have.Contains(key)) continue;
            if (BalatroContent.ModelFor<RelicModel>(key) is not RelicModel canonical)
            {
                BridgeMod.LogError($"jimbo: no relic for Balatro's {key}");
                continue;
            }
            BridgeMod.LogInfo($"jimbo: gained {key}");
            await RelicCmd.Obtain(canonical.ToMutable(), player);
        }
    }

    private static string? _watched;
    private static Player? _goldWatched;
    private static int _gold;

    /// <summary>
    /// Each frame: when a Jimbo run begins or is loaded, Balatro is told which run it is (so it
    /// loads it in the background), and a loaded run's relics and potions are matched back up to
    /// Balatro's cards.
    /// </summary>
    public static void Watch()
    {
        Player? me = RunManager.Instance.IsInProgress ? Me() : null;
        string? run = me != null ? RunId(me) : null;
        if (run == _watched) return;
        _watched = run;
        Last = null;
        if (_goldWatched != null) _goldWatched.GoldChanged -= OnGoldChanged;
        _goldWatched = me;
        if (me != null)
        {
            _gold = me.Gold;
            me.GoldChanged += OnGoldChanged;
        }
        if (me == null || BridgeMod.Session is not { IsReady: true } session) return;
        JimboLink link = LinkFor(me);
        session.JimboLink(link);
        if (link.Deck == null) return; // the Dealer hasn't dealt yet
        MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(Resync(me, session, link));
    }

    // Something in StS2 took or gave Jimbo money (a thief, say): Balatro's dollars follow.
    private static void OnGoldChanged()
    {
        if (_applying || _goldWatched is not Player me) return;
        int delta = me.Gold - _gold;
        _gold = me.Gold;
        if (delta == 0 || BridgeMod.Session is not { IsReady: true } session) return;
        session.JimboDollars(LinkFor(me), delta);
        BridgeMod.LogInfo($"jimbo: StS2 changed the money by {delta}");
    }

    private static async Task Resync(Player me, BalatroSession session, JimboLink link)
    {
        try
        {
            TableSnapshot table = await session.JimboState(link);
            JimboConsumables.Relink(me, table);
            await Apply(me, table);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: couldn't catch up with Balatro: {e.Message}");
        }
    }

    private static AccessTools.FieldRef<RunManager, long>? TryStartTime()
    {
        try
        {
            return AccessTools.FieldRefAccess<RunManager, long>("_startTime");
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"can't read the run's start time ({e.Message}); Balatro runs are matched by seed alone");
            return null;
        }
    }
}
