using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// One already initialized production provider, real rules concessions and the
// ordinary scoped native AI host. No encounter-specific rules fixture is used.
internal static class CooperativeEncounterEngineJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        var checks = new List<string>();
        var cleanupErrors = new List<string>();
        var elapsed = Stopwatch.StartNew();
        Exception? failure = null;
        bool completed = false;
        var engine = new ForgeEngine(paths);
        try {
            await engine.InitializeAsync();
            var deck = await engine.ImportAsync("Cooperative native functional check", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed");
            await RunAsync(engine, paths.Profile, deck.Id, (passed, description) => {
                if (!passed) throw new InvalidOperationException(description);
                checks.Add(description);
                File.AppendAllText(Path.Combine(paths.Profile, "cooperative-encounter-progress.log"), description + "\n");
            });
            completed = true;
        } catch (Exception error) { failure = error; throw; }
        finally {
            try { await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { cleanupErrors.Add(error.ToString()); if (failure == null) failure = error; }
            File.WriteAllText(Path.Combine(paths.Profile, "cooperative-encounter-standalone-results.json"), JsonSerializer.Serialize(new {
                passed = completed && failure == null, checks, error = failure?.ToString(), cleanupErrors,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                scope = "Focused functional verification against the explicitly supplied local Java/resources and one reused initialized JVM. This standalone entry does not establish source-bound production package provenance; the same journey also runs inside the source-bound CI suite."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (completed && failure != null) throw new InvalidOperationException("Cooperative standalone provider cleanup failed.", failure);
        }
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, string deckId, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "cooperative-encounter-engine");
        Directory.CreateDirectory(directory);
        var elapsed = Stopwatch.StartNew();
        var evidence = new List<object>();
        var cleanupErrors = new List<string>();
        Exception? failure = null;
        bool connected = false, completed = false;
        Lobby? lobby = null;
        GameSnapshot? state = null;
        var host = new AiTableHost(engine, _ => new CasualCommanderPolicy(), 261102, new(TimeSpan.FromSeconds(3), 2048));
        try {
            await engine.EnableAutomatedSeatsAsync();
            check(engine.SupportsMatchSetup && engine.SupportsTempo,
                "The real native provider explicitly advertises match setup and timing consent capabilities");
            lobby = await engine.HostAsync(3, false); connected = true;
            check(lobby.MatchSetupCapabilities?.RulesetIds.Contains(MatchRulesets.CooperativeCommander) == true,
                "The real hosted lobby advertises the versioned cooperative ruleset without inferring it from its AI seats");
            lobby = await engine.ConfigureMatchAsync(lobby.TableId, lobby.Revision, Specification(1, 2));
            lobby = await engine.ConfigureTempoAsync(lobby.TableId, TempoPreset.Relaxed, TempoExpiry.Warn);
            lobby = await AssignAndSelect(lobby);
            var initialRoster = lobby.Seats.Select(seat => seat.ParticipantId).ToImmutableArray();
            string partyId = lobby.PartyProgress!.PartyId;
            check(lobby.Seats.Length == 3 && initialRoster.Distinct().Count() == 3
                    && lobby.Seats.All(seat => seat.DeckPreview is { SelectionId.Length: > 0 }),
                "Real co-op setup captures distinct participant enrollments and each provider-owned public deck selection");
            check(await Reject(() => engine.SetMatchReadyAsync(lobby.TableId, "stale-setup", true, lobby.Tempo!.Revision))
                    && await Reject(() => engine.SetMatchReadyAsync(lobby.TableId, lobby.MatchSetupConsent!, true, "stale-tempo")),
                "The actual native ready endpoint rejects either stale setup consent or stale tempo consent independently");

            string? previousGame = null, previousEncounter = null;
            for (int encounter = 1; encounter <= 2; encounter++) {
                lobby = await engine.LobbyAsync();
                await engine.SetMatchReadyAsync(lobby.TableId, lobby.MatchSetupConsent!, true, lobby.Tempo!.Revision);
                lobby = await engine.LobbyAsync();
                check(lobby.CanStart && lobby.Seats.Single(seat => seat.Local).Ready,
                    "One aggregate Ready acknowledges both displayed policies before the real encounter starts");
                await engine.StartAsync();
                state = await Opening(3);
                ValidateRoster(state, lobby);
                evidence.Add(PublicFrame("opening-" + encounter, state, lobby));
                string gameId = state.Id;
                string encounterId = state.Encounter!.EncounterId;
                check(gameId != previousGame && encounterId != previousEncounter,
                    "Each separately consented real rematch receives a new game identity and encounter attempt identity");
                previousGame = gameId; previousEncounter = encounterId;
                await engine.ConcedeAsync(gameId);
                (state, lobby) = await SavedDefeat(gameId, encounter);
                check(state.Outcome is { Kind: MatchOutcomeKind.Win } && MatchOutcomes.ForViewer(state) == PlayerMatchOutcome.Loss
                        && state.Viewer!.Eliminated && lobby.PartyProgress is { Victories: 0 } progress
                        && progress.CompletedEncounters == encounter && progress.LastReceipt?.Outcome.GameId == gameId,
                    "A real party concession publishes global rules loss and a committed defeat receipt without awarding a victory");
                var receipt = lobby.PartyProgress!.LastReceipt!;
                check(receipt.Participants.All(participant => lobby.Seats.Single(seat => seat.ParticipantId == participant.ParticipantId)
                            .DeckPreview?.SelectionId == participant.DeckSelectionId)
                        && receipt.EncounterId == encounterId && receipt.PartyId == partyId,
                    "The real persisted receipt pins this encounter's enrollment and captured public deck-selection identities");
                evidence.Add(PublicFrame("saved-defeat-" + encounter, state, lobby));
                await engine.ReturnToLobbyAsync();
                lobby = await engine.LobbyAsync();
                check(!lobby.MatchActive && lobby.PartyProgress?.PartyId == partyId
                        && lobby.Seats.Select(seat => seat.ParticipantId).SequenceEqual(initialRoster)
                        && lobby.MatchSetup?.RulesetId == MatchRulesets.CooperativeCommander
                        && !lobby.Seats.Single(seat => seat.Local).Ready,
                    "Return retains the same actual party and roster, revokes human Ready, and permits a separately consented rematch");
            }

            lobby = await engine.ConfigureMatchAsync(lobby.TableId, lobby.Revision, Specification(4, 2));
            lobby = await AssignAndSelect(lobby);
            check(lobby.PartyProgress is { CompletedEncounters: 2, Victories: 0 } && lobby.PartyProgress.PartyId == partyId,
                "Expanding the real party to four allies against two opponents preserves its prior durable defeats");
            await engine.SetMatchReadyAsync(lobby.TableId, lobby.MatchSetupConsent!, true, lobby.Tempo!.Revision);
            await engine.StartAsync();
            state = await Opening(6);
            ValidateRoster(state, lobby);
            check(state.Players.Count(player => PlayerRelations.ForViewer(state, player.Id) == PlayerRelation.Ally) == 3
                    && state.Players.Count(player => PlayerRelations.ForViewer(state, player.Id) == PlayerRelation.Opponent) == 2,
                "The real six-player native projection identifies three allies and two opponents independently of names and seating");
            var botViews = await engine.ObserveAutomatedSeatsAsync();
            check(botViews.Length == 5 && botViews.All(bot => AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain))
                    && botViews.All(bot => bot.State.Players.Where(player => player.Id != bot.SeatId).All(player => player.Zone("Hand").Cards.IsEmpty)),
                "Every real automated ally/enemy receives its own authorized hand while the other participants' hands remain private");
            await engine.ConcedeAsync(state.Id);
            state = await ObserveUntil(current => current.Viewer?.Eliminated == true, "the six-seat host's actual rules loss");
            check(state.Status == "playing" && state.Outcome == null
                    && state.Players.Count(player => PlayerRelations.ForViewer(state, player.Id) == PlayerRelation.Ally && !player.Eliminated) == 3,
                "The real host's individual loss leaves all three allied AI participants and the shared encounter active");
            long eliminatedBoard = state.BoardRevision;
            string continuedGame = state.Id;
            var alliedSeats = state.Players.Where(player => PlayerRelations.ForViewer(state, player.Id) == PlayerRelation.Ally)
                .Select(player => player.Id).ToImmutableHashSet();
            int previousActions = AcceptedSourceActions(continuedGame, alliedSeats);
            do {
                Guard("native AI continuation after host concession");
                await host.StepAsync();
                if (host.LastFault != null) throw new InvalidOperationException("Native co-op AI host fault: " + JsonSerializer.Serialize(host.LastFault));
                state = await engine.ObserveAsync() ?? throw new IOException("The six-seat encounter disappeared after host concession.");
                EnsureHealthy(state);
                if (state.Status == "finished") throw new InvalidOperationException("The six-seat encounter ended before its allied native AI continuation was witnessed.");
            } while (AcceptedSourceActions(continuedGame, alliedSeats) <= previousActions || state.BoardRevision <= eliminatedBoard);
            lobby = await engine.LobbyAsync();
            check(state.Status == "playing" && state.Outcome == null && state.Decision == null
                    && state.Players.Count(player => PlayerRelations.ForViewer(state, player.Id) == PlayerRelation.Ally && !player.Eliminated) == 3
                    && lobby.PartyProgress is { CompletedEncounters: 2, Victories: 0, SaveState: EncounterSaveState.Ready },
                "After the real host concedes, native AI allies keep playing accepted source actions and no unfinished encounter is counted");
            evidence.Add(PublicFrame("six-seat-native-ai-continues", state, lobby));
            evidence.Add(new { stage = "allied-native-action-witness", game = continuedGame, alliedSeats,
                acceptedBefore = previousActions, acceptedAfter = AcceptedSourceActions(continuedGame, alliedSeats), eliminatedBoard, observedBoard = state.BoardRevision });
            completed = true;
        } catch (Exception error) { failure = error; throw; }
        finally {
            try { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { cleanupErrors.Add("AI host: " + error); if (failure == null) failure = error; }
            if (connected) {
                try { await engine.LeaveAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { cleanupErrors.Add("Leave unfinished table: " + error); if (failure == null) failure = error; }
            }
            File.WriteAllText(Path.Combine(directory, "cooperative-encounter-results.json"), JsonSerializer.Serialize(new {
                passed = completed && failure == null, error = failure?.ToString(), cleanupErrors, elapsedSeconds = elapsed.Elapsed.TotalSeconds, evidence,
                scope = "Reused current native provider and initialized JVM; real setup/consent/rules concessions/durable receipts/native AI continuation. The last six-seat game is explicitly left unfinished, never awarded a synthetic win."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (completed && failure != null) throw new InvalidOperationException("Cooperative native journey cleanup failed.", failure);
        }

        int AcceptedSourceActions(string game, ImmutableHashSet<int> seats) => host.Histories.SelectMany(history => history.Decisions)
            .Count(record => record.Stamp.GameId == game && seats.Contains(record.Stamp.SeatId)
                && record.Outcome == AiDecisionOutcome.Accepted && record.Command == nameof(UseOfferedAction));
        void Guard(string stage) {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(75)) throw new TimeoutException("The cooperative native journey exceeded its bounded deadline during " + stage + ".");
        }
        void EnsureHealthy(GameSnapshot current) {
            if (current.Status == "error") throw new InvalidOperationException(current.Error ?? "The cooperative rules provider stopped.");
        }
        async Task<Lobby> AssignAndSelect(Lobby current) {
            foreach (var assignment in current.MatchSetup!.Seats.Where(seat => seat.Index != 0)) {
                Guard("assigning real AI decks");
                string choice = assignment.Index % 2 == 0 ? "red" : "green";
                if (!current.AiOpponents.Any(opponent => opponent.Id == choice)) throw new InvalidOperationException("The current catalog omitted a bundled Commander opponent.");
                current = await engine.SetSeatAsync(current.TableId, assignment.Index, "AI", choice);
            }
            var selected = await engine.OpenDeckAsync(deckId);
            current = await engine.SelectDeckAsync(new(current.TableId, selected.Id, selected.LibraryToken));
            MatchSetupFacts.Validate(current);
            return current;
        }
        async Task<GameSnapshot> Opening(int players) {
            bool kept = false;
            while (true) {
                Guard("opening a real " + players + "-seat encounter");
                var current = await engine.ObserveAsync() ?? throw new IOException("The cooperative match did not start.");
                EnsureHealthy(current);
                if (current.Status == "finished") throw new InvalidOperationException("The encounter ended before its opening hand was kept.");
                if (!kept && current.Decision is { Payload: OpeningHandPayload { Keep.Enabled: true } } opening) {
                    await engine.ExecuteAsync(new KeepOpeningHand(current.Id, opening.Id)); kept = true;
                }
                await host.StepAsync();
                if (host.LastFault != null) throw new InvalidOperationException("Co-op opening AI host fault: " + JsonSerializer.Serialize(host.LastFault));
                if (kept && current.Status == "playing" && current.Decision?.Payload is not OpeningHandPayload
                    && current.Players.Length == players && current.Viewer!.Zone("Hand").Count > 0) return current;
                await Task.Delay(5);
            }
        }
        async Task<GameSnapshot> ObserveUntil(Func<GameSnapshot, bool> predicate, string stage) {
            while (true) {
                Guard(stage);
                var current = await engine.ObserveAsync() ?? throw new IOException("The cooperative match disappeared during " + stage + ".");
                EnsureHealthy(current);
                if (predicate(current)) return current;
                await host.StepAsync(); await Task.Delay(5);
            }
        }
        async Task<(GameSnapshot State, Lobby Lobby)> SavedDefeat(string game, int expectedCount) {
            while (true) {
                Guard("committing the actual party defeat");
                var current = await engine.ObserveAsync() ?? throw new IOException("The completed cooperative match disappeared.");
                EnsureHealthy(current);
                var table = await engine.LobbyAsync();
                if (table.PartyProgress?.SaveState == EncounterSaveState.Failed) throw new IOException(table.PartyProgress.SaveError ?? "The encounter receipt was not saved.");
                if (current.Id != game) throw new IOException("The match was replaced before its encounter receipt committed.");
                if (EncounterCheckpoint.For(current, table.PartyProgress) == EncounterCheckpointStatus.Committed
                    && table.PartyProgress?.CompletedEncounters == expectedCount) return (current, table);
                await host.StepAsync(); await Task.Delay(5);
            }
        }
        void ValidateRoster(GameSnapshot current, Lobby captured) {
            check(current.Encounter is { PartyId.Length: > 0, EncounterId.Length: > 0 } identity
                    && identity.PartyId == captured.PartyProgress?.PartyId
                    && current.Players.Length == captured.Seats.Length
                    && captured.Seats.All(seat => current.Players.Single(player => player.ParticipantId == seat.ParticipantId).TeamId == seat.TeamId)
                    && current.Players.Where(player => player.Id != current.ViewerId).All(player => player.Zone("Hand").Cards.IsEmpty && player.Zone("Hand").TopCard == null),
                "The real native match binds lobby enrollments to actual rules player/team IDs while hiding every other hand");
        }
    }

    private static MatchSetupSpecification Specification(int party, int opposition) => new(MatchRulesets.CooperativeCommander,
        Enumerable.Range(0, party + opposition).Select(index => new MatchSeatAssignment(index, index < party ? 0 : 1,
            index < party ? MatchSeatRole.Party : MatchSeatRole.Opposition)).ToImmutableArray());
    private static async Task<bool> Reject(Func<Task> command) {
        try { await command(); return false; }
        catch (EngineCommandRejectedException) { return true; }
    }
    private static object PublicFrame(string stage, GameSnapshot state, Lobby lobby) => new {
        stage, state.Id, state.Revision, state.BoardRevision, state.Status, state.Encounter, state.Outcome,
        players = state.Players.Select(player => new { player.Id, player.ParticipantId, player.TeamId, player.Eliminated,
            handCount = player.Zone("Hand").Count, disclosedHandFaces = player.Zone("Hand").Cards.Length }),
        progress = lobby.PartyProgress
    };
}
