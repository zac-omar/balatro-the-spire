using System.Collections.Generic;
using System.Linq;
using BalatroBridge.Content;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo joins StS2's character list, after the Defect, except where a random character is
/// rolled for a co-op or daily run (the Balatro engine behind him plays for one).
/// </summary>
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.AllCharacters), MethodType.Getter)]
internal static class JimboInCharacterList
{
    [System.ThreadStatic]
    internal static bool Excluded;

    private static void Postfix(ref IEnumerable<CharacterModel> __result)
    {
        if (Excluded) __result = __result.Where(c => c is not Jimbo).ToList();
        else if (!__result.OfType<Jimbo>().Any()) __result = __result.Append(ModelDb.Character<Jimbo>()).ToList();
    }
}

[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally")]
internal static class NoRandomJimboInCoOp
{
    private static void Prefix(StartRunLobby __instance) => JimboInCharacterList.Excluded = __instance.NetService.Type.IsMultiplayer();

    private static void Finalizer() => JimboInCharacterList.Excluded = false;
}

[HarmonyPatch(typeof(NDailyRunScreen), "SetupLobbyParams")]
internal static class NoJimboInDailies
{
    private static void Prefix() => JimboInCharacterList.Excluded = true;

    private static void Finalizer() => JimboInCharacterList.Excluded = false;
}

/// <summary>Co-op lobbies don't offer Jimbo.</summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "AfterInitialized")]
internal static class NoJimboInCoOpLobbies
{
    private static readonly AccessTools.FieldRef<NCharacterSelectScreen, StartRunLobby>? Lobby = Field<StartRunLobby>("_lobby");
    private static readonly AccessTools.FieldRef<NCharacterSelectScreen, Godot.Control>? Buttons = Field<Godot.Control>("_charButtonContainer");

    private static void Postfix(NCharacterSelectScreen __instance)
    {
        if (Lobby == null || Buttons == null) return;
        bool coOp = Lobby(__instance).NetService.Type.IsMultiplayer();
        foreach (NCharacterSelectButton button in Buttons(__instance).GetChildren().OfType<NCharacterSelectButton>())
        {
            if (button.Name == "JIMBO_button") button.Visible = !coOp;
        }
    }

    private static AccessTools.FieldRef<NCharacterSelectScreen, T>? Field<T>(string name)
    {
        try
        {
            return AccessTools.FieldRefAccess<NCharacterSelectScreen, T>(name);
        }
        catch (System.Exception e)
        {
            BridgeMod.LogError($"jimbo: character select's {name} not found ({e.Message}); co-op lobbies may show Jimbo");
            return null;
        }
    }
}

/// <summary>
/// Jimbo's starting stake follows the ascension level: StS2 creates players before it knows the
/// ascension, so the White Stake he starts with is swapped once the run exists. His potion belt
/// starts at Balatro's two consumable slots.
/// </summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
internal static class JimboStartingStake
{
    private static void Postfix(RunState __result, int ascensionLevel)
    {
        BalatroItem stake = BalatroContent.StakeFor(ascensionLevel);
        foreach (Player player in __result.Players.Where(JimboRun.IsJimbo))
        {
            RelicModel? white = player.Relics.OfType<StakeRelic>().FirstOrDefault();
            if (white == null || white.GetType() == stake.Model) continue;
            int index = player.Relics.ToList().IndexOf(white);
            player.RemoveRelicInternal(white, silent: true);
            RelicModel relic = ModelDb.GetById<RelicModel>(ModelDb.GetId(stake.Model)).ToMutable();
            relic.FloorAddedToDeck = 1;
            player.AddRelicInternal(relic, index, silent: true);
        }
        // His potion belt is Balatro's consumable slots, from the start.
        foreach (Player player in __result.Players.Where(JimboRun.IsJimbo))
        {
            if (player.MaxPotionCount > JimboConsumables.StartingSlots) player.SubtractFromMaxPotionCount(player.MaxPotionCount - JimboConsumables.StartingSlots);
        }
    }
}

/// <summary>
/// StS2's ascension also changes the player (a potion slot less, Ascender's Bane in the deck),
/// which means nothing for a Balatro deck and Balatro's consumable slots: Jimbo's stake is his
/// handicap instead. The ascension's effects on enemies, healing and the like still apply.
/// </summary>
[HarmonyPatch(typeof(AscensionManager), nameof(AscensionManager.ApplyEffectsTo))]
internal static class JimboIgnoresPlayerAscension
{
    private static bool Prefix(Player player) => !JimboRun.IsJimbo(player);
}

/// <summary>
/// StS2's timeline unlocks epochs per character for each act's first boss win and for 15 elite
/// and 15 boss wins, and has none for a fifth character: those checks would throw on Jimbo's wins
/// and cut the end of the fight short. Jimbo simply has no such epochs.
/// </summary>
[HarmonyPatch]
internal static class JimboHasNoEpochs
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ProgressSaveManager), "CheckFifteenElitesDefeatedEpoch");
        yield return AccessTools.Method(typeof(ProgressSaveManager), "CheckFifteenBossesDefeatedEpoch");
        yield return AccessTools.Method(typeof(ProgressSaveManager), "ObtainCharUnlockEpoch");
    }

    private static bool Prefix(Player localPlayer) => !JimboRun.IsJimbo(localPlayer);
}

