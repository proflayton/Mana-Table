using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

internal static partial class PolledEvidenceInboxChecks
{
    private static async Task ProgressAndControlScalars(string profile, Action<bool, string> check)
    {
        string directory = DirectoryFor(profile, "progress-scalars"); var clock = new Clock();
        var rows = ProgressRows(10); var port = new ScalarControlPort(rows);
        using (var evidence = new AiCampaignDiagnostics(directory)) {
            var inbox = evidence.EnableOrderedHandoff(256, clock);
            var provider = new AiCampaignProvider(port, evidence, inbox: inbox);
            clock.Advance(1); await provider.ObserveAutomatedSeatsAsync();
            var control = new AutomatedPriorityControl.Acknowledge(rows[0].State.Id, 10, 0);
            var task = provider.ExecuteAutomatedPriorityControlAsync(1, 2, control);
            check(provider.PriorityControlsExecuting == 1 && port.Controls == 1 && ReferenceEquals(port.LastControl, control)
                    && inbox.Status.Enqueued == 1 && evidence.Accepted == 0,
                "A held ordinary priority control queues no completion or gameplay acceptance and receives its exact unchanged control");
            clock.Advance(4); var result = new AutomatedPriorityControlResult.Retired(); port.Completion.SetResult(result);
            check(ReferenceEquals(await task, result) && provider.PriorityControlsExecuting == 0
                    && inbox.Status is { Enqueued: 2, Applied: 0 } && inbox.AcceptedBySeat.IsEmpty,
                "One immutable scalar control completion is enqueued without treating consent metadata as an accepted policy command");
            clock.Advance(60); evidence.DrainPending();
            check(inbox.Status is { Applied: 2, Pending: 0, ReturnedBatches: 1 } && evidence.Accepted == 0 && evidence.Commands.Count == 0,
                "Only the existing owner drains private progress/control scalars; provider and gameplay counters stay distinct");
            evidence.RetireHandoff();
        }
        var lines = PrivateLines(directory);
        using var first = JsonDocument.Parse(lines[0]); using var second = JsonDocument.Parse(lines[1]);
        var batch = first.RootElement.GetProperty("batchProgress"); var span = second.RootElement.GetProperty("controlSpan");
        check(batch.GetProperty("Rows").GetArrayLength() == 2 && batch.GetProperty("Games").GetArrayLength() == 1
                && batch.GetProperty("Rows")[0].GetProperty("BoardRevision").GetInt64() == 10
                && batch.GetProperty("Games")[0].GetProperty("Players").GetArrayLength() == 4
                && batch.GetProperty("Rows")[0].GetProperty("PublicRosterAgrees").GetBoolean(),
            "The actual private returned-batch envelope binds each owned board/revision and one complete public ID/elimination roster");
        check(span.GetProperty("ReturnedBatchSequence").GetInt64() == 1 && span.GetProperty("StartedAt").GetInt64() == TimeSpan.FromSeconds(1).Ticks
                && second.RootElement.GetProperty("stamp").GetProperty("MonotonicTicks").GetInt64() == TimeSpan.FromSeconds(5).Ticks
                && span.GetProperty("Control").GetString() == "Acknowledge" && span.GetProperty("Result").GetString() == "Retired",
            "Control-span start and captured completion retain their real cycle and event times instead of the later drain time");
        check(lines.All(line => !line.Contains("PRIVATE-CARD-MARKER") && !line.Contains("DecisionId") && !line.Contains("GrantId")
                && !line.Contains("SourceCardKey") && !line.Contains("\"Cards\"")),
            "Progress and control diagnostics never duplicate private cards, current decision/action handles or consent tokens");
    }

