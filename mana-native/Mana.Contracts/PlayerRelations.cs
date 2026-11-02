namespace Mana.Contracts;

public enum PlayerRelation { Unknown, Self, Ally, Opponent }

public static class PlayerRelations
{
    public static PlayerRelation ForViewer(GameSnapshot state, int otherId) => Between(state, state.ViewerId, otherId);

    public static PlayerRelation Between(GameSnapshot state, int observerId, int otherId)
    {
        if (state.Players.IsDefault) return PlayerRelation.Unknown;
        Player? observer = null, other = null;
        // This is also used in policy scoring; small table scans avoid per-query
        // dictionaries and do not retain a relationship cache across revisions.
        for (int i = 0; i < state.Players.Length; i++) {
            var player = state.Players[i];
            if (player == null || player.Id < 0) return PlayerRelation.Unknown;
            for (int previous = 0; previous < i; previous++)
                if (state.Players[previous].Id == player.Id) return PlayerRelation.Unknown;
            if (player.Id == observerId) observer = player;
            if (player.Id == otherId) other = player;
        }
        if (observer == null || other == null) return PlayerRelation.Unknown;
        if (observerId == otherId) return PlayerRelation.Self;
        if (observer.TeamId is not >= 0 || other.TeamId is not >= 0) return PlayerRelation.Unknown;
        return observer.TeamId == other.TeamId ? PlayerRelation.Ally : PlayerRelation.Opponent;
    }
}
