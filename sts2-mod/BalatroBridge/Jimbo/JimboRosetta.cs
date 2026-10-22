using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BalatroBridge.Content;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace BalatroBridge.Jimbo;

/// <summary>
/// The Rosetta Stone: StS2's numbers in Balatro's terms, so what StS2 does to Jimbo means
/// something at the card table (bridge/jimbo.lua adjust_turn and its evaluate_play).
/// <list type="bullet">
/// <item>Strength is +Mult on every hand, and Vigor +Chips on the next hand (then it's used up).
/// Both join the hand's base, so jokers' xMult multiplies them; Strength no longer adds to the
/// strikes themselves.</item>
/// <item>What StS2 would change about a normal turn's energy (3) and draw (5) is this turn's hands
/// and discards: Waste Away costs a hand, Mind Rot a discard, Demesne gives a hand. A turn gains at
/// most one hand and always keeps one.</item>
/// <item>Each card-afflicting debuff on Jimbo (Tangled, Hex, Ringing, Smoggy) debuffs a card in his
/// hand for the turn, as a Balatro Boss Blind would: it doesn't score.</item>
/// </list>
/// </summary>
internal static class JimboRosetta
{
    private const decimal NormalEnergy = 3;
    private const decimal NormalDraw = 5;

    private static readonly Type[] Afflicting = [typeof(TangledPower), typeof(HexPower), typeof(RingingPower), typeof(SmoggyPower)];

    /// <summary>What this turn gains or loses, for jimbo_turn, and a line saying so (null if nothing).</summary>
    public static (JsonObject Data, string? Said) Turn(Player player)
    {
        var data = new JsonObject();
        if (player.Creature.CombatState is not CombatState state) return (data, null);
        // StS2's own modifiers against a normal turn; the mod's relics are left out, since they
        // zero Jimbo's energy and draw (his hand is Balatro's).
        List<AbstractModel> listeners = state.IterateHookListeners().Where(m => m is not BalatroRelic).ToList();
        decimal energy = NormalEnergy, draw = NormalDraw;
        foreach (AbstractModel model in listeners)
        {
            energy = model.ModifyMaxEnergy(player, energy);
            draw = model.ModifyHandDraw(player, draw);
        }
        foreach (AbstractModel model in listeners) draw = model.ModifyHandDrawLate(player, draw);
        int hands = (int)(energy - NormalEnergy);
        int discards = (int)(draw - NormalDraw);
        int afflicted = player.Creature.Powers.Count(p => Array.IndexOf(Afflicting, p.GetType()) >= 0);
        if (hands != 0) data["hands"] = hands;
        if (discards != 0) data["discards"] = discards;
        if (afflicted > 0) data["afflict"] = afflicted;

        var said = new List<string>();
        if (hands > 0) said.Add("+1 hand");
        if (hands < 0) said.Add(Count(hands, "hand"));
        if (discards != 0) said.Add(Count(discards, "discard"));
        if (afflicted > 0) said.Add($"{afflicted} card{(afflicted == 1 ? "" : "s")} debuffed");
        (int strength, int vigor) = Bonus(player);
        if (strength != 0) said.Add($"{(strength > 0 ? "+" : "")}{strength} Mult (Strength)");
        if (vigor > 0) said.Add($"+{vigor} Chips next hand (Vigor)");
        return (data, said.Count == 0 ? null : "This turn: " + string.Join(", ", said));
    }

    private static string Count(int n, string what) => $"{(n > 0 ? "+" : "")}{n} {what}{(Math.Abs(n) == 1 ? "" : "s")}";

    /// <summary>The Mult (Strength) and Chips (Vigor) a hand played now gets.</summary>
    public static (int Mult, int Chips) Bonus(Player player) =>
        (player.Creature.GetPowerAmount<StrengthPower>(), Math.Max(0, player.Creature.GetPowerAmount<VigorPower>()));

    /// <summary>A hand was played: Vigor is spent, as StS2 spends it on an attack.</summary>
    public static async Task Spent(Player player)
    {
        if (player.Creature.GetPower<VigorPower>() is VigorPower vigor) await PowerCmd.Remove(vigor);
    }
}

/// <summary>Jimbo's Strength is Mult on his hands (see <see cref="JimboRosetta"/>), so it adds nothing to his strikes.</summary>
[HarmonyPatch(typeof(StrengthPower), nameof(StrengthPower.ModifyDamageAdditive))]
internal static class JimboStrengthIsMult
{
    private static bool Prefix(StrengthPower __instance, Creature? dealer, ValueProp props, ref decimal __result)
    {
        if (dealer == null || __instance.Owner != dealer || dealer.Player is not Player player || !JimboRun.IsJimbo(player)) return true;
        __result = 0m;
        return false;
    }
}
