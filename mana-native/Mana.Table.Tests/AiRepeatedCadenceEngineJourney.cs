using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Real Forge simulation with four automated controllers, two finite client
// drivers and two AI drivers. This is not a networked two-human lobby fixture.
internal static class AiRepeatedCadenceEngineJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths, string productionJar)
    {
        Directory.CreateDirectory(paths.Profile);
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar);
        var checks = new List<string>(); string? failure = null;
        try {
            await using var engine = new ForgeEngine(paths); await engine.InitializeAsync();
            await RunAsync(engine, paths.Profile, (passed, description) => {
                if (!passed) throw new InvalidDataException(description);
                checks.Add(description);
            });
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(paths.Profile, "ai-cadence-engine-result.json"), JsonSerializer.Serialize(new {
            passed = failure == null, error = failure, runtime, checks,
            scope = "Local development real-rules simulation; not exact-source package release proof or a networked mixed lobby."
        })); }
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "ai-cadence-fifty"); Directory.CreateDirectory(directory);
        string actor = await Save("AI cadence fifty forced draws"), other = await Save("AI cadence harmless opponent");
        await engine.EnableAutomatedSeatsAsync();
        var started = await engine.StartSimulationAsync([actor, other, other, other]);
        var frames = new List<GameSnapshot>(); var replies = new List<object>();
        var setup = Stopwatch.StartNew(); var workload = new Stopwatch();
        var provider = new BotSeats(engine, workload);
        var humans = Enumerable.Range(0, 2).Select(seat => new ClientSession(new SeatConnection(provider, seat))).ToArray();
        var intents = new FiniteResolutionIntent?[2];
        var terminals = new[] { new HashSet<string>(), new HashSet<string>() };
        for (int seat = 0; seat < 2; seat++) {
            int capturedSeat = seat;
            humans[seat].Changed += (_, _) => {
                foreach (var item in humans[capturedSeat].ExecutionChanges.Transitions.Where(item => item.Step == ExecutionStep.Complete))
                    terminals[capturedSeat].Add(item.ExecutionId);
            };
        }
        var answered = new HashSet<(int Seat, string Decision, long Revision)>();
        GameSnapshot? last = null; string source = "", game = started.Id;
        ImmutableHashSet<string> captured = [];
        var draws = new HashSet<long>();
        var policy = new CountingPolicy();
        CadenceProbe? pacing = null;
        Exception? failure = null;
        try {
            while (captured.IsEmpty) {
                if (setup.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The real cadence setup exceeded its fixed30s watchdog.");
                await provider.ObserveAutomatedSeatsAsync();
                var seats = provider.Published; last = seats.Single(item => item.SeatId == 0).State;
                RequirePlaying(last);
                if (last.Stack.Length == 50 && seats.Any(item => item.State.Decision?.Payload is PriorityPayload)) {
                    var groups = last.Stack.Select(item => item.Repetition).ToArray();
                    source = CardViews.Identity(last.Players[0].Zone("Battlefield").Cards.Single(card => card.Name == "Grim Haruspex"));
                    check(groups.All(item => item != null && item.SourceCardId == source) && groups.Distinct().Count() == 1
                            && last.Stack.All(item => item.Kind == StackActionKind.TriggeredAbility)
                            && !last.Players[0].Zone("Battlefield").Cards.Any(card => card.Name == "Memnite"),
                        "The real Pestilence activation kills exactly50Memnites and publishes50same-source typed Haruspex draw triggers before timed host continuation");
                    captured = last.Stack.Select(item => item.ExecutionId).ToImmutableHashSet(StringComparer.Ordinal);
                    break;
                }
                bool acted = false;
                foreach (var observation in seats) {
                    var state = observation.State;
                    if (state.Decision is not { } decision || !answered.Add((observation.SeatId, decision.Id, state.Revision))) continue;
                    var command = SetupAnswer(observation);
                    if (!GameCommandPermissions.Allows(state, command)) throw new InvalidDataException("The cadence setup did not use its current ordinary input.");
                    await engine.ExecuteAutomatedAsync(observation.SeatId, command);
                    replies.Add(new { stage = "setup", observation.SeatId, command, state.Revision }); acted = true; break;
                }
                if (!acted) await Task.Delay(2);
            }
            int hand = last!.Players[0].Zone("Hand").Count;
            int setupDiscoveries = provider.PrivateObservations;
            workload.Start();
            pacing = new(last.Stack.Select(item => item.Id).ToImmutableHashSet(StringComparer.Ordinal), () => provider.Commands, workload);
            await using var host = new AiTableHost(provider, _ => policy, 261029,
                pacing: pacing);
            // Observe the actual queue for both bot audiences before either
            // finite client can yield it. The shipping monotonic clock is used.
            await host.StepAsync();
            int steps = 0;
            while (true) {
                if (workload.Elapsed > TimeSpan.FromSeconds(30) || steps++ >= 2048)
                    throw new TimeoutException("The real50-trigger cadence exceeded its fixed30s/2048-step workload budget.");
                await host.StepAsync();
                var seats = provider.Published; last = seats.Single(item => item.SeatId == 0).State;
                RequirePlaying(last);
                if (frames.Count == 0 || frames[^1].Revision != last.Revision) frames.Add(last);
                foreach (var entry in last.Activity.Where(item => item.Kind == "draw" && captured.Contains(item.ExecutionId))) draws.Add(entry.Id);
                foreach (int seat in Enumerable.Range(0, 2)) {
                    var state = seats.Single(item => item.SeatId == seat).State;
                    humans[seat].Pump(); humans[seat].Accept(state);
                    if (intents[seat] == null && state.Decision?.Payload is PriorityPayload)
                        intents[seat] = FiniteResolutionIntent.TryArm(state, state.Stack.Select(item => item.Id));
                    if (!humans[seat].Busy && intents[seat]?.IsActive == true)
                        humans[seat].AdvanceFinite(intents[seat]!, (_, _) => false);
                }
                if (host.LastFault != null) throw new InvalidDataException("Actual timed bot host faulted: " + JsonSerializer.Serialize(host.LastFault));
                if (last.Stack.IsEmpty && captured.All(id => ExecutionFrames.Find(last, id)?.Outcome == ExecutionOutcome.Resolved)
                    && intents.All(intent => intent?.Progress.CompletedCount == 50)) break;
                await Task.Delay(1);
            }
            check(last.Players[0].Zone("Hand").Count == hand + 50 && draws.Count == 50
                    && intents.All(intent => intent?.Progress is { CapturedCount: 50, CompletedCount: 50, ResolvedCount: 50 })
                    && terminals.All(ids => captured.IsSubsetOf(ids)),
                "Both finite client drivers observe every real terminal receipt and fifty actual Haruspex draws through the production native adapter");
            check(provider.FirstCommandSeconds >= 2.4 && provider.Commands == 100 && policy.Evaluations == 100
                    && provider.Decisions.Count == 100 && host.LastFault == null && workload.Elapsed < TimeSpan.FromSeconds(30)
                    && pacing.UnexpectedWaits.Count == 0,
                "The actual two-seat bot host keeps one initial2.4s beat, reevaluates100fresh legal priority answers and completes50real triggers within the fixed30s budget without per-trigger2.4s multiplication");
            check(provider.PrivateObservations == setupDiscoveries + steps + 1 && provider.ReplicaReads.All(count => count > 0),
                "Only the host discovers private automated batches; both finite clients reconcile commands from subsequently published seat replicas without rediscovering bot authority");
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
                passed = true, scope = "Four-seat real-rules simulation with two finite client drivers and two AI drivers; no networked mixed-lobby claim.",
                captured = captured.Count, actualDraws = draws.Count, provider.Commands, policy.Evaluations, provider.FirstCommandSeconds,
                workloadMilliseconds = workload.Elapsed.TotalMilliseconds, steps, provider.PrivateObservations, provider.ReplicaReads,
                pacing.ReadyChecks, unexpectedPacingWaits = pacing.UnexpectedWaits.Count, provider.CommandTimes,
                terminalCounts = terminals.Select(ids => captured.Count(ids.Contains))
            }));
        } catch (Exception error) {
            failure = error;
            try { File.WriteAllText(Path.Combine(directory, "primary-error.txt"), error.ToString()); } catch { }
            throw;
        }
        finally {
            foreach (var human in humans) human.Close();
            provider.ClosePublications();
            try { File.WriteAllText(Path.Combine(directory, "private-evidence.json"), JsonSerializer.Serialize(new {
                privacy = "Authorized seat-zero hands and scopes; private test profile only.", error = failure?.ToString(),
                setupMilliseconds = setup.Elapsed.TotalMilliseconds, workloadMilliseconds = workload.Elapsed.TotalMilliseconds,
                captured, draws, last, frames, replies, provider.Commands, provider.FirstCommandSeconds, policy.Evaluations, provider.Attempts,
                provider.CommandTimes, pacingWaits = pacing?.UnexpectedWaits,
                clients = humans.Select((session, seat) => new { seat, session.Busy, session.NeedsRecovery,
                    session.LastConnectionError, session.UncertainCommand, progress = intents[seat]?.Progress is { } progress
                        ? new { progress.CapturedCount, progress.CompletedCount, progress.RemainingCount, progress.UnknownCount,
                            outcomes = progress.Outcomes.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value) } : null,
                    stop = intents[seat]?.StopReason, state = session.State })
            })); } catch when (failure != null) { /* Preserve the primary journey failure. */ }
            finally { try { await engine.StopSimulationAsync(game); } catch when (failure != null) { } }
        }

        async Task<string> Save(string name)
        {
            var imported = await engine.ImportAsync(name, "Deck\n99 Swamp\nCommander\n1 The Raven Man");
            var deck = await engine.OpenDeckAsync(imported.Id);
            if (deck.StructureValid == false || deck.Problem.Length > 0 || !deck.Issues.IsEmpty) throw new InvalidDataException("The cadence deck must be valid Commander.");
            var saved = await engine.SaveDeckAsync(deck.Id, deck.Revision);
            if (saved.SaveError.Length > 0) throw new InvalidDataException(saved.SaveError);
            return "deck:" + saved.Id;
        }
    }

    private static DecisionCommand SetupAnswer(AutomatedSeatObservation observation)
    {
        var state = observation.State; var decision = state.Decision!;
        switch (decision.Payload) {
            case OpeningHandPayload: return new KeepOpeningHand(state.Id, decision.Id);
            case PriorityPayload:
                if (observation.SeatId == 0 && state.ActivePlayerId == 0 && state.PhaseKey == "MAIN1" && state.Stack.IsEmpty) {
                    var pestilence = state.Viewer!.Zone("Battlefield").Cards.Single(card => card.Name == "Pestilence");
                    var source = observation.Domain.Sources.Single(item => item.SourceCardId == CardViews.Identity(pestilence));
                    return new UseOfferedAction(state.Id, decision.Id, pestilence.Key, source.Actions.Single(action => action.Kind == "activate").Id);
                }
                return new PassPriority(state.Id, decision.Id);
            case PaymentPayload payment:
                var swamp = state.Viewer!.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Swamp" && !card.Tapped);
                if (swamp != null) {
                    var source = observation.Domain.Sources.Single(item => item.SourceCardId == CardViews.Identity(swamp));
                    return new UseOfferedAction(state.Id, decision.Id, swamp.Key, source.Actions.Single(action => observation.Domain.Actions.Any(
                        semantic => semantic.ActionId == action.Id && semantic.ManaAbility)).Id);
                }
                if (payment.Pay.Enabled) return new PayMana(state.Id, decision.Id);
                break;
            case EntitySelectionPayload selection when !selection.Players.IsEmpty:
                return new SelectPlayer(state.Id, decision.Id, selection.Players.Contains(0) ? 0 : selection.Players[0]);
            case OrderingPayload ordering: return new OrderOptions(state.Id, decision.Id, ordering.Selection.Choices.Select(item => item.Index).ToImmutableArray());
            case OptionsPayload options: return new ChooseOptions(state.Id, decision.Id, options.Selection.Choices.Take(options.Selection.Range.Min).Select(item => item.Index).ToImmutableArray());
            case AbilityChoicePayload abilities: return new ChooseOptions(state.Id, decision.Id, abilities.Selection.Choices.Take(abilities.Selection.Range.Min).Select(item => item.Index).ToImmutableArray());
        }
        return DecisionCommands.Confirm(state) ?? throw new InvalidDataException("Unsupported real cadence setup choice: " + JsonSerializer.Serialize(decision));
    }
    private static void RequirePlaying(GameSnapshot state)
    {
        if (state.Status is "error" or "finished") throw new InvalidDataException(state.Error ?? "The cadence game ended before its proof.");
    }
    private sealed class CountingPolicy : IAiPolicy
    {
        private readonly CasualCommanderPolicy inner = new();
        internal int Evaluations;
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        { Evaluations++; return inner.EvaluateAsync(request, cancellation); }
    }
    private sealed class CadenceProbe(ImmutableHashSet<string> stackIds, Func<int> acceptedCommands, Stopwatch time) : IAiHostPacing
    {
        private readonly TimedAiHostPacing inner = new(TimeSpan.FromSeconds(2.4));
        private readonly Dictionary<(string Game, int Seat), int> remaining = [];
        private readonly HashSet<(int Seat, int Remaining)> recorded = [];
        internal int ReadyChecks;
        internal List<object> UnexpectedWaits { get; } = [];
        public void Observe(ImmutableArray<AiTableProgress> progress)
        {
            inner.Observe(progress);
            foreach (var item in progress) remaining[(item.GameId, item.SeatId)] = item.StackIds.Count(stackIds.Contains);
        }
        public bool Ready(string gameId, int seatId)
        {
            ReadyChecks++;
            bool ready = inner.Ready(gameId, seatId);
            if (!ready && acceptedCommands() > 0 && remaining.TryGetValue((gameId, seatId), out int count)
                    && count > 0 && recorded.Add((seatId, count)))
                UnexpectedWaits.Add(new { seatId, remaining = count, elapsed = time.Elapsed.TotalSeconds });
            return ready;
        }
    }
    private sealed class BotSeats(ForgeEngine engine, Stopwatch time) : IAutomatedSeatProvider
    {
        private readonly object gate = new();
        private ImmutableArray<AutomatedSeatObservation> published = [];
        private TaskCompletionSource publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long generation;
        private readonly int[] replicaReads = new int[2];
        private bool closed;
        internal ImmutableArray<AutomatedSeatObservation> Published { get { lock (gate) return published; } }
        internal long Generation { get { lock (gate) return generation; } }
        internal int[] ReplicaReads { get { lock (gate) return replicaReads.ToArray(); } }
        internal int PrivateObservations;
        internal int Commands;
        internal double FirstCommandSeconds = -1;
        internal HashSet<(int Seat, string Decision)> Decisions { get; } = [];
        internal List<object> Attempts { get; } = [];
        internal List<object> CommandTimes { get; } = [];
        public Task EnableAutomatedSeatsAsync() => engine.EnableAutomatedSeatsAsync();
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync()
        {
            // Only the actual host owns private discovery. Client drivers read
            // copied publications, as separate human connection replicas do;
            // they cannot supersede another bot's prepared private scope.
            PrivateObservations++;
            var observed = await engine.ObserveAutomatedSeatsAsync();
            TaskCompletionSource previous;
            lock (gate) {
                published = observed; generation++; previous = publication;
                publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previous.TrySetResult();
            return observed.Where(item => item.SeatId is 2 or 3).ToImmutableArray();
        }
        public async Task ExecuteAutomatedAsync(int seatId, GameCommand command)
        {
            if (command is not PassPriority priority) throw new InvalidDataException("A cadence bot attempted more than fresh priority passing.");
            if (Commands == 0) FirstCommandSeconds = time.Elapsed.TotalSeconds;
            try { await engine.ExecuteAutomatedAsync(seatId, command); }
            catch (Exception error) {
                Attempts.Add(new { seatId, priority.DecisionId, elapsed = time.Elapsed.TotalSeconds, error = error.ToString() });
                throw;
            }
            Commands++; Decisions.Add((seatId, priority.DecisionId));
            CommandTimes.Add(new { seatId, priority.DecisionId, elapsed = time.Elapsed.TotalSeconds });
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode)
            => engine.ReportAutomationFaultAsync(gameId, seatId, reasonCode);
        internal Task ExecuteClientAsync(int seat, GameCommand command) => engine.ExecuteAutomatedAsync(seat, command);
        internal async Task<GameSnapshot?> ObserveClientAsync(int seat, long minimumPublication)
        {
            lock (gate) replicaReads[seat]++;
            while (true) {
                Task next;
                lock (gate) {
                    if (closed) throw new IOException("The cadence fixture's published connection closed.");
                    if (generation >= minimumPublication && !published.IsEmpty) return published.Single(item => item.SeatId == seat).State;
                    next = publication.Task;
                }
                await next;
            }
        }
        internal void ClosePublications()
        {
            TaskCompletionSource pending;
            lock (gate) { closed = true; pending = publication; }
            pending.TrySetResult();
        }
    }
    private sealed class SeatConnection(BotSeats provider, int seat) : IMatchConnection
    {
        private long minimumPublication;
        public Task<GameSnapshot?> ObserveAsync() => provider.ObserveClientAsync(seat, Volatile.Read(ref minimumPublication));
        public async Task ExecuteAsync(GameCommand command)
        {
            await provider.ExecuteClientAsync(seat, command);
            // A receipt must observe a publication after the acknowledged
            // command, never the stale cached frame that authorized it.
            Volatile.Write(ref minimumPublication, provider.Generation + 1);
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("The cadence client never concedes.");
    }
}