    private static async Task ControlFailureKeepsPrimary(string profile, Action<bool, string> check)
    {
        using var evidence = new AiCampaignDiagnostics(DirectoryFor(profile, "control-span-overflow"));
        var inbox = evidence.EnableOrderedHandoff(1); var port = new ScalarControlPort(ProgressRows(1));
        var provider = new AiCampaignProvider(port, evidence, inbox: inbox);
        await provider.ObserveAutomatedSeatsAsync();
        var task = provider.ExecuteAutomatedPriorityControlAsync(1, 2, new AutomatedPriorityControl.Acknowledge("progress-evidence", 1, 0));
        var original = new IOException("Controlled priority failure"); port.Completion.SetException(original);
        var thrown = await FailureAsync(() => task);
        var drain = Failure(evidence.DrainPending);
        check(ReferenceEquals(original, thrown) && drain is InvalidDataException && inbox.Status is { Capacity: 1, HighWater: 1, Enqueued: 1, Applied: 1 }
                && inbox.Status.ControlSpanAdmissionFailures == 1
                && provider.PriorityControlsExecuting == 0 && evidence.Accepted == 0 && inbox.AcceptedBySeat.IsEmpty,
            "Control diagnostics preserve the original provider exception while bounded metadata overflow independently fails evidence without gameplay acceptance");
        evidence.RetireHandoff();
    }

