package forge.api;

import forge.game.card.CardView;
import forge.game.Game;
import forge.game.player.PlayerView;

import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.function.Function;

/** Committed display references, separate from target offers and per-effect
 * allocation. Each audience admits the source and supplies its own authorized
 * opaque identity before this owner copies scalar target facts. */
final class StackTargetProjection {
    record Reference(String kind, String id, String name, Integer playerId) { }

    static Reference card(Game game, CardView card, Function<CardView, String> audienceIdentity) {
        // Check the exact original view before the audience can allocate an ID.
        // A public zone move creates a new rules object, not a retargeting.
        if (!CurrentCardViews.isCurrent(game, card)) return null;
        String admittedId = audienceIdentity.apply(card);
        if (admittedId == null || admittedId.isBlank()) return null;
        return new Reference("card", admittedId, card.isFaceDown() ? "Face-down card" : card.getCurrentState().getName(),
                card.getController() == null ? null : card.getController().getId());
    }

    static Reference player(PlayerView player, String label) {
        return new Reference("player", String.valueOf(player.getId()), label, player.getId());
    }

    static List<Reference> distinct(Collection<Reference> references) {
        var targets = new LinkedHashMap<String, Reference>();
        for (var reference : references) if (reference != null)
            targets.put(reference.kind() + ":" + reference.id(), reference);
        return List.copyOf(targets.values());
    }

    private StackTargetProjection() { }
}
