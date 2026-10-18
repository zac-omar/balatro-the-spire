using System;
using System.IO;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Relics;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace BalatroBridge;

/// <summary>
/// "Play a hand of Balatro" inside StS2: opens the table screen over the fight, relays the
/// player's discards and play to the hidden Balatro engine, and animates the scoring. A hand
/// that beats the blind opens Balatro's shop on the same table, where jokers are bought as
/// relics. Call from the Godot main thread.
/// </summary>
internal static class BalatroTable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);

    // While waiting on Balatro, it is pinged this often; a ping unanswered this long means it hung.
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs one hand for <paramref name="player"/>'s run (null outside a run: no relics, and the
    /// shop only looks). <paramref name="damageFor"/> turns the score into the damage shown on the
    /// table. Returns null, with the reason, if Balatro couldn't be reached.
    /// </summary>
    public static async Task<(ScoredHand? Hand, string? Problem)> PlayHand(Creature? target, Player? player, Func<double, decimal> damageFor)
    {
        BalatroSession? session = BridgeMod.Session;
        if (session == null) return (null, "the Balatro bridge failed to start");

        BridgeMod.LogInfo("table: opening");
        TableScreen? screen = null;
        try
        {
            TableMusic.Start();
            screen = TableScreen.Open(session, target);
            if (!session.IsReady)
            {
                if (!BridgeMod.Launcher.IsRunning && !BridgeMod.TryLaunchBalatro(out string message)) return await Fail(screen, message);
                if (!await WaitForFrames(() => session.IsReady, ConnectTimeout)) return await Fail(screen, "Balatro didn't start in time");
            }

            TableSnapshot table = await OpenTable(screen, session, player);
            if (table.Shop != null)
            {
                // A blind was beaten last time but its shop was never shown (the game closed, say).
                await VisitShop(screen, session, table, player);
                await screen.CloseShop();
                table = await OpenTable(screen, session, player);
            }
            BridgeMod.LogInfo($"table: dealt {table.Hand.Count} cards ({table.Blind?.Name}, {table.HandsLeft} hands left)");
            await screen.Deal(table);
            while (true)
            {
                TableAction action = await screen.NextAction();
                if (!action.Play)
                {
                    BridgeMod.LogInfo($"table: discarding {action.CardIds.Length}");
                    Task throwAway = screen.ThrowAway(action.CardIds);
                    TableSnapshot refilled = await Guard(session, session.Discard(action.CardIds));
                    await throwAway;
                    await screen.Deal(refilled);
                    continue;
                }

                BridgeMod.LogInfo($"table: playing {action.CardIds.Length}");
                Task moveToPlay = screen.MoveToPlay(action.CardIds);
                ScoredHand scored = await Guard(session, session.PlayCards(action.CardIds));
                await moveToPlay;
                decimal damage = damageFor(scored.Score);
                BridgeMod.LogInfo($"table: {scored.Hand} scored {scored.Score} -> {damage} damage" +
                    (scored.Defeated ? ", blind defeated" : scored.Failed ? ", blind failed" : ""));
                await screen.Score(scored, damage);
                if (scored.Defeated)
                {
                    try
                    {
                        // Balatro cashes out and opens its shop; it answers once the shop is stocked.
                        TableSnapshot shop = await OpenTable(screen, session, player);
                        if (shop.Shop != null) await VisitShop(screen, session, shop, player);
                    }
                    catch (Exception e)
                    {
                        // The hand already counts; a shop that fails can wait for the next Deal In.
                        BridgeMod.LogError($"Balatro shop failed: {e}");
                    }
                }
                return (scored, null);
            }
        }
        catch (Exception e)
        {
            // Whatever went wrong, the fight must carry on: report it, close the table, fizzle the card.
            BridgeMod.LogError($"Balatro table failed: {e}");
            return screen != null ? await Fail(screen, e.Message) : (null, e.Message);
        }
        finally
        {
            TableMusic.Stop();
            if (screen != null)
            {
                try
                {
                    await screen.Close();
                }
                catch (Exception e)
                {
                    BridgeMod.LogError($"table close failed, removing it: {e}");
                    screen.Remove();
                }
            }
            BridgeMod.LogInfo("table: closed");
        }
    }

    /// <summary>
    /// Asks Balatro for the hand, or the shop. That can take a few seconds if it is loading a run.
    /// If Balatro hangs meanwhile, it is restarted once (it picks the run back up from its save)
    /// and asked again.
    /// </summary>
    private static async Task<TableSnapshot> OpenTable(TableScreen screen, BalatroSession session, Player? player)
    {
        try
        {
            return await Guard(session, session.OpenTable(DealInRelics.LinkFor(player)));
        }
        catch (BalatroHungException) when (BridgeMod.Launcher.IsRunning)
        {
            BridgeMod.LogError("table: Balatro stopped responding; restarting it");
            screen.SetStatus("Balatro got stuck. Restarting it...", StsColors.cream);
            BridgeMod.Launcher.Kill();
            await WaitForFrames(() => !session.IsConnected, TimeSpan.FromSeconds(5));
            if (!BridgeMod.TryLaunchBalatro(out string message)) throw new IOException(message);
            if (!await WaitForFrames(() => session.IsReady, ConnectTimeout)) throw new TimeoutException("Balatro didn't restart in time");
            screen.SetStatus("Shuffling the deck...", StsColors.cream);
            return await Guard(session, session.OpenTable(DealInRelics.LinkFor(player)));
        }
    }

    /// <summary>
    /// Waits for a reply, pinging Balatro every few seconds meanwhile. A hung Balatro (it stops
    /// answering pings) fails the wait at once instead of after the request's full timeout.
    /// </summary>
    private static async Task<T> Guard<T>(BalatroSession session, Task<T> request)
    {
        while (!await WaitForFrames(() => request.IsCompleted, PingInterval))
        {
            Task ping = session.Ping();
            if (await WaitForFrames(() => ping.IsCompleted, PingTimeout) && ping.IsCompletedSuccessfully) continue;
            Observe(ping);
            Observe(request); // it fails once Balatro is restarted or gives up
            throw new BalatroHungException();
        }
        return await request;
    }

    private static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private sealed class BalatroHungException() : Exception("Balatro stopped responding");

    /// <summary>Runs the shop until the player moves on. Each joker bought is obtained as its relic.</summary>
    private static async Task VisitShop(TableScreen screen, BalatroSession session, TableSnapshot table, Player? player)
    {
        BridgeMod.LogInfo($"table: shop open, {table.Shop!.Cards.Count} for sale, ${table.Dollars}");
        await screen.OpenShop(table, canBuy: player != null);
        while (true)
        {
            ShopAction action = await screen.NextShopAction();
            if (action.Choice == ShopChoice.Leave) break;
            try
            {
                if (action.Choice == ShopChoice.Reroll)
                {
                    table = await session.RerollShop();
                    await screen.ShowShop(table, dealIn: true);
                    continue;
                }
                table = await session.BuyFromShop(action.CardId);
                BridgeMod.LogInfo($"table: bought {table.BoughtKey}");
                if (DealInRelics.RelicFor(table.BoughtKey) is RelicModel relic && player != null)
                {
                    await RelicCmd.Obtain(relic.ToMutable(), player);
                }
                await screen.Bought(table, action.CardId);
            }
            catch (BalatroBridgeException e)
            {
                // Balatro said no (not enough money, say): show why and let the player choose again.
                BridgeMod.LogInfo($"table: shop refused: {e.Message}");
                await screen.ShowShop(table, dealIn: false);
                screen.SetStatus(e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..], StsColors.red);
            }
        }
        await session.LeaveShop();
        BridgeMod.LogInfo("table: left the shop");
    }

    private static async Task<(ScoredHand?, string?)> Fail(TableScreen screen, string problem)
    {
        try
        {
            screen.SetStatus("Balatro isn't answering...", StsColors.red);
            await WaitForFrames(() => false, TimeSpan.FromSeconds(1.2));
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"table status failed: {e.Message}");
        }
        return (null, problem);
    }

    private static async Task<bool> WaitForFrames(Func<bool> condition, TimeSpan timeout)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }
}
