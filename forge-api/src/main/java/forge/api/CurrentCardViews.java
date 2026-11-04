package forge.api;

import forge.game.Game;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.zone.ZoneType;

/** Exact rules-occurrence membership, independent of audience disclosure.
 * A historical view sharing a physical numeric ID is not a current occurrence. */
final class CurrentCardViews {
    static boolean isCurrent(Game game, CardView card) {
        if (card == null || card.getZone() == null || card.getZone() == ZoneType.None) return false;
        // Absence is normal for historical portraits: do not use a diagnostic
        // failed lookup or substitute the new card with the same physical ID.
        return game.getCardsIncludePhasingIn(card.getZone()).stream().anyMatch(current -> current.getView() == card)
                || game.isGameOver() && registeredFinal(game, card.getId()) == card;
    }

    /** Visibility-refresh bookkeeping only; never rebind a committed target. */
    static CardView currentOccurrence(Game game, CardView prior) {
        if (prior == null) return null;
        Card current = game.findById(prior.getId());
        if (current == null) current = game.getCardsIncludePhasingIn(ZoneType.Sideboard).stream()
                .filter(card -> card.getId() == prior.getId()).findFirst().orElse(null);
        return current != null ? current.getView() : game.isGameOver() ? registeredFinal(game, prior.getId()) : null;
    }

    private static CardView registeredFinal(Game game, int physicalId) {
        // The last loss retains actual registered-player zones for final-board
        // display only. Never recover a current face from cached/LKI views.
        for (Player player : game.getRegisteredPlayers()) for (ZoneType kind : ZoneType.values()) {
            var zone = player.getZone(kind);
            if (zone == null) continue;
            for (Card current : zone.getCards(false))
                if (current.getId() == physicalId && current.getZone() == zone) return current.getView();
        }
        return null;
    }

    private CurrentCardViews() { }
}
