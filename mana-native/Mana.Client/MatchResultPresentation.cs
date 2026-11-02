using Mana.Contracts;

namespace Mana.Client;

/// <summary>Labels for the current audience. These never certify completion,
/// grant progress, or convert a seat's concession into a whole-match result.</summary>
public sealed record MatchResultPresentation(string Title, string Banner, bool Victory, bool Interrupted)
{
    public static MatchResultPresentation For(GameSnapshot state)
    {
        if (state.Status == "error" || !string.IsNullOrEmpty(state.Error)) return new("Game interrupted", "GAME INTERRUPTED", false, true);
        if (state.Status != "finished") return new("Game in progress", "GAME IN PROGRESS", false, false);
        return MatchOutcomes.ForViewer(state) switch {
            PlayerMatchOutcome.Win => new("Victory", "VICTORY", true, false),
            PlayerMatchOutcome.TeamWin => new("Team victory", "TEAM VICTORY", true, false),
            PlayerMatchOutcome.Loss => new("Defeat", "DEFEAT", false, false),
            PlayerMatchOutcome.Draw => new("Draw", "DRAW", false, false),
            // Older providers retain their display text. It supplies no typed
            // completion authority, and cannot override a present outcome.
            _ when state.Outcome == null => new(state.Result ?? "Game finished",
                state.Result == "Victory" ? "VICTORY" : state.Result == "Defeat" ? "DEFEAT"
                    : state.Result == "Draw" ? "DRAW" : "GAME COMPLETE", state.Result == "Victory", false),
            _ => new("Game finished", "GAME COMPLETE", false, false)
        };
    }

    public static string PlayerLabel(GameSnapshot state, Player player, bool timedOut = false)
    {
        if (state.Status == "error" || !string.IsNullOrEmpty(state.Error)) return "Not completed";
        if (state.Status != "finished") return player.Eliminated ? "Eliminated" : "Playing";
        return MatchOutcomes.ForPlayer(state, player.Id) switch {
            PlayerMatchOutcome.Win => "Winner",
            PlayerMatchOutcome.TeamWin => player.Eliminated ? "Team won (eliminated)" : "Team won",
            PlayerMatchOutcome.Draw when !player.Eliminated => "Draw",
            PlayerMatchOutcome.Loss when !player.Eliminated => "Defeated",
            _ => player.Eliminated ? timedOut ? "Timed out" : "Eliminated" : "Remaining"
        };
    }
}
