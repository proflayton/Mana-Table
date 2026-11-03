using System.Collections.Immutable;
using Mana.Contracts;

internal static class ConfiguredSimulationRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var setup = new ConfiguredSimulationSetup(new(MatchRulesets.CooperativeCommander,
            [new(0, 17, MatchSeatRole.Party), new(1, 42, MatchSeatRole.Opposition),
             new(2, 17, MatchSeatRole.Party), new(3, 42, MatchSeatRole.Opposition)]), ["a", "b", "c", "d"]);
        var participants = ImmutableArray.Create(new ConfiguredSimulationParticipant(2, 83), new(0, 29), new(3, 11), new(1, 47));
        var players = participants.Reverse().Select(person => new Player(person.PlayerId, "Same name", 40, false, false,
            ImmutableDictionary<string, int>.Empty, []) { TeamId = setup.Specification.Seats[person.Index].TeamId }).ToImmutableArray();
        var state = new GameSnapshot("actual-game", "playing", null, null, -1, 0, "Pregame", "PREGAME", 29,
            players, [], null, null);
        var receipt = new ConfiguredSimulationStart(setup, participants, state);
        ConfiguredSimulationFacts.ValidateReceipt(receipt, setup);
        check(ConfiguredSimulationFacts.IsReady(receipt, state),
            "Configured simulation binds nonsequential actual player/team IDs by explicit slot despite reordered identical-name players");
        var ffa = setup with { Specification = new(MatchRulesets.FreeForAllCommander,
            [new(0, 17, MatchSeatRole.Participant), new(1, 42, MatchSeatRole.Participant),
             new(2, 83, MatchSeatRole.Participant), new(3, 104, MatchSeatRole.Participant)]) };
        ConfiguredSimulationFacts.ValidateSetup(ffa);
        ConfiguredSimulationFacts.ValidateSetup(setup with { Decks = ["a", "a", "a", "a"] });
        check(true, "Explicit four-seat FFA and intentionally repeated deck assignments remain valid configured plans");
        foreach (var invalid in new[] {
            setup with { Decks = default }, setup with { Decks = ["a", "b", "c"] },
            setup with { Decks = ["a", "b", "c", " "] }, setup with { Decks = ["a", "b", "c", new string('x', 201)] },
            setup with { Specification = setup.Specification with { RulesetId = "unknown.rules.v1" } },
            setup with { Specification = setup.Specification with { Seats = setup.Specification.Seats.SetItem(2, new(1, 17, MatchSeatRole.Party)) } },
            setup with { Specification = setup.Specification with { Seats = setup.Specification.Seats.SetItem(2, new(2, -1, MatchSeatRole.Party)) } },
            setup with { Specification = setup.Specification with { Seats = setup.Specification.Seats.SetItem(2, new(2, 42, MatchSeatRole.Opposition)) } }
        }) check(Reject(() => ConfiguredSimulationFacts.ValidateSetup(invalid)),
            "Configured simulation rejects incomplete handles, unknown rulesets and malformed topology before provider mutation");
        foreach (var invalid in new[] {
            receipt with { Setup = setup with { Decks = ["changed", "b", "c", "d"] } },
            receipt with { Participants = participants.RemoveAt(0) },
            receipt with { Participants = participants.SetItem(0, participants[1]) },
            receipt with { Participants = participants.SetItem(0, new(2, 29)) },
            receipt with { Participants = participants.SetItem(0, new(2, -1)) }
        }) check(Reject(() => ConfiguredSimulationFacts.ValidateReceipt(invalid, setup)),
            "An accepted configured receipt cannot replace the pinned plan or duplicate, omit or invent actual participant bindings");
        var starting = state with { Status = "starting", Players = [] };
        var early = receipt with { State = starting };
        ConfiguredSimulationFacts.ValidateReceipt(early, setup);
        check(!ConfiguredSimulationFacts.IsReady(early, starting)
                && Reject(() => ConfiguredSimulationFacts.ValidateState(early, starting)),
            "A genuine early empty starting publication is retained but supplies no policy-ready roster");
        var fullStarting = state with { Status = "starting" };
        check(!ConfiguredSimulationFacts.IsReady(receipt, fullStarting)
                && Reject(() => ConfiguredSimulationFacts.ValidateState(receipt, fullStarting))
                && Reject(() => ConfiguredSimulationFacts.IsReady(receipt, fullStarting with {
                    Players = players.SetItem(0, players[0] with { TeamId = 999 }) })),
            "A constructor spectator roster remains unready until committed play, while wrong starting teams still fail validation");
        foreach (var invalid in new[] {
            state with { Id = "replacement-game" }, state with { Players = [] },
            state with { Players = players.SetItem(0, players[0] with { TeamId = null }) },
            state with { Players = players.SetItem(0, players[0] with { TeamId = 999 }) },
            state with { Players = players.SetItem(0, players[0] with { Id = 999 }) },
            state with { Players = players.SetItem(0, players[1]) },
            state with { Encounter = new("party", "encounter") }, state with { ViewerId = 29 },
            state with { Status = "error", Error = "Interrupted" },
            state with { Players = players.SetItem(0, players[0] with {
                Zones = [new("Hand", 1, [new Card { Name = "Private card" }], null)] }) }
        }) check(Reject(() => ConfiguredSimulationFacts.ValidateState(receipt, invalid)),
            "Later configured publications cannot change game, mapped teams/roster, spectator privacy or encounter isolation");
        var owned = state with { ViewerId = 29, Decision = new("own-decision", new(), new PriorityPayload(new("Pass", true))) };
        ConfiguredSimulationFacts.ValidateOwnedState(receipt, owned);
        check(Reject(() => ConfiguredSimulationFacts.ValidateState(receipt, owned))
                && Reject(() => ConfiguredSimulationFacts.ValidateOwnedState(receipt, owned with { ViewerId = 999 }))
                && Reject(() => ConfiguredSimulationFacts.ValidateOwnedState(receipt, owned with { Players = [] })),
            "A real owned decision keeps its audience while unknown viewers and changed roster are rejected before policy use");
        var finished = state with { Status = "finished", Outcome = new(state.Id, MatchOutcomeKind.Win, [29, 83], [17], "AllOpposingTeamsLost") };
        ConfiguredSimulationFacts.ValidateState(receipt, finished);
        check(Reject(() => ConfiguredSimulationFacts.ValidateState(receipt, finished with {
                Outcome = finished.Outcome! with { WinningTeamIds = [42] } })),
            "Natural typed team outcomes remain bound to the actual configured players rather than result prose");
        IAutomatedSimulation legacy = new Legacy();
        bool unsupported = false;
        try { legacy.StartConfiguredSimulationAsync(setup).GetAwaiter().GetResult(); } catch (NotSupportedException) { unsupported = true; }
        check(!legacy.SupportsConfiguredSimulation && unsupported,
            "Existing simulation providers stay source-compatible and explicitly unsupported for configured setup");
    }

    private static bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    private sealed class Legacy : IAutomatedSimulation
    {
        public Task<GameSnapshot> StartSimulationAsync(ImmutableArray<string> deckIds) => throw new NotSupportedException();
        public Task<GameSnapshot?> ObserveSimulationAsync() => Task.FromResult<GameSnapshot?>(null);
        public Task StopSimulationAsync(string gameId) => Task.CompletedTask;
    }
}
