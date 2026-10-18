using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace BalatroBridge.Jimbo;

/// <summary>
/// A Jimbo fight multiplies the enemies' HP up to the blind's chips (<see cref="JimboCombat"/>).
/// Everything else in a fight that is measured in enemy HP is multiplied the same way, so an
/// enemy keeps its shape at any size: its Block, its heals, the HP it comes back with, and the
/// powers that count HP (Hardened Shell's cap on HP lost a turn, Hard to Kill's cap on a hit,
/// the HP that Plow and Shriek wait for). What enemies do to Jimbo isn't scaled.
/// </summary>
internal static class JimboScaling
{
    // Above this an HP figure is a sentinel ("can't be killed this way"), not a size.
    private const decimal Sentinel = 100_000_000m;

    /// <summary>The fight's multiplier, or null when no Jimbo fight is scaling.</summary>
    public static decimal? Scale => JimboCombat.HpScale;

    /// <summary>Set while Jimbo's own code sets an enemy's HP to an already-scaled figure.</summary>
    public static bool Bypass { get; set; }

    /// <summary>Powers whose amount is a quantity of the owner's HP.</summary>
    public static bool CountsHp(PowerModel power) =>
        power is HardenedShellPower or HardToKillPower or PlowPower or ShriekPower;

    public static decimal Scaled(decimal amount) => Scale is decimal scale ? Math.Round(amount * scale) : amount;

    private static bool Applies(Creature creature, decimal amount) =>
        Scale != null && !Bypass && creature.IsEnemy && amount > 0 && amount < Sentinel && CombatManager.Instance.IsInProgress;

    /// <summary>The fight's scale is known: an enemy's HP-counting powers and Block catch up with its HP.</summary>
    public static void Rescale(Creature enemy)
    {
        if (Scale is not decimal scale) return;
        foreach (PowerModel power in enemy.Powers.Where(CountsHp).ToList())
        {
            power.SetAmount((int)Math.Clamp(Math.Round(power.Amount * scale), 1, int.MaxValue));
            BridgeMod.LogInfo($"jimbo: {power.Id.Entry} on {enemy.Name} scaled to {power.Amount}");
        }
        if (enemy.Block > 0) enemy.GainBlockInternal(enemy.Block * (scale - 1));
    }

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.ApplyInternal))]
    private static class PowerApplied
    {
        private static void Prefix(PowerModel __instance, Creature owner, ref decimal amount)
        {
            if (!CountsHp(__instance) || !Applies(owner, Math.Abs(amount))) return;
            amount = Scaled(amount);
            BridgeMod.LogInfo($"jimbo: {__instance.Id.Entry} on {owner.Name} scaled to {amount}");
        }
    }

    [HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount))]
    private static class PowerChanged
    {
        private static void Prefix(PowerModel power, ref decimal offset)
        {
            if (CountsHp(power) && power.Owner is Creature owner && Applies(owner, Math.Abs(offset))) offset = Scaled(offset);
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.SetMaxHp))]
    private static class MaxHp
    {
        private static void Prefix(Creature creature, ref decimal amount)
        {
            if (Applies(creature, amount)) amount = Scaled(amount);
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.SetCurrentHp))]
    private static class CurrentHp
    {
        private static void Prefix(Creature creature, ref decimal amount)
        {
            if (Applies(creature, amount)) amount = Scaled(amount);
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
    private static class Heal
    {
        // Heals past full stop at full, so a "heal to full" scaled up still heals to full.
        private static void Prefix(Creature creature, ref decimal amount)
        {
            if (Applies(creature, amount)) amount = Scaled(amount);
        }
    }
}
