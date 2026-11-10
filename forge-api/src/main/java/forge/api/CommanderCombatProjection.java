package forge.api;

import forge.game.Game;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** Public rules lineage, independent of current occurrence and command identities.
 * One table owns these IDs for every audience. The actual registered designation,
 * not a copied name, controller or commander tax, identifies the damage source. */
final class CommanderCombatProjection {
    private static final PlayerView PUBLIC = new PlayerView(-1, null);
    private final Game game;
    private final Map<Integer, String> lineages = new HashMap<>();

    CommanderCombatProjection(Game game) { this.game = game; }

    Map<String, Object> rules() {
        return Collections.singletonMap("damageLimit", game.getRules().getCommanderCombatDamageLimit());
    }

    List<Map<String, Object>> damage(Player recipient) {
        var rows = new ArrayList<Map<String, Object>>();
        for (Player owner : game.getRegisteredPlayers()) for (Card commander : owner.getCommanders()) {
            var view = commander.getView();
            var row = new LinkedHashMap<String, Object>();
            row.put("commanderId", id(commander));
            row.put("name", view.isFaceDown() ? "Face-down commander" : view.getCurrentState().getName());
            row.put("ownerId", owner.getId()); row.put("owner", owner.getName());
            row.put("damage", recipient.getCommanderDamage(commander));
            rows.add(Collections.unmodifiableMap(row));
        }
        return List.copyOf(rows);
    }

    void bindPublicCard(Map<String, Object> face, CardView view) {
        String binding = publicId(view);
        if (binding != null) face.put("commanderId", binding);
    }

    String publicId(CardView view) {
        // The first consumer is current battlefield combat. Command-zone cards
        // also expose their public designation; private/hidden/history readers
        // cannot use a lineage to follow a concealed current occurrence.
        if (view == null || !view.isCommander() || view.isFaceDown()
                || view.getZone() != ZoneType.Battlefield && view.getZone() != ZoneType.Command
                || !view.canBeShownTo(PUBLIC) || !CurrentCardViews.isCurrent(game, view)) return null;
        Card physical = null;
        for (Player player : game.getRegisteredPlayers()) for (Card candidate : player.getCardsIn(view.getZone()))
            if (candidate.getView() == view && candidate.getZone() != null && candidate.getZone().getZoneType() == view.getZone()) {
                physical = candidate; break;
            }
        Card actual = physical == null ? null : physical.getRealCommander();
        if (actual == null) return null;
        for (Player owner : game.getRegisteredPlayers()) for (Card designated : owner.getCommanders())
            if (designated.getId() == actual.getId()) return id(designated);
        return null;
    }

    private synchronized String id(Card designated) {
        // Rules physical IDs survive ordinary zone copies; occurrence IDs are
        // deliberately revoked on concealment and therefore cannot serve here.
        return lineages.computeIfAbsent(designated.getId(), ignored -> UUID.randomUUID().toString());
    }
}
