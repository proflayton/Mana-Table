using System.Diagnostics;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>Drains authorized empty decisions without publishing the intervening snapshots.
/// Uses the same seat connection for local and remote games; rules still run in the engine.</summary>
public sealed class DecisionAdvance(IMatchConnection connection)
{
    private string? automaticSubmitted, quietSubmitted;

    public Task<GameSnapshot?> RunAsync(GameSnapshot initial, Func<GameSnapshot, bool> canPass,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
        => RunBatchAsync(initial, null, canPass, shouldPresent, cancellation);

    public async Task<GameSnapshot?> RunQuietlyAsync(GameSnapshot initial, string token, Func<GameSnapshot, bool> canContinue,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
    {
        if (!QuietPriorityPolicy.CanPass(initial, token) || !canContinue(initial)) return initial;
        return await RunBatchAsync(initial, token, canContinue, shouldPresent, cancellation);
    }

    public async Task<GameSnapshot?> RunStackAsync(GameSnapshot initial, StackResolutionIntent intent,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var clock = Stopwatch.StartNew();
        var state = initial;
        int passes = 0;
        try {
            while (!cancellation.IsCancellationRequested) {
                if (!intent.CanContinue(state) || shouldPresent(initial, state)) return state;
                var command = intent.NextPass(state);
                if (!intent.IsActive) return state;
                if (command != null) {
                    if (cancellation.IsCancellationRequested) return state;
                    await connection.ExecuteAsync(command);
                    intent.Submitted(state); passes++;
                }
                // Cancellation never hides the result of a pass already sent.
                var next = await connection.ObserveAsync();
                if (next == null) { intent.Stop(); return null; }
                if (next.Id != state.Id) { intent.Stop(); return next; }
                if (next.Revision >= state.Revision) state = next;
                // Yield at every real effect boundary. The application presents
                // its full beat before it starts another batch of this intent.
                if (!intent.CanContinue(state) || shouldPresent(initial, state)) return state;
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
            if (quietToken == null) {
                if (state.Id != initial.Id || state.Status is "finished" or "error" || shouldPresent(initial, state)) return state;
            } else if (!QuietPriorityPolicy.CanWait(state, initial.Id, initial.Turn, initial.ActivePlayerId, quietToken)
                || QuietPriorityPolicy.HasInterruption(state, activity) || !policy(state) || shouldPresent(initial, state)) return state;
            string scope = state.Id + ":" + state.Decision?.Id;
            string? submitted = quietToken == null ? automaticSubmitted : quietSubmitted;
            bool waiting = state.Decision == null || submitted == scope;
            if (!waiting) {
                if (state.Decision is not { } decision || quietToken == null && !policy(state)) return state;
                // The two intents share transport, never authority. A token
                // cannot broaden Auto, and no required choice is a batch action.
                GameCommand command = quietToken == null ? new AutoPassPriority(state.Id, decision.Id)
                    : new PassForNow(state.Id, decision.Id, quietToken);
                if (!GameCommandPermissions.Allows(state, command)) return state;
                if (cancellation.IsCancellationRequested) return state;
                await connection.ExecuteAsync(command);
                if (quietToken == null) automaticSubmitted = scope; else quietSubmitted = scope;
                submitted = scope; passes++;
            }
            // Always reconcile an already submitted command, even if Hold was pressed in flight.
            var next = await connection.ObserveAsync();
            if (next == null) return null;
            if (next.Id != state.Id) return next;
            if (next.Revision >= state.Revision) state = next;
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
