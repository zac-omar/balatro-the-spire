using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BalatroBridge.Content;
using BalatroBridge.Core;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Skipping a blind, as in Balatro: from the shop, Jimbo can pass on the next Small or Big Blind
/// for its tag. The blind's map node becomes a chest holding the tag, with no fight and no
/// cash-out behind it; the shop after it still opens.
/// </summary>
internal static class JimboSkips
{
    private static readonly MethodInfo? UpdateIcon = AccessTools.Method(typeof(NNormalMapPoint), "UpdateIcon");

    /// <summary>Turns the next blind's node into the tag's node (and remembers the tag for it).</summary>
    public static void MarkSkipped(Player player, TagInfo? tag)
    {
        if (player.RunState is not RunState state || state.CurrentMapCoord is not MapCoord coord) return;
        MapPoint? blind = state.Map.GetPoint(coord)?.Children.FirstOrDefault(p => p.PointType == MapPointType.Monster);
        if (blind == null) return;
        blind.PointType = MapPointType.Treasure;
        if (player.Relics.OfType<StakeRelic>().FirstOrDefault() is StakeRelic stake) stake.SkippedTag = tag?.Key ?? "";
        try
        {
            foreach (NNormalMapPoint node in FindPoints(NMapScreen.Instance))
            {
                if (node.Point == blind) UpdateIcon?.Invoke(node, null);
            }
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"jimbo: couldn't redraw the skipped blind's map node: {e.Message}");
        }
        BridgeMod.LogInfo($"jimbo: skipped the blind at row {blind.coord.row} for {tag?.Name}");
    }

    private static IEnumerable<NNormalMapPoint> FindPoints(Node? root)
    {
        if (root == null) yield break;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            if (node is NNormalMapPoint point) yield return point;
            foreach (Node child in node.GetChildren()) stack.Push(child);
        }
    }
}

/// <summary>The node of a skipped blind: the tag Jimbo took for it.</summary>
public sealed class BalatroTag : EventModel
{
    private const string Key = "BALATRO_TAG";

    protected override IReadOnlyList<EventOption> GenerateInitialOptions() =>
        [new EventOption(this, Onward, $"{Key}.pages.INITIAL.options.ONWARD")];

    // The page names the tag and quotes Balatro's text for it, written into the loc table as
    // it's shown (the tag differs from one skip to the next).
    public override LocString InitialDescription
    {
        get
        {
            string? tag = Owner?.Relics.OfType<StakeRelic>().FirstOrDefault()?.SkippedTag;
            string text = new LocString(LocTable, $"{Key}.pages.INITIAL.description").GetRawText()
                .Replace("{Tag}", BalatroCatalog.Name(tag) ?? "tag")
                .Replace("{Text}", BalatroCatalog.Text(tag) ?? "");
            LocManager.Instance?.GetTable(LocTable).MergeWith(new Dictionary<string, string> { [$"{Key}.pages.TAKEN.description"] = text });
            return L10NLookup($"{Key}.pages.TAKEN.description");
        }
    }

    private async Task Onward()
    {
        SetEventFinished(L10NLookup($"{Key}.pages.DONE.description"));
        await NEventRoom.Proceed();
    }
}
