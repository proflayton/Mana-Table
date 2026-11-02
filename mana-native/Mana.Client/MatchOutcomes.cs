using Mana.Contracts;

namespace Mana.Client;

// Audience perspective for presentation, not an individual score or progress
// award. Draw describes the global game; a participant may already have lost.
public enum PlayerMatchOutcome { Unknown, Win, TeamWin, Loss, Draw }

// Reads certified rules facts only. This does not complete a local leave flow
// and does not interpret viewer-local Result labels from older providers.
// Global draws preserve the participant's separate Eliminated flag; circuit
// scoring will need explicit individual outcome facts and a scoring policy.
public static class MatchOutcomes
{
    public static PlayerMatchOutcome ForViewer(GameSnapshot state) => ForPlayer(state, state.ViewerId);

    public static PlayerMatchOutcome ForPlayer(GameSnapshot state, int playerId)
    {
        if (state.Outcome is not { } outcome) return PlayerMatchOutcome.Unknown;
        try { MatchOutcomeFacts.Validate(state); }
        catch (InvalidOperationException) { return PlayerMatchOutcome.Unknown; }
        var player = state.Players.FirstOrDefault(player => player.Id == playerId);
        if (player == null) return PlayerMatchOutcome.Unknown;
        if (outcome.Kind == MatchOutcomeKind.Draw) return PlayerMatchOutcome.Draw;
        if (outcome.WinningPlayerIds.Contains(playerId)) return PlayerMatchOutcome.Win;
        if (player.TeamId is { } team && outcome.WinningTeamIds.Contains(team)) return PlayerMatchOutcome.TeamWin;
        return PlayerMatchOutcome.Loss;
    }
}
