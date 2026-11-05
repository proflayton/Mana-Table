package forge.api;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import java.util.*;
import java.util.function.LongSupplier;

/** Latest, expiring public inspection only. Called under the match boundary;
 * never advances an input, emits an event or looks up a rules/action object. */
final class MatchPresence {
    static final String SUPERSEDED = "table-presence-superseded";
    static final int LEASE_MILLIS = 1500;
    private static final int UPDATE_MILLIS = 250;
    enum Kind { CARD, STACK }
    record Subject(Kind kind, String id) {
        Map<String, Object> wire() { return Map.of("kind", kind == Kind.CARD ? "card" : "stack", "id", id); }
    }
    record Context(String id, long boardRevision, int playerId) { }
    private record Cue(String contextId, Subject subject, long expires) { }
    private final String gameId;
    private final LongSupplier clock;
    private final Map<Integer, Cue> cues = new HashMap<>();
    private final Map<Integer, Long> sequences = new HashMap<>(), lastUpdates = new HashMap<>();
    private long revision;

    MatchPresence(String gameId, LongSupplier clock) { this.gameId = gameId; this.clock = clock; }

    /** A global acting context is not automatically a capability of every
     * audience's intermediate frame. Inspect only detached publication facts. */
    static Context projectContext(Map<String, Object> frame, Context context) {
        if (context == null || context.id() == null || !validId(context.id()) || context.playerId() < 0 || context.boardRevision() < 0
                || !"playing".equals(frame.get("status")) || !"priority".equals(frame.get("actionClass"))
                || !sameNumber(frame.get("boardRevision"), context.boardRevision())
                || !sameNumber(frame.get("priorityPlayerId"), context.playerId())
                || !sameNumber(frame.get("decisionOwnerId"), context.playerId())
                || !(frame.get("viewerId") instanceof Number)
                || !(frame.get("players") instanceof List<?> players)
                || players.stream().noneMatch(value -> value instanceof Map<?, ?> player && sameNumber(player.get("id"), context.playerId()))) return null;
        if (sameNumber(frame.get("viewerId"), context.playerId()) && (!(frame.get("prompt") instanceof Map<?, ?> prompt)
                || !"priority".equals(prompt.get("task")))) return null;
        return context;
    }

    private static boolean sameNumber(Object value, long expected) {
        return value instanceof Number number && number.longValue() == expected && number.doubleValue() == expected;
    }

    void disconnected(int player) {
        if (cues.remove(player) != null) revision++;
        sequences.remove(player); lastUpdates.remove(player);
    }

    Map<String, Object> exchange(int player, JsonObject request, long boardRevision, Context context,
                                 Set<Subject> senderVisible, Set<Subject> recipientVisible) {
        if (!gameId.equals(text(request.get("gameId")))) throw superseded();
        long sequence = number(request.get("sequence")), requestedBoard = number(request.get("boardRevision"));
        if (sequence <= 0 || requestedBoard < 0) throw new IllegalArgumentException("Invalid table inspection scope");
        String contextId = nullableText(request.get("contextId"));
        Subject subject = subject(request.get("subject"));
        long now = clock.getAsLong();
        if (cues.entrySet().removeIf(entry -> entry.getValue().expires() <= now || context == null
                || context.boardRevision() != boardRevision
                || entry.getKey() != context.playerId() || !entry.getValue().contextId().equals(context.id()))) revision++;
        long previous = sequences.getOrDefault(player, 0L);
        if (sequence > previous) {
            sequences.put(player, sequence);
            boolean current = context != null && context.playerId() == player && requestedBoard == boardRevision
                    && context.boardRevision() == boardRevision && context.id().equals(contextId);
            if (subject == null || !current || !senderVisible.contains(subject)) {
                if (cues.remove(player) != null) revision++;
            } else if (!lastUpdates.containsKey(player) || now - lastUpdates.get(player) >= UPDATE_MILLIS) {
                cues.put(player, new Cue(context.id(), subject, now + LEASE_MILLIS));
                lastUpdates.put(player, now); revision++;
            }
        }
        var visible = new ArrayList<Map<String, Object>>();
        for (var entry : cues.entrySet()) if (recipientVisible.contains(entry.getValue().subject())) {
            var cue = entry.getValue();
            visible.add(Map.of("playerId", entry.getKey(), "contextId", cue.contextId(), "subject", cue.subject().wire(),
                    "remainingMilliseconds", (int)Math.min(LEASE_MILLIS, cue.expires() - now)));
        }
        visible.sort(Comparator.comparingInt(entry -> (int)entry.get("playerId")));
        return Map.of("gameId", gameId, "boardRevision", boardRevision, "revision", revision, "cues", List.copyOf(visible));
    }

    static forge.api.sync.SyncTransport.CodedFailure superseded() {
        return new forge.api.sync.SyncTransport.CodedFailure(SUPERSEDED, "This table inspection has changed.");
    }

    /** Detached projected values only, captured once with the board. */
    static Set<Subject> subjects(List<?> players, List<?> stack) {
        var result = new HashSet<Subject>();
        for (Object value : players) if (value instanceof Map<?, ?> player && player.get("zones") instanceof List<?> zones)
            for (Object zoneValue : zones) if (zoneValue instanceof Map<?, ?> zone && "Battlefield".equals(zone.get("name"))
                    && zone.get("cards") instanceof List<?> cards)
                for (Object cardValue : cards) if (cardValue instanceof Map<?, ?> card && Boolean.FALSE.equals(card.get("faceDown"))
                        && !Boolean.TRUE.equals(card.get("phasedOut"))
                        && card.get("visualId") instanceof String id && validId(id)) result.add(new Subject(Kind.CARD, id));
        for (Object value : stack) if (value instanceof Map<?, ?> item && item.get("card") instanceof Map<?, ?> card
                && Boolean.FALSE.equals(card.get("faceDown")) && item.get("name") instanceof String name && !name.isBlank()
                && item.get("id") != null) {
            String id = String.valueOf(item.get("id"));
            if (validId(id)) result.add(new Subject(Kind.STACK, id));
        }
        return Set.copyOf(result);
    }

    private static Subject subject(JsonElement value) {
        if (value == null || value.isJsonNull()) return null;
        if (!value.isJsonObject()) throw new IllegalArgumentException("Invalid public inspection subject");
        var object = value.getAsJsonObject();
        String kind = text(object.get("kind")), id = text(object.get("id"));
        if (!validId(id)) throw new IllegalArgumentException("Invalid public inspection subject");
        return new Subject(switch (kind) {
            case "card" -> Kind.CARD; case "stack" -> Kind.STACK;
            default -> throw new IllegalArgumentException("Unknown public inspection kind");
        }, id);
    }
    private static boolean validId(String value) {
        return !value.isBlank() && value.length() <= 128 && value.chars().noneMatch(Character::isISOControl);
    }
    private static String text(JsonElement value) {
        if (value == null || !value.isJsonPrimitive() || !value.getAsJsonPrimitive().isString()
                || value.getAsString().length() > 128) throw new IllegalArgumentException("Invalid table inspection value");
        return value.getAsString();
    }
    private static String nullableText(JsonElement value) { return value == null || value.isJsonNull() ? null : text(value); }
    private static long number(JsonElement value) {
        try {
            if (value == null || !value.isJsonPrimitive() || !value.getAsJsonPrimitive().isNumber()) throw new ArithmeticException();
            return value.getAsBigDecimal().longValueExact();
        } catch (ArithmeticException | NumberFormatException invalid) { throw new IllegalArgumentException("Invalid table inspection sequence"); }
    }
}
