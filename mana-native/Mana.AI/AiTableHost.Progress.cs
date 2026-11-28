using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

public sealed partial class AiTableHost
{
    private readonly Dictionary<string, MatchProgressWatch> progressWatches = [];
    private long? supersededSince;
    private TimeSpan ProgressTime => operationTime.GetElapsedTime(0, operationTime.GetTimestamp());

    private async Task<bool> CheckMatchProgressAsync(ImmutableArray<AutomatedSeatObservation> rows, CancellationToken token)
    {
        var games = rows.Select(row => row.State.Id).ToHashSet();
        foreach (var old in progressWatches.Keys.Where(game => !games.Contains(game)).ToArray()) progressWatches.Remove(old);
        bool repaired = false;
        foreach (var group in rows.GroupBy(row => row.State.Id)) {
            if (faultedGames.Contains(group.Key)) continue;
            foreach (var item in group) MatchProgressWitness.Validate(item.State);
            var first = group.First();
            var missing = first.State.MatchWaits?.FirstOrDefault(wait => !wait.Published);
            var ownWait = first.State.MatchWaits?.FirstOrDefault(wait => group.Any(item => item.SeatId == wait.PlayerId));
            var row = missing != null ? group.FirstOrDefault(item => item.SeatId == missing.PlayerId) ?? first
                : ownWait != null ? group.First(item => item.SeatId == ownWait.PlayerId)
                    : group.FirstOrDefault(item => item.State.DecisionOwnerId == item.SeatId) ?? first;
            var state = row.State;
            bool human = missing == null && (state.MatchWaits is { } waits
                ? ownWait == null && !waits.IsEmpty : MatchProgressWitness.AwaitsHuman(state));
            bool paused = missing == null && (group.Any(item => item.State.Viewer?.Eliminated == false && pacing?.WaitingForHuman(group.Key, item.SeatId) == true)
                || state.MatchReadingWaits is { IsEmpty: false });
            bool automatic = missing != null || ownWait != null || state.DecisionOwnerId is { } owner && group.Any(item => item.SeatId == owner)
                || state.DecisionOwnerId == null && (state.Status == "resolving" || state.ActionClass == "resolving");
            var watch = progressWatches.GetValueOrDefault(group.Key, MatchProgressWatch.Empty)
                .Observe(!human && !paused && automatic && state.Status is "playing" or "resolving"
                    ? MatchProgressWitness.From(state, missing?.PlayerId ?? row.SeatId) : null, ProgressTime);
            progressWatches[group.Key] = watch;
            if (!watch.Due(ProgressTime)) continue;
            // Preserve the more specific accepted-command failure when that
            // same command has exhausted its budget. Fresh metadata alone may
            // not repeatedly replace that budget or bypass the table watch.
            foreach (var pending in awaitingObservation.Keys.Where(key => key.Game == group.Key).ToArray())
                await CheckObservationProgress(pending, token).ConfigureAwait(false);
            if (faultedGames.Contains(group.Key)) continue;
            if (!watch.RepairAttempted && provider is IAutomatedMatchProgressRecovery { SupportsMatchProgressRecovery: true } repair) {
                progressWatches[group.Key] = watch.Repair();
                try {
                    await AwaitProvider("progress-reconciliation", stop => repair.ReconcileAutomatedMatchProgressAsync(group.Key, row.SeatId, stop), token).ConfigureAwait(false);
                    repaired = true;
                    continue;
                } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) {
                    await FaultAsync(group.Key, row.SeatId, "progress-recovery-failed", token, error).ConfigureAwait(false);
                    continue;
                }
            }
            await FaultAsync(group.Key, row.SeatId, "match-no-progress", token,
                new EngineMatchProgressException("The automated game stopped making progress and could not restore its current decision.")).ConfigureAwait(false);
        }
        return repaired;
    }

    private async Task CheckSupersededProgressAsync(CancellationToken token)
    {
        supersededSince ??= operationTime.GetTimestamp();
        if (operationTime.GetElapsedTime(supersededSince.Value) < MatchProgressWatch.Limit) return;
        (string Game, int Seat)[] owned;
        lock (seats) owned = seats.Keys.ToArray();
        if (owned.Length == 0) throw new EngineMatchProgressException("The engine could not publish a current automated observation in time.");
        foreach (var game in owned.GroupBy(key => key.Game)) if (!faultedGames.Contains(game.Key)) {
            var seat = game.First();
            await FaultAsync(seat.Game, seat.Seat, "observation-no-progress", token,
                new EngineMatchProgressException("Repeated superseded observations prevented the game from progressing.")).ConfigureAwait(false);
        }
    }
}
