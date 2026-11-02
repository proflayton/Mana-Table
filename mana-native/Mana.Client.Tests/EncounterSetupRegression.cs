using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class EncounterSetupRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var capabilities = new MatchSetupCapabilities([MatchRulesets.FreeForAllCommander, MatchRulesets.CooperativeCommander], 6, 4, 2);
        var cooperative = new MatchSetupSpecification(MatchRulesets.CooperativeCommander,
            [new(0, 30, MatchSeatRole.Party), new(1, 80, MatchSeatRole.Opposition), new(2, 30, MatchSeatRole.Party), new(3, 80, MatchSeatRole.Opposition)]);
        MatchSetupFacts.Validate(cooperative, capabilities);
        check(true, "An explicit cooperative specification supports nonadjacent allies and separate opposition seats without deriving teams from positions");
        var roster = cooperative.Seats.Select(seat => new Seat(seat.Index, "Same name", seat.Role == MatchSeatRole.Opposition ? "AI" : "OPEN", null, false, seat.Index == 0) {
            ParticipantId = "participant-" + seat.Index, TeamId = seat.TeamId, Role = seat.Role
        }).ToImmutableArray();
        var lobby = new Lobby("hosting", "Ready", null, false, null, false, "", null, [], roster, "table") {
            Revision = 4, MatchSetup = cooperative, MatchSetupCapabilities = capabilities, MatchSetupConsent = "reviewed-setup"
        };
        MatchSetupFacts.Validate(lobby);
        foreach (var invalid in new[] {
            cooperative with { Seats = cooperative.Seats.SetItem(0, cooperative.Seats[0] with { TeamId = 80 }) },
            cooperative with { Seats = cooperative.Seats.SetItem(1, cooperative.Seats[1] with { Role = MatchSeatRole.Participant }) },
            cooperative with { Seats = cooperative.Seats.SetItem(3, cooperative.Seats[3] with { Index = 2 }) },
            cooperative with { Seats = cooperative.Seats.SetItem(2, cooperative.Seats[2] with { TeamId = -1 }) },
            cooperative with { RulesetId = MatchRulesets.FreeForAllCommander }
        }) check(Reject(() => MatchSetupFacts.Validate(invalid, capabilities)),
            "Known rulesets reject ambiguous teams, missing roles and duplicate assignment positions");
        check(Reject(() => MatchSetupFacts.Validate(lobby with { MatchSetupConsent = null }))
                && Reject(() => MatchSetupFacts.Validate(lobby with { Seats = roster.SetItem(1, roster[1] with { Type = "REMOTE" }) }))
                && Reject(() => MatchSetupFacts.Validate(lobby with { Seats = roster.SetItem(2, roster[2] with { ParticipantId = roster[0].ParticipantId }) })),
            "An opposition seat cannot silently become human and two seats cannot share a participant identity");

        var oldReceipt = Receipt("old-game", "encounter-1");
        var state = State("current-game", "encounter-2");
        var progress = new PartyProgress("party", "encounter-2", 1, 1, EncounterSaveState.Pending, null, false, oldReceipt);
        var earlyFailure = state with { Status = "error", Error = "Setup stopped before the opening roster", Outcome = null, Players = [] };
        EncounterFacts.Validate(earlyFailure);
        check(earlyFailure.Encounter == state.Encounter && earlyFailure.Error.Length > 0
                && MatchOutcomes.ForViewer(earlyFailure) == PlayerMatchOutcome.Unknown
                && EncounterCheckpoint.For(earlyFailure, progress with { SaveState = EncounterSaveState.Ready }) == EncounterCheckpointStatus.NotCompleted
                && progress.CompletedEncounters == 1,
            "An early error can retain encounter identity without a roster while granting no completion or progress authority");
        check(Reject(() => EncounterFacts.Validate(earlyFailure with { Outcome = state.Outcome }))
                && Reject(() => EncounterFacts.Validate(earlyFailure with { Status = "playing" }))
                && Reject(() => EncounterFacts.Validate(earlyFailure with { Status = "finished" })),
            "Missing participants stay invalid for actual gameplay/completion and an early error cannot borrow a rules outcome");
        check(EncounterCheckpoint.For(state, progress) == EncounterCheckpointStatus.Pending
                && progress.CompletedEncounters == 1 && progress.LastReceipt!.EncounterId == "encounter-1",
            "A new rules win remains pending while the public checkpoint retains only the previous durable receipt and counters");
        var failed = progress with { SaveState = EncounterSaveState.Failed, SaveError = "Disk unavailable", RetryAllowed = true };
        check(EncounterCheckpoint.For(state, failed) == EncounterCheckpointStatus.Failed
                && EncounterCheckpoint.For(state, failed with { RetryAllowed = false }) == EncounterCheckpointStatus.Failed,
            "Save failure remains distinct from rules loss and guests can observe it without receiving retry permission");
        var currentReceipt = Receipt(state.Id, state.Encounter!.EncounterId);
        var committed = progress with { CompletedEncounters = 2, Victories = 2, SaveState = EncounterSaveState.Ready, LastReceipt = currentReceipt };
        check(EncounterCheckpoint.For(state, committed) == EncounterCheckpointStatus.Committed
                && committed.LastReceipt!.Outcome.WinningPlayerIds.SequenceEqual([7]),
            "Only a matching committed receipt establishes a saved encounter while preserving actual individual winner membership");
        check(EncounterCheckpoint.For(state with { ViewerId = -1 }, committed) == EncounterCheckpointStatus.Committed,
            "The checkpoint is public same-game evidence and does not depend on the current audience or a private hand");
        foreach (var changed in new[] {
            committed with { PartyId = "another-party" },
            committed with { EncounterId = "encounter-1" },
            committed with { LastReceipt = oldReceipt },
            committed with { LastReceipt = currentReceipt with { GameId = "foreign-game" } },
            committed with { LastReceipt = currentReceipt with { Participants = currentReceipt.Participants.SetItem(0,
                currentReceipt.Participants[0] with { ParticipantId = "wrong-person" }) } },
            committed with { LastReceipt = currentReceipt with { Outcome = currentReceipt.Outcome with { WinningPlayerIds = [21] } } }
        }) check(EncounterCheckpoint.For(state, changed) == EncounterCheckpointStatus.Unknown,
            "A stale or foreign receipt cannot borrow current party, match, outcome or participant identity");
        check(EncounterCheckpoint.For(state with { Outcome = null, Status = "playing" }, progress) == EncounterCheckpointStatus.InProgress
                && EncounterCheckpoint.For(state with { Outcome = null, Status = "finished", Result = "Defeat" }, committed) == EncounterCheckpointStatus.NotCompleted
                && EncounterCheckpoint.For(state with { Status = "error", Error = "Interrupted", Outcome = null }, committed) == EncounterCheckpointStatus.NotCompleted
                && EncounterCheckpoint.For(state with { Encounter = null }, committed) == EncounterCheckpointStatus.NotEncounter,
            "Local concession, interruption and nonencounter games cannot manufacture a checkpoint from display text");
        foreach (var invalid in new[] {
            committed with { CompletedEncounters = 0 }, committed with { Victories = 3 },
            committed with { LastReceipt = null }, progress with { RetryAllowed = true },
            failed with { SaveError = null }, committed with { SaveError = "Failed" },
            committed with { LastReceipt = currentReceipt with { PartyTeamId = 80 } },
            committed with { LastReceipt = currentReceipt with { Participants = currentReceipt.Participants.SetItem(0,
                currentReceipt.Participants[0] with { DeckSelectionId = "" }) } }
        }) check(Reject(() => EncounterFacts.Validate(invalid)),
            "Public counters, save state and receipt ownership cannot contradict one another");
        string json = JsonSerializer.Serialize(committed);
        var decoded = JsonSerializer.Deserialize<PartyProgress>(json)!;
        check(EncounterCheckpoint.For(state, decoded) == EncounterCheckpointStatus.Committed,
            "Immutable durable receipt identities and canonical role/save enums survive a neutral JSON round trip");
    }

    internal static EncounterReceipt Receipt(string game, string encounter) => new("receipt-" + encounter, "party", encounter, game,
        MatchRulesets.CooperativeCommander, 30, new(game, MatchOutcomeKind.Win, [7], [30], "AllOpposingTeamsLost"),
        [new("person-a", 0, 7, 30, MatchSeatRole.Party, "selection-a"), new("person-b", 1, 13, 80, MatchSeatRole.Opposition, "selection-b"),
         new("person-c", 2, 21, 30, MatchSeatRole.Party, "selection-c"), new("person-d", 3, 42, 80, MatchSeatRole.Opposition, "selection-d")],
        new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
    internal static GameSnapshot State(string game, string encounter) {
        var receipt = Receipt(game, encounter);
        return new(game, "finished", null, "Defeat", 21, 12, "Cleanup", "CLEANUP", 7,
            receipt.Participants.Reverse().Select(person => new Player(person.PlayerId, "Same name", 40, false, person.PlayerId != 7,
                ImmutableDictionary<string, int>.Empty, []) { ParticipantId = person.ParticipantId, TeamId = person.TeamId }).ToImmutableArray(), [], null, null) {
            Encounter = new("party", encounter), Outcome = receipt.Outcome
        };
    }
    private static bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
}
