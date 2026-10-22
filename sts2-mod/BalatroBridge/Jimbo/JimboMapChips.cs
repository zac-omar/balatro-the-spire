using System.Collections.Generic;
using System.Reflection;
using BalatroBridge.Content;
using BalatroBridge.Core;
using BalatroBridge.Table;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's map shows Balatro's blind chips: Small and Big Blinds as their chips, and the
/// current ante's Boss Blind as the very boss Balatro has picked (later antes' bosses stay a
/// mystery, as in Balatro). Hovering a blind tells its name, ante, HP and reward, and what a Boss
/// Blind does, as Balatro's blind select would.
/// </summary>
[HarmonyPatch(typeof(NNormalMapPoint), "UpdateIcon")]
internal static class JimboMapChips
{
    private const string TipTitleKey = "JIMBO.mapTip.title";
    private const int Cell = 68;     // a chip in BlindChips.png at 2x
    private const int Canvas = 112;  // StS2's map icons are drawn this much bigger than their art

    private static readonly FieldInfo? IconField = AccessTools.Field(typeof(NNormalMapPoint), "_icon");
    private static readonly FieldInfo? OutlineField = AccessTools.Field(typeof(NNormalMapPoint), "_outline");
    private static readonly MethodInfo? UpdateIcon = AccessTools.Method(typeof(NNormalMapPoint), "UpdateIcon");
    private static readonly Dictionary<int, (Texture2D Chip, Texture2D Outline)> Chips = new();
    private static Shader? _plain;
    private static string _shown = "";

    // StS2 inks its map icons with a shader; chips keep their colours (its parameters stay, unused).
    private static Shader Plain => _plain ??= new Shader
    {
        Code = """
            shader_type canvas_item;
            uniform vec4 initial_color : source_color;
            uniform vec4 map_color : source_color;
            void fragment() { } // COLOR is already the texture times the modulate
            """,
    };

    private static void Postfix(NNormalMapPoint __instance)
    {
        if (IconField?.GetValue(__instance) is not TextureRect icon) return;
        if (Blind(__instance) is (NextBlind blind, _, _) && blind.Sprite is SpriteRef sprite && Chip(sprite.Y) is var (chip, outline))
        {
            icon.Texture = chip;
            if (!icon.HasMeta(InkMeta) && icon.Material is ShaderMaterial ink)
            {
                var plain = new ShaderMaterial { Shader = Plain };
                plain.SetShaderParameter("map_color", ink.GetShaderParameter("map_color"));
                icon.SetMeta(InkMeta, ink);
                icon.Material = plain;
            }
            if (OutlineField?.GetValue(__instance) is TextureRect edge) edge.Texture = outline;
        }
        else if (icon.HasMeta(InkMeta) && icon.GetMeta(InkMeta).AsGodotObject() is ShaderMaterial ink)
        {
            // No longer a blind (skipped for a tag): StS2's own ink icon again.
            if (icon.Material is ShaderMaterial plain) ink.SetShaderParameter("map_color", plain.GetShaderParameter("map_color"));
            icon.Material = ink;
            icon.RemoveMeta(InkMeta);
        }
    }

    private const string InkMeta = "balatro_ink";

    /// <summary>
    /// The blind at a map point (with its ante, and whether it's this ante's) or null for points
    /// that aren't blinds, aren't known yet, or were skipped.
    /// </summary>
    private static (NextBlind Blind, int Ante, bool Current)? Blind(NNormalMapPoint point)
    {
        IRunState? state = RunManager.Instance.DebugOnlyGetState();
        if (!JimboRun.IsJimboRun(state) || point.Point.PointType is not (MapPointType.Monster or MapPointType.Elite)) return null;
        if (JimboRun.Last?.Road is not RoadInfo road || JimboActMap.BlindAt(state!.CurrentActIndex, point.Point.coord.row) is not var (ante, kind)) return null;
        if (ante == road.Ante)
        {
            NextBlind? now = kind switch { "Small" => road.Small, "Big" => road.Big, _ => road.Boss };
            return now == null ? null : (now, ante, true);
        }
        // Another ante: Small and Big Blinds are the same chips at that ante's size; its Boss Blind
        // isn't chosen until the ante begins.
        if (kind == "Boss" || ante < 1 || ante > road.Antes.Count) return null;
        NextBlind? like = kind == "Small" ? road.Small : road.Big;
        if (like == null) return null;
        return (like with { Chips = road.Antes[ante - 1] * (kind == "Small" ? 1 : 1.5) }, ante, false);
    }

