using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

internal static class MixedGamePublicCohortChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var frames = Enumerable.Range(0, 4).Select(viewer => Frame(viewer)).ToArray();
        var tracker = new MixedGamePublicCohorts([0, 1, 2, 3]);
        var initial = frames.Select(frame => tracker.Observe(frame.ViewerId, frame)).ToArray();
        check(initial.All(item => item.Compared) && initial.Count(item => item.FirstCohort) == 1
                && tracker.Comparisons == 3 && tracker.FourAudienceCohorts == 1
                && tracker.Evidence is { ComparedFrames: 4, RetainedCohorts: 1, AudienceCount: 4 },
            "Four actual audience frames establish one common public cohort despite their different private hands");
        bool skipped = true;
        for (int iteration = 0; iteration < 10000; iteration++)
            foreach (var frame in frames) skipped &= !tracker.Observe(frame.ViewerId, frame).Compared;
        check(skipped && tracker.Evidence is { ComparedFrames: 4, ImmutableRepeatSkips: 40000 }
                && tracker.Comparisons == 3 && tracker.FourAudienceCohorts == 1,
            "Forty thousand repeated immutable frame samples perform no extra projection comparisons or cohort credit");

        var fresh = frames[0] with { Revision = 11 };
        check(tracker.Observe(0, fresh) is { Compared: true, FirstCohort: false }
                && tracker.Evidence.ComparedFrames == 5 && tracker.Comparisons == 3,
            "A fresh accepted object is checked even when its public board has already been compared");
        var privateChange = OwnHand(fresh with { Revision = 12 }, "PRIVATE-NEW-FACE-CANARY");
        var privateResult = tracker.Observe(0, privateChange);
        check(privateResult.Compared && !privateResult.Facts!.Contains("PRIVATE-NEW-FACE-CANARY", StringComparison.Ordinal)
                && tracker.Comparisons == 3,
            "Private-face changes remain excluded from public comparisons without making a fresh frame invisible to validation");
        check(Reject(() => tracker.Observe(1, PublicLife(frames[1] with { Revision = 11 }, 39))),
            "A fresh same-board public contradiction cannot hide behind an already-seen board or audience ID");

        foreach (var frame in frames) tracker.Observe(frame.ViewerId, PublicLife(frame with { Revision = 20, BoardRevision = 11 }, 39));
        check(tracker.Comparisons == 6 && tracker.FourAudienceCohorts == 2,
            "A genuine new public board earns exactly one new four-audience checkpoint");
        check(Reject(() => tracker.Observe(0, frames[0]))
                && Reject(() => tracker.Observe(0, Frame(0) with { Id = "foreign-game", Revision = 30, BoardRevision = 30 }))
                && Reject(() => tracker.Observe(0, Frame(1)))
                && Reject(() => tracker.Observe(0, Frame(0) with { Revision = 30, BoardRevision = 30, Error = "Retained rules error" })),
            "Reference caching cannot bypass regressed, foreign or failed current audience validation");

        var boundedPreparation = new PublicProjectionPreparation([0, 1, 2, 3]);
        var bounded = new MixedGamePublicCohorts([0, 1, 2, 3], capacity: 1, preparation: boundedPreparation);
        bounded.Observe(0, frames[0]); bounded.Observe(1, frames[1]);
        bounded.Observe(0, Frame(0, 11, 11)); bounded.Observe(0, Frame(0, 12, 12));
        check(bounded.Evidence is { MaximumRetainedCohorts: 1, RetainedCohorts: 1 }
                && Reject(() => bounded.Observe(1, PublicLife(Frame(1, 11, 10), 38)))
                && bounded.Observe(1, OwnHand(Frame(1, 11, 10), "Own private refresh")).Compared
                && boundedPreparation is { KnownAudienceSlots: 4, RetainedFrames: 2 },
            "The bounded global cache retains each owner's current same-board baseline, including after that cohort was evicted");

        var correlationPreparation = new PublicProjectionPreparation([0, 1, 2, 3]);
        var correlated = new MixedGamePublicCohorts([0, 1, 2, 3], capacity: 1, preparation: correlationPreparation);
        correlated.Observe(1, StackReference(Frame(1), null));
        correlated.Observe(1, StackReference(Frame(1, 11, 10), "execution-a"));
        correlated.Observe(1, StackReference(Frame(1, 12, 10), null));
        correlated.Observe(0, Frame(0, 11, 11));
        check(Reject(() => correlated.Observe(1, StackReference(Frame(1, 13, 10), "execution-b"))),
            "An absent optional execution reference cannot erase an admitted same-board correlation before global cache eviction");
        var unadmitted = StackReference(Frame(2), "unknown") with { Executions = null };
        var invalidPreparation = new PublicProjectionPreparation([0, 1, 2, 3]);
        check(!invalidPreparation.ExecutionCorrelations(unadmitted).Valid && !invalidPreparation.ExecutionCorrelations(unadmitted).Valid
                && invalidPreparation is { CorrelationsPrepared: 2, CorrelationsReused: 0 },
            "An invalid local execution frame is validated again rather than becoming a reusable prepared correlation");
        check(Reject(() => new MixedGamePublicCohorts([0, 1, 2, 3], preparation: invalidPreparation).Observe(2, unadmitted)),
            "An unadmitted stack execution still fails evidence integrity before its immutable reference can be cached");
        var independent = new PublicProjectionPreparation([0]);
        string publicFacts = independent.Facts(frames[0]);
        check(independent is { FactsPrepared: 1, CorrelationsPrepared: 0 }
            && independent.Facts(frames[0]) == publicFacts && independent is { FactsPrepared: 1, FactsReused: 1 },
            "Preparing public facts does not eagerly validate execution correlations or recompute the same exact frame");
        independent.ExecutionCorrelations(frames[0]);
        check(independent is { FactsPrepared: 1, CorrelationsPrepared: 1 },
            "The independently lazy correlation getter performs its own first derivation only when requested");
        independent.Facts(frames[0] with { });
        independent.Facts(frames[1]);
        check(independent is { KnownAudienceSlots: 1, RetainedFrames: 1, FactsPrepared: 3, CorrelationsPrepared: 1 },
            "Record-equal fresh objects and unknown audiences rederive without growing the known audience slots");
        independent.Clear();
        check(independent.RetainedFrames == 0 && independent.Facts(frames[0]) == publicFacts && independent.FactsPrepared == 4,
            "Retiring preparation releases every retained frame and derived value before an identical frame can be used again");
    }

    private static bool Reject(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static GameSnapshot Frame(int viewer, long revision = 10, long board = 10)
    {
        var players = Enumerable.Range(0, 4).Select(id => new Player(id, "Player " + id, 40, false, false,
            ImmutableDictionary<string, int>.Empty, [
                new("Battlefield", 1, [new Card { Name = "Forest", VisualId = "public-land-" + id,
                    CombatId = "public-land-" + id, Type = "Land" }], null),
                new("Hand", 1, viewer == id ? [new Card { Name = "Private hand " + id, VisualId = "private-" + id }] : [], null)
            ])).ToImmutableArray();
        return new("cohort-game", "playing", null, null, viewer, 1, "Main", "MAIN1", 0, players, [], null, null) {
            Revision = revision, BoardRevision = board
        };
    }

    private static GameSnapshot PublicLife(GameSnapshot state, int life) => state with {
        Players = state.Players.Select(player => player.Id == 0 ? player with { Life = life } : player).ToImmutableArray()
    };

    private static GameSnapshot OwnHand(GameSnapshot state, string name) => state with {
        Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                Cards = [new Card { Name = name, VisualId = "own-private-face" }]
            }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static GameSnapshot StackReference(GameSnapshot state, string? execution) => state with {
        Stack = [new("Public spell", "Draw a card.", StackActionKind.Spell,
            new Card { Name = "Public spell", VisualId = "stack-face", Type = "Instant" }) {
                Id = "stack-occurrence", ControllerId = 0, ExecutionId = execution ?? ""
            }],
        Executions = execution == null ? null : new(1, 1,
            [new(execution, execution, ExecutionKind.Spell, ExecutionStep.Stack)],
            [new(1, execution, ExecutionStep.Stack)])
    };
}
