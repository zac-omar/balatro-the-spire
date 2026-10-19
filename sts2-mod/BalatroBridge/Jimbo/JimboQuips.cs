using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo talks, in his Balatro voice: in fights in StS2's speech bubbles (as a blind starts, at
/// each Boss Blind's twist, after a big hand, when a hand catches fire, when a status card lands
/// in his deck, when he's in trouble, when he wins) and in the shop on its sign. Some moments
/// always get a line, the rest now and then, so it stays fun to hear.
/// </summary>
internal static class JimboQuips
{
    public enum Moment
    {
        Start,
        Boss,
        BigHand,
        LowHp,
        Status,
        OnFire,
        Inferno,
        Overtime,
        Discard,
        Victory,
        ShopEnter,
        ShopBuy,
        ShopReroll,
        ShopSell,
    }

    private static readonly Dictionary<Moment, (string[] Lines, float Chance)> Quips = new()
    {
        [Moment.Start] = (["Shuffle up and deal!", "Ante up!", "Let's see what the deck's got.", "Cards on the table!", "Feeling lucky!", "Deal me in!", "Read 'em and weep, pal."], 0.6f),
        [Moment.Boss] = (["A Boss Blind? I've seen worse.", "Ooh, this one has rules.", "Rules were made to be broken!"], 1f),
        [Moment.BigHand] = (["Ka-ching!", "Read 'em and weep!", "Chips times mult, pal!", "Now [gold]that's[/gold] a hand!", "Did you see that?!", "Jackpot!", "That's gotta sting!"], 0.45f),
        [Moment.LowHp] = (["I need a better hand...", "Don't fold on me now...", "Just one good draw!", "Ow ow ow...", "I'm not out yet!"], 1f),
        [Moment.Status] = (["Hey, that's not my card!", "Who shuffled that in?", "That's cheating!", "Ugh, a dud.", "Get that out of my deck!"], 0.6f),
        [Moment.OnFire] = (["I'm on fire!", "Too hot to handle!", "Burn, baby, burn!", "Feel the heat!", "Smokin'!", "Hot hand, pal!",
            "Now we're cooking!", "Is it hot in here, or is it me?"], 1f),
        [Moment.Inferno] = (["Somebody call the fire department!", "Five-alarm fire!", "Stand back! WAY back!", "Hottest hand in the house!",
            "It's a bonfire, baby!", "The whole table's on fire!"], 1f),
        [Moment.Overtime] = (["We're in overtime!", "Time's up, pal!", "The house is getting impatient...", "Uh oh. They're getting angry.",
            "Gotta close this out!", "Running out of hands here!"], 1f),
        [Moment.Discard] = (["Not these.", "Next!", "Back in the deck with you.", "Safety first!", "Fold 'em."], 0.2f),
        [Moment.Victory] = (["Cash me out!", "Easy money!", "Pay up!", "Blind beaten!", "And that's the round!"], 0.6f),
        [Moment.ShopEnter] = (["Ooh, shiny!", "What's on sale?", "Window shopping!", "Money, money, money!", "Let's go shopping!"], 0.5f),
        [Moment.ShopBuy] = (["Sold!", "Ooh, I like this one.", "A fine addition!", "Worth every penny.", "Mine now!"], 0.5f),
        [Moment.ShopReroll] = (["Let's see what else.", "Shuffle the shelves!", "Again!", "Come on, come on..."], 0.4f),
        [Moment.ShopSell] = (["It was fun while it lasted.", "No refunds!", "Out with the old!"], 0.5f),
    };

