package forge.api;

import forge.game.GameEntity;
import forge.game.GameEntityView;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.combat.CombatView;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;

/** Only actual current defenders have public target identities. */
final class CombatDefenders {
    record CardDefender(Integer playerId, String label) { }

    static CardDefender card(CombatView combat, CardView attacker, PlayerView viewer) {
        var defender = combat == null ? null : combat.getDefender(attacker);
        if (!isCurrent(defender)) return new CardDefender(null, null);
        String label = defender instanceof CardView card && (!card.canBeShownTo(viewer) || card.isFaceDown())
                ? "Face-down permanent" : defender.getName();
        return new CardDefender(defender instanceof PlayerView ? defender.getId() : null, label);
    }

    static boolean isCurrent(GameEntity entity) {
        if (entity instanceof Player player) return !player.hasLost();
        if (entity instanceof Card card) return card.isInPlay() && (card.isPlaneswalker() || card.isBattle());
        return false;
    }

    static boolean isCurrent(GameEntityView entity) {
        if (entity instanceof PlayerView player) return !player.getHasLost();
        if (entity instanceof CardView card) return card.getZone() == ZoneType.Battlefield
                && (card.getCurrentState().getType().isPlaneswalker() || card.getCurrentState().getType().isBattle());
        return false;
    }

    private CombatDefenders() { }
}
