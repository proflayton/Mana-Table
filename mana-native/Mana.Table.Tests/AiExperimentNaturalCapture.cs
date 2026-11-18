using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

// Compact measurements have a finite owner. Full frames are sampled, never
// queued on the gameplay path. Required setup/outcome bytes have a reserve.
internal sealed class AiExperimentNaturalCapture : IDisposable
{
    private readonly string directory;
    private readonly long maximum, optionalMaximum;
    private readonly Stream trace;
    private readonly object gate = new();
    private readonly Queue<(string? File, object Value)> pending = [];
    private const int QueueCapacity = 256;
    private long written, omittedRows, omittedReplays;
    private int rows, evaluations;
    private int committedReplays;
    private long workSpent;
    private double policyMs;
    private readonly HashSet<AiTask> sampled = [];
    private readonly Dictionary<int, AiDecisionReplayCase> latest = [];
    private readonly Dictionary<int, (AiProposal? Proposal, string? Failure)> latestResults = [];
    private readonly Dictionary<(int Seat, string Episode), (int Attempts, double Ms, long Work)> episodes = [];
    private readonly Dictionary<AiTask, List<double>> timings = [];
    private readonly List<double> focalWallSamples = [];
    private readonly List<int> focalWorkSamples = [];
    private bool disposed;
    internal int? FocalPlayerId { get; set; }
    private Exception? failure;
    internal Exception? Failure { get { lock (gate) return failure; } }
    internal long BytesWritten { get { lock (gate) return written; } }
    internal long OmittedRows { get { lock (gate) return omittedRows; } }

