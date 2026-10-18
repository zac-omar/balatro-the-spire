using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BalatroBridge.Jimbo;

/// <summary>
/// Jimbo's shop nodes: Balatro's shop, opened on the card table as soon as he walks in. Leaving
/// it (Next Round) takes him straight back to the map. If Balatro can't be reached, the room
/// offers to try again rather than strand him.
/// </summary>
public sealed class BalatroShop : EventModel
{
    private const string Key = "BALATRO_SHOP";

    private bool _open;

    protected override IReadOnlyList<EventOption> GenerateInitialOptions() =>
        [new EventOption(this, Enter, $"{Key}.pages.INITIAL.options.ENTER")];

    public override Task AfterEventStarted()
    {
        if (!IsFinished) TaskHelper.RunSafely(Enter());
        return Task.CompletedTask;
    }

    private async Task Enter()
    {
        if (_open || Owner == null) return;
        _open = true;
        try
        {
            await JimboTable.VisitShop(Owner);
            SetEventFinished(L10NLookup($"{Key}.pages.LEFT.description"));
            await NEventRoom.Proceed();
        }
        catch (Exception e)
        {
            BridgeMod.LogError($"the Balatro shop failed: {e}");
            SetEventState(L10NLookup($"{Key}.pages.TROUBLE.description"),
                [new EventOption(this, Enter, $"{Key}.pages.TROUBLE.options.RETRY"), new EventOption(this, Leave, $"{Key}.pages.TROUBLE.options.LEAVE")]);
        }
        finally
        {
            _open = false;
        }
    }

    private async Task Leave()
    {
        SetEventFinished(L10NLookup($"{Key}.pages.LEFT.description"));
        await NEventRoom.Proceed();
    }
}
