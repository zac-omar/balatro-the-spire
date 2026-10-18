using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ancients;
using MegaCrit.Sts2.Core.Models.Events;

namespace BalatroBridge.Jimbo;

/// <summary>
/// At the top of the Spire the Architect has words for every character, Jimbo included. These
/// are his three conversations (one per win so far, the last repeating), in the Architect's
/// dialogue format: "THE_ARCHITECT.talk.JIMBO.{dialogue}-{line}r.{ancient|char|next}".
/// </summary>
[HarmonyPatch(typeof(TheArchitect), "DefineDialogues")]
internal static class JimboArchitect
{
    public static readonly Dictionary<string, string> Lines = new()
    {
        ["THE_ARCHITECT.talk.JIMBO.0-0r.ancient"] = "A jester? In my Spire? Who dealt you in?",
        ["THE_ARCHITECT.talk.JIMBO.0-0r.next"] = "Grin",
        ["THE_ARCHITECT.talk.JIMBO.0-1r.char"] = "The Dealer did! And I've got one more hand to play.",
        ["THE_ARCHITECT.talk.JIMBO.0-1r.next"] = "Shuffle",
        ["THE_ARCHITECT.talk.JIMBO.0-2r.ancient"] = "Then play it, little card. Let us see your score.",

        ["THE_ARCHITECT.talk.JIMBO.1-0r.ancient"] = "The jester returns. Your jokers grow louder every climb.",
        ["THE_ARCHITECT.talk.JIMBO.1-0r.next"] = "Laugh",
        ["THE_ARCHITECT.talk.JIMBO.1-1r.char"] = "Mult is a state of mind, pal!",
        ["THE_ARCHITECT.talk.JIMBO.1-1r.next"] = "Continue",
        ["THE_ARCHITECT.talk.JIMBO.1-2r.ancient"] = "Your chips are counted. Your hand is mine.",

        ["THE_ARCHITECT.talk.JIMBO.2-0r.ancient"] = "Again, Jimbo? The house always wins.",
        ["THE_ARCHITECT.talk.JIMBO.2-0r.next"] = "Wink",
        ["THE_ARCHITECT.talk.JIMBO.2-1r.char"] = "Funny thing about houses... I'm holding a full one!",
        ["THE_ARCHITECT.talk.JIMBO.2-1r.next"] = "Continue",
        ["THE_ARCHITECT.talk.JIMBO.2-2r.ancient"] = "Then show me.",
    };

    private static void Postfix(AncientDialogueSet __result)
    {
        __result.CharacterDialogues["JIMBO"] =
        [
            new AncientDialogue("", "", "") { VisitIndex = 0, EndAttackers = ArchitectAttackers.Both },
            new AncientDialogue("", "", "") { VisitIndex = 1, EndAttackers = ArchitectAttackers.Both },
            new AncientDialogue("", "", "") { VisitIndex = 2, EndAttackers = ArchitectAttackers.Both },
        ];
    }
}
