using Mana.Contracts;

namespace Mana.AI;

// Strategic preferences use public rules-owned teams. This compatibility
// fallback belongs only to the old free-for-all policy: the neutral contract
// never turns missing teams into an authoritative opponent relationship.
internal static class AiPlayerRelations
{
    internal static PlayerRelation ForViewer(GameSnapshot state, int playerId)
    {
        var relation = PlayerRelations.ForViewer(state, playerId);
        if (relation != PlayerRelation.Unknown) return relation;
        if (state.Players.IsDefaultOrEmpty) return PlayerRelation.Unknown;
        bool legacy = state.Players.All(player => player is { TeamId: null, Id: >= 0 })
            && state.Players.Select(player => player.Id).Distinct().Count() == state.Players.Length;
        return legacy && state.Players.Any(player => player.Id == state.ViewerId)
            && state.Players.Any(player => player.Id == playerId) && playerId != state.ViewerId
                ? PlayerRelation.Opponent : PlayerRelation.Unknown;
    }

    internal static bool Friendly(GameSnapshot state, int playerId)
        => ForViewer(state, playerId) is PlayerRelation.Self or PlayerRelation.Ally;

    internal static bool Opponent(GameSnapshot state, int playerId)
        => ForViewer(state, playerId) == PlayerRelation.Opponent;

    // Supporting an ally is useful without treating their resources as ours.
    internal static double Benefit(GameSnapshot state, int playerId) => ForViewer(state, playerId) switch {
        PlayerRelation.Self => 1,
        PlayerRelation.Ally => .65,
        PlayerRelation.Opponent => -.65,
        _ => 0
    };

    internal static bool FinalOpponent(GameSnapshot state, int playerId)
        => Opponent(state, playerId)
            && state.Players.Any(player => player.Id == playerId && !player.Eliminated)
            && state.Players.Where(player => !player.Eliminated).All(player =>
                ForViewer(state, player.Id) is PlayerRelation.Self or PlayerRelation.Ally || player.Id == playerId);
}
