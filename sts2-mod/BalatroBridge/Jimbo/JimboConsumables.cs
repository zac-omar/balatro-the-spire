using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's potion belt is Balatro's consumable slots: one belt slot per slot, one potion per
/// tarot, planet or spectral card, in Balatro's order. Using a potion uses the card in Balatro
/// (on the cards selected in hand, for those that need some); discarding one sells it.
/// </summary>
internal static class JimboConsumables
{
    /// <summary>Balatro's consumable slots before any voucher or edition adds more.</summary>
    public const int StartingSlots = 2;

    // Which Balatro card each potion in the belt stands for.
    private static readonly ConditionalWeakTable<PotionModel, Box> CardIds = new();

    private sealed class Box(int id)
    {
        public int Id { get; } = id;
    }

    /// <summary>The cards selected in Jimbo's hand, which a used consumable works on.</summary>
    public static Func<IReadOnlyList<int>> SelectedCards { get; set; } = () => [];

    /// <summary>Consumables Balatro said can be used right now (refreshed as cards are selected).</summary>
    private static HashSet<int> _usable = new();

    public static int? CardIdOf(PotionModel potion) => CardIds.TryGetValue(potion, out Box? box) ? box.Id : null;

    public static bool CanUse(ConsumablePotion potion) =>
        CardIdOf(potion) is int id && _usable.Contains(id) && !JimboRun.IsApplying;

    /// <summary>Records what Balatro says is usable now (after cards were selected in hand).</summary>
    public static void SetUsable(IEnumerable<int> ids) => _usable = ids.ToHashSet();

    public static async Task Sync(Player player, TableSnapshot table)
    {
        SetUsable(table.Consumables.Where(c => c.Usable).Select(c => c.Id));
        int slots = Math.Max(table.ConsumableSlots, table.Consumables.Count);
        if (player.MaxPotionCount < slots) player.AddToMaxPotionCount(slots - player.MaxPotionCount);

        IReadOnlyList<PotionModel?> belt = player.PotionSlots;
        var wanted = table.Consumables.ToDictionary(c => c.Id);
        // Potions whose card is gone (used, sold, destroyed) leave the belt.
        foreach (PotionModel potion in belt.OfType<PotionModel>().ToList())
        {
            if (CardIdOf(potion) is not int id || !wanted.ContainsKey(id)) player.DiscardPotionInternal(potion);
        }
        var shown = player.Potions.Select(CardIdOf).OfType<int>().ToHashSet();
        foreach (TableCard card in table.Consumables)
        {
            if (shown.Contains(card.Id) || BalatroContent.ModelFor<PotionModel>(card.Key) is not PotionModel canonical) continue;
            PotionModel potion = canonical.ToMutable();
            CardIds.AddOrUpdate(potion, new Box(card.Id));
            await MegaCrit.Sts2.Core.Commands.PotionCmd.TryToProcure(potion, player);
        }
        // Slots go when Balatro has fewer (a used negative card took its slot with it), between
        // fights and once they're empty: a potion still being used remembers its slot, and StS2
        // would move a potion out of a removed slot without telling the belt.
        if (player.MaxPotionCount > slots && !MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress &&
            player.PotionSlots.Skip(slots).All(p => p == null))
        {
            player.SubtractFromMaxPotionCount(player.MaxPotionCount - slots);
        }
    }

    /// <summary>
    /// A loaded save has the right potions but not which cards they stand for: match them up by
    /// key, in order.
    /// </summary>
    public static void Relink(Player player, TableSnapshot table)
    {
        var free = table.Consumables.ToList();
        foreach (PotionModel potion in player.Potions)
        {
            if (CardIdOf(potion) is int id && free.Any(c => c.Id == id))
            {
                free.RemoveAll(c => c.Id == id);
                continue;
            }
            TableCard? match = free.FirstOrDefault(c => potion is ConsumablePotion p && c.Key == p.BalatroKey);
            if (match == null) continue;
            CardIds.AddOrUpdate(potion, new Box(match.Id));
            free.Remove(match);
        }
    }

    /// <summary>StS2 used the potion: Balatro uses the card.</summary>
    /// <summary>True while a consumable is being used (Balatro is still working on the hand).</summary>
    public static bool Busy { get; private set; }

    public static async Task Use(ConsumablePotion potion)
    {
        Player? player = JimboRun.Me();
        if (player == null || CardIdOf(potion) is not int id) return;
        // The hand waits while the card works on it: Balatro changes the selected cards over a
        // second or two, and picking others meanwhile would pull them out from under it.
        JimboHand? locked = JimboHand.Current is { IsAcceptingInput: true } h ? h : null;
        locked?.SetInputEnabled(false);
        Busy = true;
        try
        {
            TableSnapshot table = await JimboRun.Session().JimboUse(JimboRun.LinkFor(player), id, SelectedCards());
            await JimboRun.Apply(player, table);
            // The hand shows what the card did to it (Balatro also lets go of the selection).
            if (JimboHand.Current is JimboHand hand && table.InFight) await hand.Deal(table, deselect: true);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: using {potion.BalatroKey} failed: {e.Message}");
            // The card is still Balatro's: put the potion back.
            if (JimboRun.Last is TableSnapshot last) await JimboRun.Apply(player, last);
        }
        finally
        {
            Busy = false;
            if (locked != null && JimboHand.Current == locked) locked.SetInputEnabled(true);
        }
    }

    /// <summary>The player threw the potion away: Balatro sells the card.</summary>
    public static async Task Discarded(PotionModel potion)
    {
        Player? player = JimboRun.Me();
        if (player == null || JimboRun.IsApplying || CardIdOf(potion) is not int id) return;
        try
        {
            TableSnapshot table = await JimboRun.Session().JimboSell(JimboRun.LinkFor(player), id);
            await JimboRun.Apply(player, table);
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: selling {potion.Id.Entry} failed: {e.Message}");
        }
    }
}

/// <summary>
/// StS2's potion belt only ever grows its holders. Jimbo's belt follows Balatro's consumable
/// slots, which can shrink, so empty holders past the last slot are taken away.
/// </summary>
[HarmonyPatch(typeof(NPotionContainer), "GrowPotionHolders")]
internal static class JimboBeltShrinks
{
    private static void Postfix(NPotionContainer __instance, int newMaxPotionSlots, List<NPotionHolder> ____holders)
    {
        if (____holders.Count <= newMaxPotionSlots || !JimboRun.IsJimboRun(RunManager.Instance.DebugOnlyGetState())) return;
        while (____holders.Count > newMaxPotionSlots && !____holders[^1].HasPotion)
        {
            NPotionHolder holder = ____holders[^1];
            ____holders.RemoveAt(____holders.Count - 1);
            holder.QueueFree();
        }
        Traverse.Create(__instance).Method("UpdateNavigation").GetValue();
    }
}
