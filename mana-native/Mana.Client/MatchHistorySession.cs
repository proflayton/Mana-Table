using Mana.Contracts;

namespace Mana.Client;

public enum MatchHistoryStatus { Idle, Loading, Ready, Unsupported, Failed }

/// <summary>Read-only history queries are independent of decision execution.
/// Methods and posted completions run on the client's owner thread. Disk and
/// provider failures never become match-command failures.</summary>
public sealed class MatchHistorySession(IMatchHistory? source, Action<Action> post, TimeSpan? timeout = null)
{
    private readonly TimeSpan requestTimeout = timeout ?? TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource lifetime = new();
    private long generation, requestedRevision = -1, completedRevision = -1, observedRevision = -1;
    private bool inFlight, closed, unavailable, hasMore;
    public MatchHistoryReadModel Model { get; private set; } = MatchHistoryReadModel.Empty();
    public MatchHistoryStatus Status { get; private set; }
    public string Error { get; private set; } = "";
    public long ObservedRevision => observedRevision;
    public bool IsCaughtUp => !closed && !inFlight && !hasMore && Status == MatchHistoryStatus.Ready
        && observedRevision >= requestedRevision;
    public bool Supported => !closed && !unavailable && source is { SupportsHistory: true };
    /// <summary>Newly admitted facts only. Subscribers must be fast; failed
    /// observers cannot interrupt reading, input, or other observers.</summary>
    public event Action<HistoryBatch>? Accepted;
    public event Action? Updated;

    public void Observe(GameSnapshot? state)
    {
        if (closed) return;
        string game = state?.Id ?? "";
        int viewer = state?.ViewerId ?? -1;
        if (Model.GameId != game || Model.ViewerId != viewer) {
            generation++; completedRevision = requestedRevision = observedRevision = -1; unavailable = hasMore = false;
            Model = MatchHistoryReadModel.Empty(game, viewer); Error = "";
        }
        if (state == null || viewer < 0) { Status = MatchHistoryStatus.Idle; return; }
        requestedRevision = Math.Max(requestedRevision, state.Revision);
        if (!Supported) { Status = MatchHistoryStatus.Unsupported; return; }
        StartIfNeeded();
    }

    // Archive readers and focused fixtures can admit typed batches without an
    // engine. The caller must first bind its exact game/audience with Observe.
    public bool Accept(HistoryBatch batch)
    {
        if (closed || batch.GameId != Model.GameId || batch.ViewerId != Model.ViewerId) return false;
        var next = Model.Append(batch, out var accepted);
        Model = next;
        if (accepted == null) return false;
        if (Accepted is { } subscribers)
            foreach (Action<HistoryBatch> subscriber in subscribers.GetInvocationList()) {
                try { subscriber(accepted); } catch { /* Evidence is not game authority. */ }
            }
        return true;
    }

    public void Retry()
    {
        if (closed || Model.GameId.Length == 0 || Model.ViewerId < 0 || !Supported) return;
        completedRevision = -1; Error = ""; StartIfNeeded();
    }

    public void Close()
    {
        if (closed) return;
        closed = true; generation++; lifetime.Cancel();
        Model = MatchHistoryReadModel.Empty(); Status = MatchHistoryStatus.Idle; Error = "";
    }

    private void StartIfNeeded()
    {
        if (closed || inFlight || !Supported || Model.GameId.Length == 0 || Model.ViewerId < 0
            || completedRevision >= requestedRevision) return;
        var scope = (Model.GameId, Model.ViewerId, Generation: generation);
        long cursor = Model.Cursor, revision = requestedRevision;
        inFlight = true; Status = MatchHistoryStatus.Loading;
        _ = Task.Run(async () => {
            HistoryBatch? result = null; Exception? failure = null;
            try { result = await source!.HistoryAsync(scope.GameId, cursor, HistoryFacts.MaximumBatchRecords)
                .WaitAsync(requestTimeout, lifetime.Token); }
            catch (Exception error) { failure = error; }
            post(() => {
                inFlight = false;
                if (closed) return;
                if (scope.Generation != generation || scope.GameId != Model.GameId || scope.ViewerId != Model.ViewerId) {
                    StartIfNeeded(); return;
                }
                completedRevision = revision;
                try {
                    if (failure != null) throw failure;
                    if (result == null || result.GameId != scope.GameId || result.ViewerId != scope.ViewerId
                        || result.NextSequence < cursor || result.HasMore && result.NextSequence <= cursor)
                        throw new IOException("The history provider returned an invalid scope or progress cursor.");
                    Accept(result); Status = MatchHistoryStatus.Ready; Error = "";
                    observedRevision = revision; hasMore = result.HasMore;
                    if (result.HasMore) completedRevision = -1;
                } catch (Exception error) {
                    unavailable = error is NotSupportedException;
                    Status = error is NotSupportedException ? MatchHistoryStatus.Unsupported : MatchHistoryStatus.Failed;
                    Error = "History could not be updated. The match can continue.";
                }
                if (Updated is { } subscribers)
                    foreach (Action subscriber in subscribers.GetInvocationList()) {
                        try { subscriber(); } catch { /* A lifecycle observer cannot interrupt play. */ }
                    }
                if (Status is MatchHistoryStatus.Ready or MatchHistoryStatus.Loading) StartIfNeeded();
            });
        });
    }
}
