package forge.api;

import forge.game.card.CardView;

import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Copied structured facts for an audience's retained decision publication. */
final class PublishedDecisionFacts {
    static Map<String, Object> freeze(Map<String, Object> facts) {
        var copy = new LinkedHashMap<String, Object>();
        facts.forEach((key, value) -> copy.put(key, freezeValue(value, 0)));
        return Collections.unmodifiableMap(copy);
    }

    /** Only known wire values belong in a retained tree. An unrecognized rules
     * object cannot become a hidden live owner through a projection fallback. */
    private static Object freezeValue(Object value, int depth) {
        if (depth > 32) throw new IllegalArgumentException("Current decision facts exceed their value-tree bound");
        if (value == null || value instanceof String || value instanceof Boolean || value instanceof Integer
                || value instanceof Long || value instanceof Float || value instanceof Double || value instanceof PublicStates.State)
            return value;
        if (value instanceof CardView.PublicBlockingCapacity capacity) {
            // Full public card portraits include this scalar-only view value.
            // Copy its wire shape explicitly; null means no finite maximum.
            var copy = new LinkedHashMap<String, Object>();
            copy.put("maximum", capacity.maximum());
            copy.put("unlimited", capacity.unlimited());
            return Collections.unmodifiableMap(copy);
        }
        if (value instanceof Map<?, ?> values) {
            var copy = new LinkedHashMap<String, Object>();
            for (var entry : values.entrySet()) {
                if (!(entry.getKey() instanceof String key)) throw new IllegalArgumentException("Current decision facts require string keys");
                copy.put(key, freezeValue(entry.getValue(), depth + 1));
            }
            return Collections.unmodifiableMap(copy);
        }
        if (value instanceof List<?> values) {
            var copy = new ArrayList<Object>(values.size());
            for (var item : values) copy.add(freezeValue(item, depth + 1));
            return Collections.unmodifiableList(copy);
        }
        throw new IllegalArgumentException("Unsupported current decision fact value");
    }

    private PublishedDecisionFacts() { }
}
