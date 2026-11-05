using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

internal static class PolledEvidenceInboxChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        OrderedCapture(profile, check);
        Overflow(profile, check);
        await ReturnedIsNotProcessed(profile, check);
    }

    private static void OrderedCapture(string profile, Action<bool, string> check)
    {
        string directory = DirectoryFor(profile, "fifo-time");
        var time = new Clock();
        using (var evidence = new AiCampaignDiagnostics(directory)) {
            var inbox = evidence.EnableOrderedHandoff(4, time);
            var state = PresentationSmoke.Example() with { Revision = 10, BoardRevision = 10 };
            var row = new AutomatedSeatObservation(0, state, DecisionActionDomain.None, new());
            time.Advance(1);
            inbox.Post(new CampaignEvidenceEvent.Public(state));
            time.Advance(1);
            inbox.Receipt(row, new PassPriority(state.Id, state.Decision!.Id), true, null);
            bool producerWasReadOnly = evidence.Accepted == 0 && evidence.Commands.Count == 0;
            time.Advance(3600);
            evidence.DrainPending();
            check(producerWasReadOnly && evidence.Accepted == 1 && evidence.Commands.GetValueOrDefault("PassPriority") == 1
                    && inbox.Status is { Enqueued: 2, Applied: 2, Pending: 0, HighWater: 2 }
                    && inbox.AcceptedBySeat.GetValueOrDefault(0) == 1,
                "Background evidence remains queued until its one owner applies the actual ordered receipt, without mutating diagnostics from the producer");
            evidence.RetireHandoff();
            var retired = inbox.Status;
            bool blocked = Throws<InvalidOperationException>(() => inbox.Post(new CampaignEvidenceEvent.Public(state)))
                && Throws<InvalidOperationException>(() => inbox.Returned([row]))
                && Throws<InvalidOperationException>(inbox.BeginNextCycle);
            check(blocked && inbox.Status.Enqueued == retired.Enqueued && inbox.Status.Applied == retired.Applied
                    && inbox.Status.ReturnedBatches == retired.ReturnedBatches && inbox.Status.Pending == 0
                    && evidence.Accepted == 1,
                "Retiring a drained evidence owner rejects late events and observation promotion without changing its frozen receipts");
        }
        var stamps = new List<(long Sequence, DateTimeOffset At, long Ticks, string? Kind)>();
        using var input = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(directory, "private-decisions.jsonl.gz")), CompressionMode.Decompress));
        while (input.ReadLine() is { } line) {
            using var item = JsonDocument.Parse(line);
            if (!item.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "evidence-handoff") continue;
            var stamp = item.RootElement.GetProperty("stamp");
            stamps.Add((stamp.GetProperty("Sequence").GetInt64(), stamp.GetProperty("CapturedAt").GetDateTimeOffset(),
                stamp.GetProperty("MonotonicTicks").GetInt64(), stamp.GetProperty("Kind").GetString()));
        }
        check(stamps.SequenceEqual(new[] {
                (1L, DateTimeOffset.UnixEpoch.AddSeconds(1), TimeSpan.FromSeconds(1).Ticks, (string?)"Public"),
                (2L, DateTimeOffset.UnixEpoch.AddSeconds(2), TimeSpan.FromSeconds(2).Ticks, (string?)"Receipt") }),
            "The actual private evidence trace preserves FIFO sequence and captured UTC/monotonic event times rather than the later drain time");
    }

    private static void Overflow(string profile, Action<bool, string> check)
    {
        using var evidence = new AiCampaignDiagnostics(DirectoryFor(profile, "overflow"));
        var inbox = evidence.EnableOrderedHandoff(1);
        var state = PresentationSmoke.Example();
        inbox.Post(new CampaignEvidenceEvent.Public(state));
        var first = Failure(() => inbox.Post(new CampaignEvidenceEvent.Public(state)));
        var drain = Failure(evidence.DrainPending);
        check(first is InvalidDataException && ReferenceEquals(first, drain)
                && inbox.Status is { Capacity: 1, HighWater: 1, Enqueued: 1, Applied: 1, Pending: 0 }
                && evidence.Summary().DiagnosticErrors.Length == 1,
            "Evidence overflow preserves its first failure and already admitted prefix, and fails the owner instead of dropping or overwriting work silently");
        var repeated = Failure(evidence.DrainPending);
        check(ReferenceEquals(first, repeated) && evidence.Summary().DiagnosticErrors.Length == 1
                && AssessCutoff(evidence).Status == MixedGameQualificationStatus.Failed,
            "A bounded evidence failure stays a failed qualification on repeated drains and cannot become a successful or merely missing terminal witness");
        evidence.RetireHandoff();
    }

    private static async Task ReturnedIsNotProcessed(string profile, Action<bool, string> check)
    {
        using var evidence = new AiCampaignDiagnostics(DirectoryFor(profile, "processed-terminal"));
        var inbox = evidence.EnableOrderedHandoff(8);
        var terminal = PresentationSmoke.Example() with { Status = "finished", Revision = 20, BoardRevision = 20,
            Decision = null, Outcome = new("presentation", MatchOutcomeKind.Draw, [], [], "AllPlayersDrew") };
        MatchOutcomeFacts.Validate(terminal);
        var row = new AutomatedSeatObservation(0, terminal, DecisionActionDomain.None, new());
        var port = new BotPort([row]);
        var provider = new AiCampaignProvider(port, evidence, inbox: inbox);
        await provider.ObserveAutomatedSeatsAsync();
        evidence.DrainPending();
        check(provider.SnapshotObservations is [var returned] && ReferenceEquals(returned.State, terminal)
                && provider.ProcessedObservations.IsEmpty && inbox.Status is { ReturnedBatches: 1, ProcessedBatches: 0 },
            "A provider-returned terminal batch is not certified as processed bot work merely because its evidence was drained");
        var second = provider.ObserveAutomatedSeatsAsync();
        await port.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(provider.ProcessedObservations is [var processed] && ReferenceEquals(processed.State, terminal)
                && inbox.Status is { ReturnedBatches: 1, ProcessedBatches: 1, ProcessedReturnSequence: 1 },
            "Only the serialized worker's next observation entry promotes the prior returned batch to its processed marker");
        port.SecondReply.TrySetException(new EngineObservationSupersededException());
        var superseded = await FailureAsync(() => second);
        var third = provider.ObserveAutomatedSeatsAsync();
        await port.ThirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(superseded is EngineObservationSupersededException
                && inbox.Status is { ReturnedBatches: 1, ProcessedBatches: 1, ProcessedReturnSequence: 1 },
            "A superseded next read followed by another entry cannot count the same processed batch twice");
        evidence.DrainPending();
        check(AssessCutoff(evidence).Status == MixedGameQualificationStatus.Inconclusive,
            "A processed terminal marker alone cannot turn the runner's actual cutoff or missing owned audiences into a verified natural game");
        var fault = new AiHostFault(terminal.Id, 0, "controlled-late-host-fault");
        inbox.Report(fault, new IOException("Controlled producer failure after terminal observation"));
        evidence.DrainPending();
        check(ReferenceEquals(evidence.CapturedHostFault, fault) && evidence.Summary().DiagnosticErrors.Length == 1
                && AssessCutoff(evidence).Status == MixedGameQualificationStatus.Failed,
            "A host fault arriving after terminal observation remains owner-visible failed evidence after draining, never a successful finish");
        evidence.RetireHandoff();
        port.ThirdReply.TrySetResult([row with { State = terminal with { Revision = 21 } }]);
        var late = await FailureAsync(() => third);
        var newRead = await FailureAsync(() => provider.ObserveAutomatedSeatsAsync());
        var command = await FailureAsync(() => provider.ExecuteAutomatedAsync(0, new PassPriority(terminal.Id, "retired")));
        check(late is InvalidOperationException && newRead is InvalidOperationException && command is InvalidOperationException
                && port.Reads == 3 && port.Commands == 0 && inbox.Status.ReturnedBatches == 1
                && ReferenceEquals(provider.SnapshotObservations[0].State, terminal),
            "Retirement fences an already held bot return and refuses new observation/command RPCs before they reach the underlying provider");
    }

    private static MixedGameQualificationAssessment AssessCutoff(AiCampaignDiagnostics evidence) => MixedGameQualification.Assess(
        new(0, "time-budget", null, false, 20, 1, 0, [], 0, 0, 0, 0, null, evidence.Summary(), []), []);

    private static string DirectoryFor(string profile, string name)
    {
        string path = Path.Combine(profile, "polled-full-game-contracts", name);
        Directory.CreateDirectory(path); return path;
    }
    private static bool Throws<T>(Action action) where T : Exception => Failure(action) is T;
    private static Exception? Failure(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static async Task<Exception?> FailureAsync(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); return null; } catch (Exception error) { return error; }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        internal void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class BotPort(ImmutableArray<AutomatedSeatObservation> initial) : IAutomatedSeatProvider
    {
        internal int Reads, Commands;
        internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ThirdStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ImmutableArray<AutomatedSeatObservation>> SecondReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ImmutableArray<AutomatedSeatObservation>> ThirdReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            Reads++;
            if (Reads == 1) return Task.FromResult(initial);
            if (Reads == 2) { SecondStarted.TrySetResult(); return SecondReply.Task; }
            ThirdStarted.TrySetResult(); return ThirdReply.Task;
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            Commands++; throw new InvalidOperationException("A retired provider must not execute.");
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
            => throw new InvalidOperationException("The bounded provider fixture does not report a real rules fault.");
    }
}
