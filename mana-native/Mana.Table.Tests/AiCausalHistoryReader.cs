using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

// Test-host evidence reading, never part of a policy's knowledge or commands.
// Each observation contributes at most two ordinary 128-record ledger chunks;
// unchanged publications do no work, and backlog continues on a later poll.
internal sealed class AiCausalHistoryReader
{
    private readonly Dictionary<(string Game, int Player), (long Revision, long Board, long Execution)> observed = [];
    internal async Task DrainAsync(IAutomatedSeatProvider provider, ImmutableArray<AutomatedSeatObservation> observations,
        AiCausalMechanicEvidence? evidence, bool force = false)
    {
        if (evidence == null) return;
        foreach (var observation in observations) {
            var state = observation.State;
            if (state.ViewerId != observation.SeatId)
                throw new InvalidDataException("Owned history cannot change its admitted observation audience.");
            if (state.Status == "starting") continue;
            var key = (state.Id, observation.SeatId);
            if (provider is not IAutomatedMatchHistory { SupportsAutomatedHistory: true } history) {
                evidence.HistoryUnavailable(state.Id, observation.SeatId);
                continue;
            }
            if (!evidence.NeedsHistory(state.Id, observation.SeatId)) continue;
            var stamp = (state.Revision, state.BoardRevision, state.Executions?.LastSequence ?? 0);
            if (!force && observed.TryGetValue(key, out var prior) && prior == stamp) continue;
            observed[key] = stamp;
            bool backlog = false;
            for (int chunk = 0; chunk < 2; chunk++) {
                long cursor = evidence.HistoryCursor(state.Id, observation.SeatId);
                var batch = await history.AutomatedHistoryAsync(state.Id, observation.SeatId, cursor);
                if (batch.GameId != state.Id || batch.ViewerId != observation.SeatId || batch.NextSequence < cursor)
                    throw new InvalidDataException("Owned history returned a different game, audience or older cursor.");
                evidence.ObserveHistory(batch);
                backlog = batch.HasMore;
                if (!batch.HasMore) break;
                if (batch.NextSequence <= cursor)
                    throw new InvalidDataException("Owned history backlog made no cursor progress.");
            }
            if (backlog) observed.Remove(key);
        }
    }
}
