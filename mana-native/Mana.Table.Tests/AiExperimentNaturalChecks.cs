using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class AiExperimentNaturalChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        var decoded = ForgeEngine.DecodeConfiguredSimulation(JsonSerializer.SerializeToElement(ConfiguredSimulationWireChecks.Receipt()),
            ConfiguredSimulationWireChecks.Setup());
        var specification = new MatchSetupSpecification(MatchRulesets.FreeForAllCommander,
            Enumerable.Range(0, 4).Select(slot => new MatchSeatAssignment(slot, slot, MatchSeatRole.Participant)).ToImmutableArray());
        var slotByPlayer = decoded.Participants.ToDictionary(person => person.PlayerId, person => person.Index);
        var start = decoded with { Setup = decoded.Setup with { Specification = specification },
            State = decoded.State with { Players = decoded.State.Players.Select(player => player with { TeamId = slotByPlayer[player.Id] }).ToImmutableArray() } };
        var reference = new AiExperimentVariant(AiExperimentPolicyRegistry.CasualCommanderFactoryId);
        var focal = reference with { AttachmentEvaluation = false };
        var assigned = AiExperimentNatural.BindVariants(start, 2, focal, reference);
        check(assigned.Count == 4 && assigned[83] == focal && assigned.Where(pair => pair.Key != 83).All(pair => pair.Value == reference)
                && !assigned.ContainsKey(2),
            "Experiment focal policy follows accepted slot2-to-player83 rather than assuming physical slots are player IDs");
        check(Reject(() => AiExperimentNatural.BindVariants(start with { Participants = start.Participants.SetItem(0, new(2, 29)) }, 2, focal, reference)),
            "Experiment policy binding rejects duplicate/malformed accepted actual-player mappings");
        check(Reject(() => AiExperimentNatural.BindVariants(start, 4, focal, reference)),
            "Experiment policy binding rejects a focal slot outside the accepted four-player roster");

        GameSnapshot Terminal(MatchOutcome outcome) => start.State with { Status = "finished", Outcome = outcome, Decision = null };
        var win = Terminal(new(start.State.Id, MatchOutcomeKind.Win, [83], [2], "fixture-win"));
        var proof = AiCampaignOutcomeProof.Capture(win, "natural-outcome", null, [], []);
        var result = AiExperimentNatural.Outcome(start, 2, proof, committed: true);
        check(result["terminalVerified"]!.GetValue<bool>() && result["focalPlayerId"]!.GetValue<int>() == 83
                && result["outcome"]!["kind"]!.GetValue<string>() == "win"
                && result["outcome"]!["winningPlayerIds"]![0]!.GetValue<int>() == 83,
            "Experiment natural result retains an authoritative pre-cleanup win and actual focal player identity");
        var draw = Terminal(new(start.State.Id, MatchOutcomeKind.Draw, [], [], "fixture-draw"));
        var drawResult = AiExperimentNatural.Outcome(start, 2, AiCampaignOutcomeProof.Capture(draw, "natural-outcome", null, [], []), true);
        check(drawResult["terminalVerified"]!.GetValue<bool>() && drawResult["outcome"]!["kind"]!.GetValue<string>() == "draw"
                && drawResult["outcome"]!["winningPlayerIds"]!.AsArray().Count == 0,
            "Experiment natural draws retain an empty typed winner list rather than inferring draw participants");
        foreach (string cutoff in new[] { "time-cutoff", "turn-cutoff", "policy-failure", "provider-failure" }) {
            var unresolved = AiExperimentNatural.Outcome(start, 2, AiCampaignOutcomeProof.Capture(win, cutoff, null, [], []), true);
            check(!unresolved["terminalVerified"]!.GetValue<bool>() && unresolved["outcome"] == null,
                "An experiment " + cutoff + " cannot inherit a later/cleanup terminal result or a fabricated defeat");
        }
        check(!AiExperimentNatural.Outcome(start, 2, proof, committed: false)["terminalVerified"]!.GetValue<bool>(),
            "Experiment natural terminal credit requires its pre-cleanup proof to be committed");
        var shared = Terminal(new(start.State.Id, MatchOutcomeKind.Win, [29, 83], [0, 2], "fixture-shared-win"));
        var sharedResult = AiExperimentNatural.Outcome(start, 2, AiCampaignOutcomeProof.Capture(shared, "natural-outcome", null, [], []), true);
        check(sharedResult["outcome"]!["winningPlayerIds"]!.AsArray().Count == 2,
            "Experiment result preserves shared winners for the analyzer's declared one-over-k score");
        check(Reject(() => AiExperimentNatural.ValidatePublic(start.State with { Players = start.State.Players.SetItem(0,
            start.State.Players[0] with { Zones = [new("Hand", 1, [new() { Name = "Hidden", VisualId = "private", Key = "private-action" }], null)] }) })),
            "Experiment public validation rejects private hands and actionable faces before any evidence or policy use");

        string directory = Path.Combine(profile, "natural-worker-controls", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var starter = AiExperimentNatural.ObserveStartingPlayer(start, start.State with { Turn = 1, ActivePlayerId = 47 });
        check(starter is { PlayerId: 47, Slot: 1 },
            "Experiment starting-player evidence comes from the actual turn-one active player and accepted slot map");
        check(AiExperimentNatural.ObserveStartingPlayer(start, start.State with { Turn = 0, ActivePlayerId = 29 }) == null
                && AiExperimentNatural.ObserveStartingPlayer(start, start.State with { Turn = 2, ActivePlayerId = 29 }) == null,
            "Experiment starting-player exposure remains unknown when turn one was not observed rather than inferred from the focal seat");
        check(Reject(() => AiExperimentNatural.ObserveStartingPlayer(start, start.State with { Turn = 1, ActivePlayerId = 999 })),
            "Experiment starting-player evidence rejects an active identity outside the accepted roster");
        CheckPublicParity(start, directory, check);
        var states = start.Participants.Select(person => new AutomatedSeatObservation(person.PlayerId,
            start.State with { ViewerId = person.PlayerId,
                Decision = person.PlayerId == 29 ? new("experiment-current", new(), new PriorityPayload(new("Pass", true))) : null },
            DecisionActionDomain.None, new())).ToImmutableArray();
        var inner = new FakeProvider(states);
        using (var capture = new AiExperimentNaturalCapture(directory, 1048576)) {
            var provider = new AiExperimentNaturalProvider(inner, start, capture);
            provider.ObserveAutomatedSeatsAsync().GetAwaiter().GetResult();
            provider.ExecuteAutomatedAsync(29, new PassPriority(start.State.Id, "experiment-current")).GetAwaiter().GetResult();
            check(capture.BytesWritten == 0 && new FileInfo(Path.Combine(directory, "decision-measures.jsonl")).Length == 0,
                "Experiment receipt callbacks perform no serialization or stream write before the owner drains");
            capture.Drain();
            check(inner.Observations == 1 && inner.Executions == 1 && provider.Accepted == 1,
                "Experiment receipt recording neither resnapshots authority nor executes a second gameplay command");
            check(inner.Commands.Single() is PassPriority { DecisionId: "experiment-current" },
                "Experiment provider forwards the exact current scoped command without replacing actor, input or source");
            check(provider.Last[0].State.Revision == start.State.Revision,
                "Experiment provider retains the publication actually returned before the command instead of a later receipt snapshot");
        }
        var receipt = JsonDocument.Parse(File.ReadAllLines(Path.Combine(directory, "decision-measures.jsonl")).Single()).RootElement;
        check(receipt.GetProperty("status").GetString() == "accepted" && !receipt.GetProperty("resolved").GetBoolean()
                && receipt.GetProperty("decisionId").GetString() == "experiment-current",
            "Experiment accepted command evidence remains distinct from a resolved effect and retains its original input");
        string privateDirectory = Path.Combine(directory, "privacy"); Directory.CreateDirectory(privateDirectory);
        using (var capture = new AiExperimentNaturalCapture(privateDirectory, 1048576)) {
            var foreign = states.SetItem(0, states[0] with { State = states[0].State with { ViewerId = 999 } });
            var provider = new AiExperimentNaturalProvider(new FakeProvider(foreign), start, capture);
            check(Reject(() => provider.ObserveAutomatedSeatsAsync().GetAwaiter().GetResult()) && provider.SafetyFailure && provider.Last.IsEmpty,
                "An experiment-owned frame cannot change audience or publish a foreign/private batch to a policy");
        }
        string boundedDirectory = Path.Combine(directory, "bounded"); Directory.CreateDirectory(boundedDirectory);
        using (var capture = new AiExperimentNaturalCapture(boundedDirectory, 4096)) {
            capture.Optional(new { oversized = new string('x', 8192) });
            capture.Required("required-outcome.json", new { terminalVerified = false });
            capture.Drain();
            check(capture.OmittedRows == 1 && capture.BytesWritten <= 4096
                    && new FileInfo(Path.Combine(boundedDirectory, "decision-measures.jsonl")).Length == 0,
                "Experiment optional capture stops at its byte allowance while preserving required outcome reserve and omission counts");
            check(Reject(() => capture.Required("too-large.json", new { oversized = new string('x', 8192) }))
                    && capture.Failure != null && !File.Exists(Path.Combine(boundedDirectory, "too-large.json")),
                "Experiment required capture over its cap is an explicit evidence failure without committing an oversized file");
        }
        string failedDirectory = Path.Combine(directory, "failed-sink"); Directory.CreateDirectory(failedDirectory);
        var failedCapture = new AiExperimentNaturalCapture(failedDirectory, 1048576, new FailingStream());
        try {
            var capture = failedCapture;
            var provider = new AiExperimentNaturalProvider(new FakeProvider(states), start, capture);
            provider.ObserveAutomatedSeatsAsync().GetAwaiter().GetResult();
            provider.ExecuteAutomatedAsync(29, new PassPriority(start.State.Id, "experiment-current")).GetAwaiter().GetResult();
            check(provider.Accepted == 1 && provider.Unknown == 0 && capture.Failure == null && capture.BytesWritten == 0,
                "A known accepted provider receipt returns normally before an optional measurement sink is touched");
            check(Reject(capture.Drain) && capture.Failure != null
                    && capture.Metrics(1, 1, 1, 1, 0)["criticalMetricsAvailable"]!.GetValue<bool>() == false,
                "An experiment drain failure marks affected metrics unavailable after preserving the actual accepted receipt");
        } finally { Reject(failedCapture.Dispose); }

        string queueDirectory = Path.Combine(directory, "queue-bound"); Directory.CreateDirectory(queueDirectory);
        using (var memory = new MemoryStream())
        using (var capture = new AiExperimentNaturalCapture(queueDirectory, 1048576, memory)) {
            Parallel.For(0, 1024, index => capture.Optional(new { index }));
            var queued = capture.Metrics(0, 0, 0, 0, 0);
            check(queued["pendingCaptureItems"]!.GetValue<int>() <= 256
                    && queued["pendingCaptureItems"]!.GetValue<int>() + queued["omittedTraceRows"]!.GetValue<long>() == 1024
                    && memory.Length == 0,
                "A saturated concurrent experiment callback queue remains finite and accounts every queued or omitted record without writing");
        }

        string parallelDirectory = Path.Combine(directory, "overlap"); Directory.CreateDirectory(parallelDirectory);
        using (var memory = new MemoryStream())
        using (var capture = new AiExperimentNaturalCapture(parallelDirectory, 1048576, memory) { FocalPlayerId = 29 }) {
            var policy = new ConcurrentPolicy(16);
            var sessions = new List<AiSeatSession>();
            try {
                var pending = Enumerable.Range(0, 16).Select(index => {
                    int player = index % 2 == 0 ? 29 : 83;
                    var state = start.State with { ViewerId = player,
                        Decision = new("parallel-current-" + index, new(), new PriorityPayload(new("Pass", true))) };
                    var session = new AiSeatSession(state.Id, player, capture.Record(policy), index,
                        new(TimeSpan.FromSeconds(20), 16));
                    sessions.Add(session); session.Observe(new(state, actionCoverage: AiActionCoverage.Complete)); return session.EvaluateAsync();
                }).ToArray();
                var views = Task.WhenAll(pending).GetAwaiter().GetResult();
                var metrics = capture.Metrics(0, 0, 0, 0, 0);
                if (!views.All(view => view.Status == AiSeatStatus.Ready) || policy.Peak < 2
                    || metrics["evaluationAttempts"]!.GetValue<int>() != 16 || metrics["episodeCount"]!.GetValue<int>() != 16
                    || metrics["workSpent"]!.GetValue<long>() != 16)
                    File.WriteAllText(Path.Combine(parallelDirectory, "unexpected-overlap.json"),
                        JsonSerializer.Serialize(new { views, policy.Peak, metrics }));
                check(views.All(view => view.Status == AiSeatStatus.Ready) && policy.Peak >= 2
                        && metrics["evaluationAttempts"]!.GetValue<int>() == 16
                        && metrics["episodeCount"]!.GetValue<int>() == 16
                        && metrics["workSpent"]!.GetValue<long>() == 16,
                    "Overlapping experiment policy callbacks retain exact attempt, episode and work accounting without dictionary/list corruption");
                check(metrics["focalEvaluationAttempts"]!.GetValue<int>() == 8
                        && metrics["policyEvaluationMsSamples"]!.AsArray().Count == 8
                        && memory.Length == 0 && !Directory.EnumerateFiles(parallelDirectory).Any(),
                    "Concurrent policy callbacks retain focal-only samples and enqueue immutable captures without serialization or file I/O");
                capture.Drain();
                check(memory.Length > 0 && Directory.EnumerateFiles(parallelDirectory, "replay-focal-*.json").Count() == 1,
                    "The natural owner drains queued policy evidence outside session allowance with one declared first-task replay sample");
            } finally { foreach (var session in sessions) session.Dispose(); }
        }
    }
    private static bool Reject(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { return true; }
    }
    private static void CheckPublicParity(ConfiguredSimulationStart start, string directory, Action<bool, string> check)
    {
        var publicState = start.State;
        var privateState = publicState with { ViewerId = 29, Revision = publicState.Revision + 123,
            Decision = new("private-reading-context", new(), new PriorityPayload(new("Pass", true))),
            Players = publicState.Players.Select(player => player.Id != 29 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                    Cards = [new() { Name = "Private face", VisualId = "private-occurrence", Key = "private-key" }]
                }).ToImmutableArray()
            }).ToImmutableArray() };
        var owned = new AutomatedSeatObservation(29, privateState, DecisionActionDomain.None, new());
        string positive = Path.Combine(directory, "public-parity-positive"); Directory.CreateDirectory(positive);
        using (var capture = new AiExperimentNaturalCapture(positive, 1048576)) {
            AiExperimentNatural.RequirePublicParity(publicState, owned, capture);
            check(!File.Exists(Path.Combine(positive, "public-projection-mismatch.json")),
                "Experiment public parity preserves legitimate viewer, revision, private-hand and reading-prompt differences without changing the existing canonical predicate");
        }
        void Mismatch(string label, GameSnapshot spectator, AutomatedSeatObservation other, string field)
        {
            string path = Path.Combine(directory, "public-parity-" + label); Directory.CreateDirectory(path);
            using var capture = new AiExperimentNaturalCapture(path, 1048576);
            bool failed = Reject(() => AiExperimentNatural.RequirePublicParity(spectator, other, capture));
            using var proof = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(path, "public-projection-mismatch.json")));
            var root = proof.RootElement;
            check(failed && capture.Failure == null && !root.GetProperty("factsEqual").GetBoolean()
                    && root.GetProperty("returnedNativeValues").GetProperty("spectator").GetProperty("BoardRevision").GetInt64() == spectator.BoardRevision
                    && root.GetProperty("returnedNativeValues").GetProperty("owned").GetProperty("State").GetProperty("ViewerId").GetInt32() == other.State.ViewerId,
                "Experiment parity mismatch " + label + " preserves both exact returned native values before refusing the comparison");
            check(root.GetProperty("differences").EnumerateArray().Any(item => item.GetProperty("path").GetString()!.Contains(field, StringComparison.Ordinal))
                    && root.GetProperty("differences").GetArrayLength() <= 32
                    && root.GetProperty("spectatorStamp").TryGetProperty("priorityPublicActivitySequence", out _),
                "Experiment parity mismatch " + label + " retains bounded field differences, provider status/outcome and explicit activity-counter coverage");
        }
        Mismatch("public-life", publicState, owned with { State = privateState with {
            Players = privateState.Players.SetItem(0, privateState.Players[0] with { Life = 39 }) } }, "Life");
        var finished = publicState with { Status = "finished", Outcome = new(publicState.Id, MatchOutcomeKind.Win, [83], [2], "fixture-terminal") };
        Mismatch("terminal-versus-older-owned", finished, owned, "/outcome");
        Mismatch("different-terminal-winner", finished, owned with { State = finished with { ViewerId = 29,
            Outcome = new(publicState.Id, MatchOutcomeKind.Win, [29], [0], "fixture-terminal") } }, "winningPlayerIds");
    }
    private sealed class FakeProvider(ImmutableArray<AutomatedSeatObservation> observations) : IAutomatedSeatProvider
    {
        internal int Observations, Executions;
        internal List<GameCommand> Commands { get; } = [];
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        { Observations++; return Task.FromResult(observations); }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        { Executions++; Commands.Add(command); return Task.CompletedTask; }
    }
    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Controlled natural measurement sink failure.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Controlled natural measurement sink failure.");
    }
    private sealed class ConcurrentPolicy(int count) : IAiPolicy
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int running, peak, arrived;
        internal int Peak => peak;
        public AiPolicyDescriptor Descriptor { get; } = new("experiment-overlap-control", AiTasks.All);
        public async Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            int now = Interlocked.Increment(ref running);
            int before;
            do { before = Volatile.Read(ref peak); } while (now > before && Interlocked.CompareExchange(ref peak, now, before) != before);
            if (Interlocked.Increment(ref arrived) == count) ready.TrySetResult();
            try {
                await ready.Task.WaitAsync(cancellation);
                request.Work.TrySpend();
                return new AiProposal.Answer(new PassPriority(request.Frame.State.Id, request.Frame.State.Decision!.Id), "parallel-control");
            } finally { Interlocked.Decrement(ref running); }
        }
    }
}
