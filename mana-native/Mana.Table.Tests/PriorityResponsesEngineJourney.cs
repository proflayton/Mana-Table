using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// A controlled opportunity in the existing warm JVM, not a natural game or
// strategy result. Ordinary opening decisions and actual engine Auto do the work.
internal static class PriorityResponsesEngineJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths, string productionJar)
    {
        Directory.CreateDirectory(paths.Profile);
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar, verifyPackage: false);
        var checks = new List<string>(); string? failure = null;
        try {
            await using var engine = new ForgeEngine(paths); await engine.InitializeAsync();
            await RunAsync(engine, paths.Profile, (passed, description) => {
                if (!passed) throw new InvalidDataException(description);
                checks.Add(description);
            });
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(paths.Profile, "priority-responses-engine-result.json"), JsonSerializer.Serialize(new {
            passed = failure == null, error = failure, runtime, checks,
            scope = "Controlled real-rules availability/Auto transport opportunity; not natural strategy, mixed-human qualification or UX acceptance."
        })); }
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "priority-responses-engine"); Directory.CreateDirectory(directory);
        var imported = await engine.ImportAsync("Priority response transport opportunity",
            "Commander\n1 Traxos, Scourge of Kroog\n\nDeck\n99 Wastes");
        var details = await engine.OpenDeckAsync(imported.Id);
        string deck = "deck:" + (await engine.SaveDeckAsync(details.Id, details.Revision)).Id;
        await engine.EnableAutomatedSeatsAsync();
        check(engine.SupportsAutomatedPriorityResponses, "The actual Java process negotiates independent response preparation");
        var started = await engine.StartSimulationAsync([deck, deck, deck, deck]);
        var provider = new Provider(engine);
        var host = new AiTableHost(provider, _ => new CasualCommanderPolicy(), 261110);
        var watch = Stopwatch.StartNew(); bool disposed = false; string? failure = null;
        try {
            while (!host.PriorityContinuations.Any(item => item.CertifiedAutomaticPasses > 0)) {
                if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("The actual response-preparation opportunity exceeded its fixed45s watchdog.");
                await host.StepAsync();
                if (host.LastFault != null) throw new InvalidDataException("Actual priority preparation host fault: " + JsonSerializer.Serialize(host.LastFault));
                await Task.Delay(1);
            }
            await provider.ObserveAutomatedSeatsAsync();
            check(provider.Prepared > 0 && provider.UnknownPreparations > 0 && provider.Grants > 0 && provider.AllGrantsFreshAbsent,
                "The real Java producer and native decoder expose Unknown, prepare without pass consent, then grant only a fresh owned Absent input");
            check(host.PriorityContinuations.Any(item => item.AcceptedAcknowledgements > 0 && item.AcceptedGrants > 0
                    && item.CertifiedAutomaticPasses > 0) && provider.NoPreparationConsentMutation,
                "Actual bot Auto certifies a rules pass after independent preparation while preparation itself adds no grant or sequence");
            check(provider.HistoryAfterPrepared == provider.Prepared && provider.HistoryAudienceMatched,
                "Actual owned history remains readable immediately after Prepared, before another rich decision read restores action offers");
            await host.DisposeAsync(); disposed = true;
            check(host.PriorityContinuations.All(item => item.Retired)
                    && host.PriorityContinuations.Where(item => item.AcceptedGrants > 0).All(item => item.AcceptedWithdrawals > 0),
                "Actual prepared Auto owners retain and acknowledge their original negative fences on retirement");
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            try { if (!disposed) await host.DisposeAsync(); }
            finally {
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
                    passed = failure == null, failure, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    provider.Prepared, provider.UnknownPreparations, provider.Grants, provider.AllGrantsFreshAbsent,
                    provider.NoPreparationConsentMutation, provider.HistoryAfterPrepared, provider.HistoryAudienceMatched,
                    events = provider.Events, owners = host.PriorityContinuations,
                    scope = "Controlled four-bot basic-land opportunity using ordinary policy and production process/codec/host; no forced availability or fabricated pass."
                }, new JsonSerializerOptions { WriteIndented = true }));
                await engine.StopSimulationAsync(started.Id);
            }
        }
    }

    private sealed class Provider(ForgeEngine engine) : IAutomatedSeatProvider, IAutomatedPriorityContinuation
    {
        private readonly Dictionary<int, (GameSnapshot State, long Read)> latest = [];
        private readonly Dictionary<int, long> preparedAt = [];
        private readonly Dictionary<int, PriorityContinuationState> preparedConsent = [];
        private long reads;
        internal int Prepared, UnknownPreparations, Grants, HistoryAfterPrepared;
        internal bool AllGrantsFreshAbsent = true, NoPreparationConsentMutation = true, HistoryAudienceMatched = true;
        internal List<object> Events = [];
        public bool SupportsAutomatedPriorityContinuation => engine.SupportsAutomatedPriorityContinuation;
        public bool SupportsAutomatedPriorityResponses => engine.SupportsAutomatedPriorityResponses;
        public long AutomatedPriorityEpoch => engine.AutomatedPriorityEpoch;
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => engine.EnableAutomatedSeatsAsync(cancellation);
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            var observed = await engine.ObserveAutomatedSeatsAsync(cancellation); reads++;
            foreach (var row in observed) {
                latest[row.SeatId] = (row.State, reads);
                if (preparedConsent.Remove(row.SeatId, out var before)) {
                    var after = row.State.PriorityContinuation;
                    NoPreparationConsentMutation &= after != null && before.Sequence == after.Sequence
                        && before.GrantId == after.GrantId && before.Mode == after.Mode && before.Status == after.Status
                        && before.ReasonCode == after.ReasonCode && before.AutomaticPasses == after.AutomaticPasses
                        && before.PhaseStops.SequenceEqual(after.PhaseStops);
                }
            }
            return observed;
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
            => engine.ExecuteAutomatedAsync(seatId, command, cancellation);
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
            => engine.ReportAutomationFaultAsync(gameId, seatId, reasonCode, cancellation);
        public async Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId,
            AutomatedPriorityControl control, CancellationToken cancellation = default)
        {
            var before = latest[seatId];
            if (control is AutomatedPriorityControl.PrepareResponses) {
                if (before.State.Decision?.Payload is PriorityPayload { ResponseAvailability: PriorityResponseAvailability.Unknown }) UnknownPreparations++;
                preparedAt[seatId] = before.Read;
            }
            if (control is AutomatedPriorityControl.NoResponse) {
                Grants++;
                AllGrantsFreshAbsent &= before.State.Decision?.Payload is PriorityPayload { ResponseAvailability: PriorityResponseAvailability.Absent }
                    && (!preparedAt.TryGetValue(seatId, out long prior) || before.Read > prior);
            }
            var result = await engine.ExecuteAutomatedPriorityControlAsync(epoch, seatId, control, cancellation);
            if (result is AutomatedPriorityControlResult.Prepared prepared) {
                Prepared++;
                // Preparation retired this input's action scope. Its passive
                // audience still owns history, without a gratuitous rich read.
                var history = await engine.AutomatedHistoryAsync(prepared.Receipt.GameId, seatId, 0);
                HistoryAfterPrepared++;
                HistoryAudienceMatched &= history.GameId == prepared.Receipt.GameId && history.ViewerId == seatId;
                Events.Add(new { seatId, before.Read, decision = prepared.Receipt.DecisionId, availability = prepared.Receipt.ResponseAvailability.ToString(),
                    sequence = before.State.PriorityContinuation?.Sequence, automaticPasses = before.State.PriorityContinuation?.AutomaticPasses });
                if (before.State.PriorityContinuation is { } consent) preparedConsent[seatId] = consent;
            }
            return result;
        }
    }
}
