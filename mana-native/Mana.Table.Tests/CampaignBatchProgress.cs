using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

// Private scalar evidence, never an observation, action or ownership authority.
internal sealed record CampaignBatchProgress(ImmutableArray<CampaignGameProgress> Games, ImmutableArray<CampaignOwnedProgress> Rows)
{
    // The existing qualification diagnostics support at most eight audiences.
    internal const int MaximumAudiences = 8;
    internal static CampaignBatchProgress Capture(ImmutableArray<AutomatedSeatObservation> observations)
    {
        if (observations.IsDefault || observations.Length > MaximumAudiences
            || observations.Any(row => row.State.Players.IsDefault || row.State.Players.Length > MaximumAudiences
                || row.State.Id.Length > 256 || row.State.Status.Length > 32))
            throw new InvalidDataException("Private batch progress exceeded its bounded audience/scalar shape.");
        var games = ImmutableArray.CreateBuilder<CampaignGameProgress>();
        var rows = ImmutableArray.CreateBuilder<CampaignOwnedProgress>();
        foreach (var group in observations.GroupBy(row => row.State.Id)) {
            var players = group.First().State.Players.OrderBy(player => player.Id)
                .Select(player => new CampaignPublicPlayer(player.Id, player.Eliminated)).ToImmutableArray();
            bool complete = !players.IsEmpty && players.All(player => player.Id >= 0)
                && players.Select(player => player.Id).Distinct().Count() == players.Length;
            games.Add(new(group.Key, players, complete));
            foreach (var row in group) {
                bool agrees = complete && row.State.Players.OrderBy(player => player.Id)
                    .Select(player => new CampaignPublicPlayer(player.Id, player.Eliminated)).SequenceEqual(players);
                rows.Add(new(group.Key, row.SeatId, row.State.ViewerId, row.State.Revision, row.State.BoardRevision,
                    row.State.Status, row.State.Viewer?.Eliminated, row.State.Decision != null,
                    row.State.DecisionOwnerId, row.State.PriorityPlayerId,
                    row.State.PriorityObservation?.PublicActivitySequence, row.State.StackInterruptionVersion, agrees));
            }
        }
        return new(games.ToImmutable(), rows.ToImmutable());
    }
}
internal sealed record CampaignPublicPlayer(int Id, bool Eliminated);
internal sealed record CampaignGameProgress(string GameId, ImmutableArray<CampaignPublicPlayer> Players, bool NonemptyDistinctRoster);
internal sealed record CampaignOwnedProgress(string GameId, int SeatId, int ViewerId, long Revision, long BoardRevision,
    string Status, bool? ViewerEliminated, bool HasDecision, int? DecisionOwnerId, int? PriorityPlayerId,
    long? PublicActivitySequence, long? StackInterruptionVersion, bool PublicRosterAgrees);
