using System.Diagnostics;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>Drains authorized empty decisions without publishing the intervening snapshots.
/// Uses the same seat connection for local and remote games; rules still run in the engine.</summary>
public sealed class DecisionAdvance(IMatchConnection connection)
{
    private string? submitted;

    public async Task<GameSnapshot?> RunAsync(GameSnapshot initial, Func<GameSnapshot, bool> canPass,
        Func<GameSnapshot, GameSnapshot, bool> shouldPresent, CancellationToken cancellation = default)
    {
        var clock = Stopwatch.StartNew();
        var state = initial;
        int passes = 0;
        while (!cancellation.IsCancellationRequested) {
            if (state.Id != initial.Id || state.Status is "finished" or "error" || shouldPresent(initial, state)) return state;
            string scope = state.Id + ":" + state.Decision?.Id;
            bool waiting = state.Decision == null || submitted == scope;
            if (!waiting) {
                // An application policy may restrict Auto, but can never broaden the engine's permission.
                if (state.Status != "playing" || state.Decision is not { Kind: "input", Intent: DecisionIntent.Priority, CanAutoPass: true, OkEnabled: true }
                    || !canPass(state)) return state;
                if (cancellation.IsCancellationRequested) return state;
                await connection.ReplyAsync(new(state.Id, state.Decision.Id, ReplyAction.AutoPass));
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
