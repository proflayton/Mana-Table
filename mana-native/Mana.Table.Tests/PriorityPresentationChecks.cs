using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal static class PriorityPresentationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var source = PresentationSmoke.Example();
        var target = new StackTarget("player", "0", "You", 0);
        var shared = new Card { VisualId = "same-public-source", Name = "Toothy, Imaginary Friend" };
        var initial = source with {
            Id = "priority-presentation", Revision = 10, ActivePlayerId = 2, PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Stack = [new("Draw cards", "Draw cards equal to the reported counter count.", true, shared) { Id = "ability-a", ControllerId = 1, Targets = [target] },
                new("Partner with Pir", "Target player may search their library.", true, shared) { Id = "ability-b", ControllerId = 1 },
                new("Counterspell", "Counter target spell.", false, null) { Id = "spell-c", ControllerId = 2 },
                new("Lightning Bolt", "Deal 3 damage to the chosen target.", false, null) { Id = "spell-d", ControllerId = 3 }],
            Decision = new Decision("priority-10", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: []))
        };
        bool rotation = true;
        foreach (int responder in Enumerable.Range(0, 4)) {
            var state = initial with { PriorityPlayerId = responder, DecisionOwnerId = responder,
                Decision = responder == 0 ? initial.Decision : null };
            var view = PriorityPresentation.From(state);
            rotation &= view.ResponderId == responder && view.TurnOwnerId == 2 && view.Phase == PriorityPhase.Response
                && view.CanPass == (responder == 0) && view.IsLocalResponse == (responder == 0);
        }
        check(rotation, "Four-player priority rotation names only the reported current responder while keeping the active turn owner separate");

        var privateOpponent = PriorityPresentation.From(initial with { Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 2, ActionClass = "mana" });
        check(privateOpponent is { Phase: PriorityPhase.Payment, DecisionOwnerId: 1, ResponderId: null, CanPass: false }
            && privateOpponent.Heading.Contains(initial.Players.Single(player => player.Id == 1).Name)
            && !privateOpponent.Instruction.Contains("Draw cards", StringComparison.Ordinal),
            "Another player's public payment role does not become a response window or copy their private prompt");

        var legacy = initial with { PriorityPlayerId = null, DecisionOwnerId = null, ActionClass = "",
            Players = initial.Players.Select(player => player with { Priority = false }).ToImmutableArray() };
        check(PriorityPresentation.From(legacy).CanPass
            && !PriorityPresentation.From(initial with { PriorityPlayerId = 1 }).CanPass
            && !PriorityPresentation.From(initial with { Decision = initial.Decision! with {
                Payload = ((PriorityPayload)initial.Decision.Payload) with { Pass = new("Confirm", false) } } }).CanPass
            && !PriorityPresentation.From(initial with { ActionClass = "choice" }).CanPass,
            "Passing requires an enabled local priority input and matching reported seat, with an explicit legacy fallback and no pass during a choice");

        var plain = PriorityPresentation.From(initial);
        var optional = PriorityPresentation.From(initial with { Decision = initial.Decision! with { Payload = ((PriorityPayload)(initial.Decision!).Payload) with { Abilities = [new("mesa-ability", "mesa-key", "Arid Mesa", "Pay 1 life, sacrifice: search for a land.", false)] } } });
        check(plain.Heading == optional.Heading && plain.Instruction == optional.Instruction && optional.NextToResolve?.Id == "ability-a"
            && !optional.Instruction.Contains("Arid Mesa", StringComparison.Ordinal),
            "One optional activation cannot replace the current pending effect and response-window context");

        var entries = plain.Stack;
        initial = initial with { Stack = initial.Stack.SetItem(0, initial.Stack[0] with {
            Targets = initial.Stack[0].Targets.SetItem(0, new("player", "3", "Changed later revision", 3)) }) };
        check(entries.Count == 4 && entries.Select(entry => entry.Position).SequenceEqual([1, 2, 3, 4])
            && entries[0].IsNext && entries[0].Id != entries[1].Id && entries[0].ControllerId == 1
            && entries[0].Targets.Single() == target && entries[3].Name == "Lightning Bolt",
            "All stack entries retain explicit resolution positions, distinct ability IDs from one source, controllers and detached public targets");

        var feedback = new PriorityTransitionFeedback(); feedback.Observe(null, initial, 1);
        var next = initial with { Revision = 11, PriorityPlayerId = 3, DecisionOwnerId = 3, Decision = null,
            Stack = [new("Response", "Public response.", false, null) { Id = "new-response", ControllerId = 3 }, .. initial.Stack] };
        feedback.Observe(initial, next, 2);
        var transition = feedback.Latest;
        feedback.Observe(next, next, 3); feedback.Observe(next, initial, 4);
        bool unchanged = ReferenceEquals(feedback.Latest, transition);
        var processing = next with { Revision = 12, DecisionOwnerId = null, PriorityPlayerId = null, ActionClass = "resolving", Stack = initial.Stack };
        feedback.Observe(next, processing, 5);
        check(unchanged && transition is { ResponderId: 3, ResponderChanged: true, StackChanged: true, Started: 2 }
            && transition.AddedStackIds.SequenceEqual(["new-response"]) && transition.NextStackId == "new-response"
            && feedback.Latest is { Phase: PriorityPhase.Resolving, ResponderId: null, Started: 5 } removed
            && removed.RemovedStackIds.SequenceEqual(["new-response"]),
            "Exact stack/seat changes produce one timed transition; repeated or stale revisions cannot retrigger it, and removal is not called resolution");

        var search = processing with { Revision = 13, ActionClass = "selection", DecisionOwnerId = 0,
            Decision = new Decision("required-search", new DecisionContext(Message: "Choose a player who may search their library.", Source: new DecisionSource(Card: shared)), new EntitySelectionPayload(new DecisionReply("Confirm", true), new DecisionReply("Cancel", false), [0, 1, 2, 3], null)) };
        feedback.Observe(processing, search, 6);
        var choosing = PriorityPresentation.From(search);
        check(plain.CanPass && plain.Instruction.Contains("pass priority", StringComparison.Ordinal)
            && !plain.Heading.Contains("resolv", StringComparison.OrdinalIgnoreCase)
            && choosing is { Phase: PriorityPhase.Decision, CanPass: false, ResponderId: null, DecisionOwnerId: 0 }
            && choosing.Instruction == search.Decision!.Context.Message && feedback.Latest is { Phase: PriorityPhase.Decision },
            "Passing makes no promise of resolution or skipped triggers; a later required recipient/search choice becomes its own mandatory decision");

        feedback.Observe(search, initial with { Id = "new-game", Revision = 1 }, 7);
        feedback.Observe(null, next, 8);
        check(feedback.Latest == null, "A new game clears the priority transition baseline and a retired game cannot resurrect old feedback");
    }
}