    private static async Task SuccessfulControlKeepsReceipt(string profile, Action<bool, string> check)
    {
        foreach (bool retired in new[] { false, true }) {
            using var evidence = new AiCampaignDiagnostics(DirectoryFor(profile, "successful-control-span-failure-" + retired));
            var inbox = evidence.EnableOrderedHandoff(1); var port = new ScalarControlPort(ProgressRows(1));
            var provider = new AiCampaignProvider(port, evidence, inbox: inbox);
            await provider.ObserveAutomatedSeatsAsync();
            var task = provider.ExecuteAutomatedPriorityControlAsync(1, 2, new AutomatedPriorityControl.Acknowledge("progress-evidence", 1, 0));
            if (retired) { evidence.DrainPending(); evidence.RetireHandoff(); }
            var receipt = new AutomatedPriorityReceipt(1, "progress-evidence", 2, 1, 1, 0, AutomatedPriorityMatchStatus.Playing,
                true, 0, 0, null, null, new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []), new(1, 0, 1, 0, true, false));
            receipt.Validate();
            var accepted = new AutomatedPriorityControlResult.Accepted(receipt); port.Completion.SetResult(accepted);
            var returned = await task;
            check(ReferenceEquals(returned, accepted) && port.Controls == 1 && provider.PriorityControlsExecuting == 0
                    && inbox.Status is { ControlSpanAdmissionFailures: 1, Enqueued: 1, HighWater: 1, Failure: not null }
                    && inbox.AcceptedBySeat.IsEmpty && evidence.Accepted == 0,
                "A completed accepted control keeps its exact receipt despite diagnostic overflow/retirement, with an explicit unadmitted-span fault and no gameplay acceptance: " + retired);
            var failure = Failure(evidence.DrainPending);
            check(failure != null && inbox.Status.Applied == 1 && evidence.Summary().DiagnosticErrors.Length == 1
                    && AssessCutoff(evidence).Status == MixedGameQualificationStatus.Failed,
                "The ordinary owner reports the latched control-span failure instead of silently accepting incomplete qualification evidence: " + retired);
            if (!retired) evidence.RetireHandoff();
        }
    }

    private static void ProgressDigestBounds(string profile, Action<bool, string> check)
    {
        var empty = CampaignBatchProgress.Capture([]);
        var row = ProgressRows(1)[0];
        var starting = CampaignBatchProgress.Capture([row with { State = row.State with { Players = [] } }]);
        check(empty.Rows.IsEmpty && empty.Games.IsEmpty && !starting.Games[0].NonemptyDistinctRoster
                && !starting.Rows[0].PublicRosterAgrees && starting.Rows[0].ViewerEliminated == null,
            "Starting empty progress remains initialized incomplete evidence, never a falsely complete public or privately owned roster");
        foreach (bool oversizedRows in new[] { true, false }) {
            string directory = DirectoryFor(profile, "digest-bound-" + oversizedRows);
            using var evidence = new AiCampaignDiagnostics(directory); var inbox = evidence.EnableOrderedHandoff(256);
            var rows = oversizedRows ? Enumerable.Repeat(row, CampaignBatchProgress.MaximumAudiences + 1).ToImmutableArray()
                : ImmutableArray.Create(row with { State = row.State with { Players = Enumerable.Range(0, CampaignBatchProgress.MaximumAudiences + 1)
                    .Select(id => row.State.Players[0] with { Id = id }).ToImmutableArray() } });
            inbox.Returned(rows); var failure = Failure(evidence.DrainPending);
            check(failure is InvalidDataException && evidence.Summary().DiagnosticErrors.Length == 1 && inbox.Status.Applied == 0,
                "Oversized private progress fails its evidence owner explicitly before writing a truncated apparently complete digest: " + oversizedRows);
            evidence.RetireHandoff();
        }
    }

    private static void ProgressRecorderCost(string profile, Action<bool, string> check)
    {
        const int cycles = 128; string directory = DirectoryFor(profile, "progress-cost");
        var durations = new List<double>(); long bytes = 0; int records = 0, maximum = 0;
        var elapsed = Stopwatch.StartNew();
        using (var evidence = new AiCampaignDiagnostics(directory)) {
            var inbox = evidence.EnableOrderedHandoff(256);
            for (int cycle = 1; cycle <= cycles; cycle++) {
                inbox.BeginNextCycle(); inbox.Returned(ProgressRows(cycle));
                var started = inbox.ControlStart();
                inbox.Post(new CampaignEvidenceEvent.PriorityControl(started.Batch, started.StartedAt, "progress-evidence", 2, "Acknowledge", "Retired"));
                var drain = Stopwatch.StartNew(); evidence.DrainPending(); durations.Add(drain.Elapsed.TotalMilliseconds);
            }
            check(inbox.Status.Pending == 0 && inbox.Status.HighWater <= 3 && inbox.Status.Capacity == 256
                    && inbox.Status.Enqueued == inbox.Status.Applied && evidence.Accepted == 0,
                "The bounded recorder-only progress workload keeps the existing FIFO capacity/order and adds no gameplay acceptance");
            evidence.RetireHandoff();
        }
        foreach (string line in PrivateLines(directory)) { int length = Encoding.UTF8.GetByteCount(line); records++; bytes += length; maximum = Math.Max(maximum, length); }
        durations.Sort();
        File.WriteAllText(Path.Combine(directory, "recorder-cost.json"), JsonSerializer.Serialize(new {
            cycles, records, uncompressedRecordBytes = bytes, maximumRecordBytes = maximum,
            compressedBytes = new FileInfo(Path.Combine(directory, "private-decisions.jsonl.gz")).Length,
            totalElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, totalOwnerDrainMilliseconds = durations.Sum(),
            medianOwnerDrainMilliseconds = durations[cycles / 2], p95OwnerDrainMilliseconds = durations[(int)(cycles * .95)],
            scope = "Controlled recorder-only observations and consent metadata; elapsed owner work includes actual serialization, validation/projection and I/O, not pure CPU or a gameplay throughput comparison."
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(records == cycles * 3 - 1 && maximum < 8192 && bytes < 2 * 1024 * 1024,
            "Actual private progress/control serialization is measured and remains scalar-sized without duplicating complete snapshots");
    }

    private static ImmutableArray<AutomatedSeatObservation> ProgressRows(long board)
        => new[] { 2, 3 }.Select(viewer => {
            var players = Enumerable.Range(0, 4).Select(id => new Player(id, "Public player", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 0, [], null), new("Hand", 1,
                    id == viewer ? [new Card { Name = "PRIVATE-CARD-MARKER", VisualId = "private-" + id, Type = "Instant" }] : [], null)]))
                .ToImmutableArray();
            var state = new GameSnapshot("progress-evidence", "playing", null, null, viewer, 1, "First main", "MAIN1", 0, players, [], null, null) {
                Revision = board, BoardRevision = board, DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "resolving"
            };
            return new AutomatedSeatObservation(viewer, state, DecisionActionDomain.None, new());
        }).ToImmutableArray();

    private static List<string> PrivateLines(string directory)
    {
        var lines = new List<string>();
        using var reader = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(directory, "private-decisions.jsonl.gz")), CompressionMode.Decompress));
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
    private sealed class ScalarControlPort(ImmutableArray<AutomatedSeatObservation> rows) : IAutomatedSeatProvider, IAutomatedPriorityContinuation
    {
        internal int Controls; internal AutomatedPriorityControl? LastControl;
        internal TaskCompletionSource<AutomatedPriorityControlResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsAutomatedPriorityContinuation => true;
        public long AutomatedPriorityEpoch => 1;
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.FromResult(rows);
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default) => throw new InvalidOperationException("No gameplay command belongs to this recorder control.");
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId, AutomatedPriorityControl control,
            CancellationToken cancellation = default) { Controls++; LastControl = control; return Completion.Task; }
    }
}
