using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

internal static class PreparedSpellLabChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var lab = new ExperienceLabSession(PreparedSpellScenario.Id);
        Card Source() => lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == PreparedSpellScenario.SourceId);
        SourceActionSet Offer() => lab.SourceActions(lab.State.Id, lab.State.Decision!.Id, Source().Key);
        void Cast() {
            var offered = Offer();
            lab.Reply(new UseOfferedAction(lab.State.Id, lab.State.Decision!.Id, Source().Key, offered.Actions.Single().Id));
        }
        var initial = lab.State;
        var offer = Offer();
        check(lab.State == initial && offer.Actions.Single().Card is { Name: "Ancestral Recall", Key.Length: 0, Selectable: false }
                && offer.Actions.Single().Zone == "Exile",
            "Canonical prepared-spell reading supplies the visible linked spell without committing or giving its preview action authority");
        var opponent = lab.State.Players[1].Zone("Battlefield").Cards.Single(card => card.Name == "Emeritus of Ideation");
        check(lab.SourceActions(lab.State.Id, lab.State.Decision!.Id, opponent.Key).Actions.Length == 0
                && opponent.States!.Value.Any(state => state.Key == "prepared"),
            "Canonical opponent prepared state remains public without offering the local player its cast");
        Cast();
        check(lab.StageIndex == 1 && lab.State.Decision is { Context.Source.Card.Name: "Ancestral Recall", Payload: TargetSelectionPayload },
            "A prepared cast reaches the authored spell's required player target rather than passing or moving the source creature");
        lab.Reply(new CancelSelection(lab.State.Id, lab.State.Decision!.Id));
        check(lab.StageIndex == 0 && Source().States!.Value.Any(state => state.Key == "prepared"),
            "Cancelling authored targeting retains the prepared source and its exiled spell");
        Cast();
        lab.Reply(new SelectPlayer(lab.State.Id, lab.State.Decision!.Id, 2));
        check(lab.State.Stack.Single().Name == "Ancestral Recall" && lab.State.Stack.Single().Targets.Single().PlayerId == 2
                && !Source().States!.Value.Any(state => state.Key == "prepared") && lab.State.Viewer!.Zone("Exile").Cards.Length == 0,
            "The authored pending spell retains the chosen recipient while its source remains in play, unprepared");
        lab.Reply(new PassPriority(lab.State.Id, lab.State.Decision!.Id));
        check(lab.StageIndex == 3 && lab.State.Decision == null && lab.State.PriorityPlayerId == 1,
            "The authored spell still hands priority to a different responder before its result");
        lab.AdvanceTo(4);
        check(lab.State.Players[2].Zone("Hand").Count == 4 && lab.State.Viewer!.Zone("Hand").Count == 1,
            "The canonical draw result belongs to the selected player, with other private hand faces absent");
        lab.AdvanceTo(5);
        check(Offer().Actions.Single().Card!.VisualId != offer.Actions.Single().Card!.VisualId,
            "Preparing again authors a new linked spell occurrence rather than reviving the spent preview");
        bool staleRejected = false;
        try { lab.Reply(new UseOfferedAction(initial.Id, initial.Decision!.Id, offer.SourceCardKey, offer.Actions.Single().Id)); }
        catch (EngineCommandRejectedException) { staleRejected = true; }
        check(staleRejected, "A prepared-spell lab replay never accepts the old response scope");
    }
}
