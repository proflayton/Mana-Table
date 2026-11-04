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
            if (provider is not IAutomatedMatchHistory { SupportsAutomatedHistory: true } history) {
                evidence.HistoryUnavailable(state.Id, observation.SeatId);
                continue;
            }
            await DrainOneAsync(state, evidence, cursor => history.AutomatedHistoryAsync(state.Id, observation.SeatId, cursor), force);
        }
    }

    // An ordinary player's history is read through its bound connection. No
    // automated-seat selector is exposed or substituted for this audience.
    internal async Task DrainOwnedAsync(IMatchHistory provider, OwnedDecisionObservation observation,
        AiCausalMechanicEvidence? evidence, bool force = false)
    {
        if (evidence == null) return;
        var state = observation.State;
        if (state.Status == "starting") return;
        if (!provider.SupportsHistory) { evidence.HistoryUnavailable(state.Id, state.ViewerId); return; }
        await DrainOneAsync(state, evidence, cursor => provider.HistoryAsync(state.Id, cursor), force);
    }

    private async Task DrainOneAsync(GameSnapshot state, AiCausalMechanicEvidence evidence,
        Func<long, Task<HistoryBatch>> read, bool force)
    {
        if (state.Status == "starting") return;
        var key = (state.Id, state.ViewerId);
        if (!evidence.NeedsHistory(state.Id, state.ViewerId)) return;
        var stamp = (state.Revision, state.BoardRevision, state.Executions?.LastSequence ?? 0);
        if (!force && observed.TryGetValue(key, out var prior) && prior == stamp) return;
        observed[key] = stamp;
        bool backlog = false;
        for (int chunk = 0; chunk < 2; chunk++) {
            long cursor = evidence.HistoryCursor(state.Id, state.ViewerId);
            var batch = await read(cursor);
            if (batch.GameId != state.Id || batch.ViewerId != state.ViewerId || batch.NextSequence < cursor)
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
