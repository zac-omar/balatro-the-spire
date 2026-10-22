using BalatroBridge.Content;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace BalatroBridge.Jimbo;

/// <summary>
/// A joker whose number changes shows it on its relic's counter as Balatro writes it ("+7",
/// "X2.2", "$4", "0/2"), where StS2's counters only show whole numbers, and the number pops when
/// it changes, as Balatro's jokers do when they grow.
/// </summary>
[HarmonyPatch(typeof(NRelicInventoryHolder), "RefreshAmount")]
internal static class JimboRelicCounter
{
    private static readonly AccessTools.FieldRef<NRelicInventoryHolder, MegaLabel> AmountLabel =
        AccessTools.FieldRefAccess<NRelicInventoryHolder, MegaLabel>("_amountLabel");

    private static readonly StringName Shown = "balatro_live";

    private static void Postfix(NRelicInventoryHolder __instance)
    {
        if (__instance.Relic?.Model is not JokerRelic { LiveValue: string value }) return;
        MegaLabel label = AmountLabel(__instance);
        if (!GodotObject.IsInstanceValid(label) || !label.Visible) return;
        label.SetTextAutoSize(value);
        string before = label.HasMeta(Shown) ? label.GetMeta(Shown).AsString() : "";
        label.SetMeta(Shown, value);
        if (before == "" || before == value) return;
        label.PivotOffset = label.Size / 2;
        Tween pop = label.CreateTween();
        pop.TweenProperty(label, "scale", new Vector2(1.45f, 1.45f), 0.08);
        pop.TweenProperty(label, "scale", Vector2.One, 0.2).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }
}