    // The chip art (first frame of its row) on a map-icon canvas, and its white outline.
    private static (Texture2D, Texture2D)? Chip(int row)
    {
        if (Chips.TryGetValue(row, out var made)) return made;
        if (BalatroSheets.Get("resources/textures/2x/BlindChips.png") is not Image sheet || (row + 1) * Cell > sheet.GetHeight()) return null;
        Image art = sheet.GetRegion(new Rect2I(0, row * Cell, Cell, Cell));
        art.Convert(Image.Format.Rgba8);
        Image canvas = Image.CreateEmpty(Canvas, Canvas, false, Image.Format.Rgba8);
        canvas.BlitRect(art, new Rect2I(0, 0, Cell, Cell), new Vector2I((Canvas - Cell) / 2, (Canvas - Cell) / 2));
        Image edge = BalatroIcons.Silhouette(canvas, radius: 5);
        made = (ImageTexture.CreateFromImage(canvas), ImageTexture.CreateFromImage(edge));
        Chips[row] = made;
        return made;
    }

    /// <summary>After each snapshot: when the road ahead changes (a new ante, its boss), the map's chips follow.</summary>
    public static void Refresh(TableSnapshot table)
    {
        RoadInfo? road = table.Road;
        string shown = road == null ? "" : $"{road.Ante}:{road.Boss?.Key}:{road.Small?.Chips}";
        if (shown == _shown) return;
        _shown = shown;
        if (NMapScreen.Instance is not NMapScreen map || UpdateIcon == null) return;
        var stack = new Stack<Node>();
        stack.Push(map);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            if (node is NNormalMapPoint point) UpdateIcon.Invoke(point, null);
            foreach (Node child in node.GetChildren()) stack.Push(child);
        }
    }

    [HarmonyPatch(typeof(NNormalMapPoint), "OnFocus")]
    private static class Tip
    {
        private static void Postfix(NNormalMapPoint __instance)
        {
            if (__instance.State == MapPointState.Traveled || Blind(__instance) is not var (blind, ante, current)) return;
            var lines = new List<string>
            {
                $"Ante {ante}  ·  [red]{TableText.Number(blind.Chips ?? 0)} HP[/red]  ·  [gold]${TableText.Number(blind.Dollars ?? 0)}[/gold] reward",
            };
            if (blind.Boss && current && !string.IsNullOrEmpty(blind.Text)) lines.Add(blind.Text);
            if (!blind.Boss) lines.Add("[color=#9aa0a6]From the shop before it, you can skip it for a tag.[/color]");
            // The Wager: Boss Blinds are raised instead of skipped.
            if (blind.Boss && current && blind.Raised) lines.Add("[red]Raised[/red]: beat it for a [gold]Rare Tag[/gold].");
            else if (blind.Boss && current && ante >= 2) lines.Add("[color=#9aa0a6]From the shop before it, you can raise it for a Rare Tag.[/color]");
            ModText.SetLive("characters", TipTitleKey, blind.Name);
            Texture2D? icon = blind.Sprite is SpriteRef sprite && Chip(sprite.Y) is var (chip, _) ? chip : null;
            NHoverTipSet.CreateAndShow(__instance, new HoverTip(new LocString("characters", TipTitleKey), string.Join("\n", lines), icon), HoverTipAlignment.Right);
        }
    }

    // An Elite point whose Boss Blind isn't known yet: say so when hovered.
    [HarmonyPatch(typeof(NNormalMapPoint), "OnFocus")]
    private static class MysteryTip
    {
        private static void Postfix(NNormalMapPoint __instance)
        {
            IRunState? state = RunManager.Instance.DebugOnlyGetState();
            if (!JimboRun.IsJimboRun(state) || __instance.State == MapPointState.Traveled || __instance.Point.PointType != MapPointType.Elite) return;
            if (Blind(__instance) != null || JimboActMap.BlindAt(state!.CurrentActIndex, __instance.Point.coord.row) is not var (ante, _)) return;
            ModText.SetLive("characters", TipTitleKey, "Boss Blind");
            NHoverTipSet.CreateAndShow(__instance, new HoverTip(new LocString("characters", TipTitleKey),
                $"Ante {ante}. Revealed when the ante begins.", null), HoverTipAlignment.Right);
        }
    }
}
