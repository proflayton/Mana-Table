using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.AI;
using Mana.Contracts;

internal static class AiImpactHistoryRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = State();
        using var seat = Seat(state);
        var harm = new[] { Damage(1, 1, 0, 6), Damage(2, 0, 2, 4), Departure(3) };
        seat.Observe(new(state with { Activity = harm.ToImmutableArray() }));
        var initial = seat.History;
        var noise = Enumerable.Range(4, 384).Select(id => new ActivityEntry(id, state.Turn, "MAIN1",
            id % 2 == 0 ? "draw" : "counter", 0, "PRIVATE-ANNOUNCEMENT", "public-card", "PRIVATE-FACE") {
                Impact = id % 2 == 0 ? new("draw", 1, PlayerId: 0) : new("counter", 1, "public-card"),
                Change = id % 2 == 0 ? null : new("counter", "+1/+1", id, id + 1)
            }).ToImmutableArray();
        state = state with { Revision = 2, BoardRevision = 2, Activity = noise };
        seat.Observe(new(state));
        var retained = seat.History;
        var assessed = CasualTableAssessment.Assess(state, retained);
        check(retained.Events.Length == 128 && retained.Events.All(entry => entry.Id >= 260)
                && retained.ImpactEvents is { Length: 3 },
            "A 384-event draw/counter chain rotates the bounded diagnostic history without evicting committed social impacts");
        check(assessed.Single(value => value.PlayerId == 1).Pressure == 6
                && assessed.Single(value => value.PlayerId == 2).RecentFocus == 4
                && assessed.Single(value => value.PlayerId == 3).Pressure == 2,
            "Recent aggression, own repeated focus and resolving resource losses survive a large harmless trigger chain unchanged");
        check(CasualTableAssessment.Assess(state, retained with { ImpactEvents = null })
                .All(value => value.Pressure == 0 && value.RecentFocus == 0),
            "The same truncated legacy event ring cannot reconstruct the lost aggression, demonstrating the original eviction counterexample");
        state = state with { Revision = 3, BoardRevision = 3 };
        seat.Observe(new(state));
        check(seat.History.ImpactEvents!.Value.SequenceEqual(initial.ImpactEvents!.Value)
                && initial.Events.Length == 3 && initial.ImpactEvents.Value.Length == 3,
            "Republished activity is admitted once and later noise cannot mutate an already captured immutable impact history");
        check(!JsonSerializer.Serialize(retained).Contains("PRIVATE-", StringComparison.Ordinal),
            "Separate impact memory retains no card faces, announcement prose or producing ability names");

        var nonharm = ImmutableArray.Create(
            Damage(388, 1, 0, 0), Departure(389) with { ExecutionStep = ExecutionStep.Cost },
            Departure(390) with { ExecutionId = "", ExecutionStep = null }, Departure(391) with {
                Impact = new("zone", 1, RecipientControllerId: 0, FromZone: "Command", ToZone: "Battlefield") });
        state = state with { Revision = 4, BoardRevision = 4, Activity = nonharm };
        seat.Observe(new(state));
        check(seat.History.ImpactEvents!.Value.Length == 3,
            "Zero dealt damage, paid sacrifices, unknown-step departures and battlefield arrivals do not enter committed harm memory");
        var unknown = Damage(392, 2, 0, 5) with { Cause = null };
        state = state with { Revision = 5, BoardRevision = 5, Activity = [unknown] };
        seat.Observe(new(state));
        check(seat.History.ImpactEvents!.Value.Length == 4
                && CasualTableAssessment.Assess(state, seat.History).Single(value => value.PlayerId == 2).Pressure == 0,
            "Retaining a noncombat damage fact does not promote its physical source controller into unknown producing-player attribution");

        var other = State() with { ViewerId = 1, DecisionOwnerId = 1, Activity = [] };
        using var otherSeat = Seat(other);
        otherSeat.Observe(new(other));
        check(otherSeat.History.ImpactEvents is { IsEmpty: true }
                && CasualTableAssessment.Assess(other, otherSeat.History).All(value => value.Pressure == 0 && value.RecentFocus == 0),
            "Another seat session cannot inherit impacts retained by the first seat");
        check(CasualTableAssessment.Assess(State(), initial with { ImpactEvents = [] })
                .All(value => value.Pressure == 0 && value.RecentFocus == 0),
            "An explicit empty impact channel never falls back to unrelated diagnostic events");

        await Replay(retained, state with { Activity = [] }, check);

        state = state with { Turn = 16, Revision = 6, BoardRevision = 6, Activity = [] };
        seat.Observe(new(state));
        check(CasualTableAssessment.Assess(state, seat.History).Single(value => value.PlayerId == 1).Pressure > 0,
            "An impact at the existing two-round boundary retains its ordinary aged contribution");
        state = state with { Turn = 17, Revision = 7, BoardRevision = 7 };
        seat.Observe(new(state));
        check(CasualTableAssessment.Assess(state, seat.History).All(value => value.Pressure == 0 && value.RecentFocus == 0),
            "Independent impact retention cannot extend the policy's existing two-round social horizon");
        var actualHarm = Enumerable.Range(1000, 200).Select(id => Damage(id, 1, 0, 1) with { Turn = 17 }).ToImmutableArray();
        seat.Observe(new(state with { Revision = 8, BoardRevision = 8, Activity = actualHarm }));
        check(seat.History.ImpactEvents is { Length: 128 } bounded && bounded[0].Id == 1072 && bounded[^1].Id == 1199,
            "Genuinely new committed impacts rotate their own fixed-capacity history rather than creating unbounded memory");
    }

    private static async Task Replay(AiSeatHistory retained, GameSnapshot state, Action<bool, string> check)
    {
        var recorded = new AiDecisionReplayCase(1, "trigger-noise", "impact-history-probe.v1", state,
            new(0, ActionDomainCoverage.Complete, [], []), new(), retained, 17);
        var encoded = JsonSerializer.Serialize(recorded);
        var restored = JsonSerializer.Deserialize<AiDecisionReplayCase>(encoded)!;
        var replay = await AiDecisionReplay.EvaluateAsync(restored, new ImpactPolicy());
        check(restored.History.Events.All(entry => entry.Id > 3) && restored.History.ImpactEvents is { Length: 3 }
                && replay.Status == AiSeatStatus.Ready && replay.Proposal is PassPriority,
            "A private replay JSON roundtrip preserves impact knowledge after its source events have left the diagnostic ring");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(recorded with { History = retained with {
            Events = retained.ImpactEvents!.Value, ImpactEvents = null } }))!;
        legacy["History"]!.AsObject().Remove("ImpactEvents");
        var old = JsonSerializer.Deserialize<AiDecisionReplayCase>(legacy.ToJsonString())!;
        check(old.History.ImpactEvents == null
                && (await AiDecisionReplay.EvaluateAsync(old, new ImpactPolicy())).Status == AiSeatStatus.Ready,
            "An older private replay without the new channel retains its explicitly available event evidence");
        var fact = retained.ImpactEvents!.Value[0];
        foreach (var invalid in new[] {
            ImmutableArray.Create(fact with { Turn = state.Turn + 1 }),
            ImmutableArray.Create(fact, fact),
            ImmutableArray.Create(fact with { CauseControllerId = 99 }),
            ImmutableArray.Create(fact with { Impact = fact.Impact! with { Amount = 0 } }),
            Enumerable.Range(0, 4097).Select(index => fact with { Id = index }).ToImmutableArray(),
            default(ImmutableArray<AiObservedEvent>)
        }) {
            bool rejected = false;
            try { await AiDecisionReplay.EvaluateAsync(recorded with { History = retained with { ImpactEvents = invalid } }, new ImpactPolicy()); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Replay rejects future, duplicate, foreign, nonimpact, excessive or uninitialized impact history before policy evaluation");
        }
    }

    private sealed class ImpactPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("impact-history-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var table = CasualTableAssessment.Assess(request.Frame.State, request.History);
            return Task.FromResult<AiProposal>(table.Single(value => value.PlayerId == 1).Pressure == 6
                    && table.Single(value => value.PlayerId == 2).RecentFocus == 4
                    && table.Single(value => value.PlayerId == 3).Pressure == 2
                ? new AiProposal.Answer(new PassPriority(request.Frame.State.Id, request.Frame.Stamp.DecisionId), "retained-committed-impacts")
                : new AiProposal.Unavailable("missing-impacts", "This replay lacks its committed public history."));
        }
    }
    private static AiSeatSession Seat(GameSnapshot state)
        => new(state.Id, state.ViewerId, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(5), 128));
    private static ActivityEntry Damage(long id, int actor, int recipient, long amount)
        => new(id, 8, "MAIN1", "damage", recipient, "PRIVATE-DAMAGE", "source", "PRIVATE-SOURCE") {
            Impact = new("damage", amount, PlayerId: recipient, SourceControllerId: actor, RecipientControllerId: recipient),
            Cause = new("source", "PRIVATE-CAUSE", "PRIVATE-ABILITY", actor) };
    private static ActivityEntry Departure(long id) => new(id, 8, "MAIN1", "moved", 0, "PRIVATE-MOVE", null, null) {
        Impact = new("zone", 1, RecipientControllerId: 0, FromZone: "Battlefield", ToZone: "Graveyard"),
        Cause = new(null, "PRIVATE-CAUSE", "PRIVATE-ABILITY", 3), ExecutionId = "mutation", ExecutionStep = ExecutionStep.Resolution };
    private static GameSnapshot State() => new("impact-memory", "playing", null, null, 0, 8, "Main", "MAIN1", 0,
        Enumerable.Range(0, 4).Select(id => new Player(id, "Seat " + id, 40, id == 0, false,
            ImmutableDictionary<string, int>.Empty, [])).ToImmutableArray(), [], null,
        new("priority", new(), new PriorityPayload(new("Pass", true)))) { Revision = 1, BoardRevision = 1, DecisionOwnerId = 0,
            Executions = new(1, 0, [new("mutation", "mutation", ExecutionKind.Spell, ExecutionStep.Resolution) { ActorPlayerId = 3 }], []) };
}
