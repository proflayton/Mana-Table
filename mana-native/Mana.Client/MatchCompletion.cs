using Mana.Contracts;

namespace Mana.Client;

/// <summary>Observes actual match completion before a dependent local transition.
/// A concession receipt admits the command; it does not establish the rules outcome.</summary>
public static class MatchCompletion
{
    public static async Task<GameSnapshot> WaitAsync(IMatchConnection connection, GameSnapshot initial,
        TimeSpan? timeout = null, TimeProvider? clock = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(initial);
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var time = clock ?? TimeProvider.System;
        long started = time.GetTimestamp(), revision = initial.Revision;
        while (true) {
            cancellation.ThrowIfCancellationRequested();
            var remaining = budget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The game has not finished ending. Reconnect before leaving.");
            var state = await connection.ObserveAsync().WaitAsync(remaining, time, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (time.GetElapsedTime(started) >= budget) throw new TimeoutException("The game has not finished ending. Reconnect before leaving.");
            if (state == null || state.Id != initial.Id || state.ViewerId != initial.ViewerId)
                throw new IOException("The match connection changed before this game finished ending.");
            if (state.Revision >= revision) {
                revision = state.Revision;
                if (state.Status is "finished" or "error" && state.Decision == null) return state;
            }
            remaining = budget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The game has not finished ending. Reconnect before leaving.");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), time, cancellation);
        }
    }
}
