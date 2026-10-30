using System.Diagnostics;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>Bounded continuation through current authorized decisions on the same
/// seat connection. Intent policies own consent; rules still run in the engine.</summary>
public sealed class DecisionAdvance
{
    private readonly IMatchConnection connection;
    private readonly ClientCommandDiagnostics? diagnostics;
    private long diagnosticEpoch;
    private string? automaticSubmitted, quietSubmitted;
    public DecisionAdvance(IMatchConnection connection) => this.connection = connection;
    internal DecisionAdvance(IMatchConnection connection, ClientCommandDiagnostics diagnostics)
        : this(connection) => this.diagnostics = diagnostics;
    internal void SetDiagnosticEpoch(long epoch) => diagnosticEpoch = epoch;

    public Task<GameSnapshot?> RunAsync(GameSnapshot initial, Func<GameSnapshot, bool> canPass,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
        => RunBatchAsync(initial, null, canPass, shouldPresent, cancellation);

    public async Task<GameSnapshot?> RunQuietlyAsync(GameSnapshot initial, string token, Func<GameSnapshot, bool> canContinue,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
    {
        if (!QuietPriorityPolicy.CanPass(initial, token) || !canContinue(initial)) return initial;
        return await RunBatchAsync(initial, token, canContinue, shouldPresent, cancellation);
    }

    public Task<GameSnapshot?> RunStackAsync(GameSnapshot initial, StackResolutionIntent intent,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
        => RunFiniteAsync(initial, intent, shouldPresent, null, cancellation);

    internal async Task<GameSnapshot?> RunFiniteAsync(GameSnapshot initial, IFiniteDecisionPolicy intent,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, Action<GameSnapshot>? observed,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var clock = Stopwatch.StartNew();
        var state = initial;
        int passes = 0;
        try {
            while (!cancellation.IsCancellationRequested) {
                if (UndoFacts.RestoredSince(initial, state)) { intent.Stop(); return state; }
                if (intent.IsPaused || !intent.CanContinue(state) || shouldPresent(initial, state)) return state;
                var command = intent.NextCommand(state);
                ClientDiagnosticAttempt? attempt = null;
                if (!intent.IsActive) return state;
                if (command != null) {
                    if (!GameCommandPermissions.Allows(state, command)) { intent.Stop(); return state; }
                    if (cancellation.IsCancellationRequested) return state;
                    attempt = diagnostics?.Begin(state, command, ClientDiagnosticOrigin.Finite, diagnosticEpoch);
                    if (attempt == null) await connection.ExecuteAsync(command);
                    else await diagnostics!.ExecuteAsync(connection, command, attempt);
                    intent.Submitted(state, command); passes++;
                }
                // Cancellation never hides the result of a pass already sent.
                var next = diagnostics == null ? await connection.ObserveAsync() : await diagnostics.ObserveAsync(connection, attempt);
                if (next == null) { intent.Stop(); return null; }
                if (next.Id != state.Id) { intent.Stop(); observed?.Invoke(next); return next; }
                if (next.Revision >= state.Revision) {
                    UndoFacts.ValidateProgress(state, next);
                    bool changed = next.Revision > state.Revision || next.ViewerId != state.ViewerId || next.Decision?.Id != state.Decision?.Id;
                    state = next;
                    if (changed) observed?.Invoke(state);
                }
                // The caller owns visible checkpoints. Ordinary stack passing
                // keeps every effect beat; explicit finite presentation can
                // aggregate only its captured work while observing every result.
                if (UndoFacts.RestoredSince(initial, state)) { intent.Stop(); return state; }
                if (intent.IsPaused || !intent.CanContinue(state) || shouldPresent(initial, state)) return state;
                if (clock.ElapsedMilliseconds >= 350 || passes >= 32) return state;
                if (command == null || state.Decision == null) {
                    try { await Task.Delay(10, cancellation); }
                    catch (OperationCanceledException) { return state; }
                }
            }
            return state;
        } catch { intent.Stop(); throw; }
    }

    private async Task<GameSnapshot?> RunBatchAsync(GameSnapshot initial, string? quietToken, Func<GameSnapshot, bool> policy,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var state = initial;
        int passes = 0;
        long activity = quietToken == null ? 0 : initial.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
        while (!cancellation.IsCancellationRequested) {
            if (UndoFacts.RestoredSince(initial, state)) return state;
            if (quietToken == null) {
                if (state.Id != initial.Id || state.Status is "finished" or "error" || shouldPresent(initial, state)) return state;
            } else if (!QuietPriorityPolicy.CanWait(state, initial.Id, initial.Turn, initial.ActivePlayerId, quietToken)
                || QuietPriorityPolicy.HasInterruption(state, activity) || !policy(state) || shouldPresent(initial, state)) return state;
            string scope = state.Id + ":" + state.Decision?.Id;
            string? submitted = quietToken == null ? automaticSubmitted : quietSubmitted;
            bool waiting = state.Decision == null || submitted == scope;
            ClientDiagnosticAttempt? attempt = null;
            if (!waiting) {
                if (state.Decision is not { } decision || quietToken == null && !policy(state)) return state;
                // The two intents share transport, never authority. A token
                // cannot broaden Auto, and no required choice is a batch action.
                GameCommand command = quietToken == null ? new AutoPassPriority(state.Id, decision.Id)
                    : new PassForNow(state.Id, decision.Id, quietToken);
                if (!GameCommandPermissions.Allows(state, command)) return state;
                if (cancellation.IsCancellationRequested) return state;
                attempt = diagnostics?.Begin(state, command, quietToken == null ? ClientDiagnosticOrigin.Automatic : ClientDiagnosticOrigin.Quiet, diagnosticEpoch);
                if (attempt == null) await connection.ExecuteAsync(command);
                else await diagnostics!.ExecuteAsync(connection, command, attempt);
                if (quietToken == null) automaticSubmitted = scope; else quietSubmitted = scope;
                submitted = scope; passes++;
            }
            // Always reconcile an already submitted command, even if Hold was pressed in flight.
            var next = diagnostics == null ? await connection.ObserveAsync() : await diagnostics.ObserveAsync(connection, attempt);
            if (next == null) return null;
            if (next.Id != state.Id) return next;
            if (next.Revision >= state.Revision) { UndoFacts.ValidateProgress(state, next); state = next; }
            // Yield periodically to present progress and avoid owning the connection indefinitely.
            if (clock.ElapsedMilliseconds >= 350 || passes >= 32) return state;
            if (state.Decision == null || submitted == state.Id + ":" + state.Decision.Id) {
                try { await Task.Delay(10, cancellation); }
                catch (OperationCanceledException) { return state; }
            }
        }
        return state;
    }
}
