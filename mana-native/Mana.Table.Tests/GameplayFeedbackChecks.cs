using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal static class GameplayFeedbackChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var state = PresentationSmoke.Example();
        var presence = new TablePresence();
        var tutor = state with { Players = state.Players.Select(p => p.Id == 1 ? p with { Zones = p.Zones.Select(z => z.Name == "Hand"
            ? z with { Count = z.Count + 1 } : z.Name == "Library" ? z with { Count = z.Count - 1 } : z).ToArray() } : p).ToArray(),
            Activity = [new(1, state.Turn, state.PhaseKey, "moved", 1, "A card moved from library to hand.", null, null)] };
        presence.Observe(state, tutor, 1);
        check(presence.Cues.All(c => c.Kind != SeatCueKind.Draw), "A tutor's matching library/hand deltas never invent a draw animation");
        var legacyTransfer = tutor with { Activity = [new(1, state.Turn, state.PhaseKey, "draw", 1, "A card moved from library to hand.", null, null)] };
        var legacyPresence = new TablePresence(); var legacyFeedback = new ActionFeedback();
        legacyPresence.Observe(state, legacyTransfer, 1); legacyFeedback.Observe(state, legacyTransfer, 1);
        check(legacyPresence.Cues.Count == 0 && legacyFeedback.LastPublicOutcome == null,
            "An older protocol-compatible host's uncounted library-to-hand event never invents a draw animation or asserted draw outcome");
        var wheel = tutor with { Activity = [.. tutor.Activity, new(2, state.Turn, state.PhaseKey, "draw", 1, "Opponent drew cards.", null, null) { Count = 8 }] };
        presence.Observe(tutor, wheel, 2);
        check(presence.Cues.Single(c => c.Kind == SeatCueKind.Draw).Amount == 8 && presence.PendingDraws(1, 2) == 7,
            "An actual eight-card draw keeps its full public count with a bounded anonymous visual batch despite unchanged final zones");
        presence.Observe(tutor, wheel, 2.3);
        check(presence.Cues.Single(c => c.Kind == SeatCueKind.Draw).Started == 2,
            "Reobserving an already consumed event cannot replay or prolong its draw cue");
        var feedback = new ActionFeedback();
        var noResolution = wheel with { Activity = [.. wheel.Activity, new(3, state.Turn, state.PhaseKey, "resolved", 1, "Spell did not resolve.", null, "Spell") { Outcome = "notResolved" }] };
        feedback.Observe(wheel, noResolution, 3);
        check(feedback.Last is { Heading: "DID NOT RESOLVE", Outcome: "notResolved" },
            "An engine-reported non-resolution gets a non-resolution heading rather than JUST RESOLVED");
        var success = noResolution with { Activity = [.. noResolution.Activity, new(4, state.Turn, state.PhaseKey, "resolved", 1, "Spell resolved.", null, "Spell") { Outcome = "resolved" }] };
        feedback.Observe(noResolution, success, 4);
        check(feedback.Last is { Heading: "JUST RESOLVED", Outcome: "resolved" }, "An actual successful resolution retains its affirmative heading");
        var legacy = success with { Activity = [.. success.Activity, new(5, state.Turn, state.PhaseKey, "resolved", 1, "Legacy outcome.", null, "Spell")] };
        feedback.Observe(success, legacy, 5);
        check(feedback.Last?.Heading == "REPORTED OUTCOME", "An older engine without typed outcome is not guessed to have resolved successfully");
        var events = legacy with { Activity = [.. legacy.Activity,
            new(6, state.Turn, state.PhaseKey, "draw", 1, "Opponent drew cards.", null, null) { Count = 9 },
            new(7, state.Turn, state.PhaseKey, "shuffle", 1, "Opponent shuffled their library.", null, null) { Count = 1 }] };
        feedback.Observe(legacy, events, 6);
        check(feedback.LastPublicOutcome is { Messages.Length: 2 } outcome && outcome.Messages.Any(m => m.Contains("9 cards"))
            && outcome.Messages.All(m => m.Contains("Seat 2")) && outcome.Messages.Any(m => m.Contains("shuffled")),
            "Actual public draw and shuffle events retain exact batch counts and acting seat without hidden card identity");
        feedback.Observe(legacy, events, 7);
        check(feedback.LastPublicOutcome?.Started == 6, "Repeated event observation does not restart public outcome feedback");
        var decline = events with { Revision = events.Revision + 1 };
        feedback.Observe(events, decline, 8);
        check(feedback.LastPublicOutcome?.Started == 6, "A search decline or unchanged snapshot cannot invent a shuffle event");
        feedback.Observe(events, events with { Id = "new-game" }, 9);
        check(feedback.Last == null && feedback.LastPublicOutcome == null, "A new game cannot inherit earlier resolution or public outcome cues");
    }
}
