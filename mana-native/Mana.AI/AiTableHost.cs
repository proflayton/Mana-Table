using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

public sealed record AiHostFault(string GameId, int SeatId, string ReasonCode);

/// <summary>Host-only failure evidence. Never part of a policy's seat knowledge or public match state.</summary>
public interface IAiHostDiagnostics
{
    void Report(AiHostFault fault, Exception? error);
}

/// <summary>
/// Composition-owned driver. Only this host sees the private bot port; policies
/// receive immutable individual-seat frames and submit through ordinary rules
/// inputs. There is no legacy strategy or automatic concession fallback.
/// </summary>
public sealed partial class AiTableHost(IAutomatedSeatProvider provider, Func<int, IAiPolicy> policy,
    long seed, AiSeatLimits? limits = null, IAiHostDiagnostics? diagnostics = null, IAiHostPacing? pacing = null,
    TimeProvider? operationClock = null) : IAsyncDisposable
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(45);
    private readonly TimeProvider operationTime = operationClock ?? TimeProvider.System;
    private readonly SemaphoreSlim step = new(1);
    private readonly SemaphoreSlim startup = new(1);
    private readonly Dictionary<(string Game, int Seat), AiSeatSession> seats = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> faultedGames = [];
    private readonly Dictionary<(string Game, int Seat), AiDecisionStamp> rejected = [];
    private readonly Dictionary<(string Game, int Seat), long> awaitingObservation = [];
    private Task? runner;
    private bool enabled, disposed;
    public AiHostFault? LastFault { get; private set; }
    public ImmutableArray<AiSeatHistory> Histories {
        get { lock (seats) return seats.Values.Select(seat => seat.History).ToImmutableArray(); }
    }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = stopping.Token;
        await startup.WaitAsync(token).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enabled && runner is { IsCompleted: false }) return;
            await AwaitProvider("enable", provider.EnableAutomatedSeatsAsync, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            enabled = true;
            LastFault = null;
            runner = Task.Run(RunAsync);
        } finally { startup.Release(); }
    }

    // Also used by deterministic integration runners: one fresh observation,
    // at most one scoped command per seat, with an explicit engine receipt.
    public async Task StepAsync(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await StepCoreAsync(cancellation).ConfigureAwait(false);
    }

    // Acknowledgement requests another read, never means input consumption or
    // permission to reuse this batch. Absence of work keeps the idle backoff.
    private enum AiHostStepOutcome { WaitForCurrentWork, ReobserveAfterAcceptedSubmission }

    private async Task<AiHostStepOutcome> StepCoreAsync(CancellationToken cancellation)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = stopping.Token;
        await step.WaitAsync(token).ConfigureAwait(false);
        bool acceptedSubmission = false, acceptedControl = false, backoffRequired = false;
        try {
            ImmutableArray<AutomatedSeatObservation> observed;
            try { observed = await AwaitProvider("observe", provider.ObserveAutomatedSeatsAsync, token).ConfigureAwait(false); }
            catch (EngineObservationSupersededException) {
                // Discovery produced no authority. The next ordinary host step
                // must obtain a new complete batch before evaluating any seat.
                // Other provider failures still reach the diagnostic/fault path.
                return AiHostStepOutcome.WaitForCurrentWork;
            }
            pacing?.Observe(observed.Select(item => AiTableProgress.From(item.State)).ToImmutableArray());
            var active = observed.Select(item => (item.State.Id, item.SeatId)).ToHashSet();
            await RetirePriorityOwnersAsync(active, token).ConfigureAwait(false);
            faultedGames.RemoveWhere(game => !active.Any(key => key.Id == game));
            lock (seats) {
                foreach (var key in seats.Keys.Where(key => !active.Contains(key)).ToArray()) {
                    seats[key].Dispose(); seats.Remove(key);
                    rejected.Remove(key);
                    awaitingObservation.Remove(key);
                }
            }
            foreach (var item in observed) {
                token.ThrowIfCancellationRequested();
                var state = item.State;
                if (state.ViewerId != item.SeatId) throw new InvalidDataException("The automated observation belongs to another seat.");
                if (faultedGames.Contains(state.Id)) { backoffRequired = true; continue; }
                var key = (state.Id, item.SeatId);
                AiSeatSession session;
                try {
                    lock (seats) {
                        if (!seats.TryGetValue(key, out session!)) {
                            session = new(state.Id, item.SeatId, policy(item.SeatId), seed,
                                // A causal declaration can span hundreds of
                                // acknowledged card assignments and pipe trips.
                                limits ?? new(TimeSpan.FromSeconds(30), 4096));
                            seats.Add(key, session);
                        }
                    }
                } catch (Exception error) {
                    await FaultAsync(state.Id, item.SeatId, "policy-configuration", token, error).ConfigureAwait(false);
                    backoffRequired = true;
                    continue;
                }
                if (!session.ObserveRestoration(state)) {
                    if (await CheckObservationProgress(key, token).ConfigureAwait(false)) backoffRequired = true;
                    continue;
                }
                PriorityWork priorityWork;
                try { priorityWork = await MaintainPriorityAsync(item, session, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) {
                    await FaultAsync(state.Id, item.SeatId, error is PriorityEvidenceOverflowException
                        ? "priority-observation-overflow" : "priority-control-unknown", token, error).ConfigureAwait(false);
                    backoffRequired = true; continue;
                }
                acceptedControl |= priorityWork == PriorityWork.Refreshed;
                if (priorityWork != PriorityWork.Policy) {
                    session.ObserveAutomaticProgress(state); session.Observe(null); continue;
                }
                if (state.Status != "playing" || state.Decision == null || state.Viewer?.Eliminated != false) {
                    awaitingObservation.Remove(key);
                    session.Observe(null); continue;
                }
                AiDecisionFrame? frame = null;
                // Only a valid newly admitted authority retires an accepted
                // command's wait. Identical successful polls and presentation
                // pauses cannot continually grant another waiting allowance.
                if (awaitingObservation.ContainsKey(key)) {
                    frame = new(state, domain: item.Domain, semantics: item.Semantics);
                    if (session.Observe(frame)) awaitingObservation.Remove(key);
                    if (await CheckObservationProgress(key, token).ConfigureAwait(false)) { backoffRequired = true; continue; }
                }
                // A presentation pause never caches an answer.
                // Skip this iteration and re-observe current authority next time.
                if (pacing != null && !pacing.Ready(state.Id, item.SeatId)) continue;
                frame ??= new(state, domain: item.Domain, semantics: item.Semantics);
                if (rejected.Remove(key, out var priorRejection) && priorRejection == frame.Stamp) {
                    await FaultAsync(state.Id, item.SeatId, "rejected-command", token).ConfigureAwait(false);
                    backoffRequired = true;
                    continue;
                }
                session.Observe(frame);
                var result = await session.EvaluateAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.Status is AiSeatStatus.Unavailable or AiSeatStatus.Invalid or AiSeatStatus.BudgetExceeded or AiSeatStatus.NeedsRecovery) {
                    await FaultAsync(state.Id, item.SeatId, result.Status.ToString().ToLowerInvariant(), token).ConfigureAwait(false);
                    backoffRequired = true;
                    continue;
                }
                if (session.TakeCommand() is not { } ticket) continue;
                try {
                    await AwaitProvider("execute", stop => provider.ExecuteAutomatedAsync(item.SeatId, ticket.Command, stop), token).ConfigureAwait(false);
                    acceptedSubmission |= session.Report(ticket, AiExecutionReceipt.Accepted);
                    awaitingObservation[key] = operationTime.GetTimestamp();
                } catch (EngineCommandRejectedException) {
                    session.Report(ticket, AiExecutionReceipt.Rejected);
                    rejected[key] = ticket.Stamp;
                    backoffRequired = true;
                    // The engine may have advanced between observation and reply.
                    // The next iteration must observe a new scope, never replay.
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    // Cancellation stops waiting. A dispatched command may still
                    // finish; neither acceptance nor a safe retry is inferred.
                    session.Report(ticket, AiExecutionReceipt.Unknown);
                    throw;
                } catch (Exception error) {
                    session.Report(ticket, AiExecutionReceipt.Unknown);
                    await FaultAsync(state.Id, item.SeatId, "execution-unknown", token, error).ConfigureAwait(false);
                    backoffRequired = true;
                }
            }
            return (acceptedSubmission || acceptedControl) && !backoffRequired ? AiHostStepOutcome.ReobserveAfterAcceptedSubmission
                : AiHostStepOutcome.WaitForCurrentWork;
        } finally { step.Release(); }
    }

    private async Task<bool> CheckObservationProgress((string Game, int Seat) key, CancellationToken cancellation)
    {
        if (!awaitingObservation.TryGetValue(key, out var started)
            || operationTime.GetElapsedTime(started) < ObservationTimeout) return false;
        awaitingObservation.Remove(key);
        await FaultAsync(key.Game, key.Seat, "accepted-no-progress", cancellation,
            new TimeoutException("The automated decision did not advance after an accepted command.")).ConfigureAwait(false);
        return true;
    }

    private async Task FaultAsync(string game, int seat, string reason, CancellationToken cancellation, Exception? error = null)
    {
        LastFault = new(game, seat, reason);
        ReportDiagnostic(LastFault, error);
        faultedGames.Add(game);
        try { await WithdrawPriorityGameAsync(game, cancellation).ConfigureAwait(false); }
        catch (Exception withdrawal) { ReportDiagnostic(new(game, seat, "priority-withdrawal-unknown"), withdrawal); }
        await AwaitProvider("fault-report", stop => provider.ReportAutomationFaultAsync(game, seat, reason, stop), cancellation).ConfigureAwait(false);
    }

    private Task AwaitProvider(string operation, Func<CancellationToken, Task> begin, CancellationToken cancellation)
        => AwaitProvider(operation, async stop => { await begin(stop).ConfigureAwait(false); return true; }, cancellation);

    private async Task<T> AwaitProvider<T>(string operation, Func<CancellationToken, Task<T>> begin, CancellationToken cancellation)
    {
        using var deadline = new CancellationTokenSource(ProviderTimeout, operationTime);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var token = stopping.Token;
        // A provider can ignore cancellation or block before returning its Task.
        // Its late completion has no host authority, and faults remain observed.
        var pending = Task.Run(() => { token.ThrowIfCancellationRequested(); return begin(token); });
        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try {
            var result = await pending.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested) {
            throw new TimeoutException("The automated provider did not complete its " + operation + " operation in time.", error);
        }
    }

    private void ReportDiagnostic(AiHostFault fault, Exception? error)
    {
        try { diagnostics?.Report(fault, error); }
        catch { /* Failed evidence storage cannot change the fail-closed match outcome. */ }
    }

    private async Task RunAsync()
    {
        try {
            while (!lifetime.IsCancellationRequested) {
                // This private loop's lifetime is authoritative during normal
                // disposal; public StepAsync retains its strict disposed check.
                var outcome = await StepCoreAsync(lifetime.Token).ConfigureAwait(false);
                if (outcome == AiHostStepOutcome.WaitForCurrentWork)
                    await Task.Delay(TimeSpan.FromMilliseconds(40), operationTime, lifetime.Token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) {
            // A failed private connection is not a legal pass. Revoke all work;
            // the owning engine connection reports its failure to the table.
            LastFault ??= new("", -1, "provider-failed");
            (string Game, int Seat)[] owned;
            lock (seats) { owned = seats.Keys.ToArray(); foreach (var seat in seats.Values) seat.Dispose(); }
            // The failing batch may belong to any seat. Do not misidentify the
            // first previously owned seat as the source of a provider failure.
            ReportDiagnostic(new("", -1, "provider-failed"), error);
            foreach (var game in owned.GroupBy(key => key.Game)) {
                var seat = game.First();
                // A fault-report timeout is not permission to repeat that report.
                if (faultedGames.Contains(seat.Game)) continue;
                try { await FaultAsync(seat.Game, seat.Seat, "provider-failed", lifetime.Token).ConfigureAwait(false); }
                catch { /* The disconnected engine owns connection-failure presentation. */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        await startup.WaitAsync().ConfigureAwait(false);
        try {
            if (runner != null) await runner.ConfigureAwait(false);
            await step.WaitAsync().ConfigureAwait(false);
            try {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), operationTime);
                try { await WithdrawPriorityGameAsync(null, cleanup.Token).ConfigureAwait(false); }
                finally {
                    // A failed negative receipt leaves server consent uncertain,
                    // but it cannot retain local policy work or reusable tickets.
                    lock (seats) { foreach (var seat in seats.Values) seat.Dispose(); seats.Clear(); }
                    awaitingObservation.Clear();
                }
            }
            finally { step.Release(); }
        } finally { startup.Release(); lifetime.Dispose(); }
    }
}
