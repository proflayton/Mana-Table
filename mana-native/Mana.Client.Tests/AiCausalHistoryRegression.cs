using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class AiCausalHistoryRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var state = State();
        var source = new Card { VisualId = "departed-source", CombatId = "departed-source", Name = "PRIVATE-SOURCE" };
        state = state with { Players = state.Players.Select(player => player.Id == 2 ? player with {
            Zones = [new("Battlefield", 1, [source], null)] } : player).ToImmutableArray() };
        var dealt = new ActivityEntry(1, state.Turn, "MAIN1", "damage", 0, "PRIVATE-MESSAGE", source.VisualId, source.Name) {
            Impact = new("damage", 6, PlayerId: 0, SourceCardId: source.VisualId, SourceName: source.Name,
                SourceControllerId: 1, RecipientControllerId: 0),
            Cause = new(source.VisualId, "PRIVATE-CAUSE", "PRIVATE-ABILITY", 1) };
        using var seat = new AiSeatSession(state.Id, 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(5), 128));
        seat.Observe(new(state with { Activity = [dealt] }));
        var remembered = seat.History;
        var first = CasualTableAssessment.Assess(state, remembered);
        check(first.Single(value => value.PlayerId == 1).Pressure == 6
                && first.Single(value => value.PlayerId == 2).Pressure == 0,
            "Damage memory uses the event-time source controller even when the source now belongs to another player and activity names the victim");
        var departed = state with { Revision = 2, BoardRevision = 2, Players = State().Players, Activity = [dealt] };
        seat.Observe(new(departed));
        check(seat.History.Events.Length == 1 && CasualTableAssessment.Assess(departed, seat.History)
                .Single(value => value.PlayerId == 1).Pressure == 6,
            "A public source leaving play and repeated observation cannot erase, reattribute or duplicate committed harm");
        var changedController = dealt with { Id = 2, Cause = null, Impact = dealt.Impact! with { Amount = 2, SourceControllerId = 2, Combat = true } };
        seat.Observe(new(departed with { Revision = 3, BoardRevision = 3, Activity = [dealt, changedController] }));
        var after = CasualTableAssessment.Assess(departed, seat.History);
        check(after.Single(value => value.PlayerId == 1).Pressure == 6 && after.Single(value => value.PlayerId == 2).Pressure == 2,
            "A later use of the same physical source records its newly reported controller without rewriting earlier damage");
        check(!JsonSerializer.Serialize(seat.History).Contains("PRIVATE-", StringComparison.Ordinal),
            "Causal seat memory retains typed historical attribution without source names, printed faces or announcement prose");

        var unknown = remembered with { Events = remembered.Events.Select(entry => entry with {
            CauseControllerId = null, Impact = entry.Impact! with { SourceControllerId = null } }).ToImmutableArray() };
        check(CasualTableAssessment.Assess(state, unknown).All(value => value.Pressure == 0),
            "Legacy damage with no reported source controller remains unknown despite a current source, cause portrait and affected seat");
        var stolenDuringResolution = remembered with { Events = remembered.Events.Select(entry => entry with {
            Impact = entry.Impact! with { SourceControllerId = 2, Combat = false } }).ToImmutableArray() };
        check(CasualTableAssessment.Assess(state, stolenDuringResolution).Single(value => value.PlayerId == 1).Pressure == 6
                && CasualTableAssessment.Assess(state, stolenDuringResolution).Single(value => value.PlayerId == 2).Pressure == 0,
            "A disclosed producing ability controller retains responsibility when the physical damage source changes control before resolution");
        var unknownNoncombat = stolenDuringResolution with { Events = stolenDuringResolution.Events.Select(entry => entry with {
            CauseControllerId = null }).ToImmutableArray() };
        check(CasualTableAssessment.Assess(state, unknownNoncombat).All(value => value.Pressure == 0),
            "A known physical source controller cannot stand in for an unreported noncombat ability controller");
        var ownHit = remembered with { Events = remembered.Events.Select(entry => entry with {
            CauseControllerId = 0, Impact = entry.Impact! with { SourceControllerId = 0, PlayerId = 1, RecipientControllerId = 1 } }).ToImmutableArray() };
        check(CasualTableAssessment.Assess(state, ownHit).Single(value => value.PlayerId == 1).RecentFocus == 6,
            "Repeated-focus memory counts the acting seat's committed damage rather than its accepted target proposals");

        AiObservedEvent Departure(long id, string to, ExecutionStep? step = ExecutionStep.Resolution) => new(id, state.Turn,
            "moved", 3, null, null, new("zone", 1, null, null, null, null, null, null,
                RecipientControllerId: 0, FromZone: "Battlefield", ToZone: to), 1, "shared-removal") {
            ExecutionStep = step };
        var removed = new AiSeatHistory([], [Departure(3, "Graveyard"), Departure(4, "Exile"), Departure(5, "Hand")]);
        check(CasualTableAssessment.Assess(state, removed).Single(value => value.PlayerId == 1).Pressure == 6
                && removed.Events.Select(entry => entry.Id).Distinct().Count() == 3,
            "Simultaneous committed departures remain distinct lost resources, including an anonymous bounce, without looking up a later card face or owner");
        var paid = removed with { Events = [Departure(3, "Graveyard", ExecutionStep.Cost), Departure(4, "Exile", null)] };
        check(CasualTableAssessment.Assess(state, paid).All(value => value.Pressure == 0),
            "Paying a cost and departures with unknown execution steps do not become social aggression");
        var noCause = removed with { Events = removed.Events.Select(entry => entry with {
            CauseControllerId = null }).ToImmutableArray() };
        check(CasualTableAssessment.Assess(state, noCause).All(value => value.Pressure == 0),
            "A state-based death with no reported producing player does not blame a nearby stack action or the resource's owner");
        var arrival = removed with { Events = [Departure(3, "Battlefield") with {
            Impact = removed.Events[0].Impact! with { FromZone = "Command", ToZone = "Battlefield" } }] };
        check(CasualTableAssessment.Assess(state, arrival).All(value => value.Pressure == 0),
            "A committed battlefield arrival is a resource result, not invented hostile removal");

        var wire = JsonSerializer.Deserialize<PublicImpact>("{\"Kind\":\"damage\",\"Amount\":2,\"PlayerId\":0}")!;
        check(wire.SourceControllerId == null && wire.RecipientControllerId == null && wire.FromZone == null && wire.ToZone == null,
            "Older providers retain explicit unknown controller and movement facts through JSON decoding");
        var historical = state with { Activity = [dealt with { Impact = new("zone", 1, "old-occurrence", SourceControllerId: 1,
            RecipientControllerId: 0, FromZone: "Battlefield", ToZone: "Library") }] };
        PublicActivityFacts.Validate(JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(historical))!);
        check(true, "Shared activity validation accepts event-time occurrences absent from the current board without granting them action authority");
        foreach (var invalid in new[] {
            historical.Activity[0].Impact! with { SourceControllerId = 99 },
            historical.Activity[0].Impact! with { RecipientControllerId = 99 },
            historical.Activity[0].Impact! with { FromZone = null },
            historical.Activity[0].Impact! with { ToZone = "invented" },
            historical.Activity[0].Impact! with { Kind = "damage" },
            historical.Activity[0].Impact! with { Amount = 2 }
        }) {
            bool rejected = false;
            try { _ = new AiDecisionFrame(historical with { Activity = [historical.Activity[0] with { Impact = invalid }] }); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Policy admission rejects foreign historical controllers and contradictory committed movement facts");
        }
    }

    private static GameSnapshot State() => new("causal-memory", "playing", null, null, 0, 8, "Main", "MAIN1", 0,
        Enumerable.Range(0, 4).Select(id => new Player(id, "Seat " + id, 40, id == 0, false,
            ImmutableDictionary<string, int>.Empty, [])).ToImmutableArray(), [], null,
        new("priority", new(), new PriorityPayload(new("Pass", true)))) { Revision = 1, BoardRevision = 1 };
}
