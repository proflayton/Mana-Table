package forge.api;

import forge.game.GameEndReason;
import forge.game.player.Player;

import java.util.List;

/** Public rules facts, shared by every seat and the independent spectator. */
final class MatchOutcomeProjection {
    record Outcome(String gameId, String kind, List<Integer> winningPlayerIds,
                   List<Integer> winningTeamIds, String ruleReason) { }

    static Outcome of(MatchTable table, String error) {
        var game = table.game;
        var outcome = game.getOutcome();
        // AllHumansLost also terminates host shutdown/failure. It is not a
        // completed rules contest and must never award encounter progress.
        if (error != null || !game.isGameOver() || outcome == null
                || outcome.getWinCondition() == GameEndReason.AllHumansLost) return null;
        var winners = game.getRegisteredPlayers().stream()
                .filter(player -> outcome.isWinner(player.getRegisteredPlayer()))
                .sorted(java.util.Comparator.comparingInt(Player::getId)).toList();
        return new Outcome(table.id, outcome.isDraw() ? "draw" : "win",
                winners.stream().map(Player::getId).toList(),
                winners.stream().map(Player::getTeam).distinct().sorted().toList(),
                outcome.getWinCondition().name());
    }

    static String forViewer(Outcome outcome, int playerId) {
        if (outcome == null) return null;
        return outcome.kind().equals("draw") ? "Draw"
                : outcome.winningPlayerIds().contains(playerId) ? "Victory" : "Defeat";
    }

    static String forSpectator(Outcome outcome, MatchTable table) {
        if (outcome == null) return null;
        if (outcome.kind().equals("draw")) return "Draw";
        var names = table.game.getRegisteredPlayers().stream()
                .filter(player -> outcome.winningPlayerIds().contains(player.getId()))
                .sorted(java.util.Comparator.comparingInt(Player::getId)).map(Player::getName).toList();
        return String.join(", ", names) + (names.size() == 1 ? " wins" : " win");
    }

    private MatchOutcomeProjection() { }
}