    // Each Boss Blind's twist, in Jimbo's words (Balatro's blind keys).
    private static readonly Dictionary<string, string> BossLines = new()
    {
        ["bl_hook"] = "Hey! Give those cards back!",
        ["bl_ox"] = "Don't play my favourite hand? Rude.",
        ["bl_house"] = "First hand face down? I'll wing it!",
        ["bl_wall"] = "That's a BIG wall.",
        ["bl_wheel"] = "Some of these are face down... spooky.",
        ["bl_arm"] = "Lowering my hand levels? Not cool!",
        ["bl_club"] = "No clubs allowed? Fine by me.",
        ["bl_fish"] = "Face down after every hand? Fishy...",
        ["bl_psychic"] = "Five cards or nothing. Okay!",
        ["bl_goad"] = "No spades? I'll lead with my heart!",
        ["bl_water"] = "No discards! I'll keep my cards close.",
        ["bl_window"] = "Diamonds don't count? Ouch.",
        ["bl_manacle"] = "One card less in hand. Cozy.",
        ["bl_eye"] = "No repeats? Gotta mix it up!",
        ["bl_mouth"] = "One hand type only. Better pick a good one!",
        ["bl_plant"] = "Face cards debuffed. No royalty today.",
        ["bl_serpent"] = "Only three new cards? Sneaky snake.",
        ["bl_pillar"] = "Cards I already played won't count. Hmm.",
        ["bl_needle"] = "One hand. Make it count!",
        ["bl_head"] = "No hearts?! My poor heart!",
        ["bl_tooth"] = "A dollar a card? Pricey!",
        ["bl_flint"] = "Half the chips, half the mult. Halfway there!",
        ["bl_mark"] = "Face cards face down? Mysterious...",
        ["bl_final_acorn"] = "Shuffling my jokers? Hey!",
        ["bl_final_leaf"] = "Gotta sell a joker first? Ugh.",
        ["bl_final_vessel"] = "That is a LOT of chips.",
        ["bl_final_heart"] = "Which joker will it be...",
        ["bl_final_bell"] = "Forced to pick a card? Fine!",
    };

    private const string LiveKey = "JIMBO.quips.LIVE";

    // Some quiet between lines (a fight full of status cards would have him repeat himself); a
    // Boss Blind and real trouble always get theirs.
    private const ulong Cooldown = 6000;
    private static ulong _lastSaid;

    /// <summary>The fixed lines, as loc entries (merged into StS2's "characters" table by <see cref="ModText"/>).</summary>
    public static IEnumerable<KeyValuePair<string, string>> Lines =>
        Quips.SelectMany(q => q.Value.Lines.Select((line, i) => KeyValuePair.Create(Key(q.Key, i), line)));

    private static string Key(Moment moment, int index) => $"JIMBO.quips.{moment.ToString().ToUpperInvariant()}.{index}";

    /// <summary>In a fight: maybe says something fitting (always, for moments that call for it), after <paramref name="delay"/> seconds.</summary>
    public static void Maybe(Player player, Moment moment, double delay = 0, string? bossKey = null)
    {
        if (delay > 0)
        {
            ((SceneTree)Engine.GetMainLoop()).CreateTimer(delay).Timeout += () => Maybe(player, moment, 0, bossKey);
            return;
        }
        (string[] lines, float chance) = Quips[moment];
        if (GD.Randf() > chance || !player.Creature.IsAlive || (!CombatManager.Instance.IsInProgress && moment != Moment.Victory)) return;
        bool always = moment is Moment.Boss or Moment.LowHp or Moment.OnFire or Moment.Inferno or Moment.Overtime;
        if (!always && _lastSaid != 0 && Time.GetTicksMsec() - _lastSaid < Cooldown) return;
        // A Boss Blind gets its own line when Jimbo has one for it.
        if (moment == Moment.Boss && bossKey != null && BossLines.TryGetValue(bossKey, out string? line))
        {
            ModText.SetLive("characters", LiveKey, line);
            Say(player, new LocString("characters", LiveKey), line);
            return;
        }
        int pick = GD.RandRange(0, lines.Length - 1);
        Say(player, new LocString("characters", Key(moment, pick)), lines[pick], moment is Moment.OnFire or Moment.Inferno ? VfxColor.Orange : VfxColor.White);
    }

    private static void Say(Player player, LocString line, string text, VfxColor colour = VfxColor.White)
    {
        if (TalkCmd.Play(line, player.Creature, colour) == null) return;
        _lastSaid = Time.GetTicksMsec();
        JimboSounds.Say(text);
        BridgeMod.LogInfo($"jimbo says: {text}");
    }

    /// <summary>In the shop: maybe a line on its sign (there's no Jimbo on screen to speak from).</summary>
    public static void Shop(JimboShopScreen screen, Moment moment)
    {
        (string[] lines, float chance) = Quips[moment];
        if (GD.Randf() > chance) return;
        string line = lines[GD.RandRange(0, lines.Length - 1)];
        screen.SetStatus($"Jimbo: \"{line}\"", StsColors.cream);
        JimboSounds.Say(line);
    }
}
