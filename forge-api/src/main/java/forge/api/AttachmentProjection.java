package forge.api;

import forge.game.card.CardView;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;

/** Public attachment relationships, using the same opaque identities as battlefield cards. */
final class AttachmentProjection {
    private AttachmentProjection() { }

    static void add(Map<String, Object> state, CardView card, PlayerView viewer, CombatCardIds ids) {
        var attachments = new ArrayList<Map<String, Object>>();
        Map<String, Object> attachedTo = null;
        if (MatchSession.isVisibleInZone(card, ZoneType.Battlefield, viewer)) {
            attachedTo = reference(card.getAttachedTo(), viewer, ids);
            for (var attachment : card.getAllAttachedCards()) {
                var projected = reference(attachment, viewer, ids);
                if (projected != null) attachments.add(projected);
            }
        }
        state.put("attachedTo", attachedTo);
        state.put("attachments", attachments);
    }

    private static Map<String, Object> reference(CardView card, PlayerView viewer, CombatCardIds ids) {
        if (card == null || !MatchSession.isVisibleInZone(card, ZoneType.Battlefield, viewer)) return null;
        var result = new LinkedHashMap<String, Object>();
        result.put("id", ids.id(card));
        result.put("name", card.isFaceDown() ? "Face-down permanent" : card.getCurrentState().getName());
        result.put("type", card.isFaceDown() ? "" : card.getCurrentState().getType().toString());
        return result;
    }
}
