using System;
using System.IO;
using System.Threading.Tasks;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Gets Balatro ready for Jimbo, and runs his shop visits: the shop screen shows each snapshot,
/// the player's moves go to Balatro, and every answer is applied to the run (relics, potions,
/// gold) before it is shown. Call from the main thread.
/// </summary>
internal static class JimboTable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The session once Balatro is connected, launching it first if it isn't running.</summary>
    public static async Task<BalatroSession> Connect()
    {
        BalatroSession session = BridgeMod.Session ?? throw new IOException("the Balatro bridge failed to start");
        if (session.IsReady) return session;
        if (!BridgeMod.Launcher.IsRunning && !BridgeMod.TryLaunchBalatro(out string message)) throw new IOException(message);
        if (!await WaitForFrames(() => session.IsReady, ConnectTimeout)) throw new TimeoutException("Balatro didn't start in time");
        return session;
    }

    /// <summary>Runs the shop until the player moves on (Next Round). Balatro must be in it.</summary>
    public static async Task VisitShop(Player player)
    {
        BalatroSession session = await Connect();
        JimboLink link = JimboRun.LinkFor(player);
        JimboMusic.SetTrack(BalatroAudio.Track.Shop);
        JimboShopScreen screen = JimboShopScreen.Open();
        try
        {
            // After a skipped blind Balatro owes Jimbo this shop; otherwise it's already open.
            TableSnapshot table = await session.JimboOpenShop(link);
            JimboConsumables.Relink(player, table);
            await JimboRun.Apply(player, table);
            if (table.Shop == null && table.Pack == null)
            {
                // Not in the shop (it was left already, say): nothing to buy here.
                BridgeMod.LogInfo($"jimbo: no shop to visit (Balatro is at {table.State})");
                return;
            }
            await screen.Show(table, dealIn: true);
            if (table.Pack == null) JimboQuips.Shop(screen, JimboQuips.Moment.ShopEnter);
            bool left = false;
            while (true)
            {
                ShopChoiceMade choice = await screen.NextChoice();
                if (choice.Move == ShopMove.Leave) break;
                if (choice.Move == ShopMove.SkipBlind)
                {
                    // Balatro leaves the shop and skips the blind; a pack tag's pack opens here.
                    TagInfo? tag = table.Shop?.NextBlind?.Tag;
                    table = await session.JimboSkip(link);
                    left = true;
                    await JimboRun.Apply(player, table);
                    JimboSkips.MarkSkipped(player, tag);
                    TableFx.Sound("generic1");
                    if (table.Pack == null) break;
                    await screen.Show(table, dealIn: true);
                    continue;
                }
                try
                {
                    TableSnapshot? next = choice.Move switch
                    {
                        ShopMove.Buy => await session.JimboBuy(link, choice.CardId),
                        ShopMove.BuyAndUse => await session.JimboBuy(link, choice.CardId, use: true),
                        ShopMove.Sell => await session.JimboSell(link, choice.CardId),
                        ShopMove.MoveJoker => await session.JimboMoveJoker(link, choice.CardId, choice.To),
                        ShopMove.Use => await session.JimboUse(link, choice.CardId, []),
                        ShopMove.Reroll => await session.RerollShop(),
                        ShopMove.Pick => await session.JimboPick(link, choice.CardId, choice.HandCards ?? []),
                        ShopMove.SkipPack => await session.JimboSkipPack(link),
                        ShopMove.RaiseBlind => await session.JimboRaise(link),
                        ShopMove.Refresh => await session.JimboState(link),
                        _ => null,
                    };
                    if (next == null) continue;
                    if (next.Shop == null && next.Pack == null)
                    {
                        // A pack from a skip's tag is done: the blind select is next.
                        await JimboRun.Apply(player, next);
                        break;
                    }
                    bool newStock = choice.Move is ShopMove.Reroll || (table.Pack == null) != (next.Pack == null);
                    // A tarot or spectral from the pack: its effect on the dealt cards plays out first.
                    if (next.PackEffect is PackEffect effect) await screen.ShowPackEffect(effect);
                    Feedback(choice, table, next);
                    table = next;
                    await JimboRun.Apply(player, table);
                    await screen.Show(table, dealIn: newStock);
                    if (table.Pack == null && choice.Move switch
                        {
                            ShopMove.Buy or ShopMove.BuyAndUse => JimboQuips.Moment.ShopBuy,
                            ShopMove.Reroll => JimboQuips.Moment.ShopReroll,
                            ShopMove.Sell => JimboQuips.Moment.ShopSell,
                            _ => (JimboQuips.Moment?)null,
                        } is JimboQuips.Moment moment)
                    {
                        JimboQuips.Shop(screen, moment);
                    }
                }
                catch (Exception e)
                {
                    // Balatro said no (not enough money, no room...), or something went wrong
                    // showing the result: let the player choose again, from the shop as Balatro
                    // has it now, rather than throwing them out of it.
                    string reason;
                    if (e is BalatroBridgeException)
                    {
                        BridgeMod.LogInfo($"jimbo: shop refused: {e.Message}");
                        reason = e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
                    }
                    else
                    {
                        BridgeMod.LogError($"jimbo: shop move {choice.Move} went wrong: {e}");
                        reason = "That didn't work. Here's the shop as it stands.";
                    }
                    table = await session.JimboState(link);
                    await JimboRun.Apply(player, table);
                    if (table.Shop == null && table.Pack == null) break;
                    await screen.Show(table, dealIn: false);
                    screen.SetStatus(reason, StsColors.red);
                }
            }
            if (!left) await session.LeaveShop();
            BridgeMod.LogInfo("jimbo: left the shop");
        }
        finally
        {
            JimboMusic.SetTrack(BalatroAudio.Track.Main);
            await screen.Close();
        }
    }

    // A sound for what just happened, as Balatro would play it.
    private static void Feedback(ShopChoiceMade choice, TableSnapshot before, TableSnapshot after)
    {
        switch (choice.Move)
        {
            case ShopMove.Buy or ShopMove.BuyAndUse:
                TableFx.Sound("coin1", 1f, 0.8f);
                TableFx.Sound("card1", 1f, 0.6f);
                break;
            case ShopMove.Sell:
                TableFx.Sound("coin2", 1f, 0.8f);
                break;
            case ShopMove.MoveJoker:
                TableFx.Sound("cardSlide1", 1f, 0.7f);
                break;
            case ShopMove.Reroll:
                TableFx.Sound("coin2");
                TableFx.Sound("other1");
                break;
            case ShopMove.Use or ShopMove.Pick:
                TableFx.Sound(after.Pack != null || before.Pack != null ? "card1" : "tarot1", 1f, 0.7f);
                break;
        }
    }

    public static async Task<bool> WaitForFrames(Func<bool> condition, TimeSpan timeout)
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
