using System.Diagnostics;
using Mana.Contracts;

namespace Mana.Table;

// A multiplayer concession can eliminate this viewer while the table keeps
// playing. Observe that actual departure rather than treating command admission
// as completion, ending the table, answering a foreign choice or sleeping blindly.
internal static class MatchDepartureObservation
{
    internal static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining
            : throw new TimeoutException("The existing smoke watchdog expired before match departure completed.");
    }

    internal static async Task<GameSnapshot> WaitAsync(IMatchConnection connection, GameSnapshot initial, TimeSpan timeout)
    {
        using var expiry = new CancellationTokenSource(timeout);
        var elapsed = Stopwatch.StartNew();
        long revision = initial.Revision;
        try {
            while (true) {
                Guard();
                var state = await connection.ObserveAsync().WaitAsync(expiry.Token)
                    ?? throw new InvalidOperationException("The conceding viewer's match disappeared before departure was published.");
                Guard();
                if (state.Id != initial.Id || state.ViewerId != initial.ViewerId || state.Revision < revision)
                    throw new InvalidOperationException("Match departure changed game or viewer, or regressed its accepted publication.");
                revision = state.Revision;
                if (state.Status == "error") throw new InvalidOperationException(state.Error ?? "The match failed while conceding.");
                if (state.Viewer?.Eliminated == true && state.Decision == null && state.Status is "playing" or "finished") return state;
                await Task.Delay(5, expiry.Token);
            }
        } catch (OperationCanceledException) when (expiry.IsCancellationRequested) {
            throw new TimeoutException("The existing smoke watchdog expired while observing the conceding viewer's departure.");
        }
        void Guard()
        {
            expiry.Token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= timeout) throw new TimeoutException("The existing smoke watchdog expired while observing the conceding viewer's departure.");
        }
    }
}
