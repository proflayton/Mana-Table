using Mana.Contracts;

namespace Mana.Client;

// Presentation identity and a current decision's authority handle are separate.
// The caller supplies this context only while that source remains disclosed.
public sealed record SourceActionContext(string GameId, string DecisionId, long Revision, int ViewerId,
    string SourceCardKey, string SourceCardId);

public enum SourceActionStatus { Idle, Loading, Ready, Unsupported, Stale, Failed }

public sealed record SourceActionView(SourceActionContext? Context, long Generation, SourceActionStatus Status,
    IReadOnlyList<SourceAction> Actions, string Error);

/// <summary>
/// Owns read-only action queries and their current authority. Public methods and
/// posted completions run on the caller's session thread; provider work does not.
/// A query never submits a command or establishes that an effect has completed.
/// </summary>
public sealed class SourceActionSession(ISourceActions? source, Action<Action> post, TimeSpan? timeout = null)
{
    private readonly TimeSpan requestTimeout = timeout ?? TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource lifetime = new();
    private bool inFlight, closed;
    private bool? observedSupport;
    public SourceActionView View { get; private set; } = new(null, 0, SourceActionStatus.Idle, Array.Empty<SourceAction>(), "");
    public bool Supported => !closed && source is { SupportsSourceActions: true };

    public void Observe(SourceActionContext? current)
    {
        if (closed) return;
        if (current != null && (string.IsNullOrWhiteSpace(current.GameId) || string.IsNullOrWhiteSpace(current.DecisionId)
            || string.IsNullOrWhiteSpace(current.SourceCardKey)))
            throw new ArgumentException("Source actions require a current game, decision and source handle.", nameof(current));
        bool supported = Supported;
        if (View.Context == current && observedSupport == supported) return;
        observedSupport = supported;
        View = new(current, View.Generation + 1, current == null ? SourceActionStatus.Idle
            : supported ? SourceActionStatus.Loading : SourceActionStatus.Unsupported, Array.Empty<SourceAction>(),
            current != null && !supported ? "This table does not expose card actions." : "");
        StartIfNeeded();
    }

    public void Clear() => Observe(null);

    public void Retry()
    {
        if (closed || View.Context == null || View.Status is SourceActionStatus.Loading or SourceActionStatus.Ready) return;
        var context = View.Context;
        Clear(); Observe(context);
    }

    public DecisionReply? TryCreateReply(SourceActionContext current, string actionId)
    {
        if (!Supported || View.Status != SourceActionStatus.Ready || View.Context != current
            || !View.Actions.Any(action => action.Id == actionId)) return null;
        return new(current.GameId, current.DecisionId, ReplyAction.UseSourceAction,
            Card: current.SourceCardKey, AbilityId: actionId);
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        View = new(null, View.Generation + 1, SourceActionStatus.Idle, Array.Empty<SourceAction>(), "");
        lifetime.Cancel();
    }

    private void StartIfNeeded()
    {
        var view = View;
        if (closed || inFlight || view.Status != SourceActionStatus.Loading || view.Context is not { } context) return;
        if (!Supported) {
            observedSupport = false;
            View = view with { Status = SourceActionStatus.Unsupported, Actions = Array.Empty<SourceAction>(),
                Error = "This table does not expose card actions." };
            return;
        }
        inFlight = true;
        long generation = view.Generation;
        var cancellation = lifetime.Token;
        _ = Task.Run(async () => {
            SourceActionSet? result = null;
            Exception? failure = null;
            try { result = await source!.SourceActionsAsync(context.GameId, context.DecisionId, context.SourceCardKey)
                .WaitAsync(requestTimeout, cancellation); }
            catch (Exception error) { failure = error; }
            post(() => {
                inFlight = false;
                if (closed) return;
                if (View.Generation == generation && View.Context == context) Complete(context, result, failure);
                // Coalesce intermediate sources into the latest still-disclosed one.
                // Failed/unsupported reads are never retried by this continuation.
                StartIfNeeded();
            });
        });
    }

    private void Complete(SourceActionContext context, SourceActionSet? result, Exception? failure)
    {
        var status = failure is NotSupportedException || !Supported ? SourceActionStatus.Unsupported
            : failure != null || result == null ? SourceActionStatus.Failed
            : result.GameId != context.GameId || result.DecisionId != context.DecisionId
                || result.SourceCardKey != context.SourceCardKey
                || !string.IsNullOrEmpty(result.SourceCardId) && result.SourceCardId != context.SourceCardId
                ? SourceActionStatus.Stale : SourceActionStatus.Ready;
        if (status == SourceActionStatus.Ready && (result!.Actions == null
            || result.Actions.Any(action => action == null || string.IsNullOrWhiteSpace(action.Id) || string.IsNullOrWhiteSpace(action.Text))
            || result.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != result.Actions.Length))
            status = SourceActionStatus.Failed;
        var actions = status == SourceActionStatus.Ready ? Array.AsReadOnly(result!.Actions!.ToArray()) : (IReadOnlyList<SourceAction>)Array.Empty<SourceAction>();
        View = new(context, View.Generation, status, actions, status switch {
            SourceActionStatus.Unsupported => "This table does not expose card actions.",
            SourceActionStatus.Stale => "These actions changed. Try again.",
            SourceActionStatus.Failed => "Couldn't load this card's actions.", _ => ""
        });
    }
}