/// <summary>
/// Jimbo's rooms: the act's Ancient is the Dealer, and its shops are Balatro's shop. Fights are
/// StS2's own encounters, played as Balatro blinds (see <see cref="JimboCombat"/>).
/// </summary>
[HarmonyPatch(typeof(RunManager), "CreateRoom")]
internal static class JimboRooms
{
    private static bool Prefix(RunManager __instance, RoomType roomType, MapPointType mapPointType, AbstractModel? model, ref AbstractRoom __result)
    {
        if (model != null || !JimboRun.IsJimboRun(__instance.DebugOnlyGetState())) return true;
        if (roomType == RoomType.Shop)
        {
            __result = new EventRoom(ModelDb.Event<BalatroShop>());
            return false;
        }
        if (roomType == RoomType.Event && mapPointType == MapPointType.Ancient)
        {
            __result = new EventRoom(ModelDb.Event<BalatroDealer>());
            return false;
        }
        if (roomType == RoomType.Treasure)
        {
            // A blind Jimbo skipped: its node holds the tag he took for it.
            __result = new EventRoom(ModelDb.Event<BalatroTag>());
            return false;
        }
        return true;
    }
}

/// <summary>Jimbo's map has no rest sites or unknown rooms, so its legend doesn't list them.</summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen), nameof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.SetMap))]
internal static class JimboMapLegend
{
    private static readonly string[] NotOnJimbosMap = ["UnknownLegendItem", "RestSiteLegendItem"];

    private static void Postfix(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen __instance)
    {
        bool jimbo = JimboRun.IsJimboRun(RunManager.Instance.DebugOnlyGetState());
        var stack = new Stack<Godot.Node>();
        stack.Push(__instance);
        while (stack.Count > 0)
        {
            Godot.Node node = stack.Pop();
            if (node is MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapLegendItem item && NotOnJimbosMap.Contains((string)item.Name)) item.Visible = !jimbo;
            foreach (Godot.Node child in node.GetChildren()) stack.Push(child);
        }
    }
}

/// <summary>
/// The run's end badges that judge StS2's deck (Highlander, Tiny Deck...) say nothing about
/// Jimbo, whose deck is Balatro's: an empty StS2 deck would win him Tiny Deck every time.
/// </summary>
[HarmonyPatch(typeof(BadgePool), nameof(BadgePool.CreateAll))]
internal static class JimboBadges
{
    private static readonly HashSet<System.Type> DeckBadges =
        [typeof(Highlander), typeof(TinyDeck), typeof(BigDeck), typeof(Honed), typeof(Curses)];

    private static void Postfix(SerializableRun run, ulong playerId, ref IReadOnlyCollection<Badge> __result)
    {
        SerializablePlayer? player = run.Players.FirstOrDefault(p => p.NetId == playerId);
        if (player?.CharacterId != ModelDb.GetId<Jimbo>()) return;
        __result = __result.Where(b => !DeckBadges.Contains(b.GetType())).ToList();
    }
}
