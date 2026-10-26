using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

public sealed record AiHostFault(string GameId, int SeatId, string ReasonCode);

/// <summary>
/// Composition-owned driver. Only this host sees the private bot port; policies
/// receive immutable individual-seat frames and submit through ordinary rules
/// inputs. There is no legacy strategy or automatic concession fallback.
/// </summary>
public sealed class AiTableHost(IAutomatedSeatProvider provider, Func<int, IAiPolicy> policy,
    long seed, AiSeatLimits? limits = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim step = new(1);
    private readonly SemaphoreSlim startup = new(1);
    private readonly Dictionary<(string Game, int Seat), AiSeatSession> seats = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> faultedGames = [];
    private readonly Dictionary<(string Game, int Seat), AiDecisionStamp> rejected = [];
    private Task? runner;
    private bool enabled, disposed;
    public AiHostFault? LastFault { get; private set; }
    public ImmutableArray<AiSeatHistory> Histories {
        get { lock (seats) return seats.Values.Select(seat => seat.History).ToImmutableArray(); }
    }

    public async Task StartAsync()
    {
        await startup.WaitAsync().ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enabled && runner is { IsCompleted: false }) return;
            await provider.EnableAutomatedSeatsAsync().ConfigureAwait(false);
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
        await step.WaitAsync(cancellation).ConfigureAwait(false);
        try {
            var observed = await provider.ObserveAutomatedSeatsAsync().ConfigureAwait(false);
            var active = observed.Select(item => (item.State.Id, item.SeatId)).ToHashSet();
            faultedGames.RemoveWhere(game => !active.Any(key => key.Id == game));
            lock (seats) {
                foreach (var key in seats.Keys.Where(key => !active.Contains(key)).ToArray()) {
                    seats[key].Dispose(); seats.Remove(key);
                    rejected.Remove(key);
                }
            }
            foreach (var item in observed) {
                cancellation.ThrowIfCancellationRequested();
                var state = item.State;
                if (state.ViewerId != item.SeatId) throw new InvalidDataException("The automated observation belongs to another seat.");
                if (faultedGames.Contains(state.Id)) continue;
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
                } catch {
                    await FaultAsync(state.Id, item.SeatId, "policy-configuration").ConfigureAwait(false);
                    continue;
                }
                if (state.Status != "playing" || state.Decision == null || state.Viewer?.Eliminated != false) {
                    session.Observe(null); continue;
                }
                var frame = new AiDecisionFrame(state, domain: item.Domain, semantics: item.Semantics);
                if (rejected.Remove(key, out var priorRejection) && priorRejection == frame.Stamp) {
                    await FaultAsync(state.Id, item.SeatId, "rejected-command").ConfigureAwait(false);
                    continue;
                }
                session.Observe(frame);
                var result = await session.EvaluateAsync(cancellation).ConfigureAwait(false);
                if (result.Status is AiSeatStatus.Unavailable or AiSeatStatus.Invalid or AiSeatStatus.BudgetExceeded or AiSeatStatus.NeedsRecovery) {
                    await FaultAsync(state.Id, item.SeatId, result.Status.ToString().ToLowerInvariant()).ConfigureAwait(false);
                    continue;
                }
                if (session.TakeCommand() is not { } ticket) continue;
                try {
                    await provider.ExecuteAutomatedAsync(item.SeatId, ticket.Command).ConfigureAwait(false);
                    session.Report(ticket, AiExecutionReceipt.Accepted);
                } catch (EngineCommandRejectedException) {
                    session.Report(ticket, AiExecutionReceipt.Rejected);
                    rejected[key] = ticket.Stamp;
                    // The engine may have advanced between observation and reply.
                    // The next iteration must observe a new scope, never replay.
                } catch {
                    session.Report(ticket, AiExecutionReceipt.Unknown);
                    await FaultAsync(state.Id, item.SeatId, "execution-unknown").ConfigureAwait(false);
                }
            }
        } finally { step.Release(); }
    }

    private async Task FaultAsync(string game, int seat, string reason)
    {
        LastFault = new(game, seat, reason);
        faultedGames.Add(game);
        await provider.ReportAutomationFaultAsync(game, seat, reason).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try {
            while (!lifetime.IsCancellationRequested) {
                await StepAsync(lifetime.Token).ConfigureAwait(false);
                await Task.Delay(40, lifetime.Token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch {
            // A failed private connection is not a legal pass. Revoke all work;
            // the owning engine connection reports its failure to the table.
            LastFault ??= new("", -1, "provider-failed");
            (string Game, int Seat)[] owned;
            lock (seats) { owned = seats.Keys.ToArray(); foreach (var seat in seats.Values) seat.Dispose(); }
            foreach (var game in owned.GroupBy(key => key.Game)) {
                var seat = game.First();
                try { await FaultAsync(seat.Game, seat.Seat, "provider-failed").ConfigureAwait(false); }
                catch { /* The disconnected engine owns connection-failure presentation. */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        if (runner != null) await runner.ConfigureAwait(false);
        await step.WaitAsync().ConfigureAwait(false);
        try { lock (seats) { foreach (var seat in seats.Values) seat.Dispose(); seats.Clear(); } }
        finally { step.Release(); lifetime.Dispose(); }
    }
}