    internal AiExperimentNaturalCapture(string directory, long maximum, Stream? traceOverride = null)
    {
        this.directory = directory; this.maximum = maximum;
        optionalMaximum = maximum / 2;
        trace = traceOverride ?? new FileStream(Path.Combine(directory, "decision-measures.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    internal void Required<T>(string name, T value)
    {
        byte[] bytes;
        try { bytes = SerializeBounded(value, maximum - BytesWritten); }
        catch (AiExperimentNaturalEvidenceException error) { lock (gate) failure ??= error; throw; }
        try {
            lock (gate) written += bytes.Length; // A failed write may have committed a prefix.
            using var output = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(bytes); output.Flush(flushToDisk: true);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            throw Failed("Required natural evidence could not be committed: " + name, error);
        }
    }

    internal void Optional<T>(T row)
    {
        lock (gate) {
            if (disposed) return;
            if (pending.Count >= QueueCapacity) { omittedRows++; return; }
            pending.Enqueue((null, row!));
        }
    }

    // Only the game owner drains, after StepAsync returns or during retirement.
    // No policy/receipt callback performs serialization or touches a stream.
    internal void Drain()
    {
        if (Failure is { } prior) throw prior;
        while (true) {
            (string? File, object Value) item;
            lock (gate) {
                if (failure != null) throw failure;
                if (disposed || pending.Count == 0) return;
                item = pending.Dequeue();
            }
            if (item.File == null) WriteOptional(item.Value);
            else WriteSample((AiDecisionReplayCase)item.Value, item.File);
        }
    }

    private void WriteOptional(object row)
    {
        if (BytesWritten >= optionalMaximum) { lock (gate) omittedRows++; return; }
        byte[] bytes;
        try { bytes = SerializeBounded(row, optionalMaximum - BytesWritten - 1); }
        catch (AiExperimentNaturalEvidenceException) { lock (gate) omittedRows++; return; }
        try {
            lock (gate) written += bytes.Length + 1;
            trace.Write(bytes); trace.WriteByte((byte)'\n'); trace.Flush();
            lock (gate) rows++;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            throw Failed("Natural measurement sink failed; metrics are not silently zero.", error);
        }
    }

    internal IAiPolicy Record(IAiPolicy policy) => new MeasuredPolicy(policy, this);
    private sealed class MeasuredPolicy(IAiPolicy inner, AiExperimentNaturalCapture capture) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public async Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var frame = request.Frame;
            var task = AiTasks.From(frame.State.Decision!.Payload);
            int initialWork = request.Work.Remaining;
            var replay = AiDecisionReplayCase.Capture("natural-" + frame.Stamp.SeatId + "-" + frame.Stamp.DecisionId, inner.Descriptor.Id, request);
            capture.Begin(replay, task);
            var clock = Stopwatch.StartNew();
            AiProposal? proposal = null;
            string? failure = null;
            try { return proposal = await inner.EvaluateAsync(request, cancellation); }
            catch (Exception error) { failure = error.GetType().Name; throw; }
            finally {
                clock.Stop();
                capture.Evaluation(frame, task, inner.Descriptor.Id, request.Seed, request.RemainingTime,
                    initialWork, request.Work.Remaining, clock.Elapsed.TotalMilliseconds, proposal, failure);
            }
        }
    }

    private void Evaluation(AiDecisionFrame frame, AiTask task, string policy, long seed, TimeSpan remaining,
        int initial, int final, double milliseconds, AiProposal? proposal, string? failure)
    {
        lock (gate) {
        if (disposed) return;
        if (evaluations >= 100000) { Failed("Natural measurement invocation capacity was reached."); return; }
        evaluations++; policyMs += milliseconds;
        long spent = Math.Max(0, initial - final); workSpent += spent;
        if (frame.Stamp.SeatId == FocalPlayerId) { focalWallSamples.Add(milliseconds); focalWorkSamples.Add(checked((int)spent)); }
        string episode = frame.EpisodeId.Length > 0 ? frame.EpisodeId : "input:" + frame.Stamp.DecisionId;
        var key = (frame.Stamp.SeatId, episode);
        if (!episodes.ContainsKey(key) && episodes.Count >= 32768) {
            Failed("Natural episode measurement capacity was reached."); return;
        }
        var prior = episodes.GetValueOrDefault(key);
        episodes[key] = (prior.Attempts + 1, prior.Ms + milliseconds, prior.Work + spent);
        if (!timings.TryGetValue(task, out var values)) timings.Add(task, values = []);
        values.Add(milliseconds);
        if (latest.TryGetValue(frame.Stamp.SeatId, out var retained)
            && retained.State.Decision?.Id == frame.Stamp.DecisionId && retained.State.Revision == frame.Stamp.Revision)
            latestResults[frame.Stamp.SeatId] = (proposal, failure);
        var answer = proposal as AiProposal.Answer;
        Optional(new {
            kind = "policy-evaluation", frame.Stamp, task = task.ToString(), policy, episode,
            seed = seed.ToString(CultureInfo.InvariantCulture), remainingMs = remaining.TotalMilliseconds,
            initialWork = initial, remainingWork = final, workSpent = spent, innerPolicyWallMs = milliseconds,
            status = failure != null ? "exception" : proposal is AiProposal.Answer ? "proposed" : "unavailable",
            command = answer?.Command.GetType().Name, reason = answer?.ReasonCode,
            capability = (proposal as AiProposal.Unavailable)?.Capability, failure,
            executed = false, candidateCoverage = (object?)null
        });
        }
    }

    private void Begin(AiDecisionReplayCase replay, AiTask task)
    {
        lock (gate) {
            if (disposed) return;
            latest[replay.State.ViewerId] = replay;
            if (replay.State.ViewerId != FocalPlayerId || !sampled.Add(task)) return;
            if (pending.Count >= QueueCapacity) { omittedReplays++; return; }
            pending.Enqueue(("replay-focal-" + task + ".json", replay));
        }
    }

    private void WriteSample(AiDecisionReplayCase replay, string file)
    {
        if (BytesWritten >= optionalMaximum) { lock (gate) omittedReplays++; return; }
        byte[] bytes;
        try { bytes = SerializeBounded(replay, optionalMaximum - BytesWritten); }
        catch (AiExperimentNaturalEvidenceException) { lock (gate) omittedReplays++; return; }
        try {
            lock (gate) written += bytes.Length;
            File.WriteAllBytes(Path.Combine(directory, file), bytes);
            lock (gate) committedReplays++;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            throw Failed("Natural replay sample sink failed.", error);
        }
    }

    internal void CriticalReplay(int player)
    {
        AiDecisionReplayCase? replay;
        (AiProposal? Proposal, string? Failure) result;
        lock (gate) { latest.TryGetValue(player, out replay); result = latestResults.GetValueOrDefault(player); }
        if (replay != null) {
            Required("critical-last-policy-request.json", replay);
            var answer = result.Proposal as AiProposal.Answer;
            Required("critical-last-policy-result.json", new {
                replay.Id, replay.PolicyId, result.Failure,
                status = result.Failure != null ? "exception" : answer != null ? "proposed" : "unavailable",
                reason = answer?.ReasonCode, commandType = answer?.Command.GetType().Name,
                command = answer == null ? (JsonElement?)null : JsonSerializer.SerializeToElement(answer.Command, answer.Command.GetType()),
                executed = false
            });
        }
    }

    internal JsonNode Metrics(int steps, int turns, double elapsedMs, double workerElapsedMs, int comparisons)
    {
        lock (gate) return JsonSerializer.SerializeToNode(new {
            steps, turns, elapsedMs, workerElapsedMs, publicComparisons = comparisons,
            evaluationAttempts = evaluations, episodeCount = episodes.Count, policyWallMs = policyMs, workSpent,
            measurementIntegrity = Failure == null, criticalMetricsAvailable = Failure == null,
            policyMeasurementAudience = "focal-seat", focalEvaluationAttempts = focalWallSamples.Count,
            focalEpisodeCount = episodes.Count(pair => pair.Key.Seat == FocalPlayerId),
            focalPolicyWallMs = focalWallSamples.Sum(), focalWorkSpent = focalWorkSamples.Sum(value => (long)value),
            policyEvaluationMsSamples = focalWallSamples.Count <= 16384 && Failure == null ? focalWallSamples.ToArray() : null,
            episodePolicyWallMsSamples = episodes.Count(pair => pair.Key.Seat == FocalPlayerId) <= 16384 && Failure == null
                ? episodes.Where(pair => pair.Key.Seat == FocalPlayerId).Select(pair => pair.Value.Ms).ToArray() : null,
            workConsumedSamples = focalWorkSamples.Count <= 16384 && Failure == null ? focalWorkSamples.ToArray() : null,
            episodeActiveMsSamples = (double[]?)null,
            sampleLimit = 16384, samplesUnavailable = focalWallSamples.Count > 16384 || Failure != null,
            measurementBytes = written, measurementByteLimit = maximum, traceRows = rows, omittedTraceRows = omittedRows,
            pendingCaptureItems = pending.Count, captureQueueCapacity = QueueCapacity,
            replaySampling = "first-focal-evaluation-per-task", replaySamples = committedReplays, plannedReplaySamples = sampled.Count,
            omittedReplaySamples = omittedReplays,
            perTaskAudience = "all-seats", perTask = timings.OrderBy(pair => pair.Key).Select(pair => new { task = pair.Key.ToString(), attempts = pair.Value.Count,
                innerPolicyWallMs = new { p50 = Quantile(pair.Value, .50), p95 = Quantile(pair.Value, .95), p99 = Quantile(pair.Value, .99) } }),
            causalConsequenceCredits = (int?)null, candidateCoverage = (double?)null,
            metricScope = "Bounded compact evaluation measurements; unavailable coverage is null, not zero. Episode totals count each evaluation once."
        })!;
    }

    private static double Quantile(List<double> source, double fraction)
    {
        var ordered = source.Order().ToArray();
        return ordered[(int)Math.Ceiling(fraction * ordered.Length) - 1];
    }
    private static byte[] SerializeBounded<T>(T value, long remaining)
    {
        if (remaining <= 0) throw new AiExperimentNaturalEvidenceException("Natural capture byte limit reached.");
        using var destination = new BoundedBuffer(remaining);
        try { JsonSerializer.Serialize(destination, value); return destination.ToArray(); }
        catch (Exception error) when (error is JsonException or NotSupportedException) {
            throw new AiExperimentNaturalEvidenceException("Natural evidence serialization failed.", error);
        }
    }
    private sealed class BoundedBuffer(long maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count) {
            if (Position + count > maximum) throw new AiExperimentNaturalEvidenceException("Natural capture byte limit reached.");
        }
    }
    public void Dispose()
    {
        lock (gate) if (disposed) return;
        try { if (Failure == null) Drain(); }
        finally {
            lock (gate) {
                disposed = true; latest.Clear(); latestResults.Clear();
                foreach (var item in pending) { if (item.File == null) omittedRows++; else omittedReplays++; }
                pending.Clear();
            }
            try { trace.Dispose(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                throw Failed("Natural trace finalization failed.", error);
            }
        }
    }
    private AiExperimentNaturalEvidenceException Failed(string message, Exception? error = null)
    {
        var captured = new AiExperimentNaturalEvidenceException(message, error);
        lock (gate) failure ??= captured;
        return captured;
    }
}

internal sealed class AiExperimentNaturalEvidenceException(string message, Exception? cause = null) : IOException(message, cause);

// This wrapper observes the returned values and the actual execute completion.
// It never calls a second observation while recording a command receipt.
internal sealed class AiExperimentNaturalProvider(IAutomatedSeatProvider inner, ConfiguredSimulationStart configured,
    AiExperimentNaturalCapture capture) : IAutomatedSeatProvider, IAutomatedPriorityContinuation
{
    internal ImmutableArray<AutomatedSeatObservation> Last { get; private set; } = [];
    internal Exception? LastError { get; private set; }
    internal bool SafetyFailure { get; private set; }
    internal int Accepted { get; private set; }
    internal int Rejected { get; private set; }
    internal int Unknown { get; private set; }
    public bool SupportsAutomatedPriorityContinuation => inner is IAutomatedPriorityContinuation { SupportsAutomatedPriorityContinuation: true };
    public bool SupportsAutomatedPriorityResponses => inner is IAutomatedPriorityContinuation { SupportsAutomatedPriorityResponses: true };
    public long AutomatedPriorityEpoch => inner is IAutomatedPriorityContinuation port ? port.AutomatedPriorityEpoch : 0;
    public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => inner.EnableAutomatedSeatsAsync(cancellation);
    public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        => inner.ReportAutomationFaultAsync(gameId, seatId, reasonCode, cancellation);
    public async Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId,
        AutomatedPriorityControl control, CancellationToken cancellation = default)
    {
        if (inner is not IAutomatedPriorityContinuation port) return new AutomatedPriorityControlResult.Retired();
        try { return await port.ExecuteAutomatedPriorityControlAsync(epoch, seatId, control, cancellation); }
        catch (Exception error) { LastError = error; throw; }
    }

