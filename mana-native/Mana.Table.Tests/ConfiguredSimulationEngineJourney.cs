using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// A real provider/identity/privacy prefix. Repeated legal decks intentionally
// isolate this protocol proof; varied natural games belong to AiEngineCampaign.
internal static class ConfiguredSimulationEngineJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        await using var engine = new ForgeEngine(paths);
        await engine.InitializeAsync();
        var deck = await engine.ImportAsync("Configured boundary symmetry control", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed");
        await RunAsync(engine, paths.Profile, deck.Id, (passed, detail) => {
            File.AppendAllText(Path.Combine(paths.Profile, "configured-simulation-progress.log"), detail + "\n");
            if (!passed) throw new InvalidOperationException(detail);
        });
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, string deckId, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "configured-simulation-boundary"); Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        var checks = new List<string>();
        var observedHands = new HashSet<int>();
        var elapsed = Stopwatch.StartNew();
        ConfiguredSimulationStart? started = null;
        GameSnapshot? state = null;
        ImmutableArray<AutomatedSeatObservation> lastOwned = [];
        Exception? primary = null;
        var cleanupFailures = new List<Exception>();
        int comparisons = 0, acceptedActions = 0;
        void Verify(bool passed, string detail) { check(passed, detail); if (passed) checks.Add(detail); }
        using var diagnostics = new AiCampaignDiagnostics(directory);
        var observed = new AiCampaignProvider(engine, diagnostics);
        await using var host = new AiTableHost(observed, _ => diagnostics.Record(new CasualCommanderPolicy()), 261102,
            new(TimeSpan.FromSeconds(3), 2048));
        try {
            Verify(engine.SupportsConfiguredSimulation, "The real provider explicitly advertises configured-simulation capability");
            var details = await engine.OpenDeckAsync(deckId);
            var saved = await engine.SaveDeckAsync(deckId, details.Revision);
            if (saved.SaveError.Length > 0) throw new InvalidDataException(saved.SaveError);
            var semantic = await engine.WorkshopAsync(deckId, saved.Revision);
            var descriptor = (await engine.PrepareSoloAsync(deckId)).Opponents.Single(opponent =>
                opponent.Id.StartsWith("deck:" + deckId + ":", StringComparison.Ordinal));
            var setup = new ConfiguredSimulationSetup(new(MatchRulesets.CooperativeCommander,
                [new(0, 17, MatchSeatRole.Party), new(1, 42, MatchSeatRole.Opposition),
                 new(2, 17, MatchSeatRole.Party), new(3, 42, MatchSeatRole.Opposition)]),
                [descriptor.Id, descriptor.Id, descriptor.Id, descriptor.Id]);
            await engine.EnableAutomatedSeatsAsync();
            started = await engine.StartConfiguredSimulationAsync(setup); state = started.State;
            ConfiguredSimulationFacts.ValidateState(started, state);
            observed.BindConfigured(started);
            Verify(started.Participants.Select(person => person.PlayerId).Distinct().Count() == 4
                    && state.Encounter == null && state.Players.All(player => player.Life == 40),
                "Real configured startup returns four independently registered players on the exact accepted teams, without encounter progress");
            foreach (var person in started.Participants) {
                var relation = PlayerRelations.Between(state, started.Participants.Single(item => item.Index == 0).PlayerId, person.PlayerId);
                Verify(person.Index == 0 ? relation == PlayerRelation.Self
                        : relation == (person.Index == 2 ? PlayerRelation.Ally : PlayerRelation.Opponent),
                    "Native actual player relations follow explicit nonadjacent team assignments at slot " + person.Index);
            }
            bool invalidRejected = false;
            try { await engine.StartConfiguredSimulationAsync(setup with { Decks = [descriptor.Id] }); }
            catch (InvalidOperationException) { invalidRejected = true; }
            var retained = await engine.ObserveSimulationAsync() ?? throw new IOException("The configured game disappeared.");
            ConfiguredSimulationFacts.ValidateState(started, retained);
            Verify(invalidRejected && retained.Id == state.Id,
                "An invalid configured request fails before replacing the current real rules game");
            while (observedHands.Count != 4 || acceptedActions == 0 || comparisons == 0) {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("Configured identity/privacy prefix exceeded its bound; observed hands="
                        + observedHands.Count + ", accepted=" + acceptedActions + ", comparisons=" + comparisons);
                state = await engine.ObserveSimulationAsync() ?? throw new IOException("The configured game disappeared.");
                ConfiguredSimulationFacts.ValidateState(started, state);
                if (state.Status == "finished") throw new InvalidOperationException("The real configured game ended before its ownership prefix completed.");
                lastOwned = await observed.ObserveAutomatedSeatsAsync();
                foreach (var item in lastOwned) {
                    ConfiguredSimulationFacts.ValidateOwnedState(started, item.State);
                    Verify(item.SeatId == item.State.ViewerId
                            && AiCampaignVisibility.PrivateWindowsAreDisclosed(item.State, item.Semantics, item.Domain),
                        "A configured bot sees only its actual audience, including privacy from its teammate: " + item.SeatId);
                    var hand = item.State.Viewer!.Zone("Hand");
                    if (hand.Count > 0 && hand.Cards.Length == hand.Count) observedHands.Add(item.SeatId);
                    if (item.State.BoardRevision == state.BoardRevision) {
                        Verify(AiWholeSeatEngineCheck.ComparePublicProjection(state, item.State, directory),
                            "Same-revision configured spectator and owned public projections agree for actual player " + item.SeatId);
                        comparisons++;
                    }
                }
                await host.StepAsync();
                if (host.LastFault != null) throw new InvalidOperationException("Configured whole-seat host fault: " + JsonSerializer.Serialize(host.LastFault));
                acceptedActions = host.Histories.SelectMany(history => history.Decisions).Count(record => record.Outcome == AiDecisionOutcome.Accepted);
                await Task.Delay(1);
            }
            Verify(observedHands.Count == 4 && acceptedActions > 0 && comparisons > 0,
                "All four real automated audiences retain independent hands and perform current policy actions after accepted roster validation");
            Verify(state.Outcome == null && state.Encounter == null,
                "The bounded configured prefix grants neither a fabricated natural outcome nor live-party progress");
            File.WriteAllText(Path.Combine(directory, "accepted-setup.json"), JsonSerializer.Serialize(new {
                semantic.VersionId, sourceAssignment = descriptor.Id, started.Setup, started.Participants,
                scope = "Host-private symmetry control; not varied-deck qualification or natural completion."
            }, json));
        } catch (Exception error) { primary = error; throw; }
        finally {
            // A rejected provider batch is still private diagnostic evidence;
            // the await above deliberately never accepted it as a policy frame.
            lastOwned = observed.Last;
            try {
                File.WriteAllText(Path.Combine(directory, "pre-cleanup-public-state.json"), JsonSerializer.Serialize(state, json));
                File.WriteAllText(Path.Combine(directory, "private-final-observations.json"), JsonSerializer.Serialize(lastOwned, json));
            } catch (Exception error) { cleanupFailures.Add(error); }
            try { if (started != null) await engine.StopSimulationAsync(started.State.Id); }
            catch (Exception error) { cleanupFailures.Add(error); }
            try {
                File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new {
                    passed = primary == null && cleanupFailures.Count == 0, checks, error = primary?.ToString(),
                    cleanupErrors = cleanupFailures.Select(error => error.ToString()), observedHands, acceptedActions,
                    comparisons, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    scope = "Actual protocol/privacy/identity prefix; explicitly unfinished teardown is not a natural outcome."
                }, json));
            } catch (Exception error) { cleanupFailures.Add(error); }
            if (primary == null && cleanupFailures.Count > 0)
                throw new AggregateException("Configured simulation proof or teardown failed.", cleanupFailures);
        }
    }
}
