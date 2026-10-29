package forge.api;

import forge.game.Game;
import forge.game.ReversibleManaAction;
import forge.game.card.Card;
import forge.game.zone.ZoneType;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.function.Function;

/** Immutable audience projection only. The rules owner retains all restoration
 * data and validates the action; neither a snapshot nor an event tail is a save. */
final class MatchUndo {
    static Map<String, Object> project(Game game, Integer viewerId, String decisionId,
                                       Function<Card, String> disclosedId) {
        var reversal = game.getReversibleManaAction();
        var current = reversal.current();
        Map<String, Object> offer = null;
        if (current != null && viewerId != null && current.actorId() == viewerId && decisionId != null) {
            Card source = game.findById(current.sourceId());
            if (source != null && source.isInZone(ZoneType.Battlefield) && !source.isFaceDown()
                    && source.getController().getId() == viewerId) {
                String sourceId = disclosedId.apply(source);
                if (sourceId != null && !sourceId.isEmpty()) offer = fields("id", current.id(), "actionId", current.actionId(),
                        "executionId", current.executionId(), "decisionId", decisionId, "playerId", viewerId,
                        "sourceCardId", sourceId, "sourceName", source.getName(), "barrierVersion", current.barrierVersion());
            }
        }
        ReversibleManaAction.Restoration restored = reversal.lastRestoration();
        Map<String, Object> marker = restored == null ? null : fields("version", restored.version(),
                "actionId", restored.actionId(), "executionId", restored.executionId(), "playerId", restored.actorId());
        return fields("barrierVersion", reversal.barrierVersion(), "restorationVersion", reversal.restorationVersion(),
                "offer", offer, "lastRestoration", marker);
    }

    private static Map<String, Object> fields(Object... entries) {
        var result = new LinkedHashMap<String, Object>();
        for (int i = 0; i < entries.length; i += 2) result.put((String) entries[i], entries[i + 1]);
        return Collections.unmodifiableMap(result);
    }

    /** A terminal fault retains the last committed board and its restoration
     * marker, even if an inverse completed before its publication failed. */
    static Map<String, Object> faultFrame(Object previous, long barrierVersion) {
        if (!(previous instanceof Map<?, ?> frame))
            return fields("barrierVersion", barrierVersion, "restorationVersion", 0L, "offer", null, "lastRestoration", null);
        return fields("barrierVersion", barrierVersion, "restorationVersion", frame.get("restorationVersion"),
                "offer", null, "lastRestoration", frame.get("lastRestoration"));
    }

    private MatchUndo() { }
}