    public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
    {
        ImmutableArray<AutomatedSeatObservation> received;
        try { received = await inner.ObserveAutomatedSeatsAsync(cancellation); }
        catch (EngineObservationSupersededException) { throw; }
        catch (Exception error) { LastError = error; throw; }
        try {
            if (received.Length != configured.Participants.Length || received.Select(row => row.SeatId).Distinct().Count() != received.Length
                || !received.Select(row => row.SeatId).Order().SequenceEqual(configured.Participants.Select(row => row.PlayerId).Order()))
                throw new InvalidDataException("Configured automated observations changed actual-player bindings.");
            foreach (var item in received) {
                if (item.SeatId != item.State.ViewerId || !AiCampaignVisibility.PrivateWindowsAreDisclosed(item.State, item.Semantics, item.Domain))
                    throw new InvalidDataException("An automated observation disclosed another seat's private knowledge.");
                ConfiguredSimulationFacts.ValidateOwnedState(configured, item.State);
            }
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException) {
            SafetyFailure = true; LastError = error;
            throw new AiExperimentNaturalSafetyException("Natural owned observation failed identity/privacy validation: " + error.Message);
        }
        Last = received; LastError = null;
        return received;
    }
    public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
    {
        var original = Last.FirstOrDefault(row => row.SeatId == seatId);
        try { await inner.ExecuteAutomatedAsync(seatId, command, cancellation); }
        catch (Exception error) {
            LastError = error;
            if (error is EngineCommandRejectedException) Rejected++; else Unknown++;
            capture.Optional(new { kind = "provider-receipt", seatId, stamp = original?.State.Decision?.Id,
                command = command.GetType().Name, status = error is EngineCommandRejectedException ? "rejected" : "execution-unknown",
                error = error.GetType().Name, resolved = false });
            throw;
        }
        Accepted++;
        capture.Optional(new { kind = "provider-receipt", seatId,
            gameId = original?.State.Id, decisionId = original?.State.Decision?.Id,
            revision = original?.State.Revision, boardRevision = original?.State.BoardRevision,
            command = command.GetType().Name, status = "accepted", resolved = false });
    }
}
