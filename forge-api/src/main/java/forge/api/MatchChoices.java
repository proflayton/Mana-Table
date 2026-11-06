package forge.api;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.annotations.SerializedName;
import java.util.*;

/** Ephemeral consideration of an already public decision. Domains contain only
 * detached publication values; exchanging a preview never queries rules. */
final class MatchChoices {
    enum Kind { @SerializedName("attack") ATTACK, @SerializedName("block") BLOCK, @SerializedName("target") TARGET }
    enum ReferenceKind { @SerializedName("card") CARD, @SerializedName("stack") STACK, @SerializedName("player") PLAYER }
    record Reference(ReferenceKind kind, String id) { }
    record Preview(Reference source, Reference target) { }
    record Request(String contextId, Preview preview) { }
    record Context(String id, long boardRevision, int playerId, Kind kind, Reference source) { }
    record Domain(Kind kind, Reference source, Map<Reference, Set<Reference>> targets) {
        Domain {
            var copied = new HashMap<Reference, Set<Reference>>();
            targets.forEach((key, value) -> copied.put(key, Set.copyOf(value)));
            targets = Map.copyOf(copied);
        }
        boolean accepts(Preview preview) {
            return preview != null && targets.containsKey(preview.source())
                    && (preview.target() == null || targets.get(preview.source()).contains(preview.target()));
        }
    }
    private record Cue(String contextId, Preview preview, long expires) { }
    private final Map<Integer, Cue> cues = new HashMap<>();
    private final Map<Integer, Long> sequences = new HashMap<>(), lastUpdates = new HashMap<>();
    private long revision;

    long revision() { return revision; }
    void disconnected(int player) {
        if (cues.remove(player) != null) revision++;
        sequences.remove(player); lastUpdates.remove(player);
    }

    static Request request(JsonObject request) { return new Request(contextId(request.get("choiceContextId")), preview(request.get("choice"))); }

    List<Map<String, Object>> exchange(int player, Request request, long sequence, long requestedBoard,
            long board, Context context, Domain domain, Set<Reference> recipientVisible, long now) {
        String contextId = request.contextId();
        Preview preview = request.preview();
        if (cues.entrySet().removeIf(entry -> entry.getValue().expires() <= now || context == null
                || context.boardRevision() != board || entry.getKey() != context.playerId()
                || !entry.getValue().contextId().equals(context.id()))) revision++;
        if (sequence > sequences.getOrDefault(player, 0L)) {
            sequences.put(player, sequence);
            boolean current = context != null && domain != null && context.playerId() == player
                    && context.boardRevision() == board && requestedBoard == board && context.id().equals(contextId)
                    && context.kind() == domain.kind() && Objects.equals(context.source(), domain.source());
            if (!current || !domain.accepts(preview)) {
                if (cues.remove(player) != null) revision++;
            } else if (!lastUpdates.containsKey(player) || now - lastUpdates.get(player) >= 250) {
                cues.put(player, new Cue(context.id(), preview, now + MatchPresence.LEASE_MILLIS));
                lastUpdates.put(player, now); revision++;
            }
        }
        var visible = new ArrayList<Map<String, Object>>();
        for (var entry : cues.entrySet()) {
            var cue = entry.getValue();
            if (!recipientVisible.contains(cue.preview().source())
                    || cue.preview().target() != null && !recipientVisible.contains(cue.preview().target())) continue;
            visible.add(Map.of("playerId", entry.getKey(), "contextId", cue.contextId(), "preview", cue.preview(),
                    "remainingMilliseconds", (int)Math.min(MatchPresence.LEASE_MILLIS, cue.expires() - now)));
        }
        visible.sort(Comparator.comparingInt(value -> (int)value.get("playerId")));
        return List.copyOf(visible);
    }

    static Context projectContext(Map<String, Object> frame, Context context, Set<Reference> visible) {
        if (context == null || !validId(context.id()) || context.boardRevision() < 0 || context.playerId() < 0
                || context.kind() == null || !"playing".equals(frame.get("status"))
                || !same(frame.get("boardRevision"), context.boardRevision())
                || !same(frame.get("decisionOwnerId"), context.playerId()) || !(frame.get("viewerId") instanceof Number)
                || !(frame.get("players") instanceof List<?> players)
                || players.stream().noneMatch(value -> value instanceof Map<?, ?> player
                        && same(player.get("id"), context.playerId()) && !Boolean.TRUE.equals(player.get("eliminated")))) return null;
        String action = context.kind() == Kind.ATTACK ? "attack" : context.kind() == Kind.BLOCK ? "block" : "selection";
        if (!action.equals(frame.get("actionClass"))) return null;
        if (context.kind() == Kind.TARGET ? context.source() == null
                || context.source().kind() == ReferenceKind.PLAYER || !visible.contains(context.source())
                : context.source() != null) return null;
        if (same(frame.get("viewerId"), context.playerId())) {
            if (!(frame.get("prompt") instanceof Map<?, ?> prompt) || !action.equals(prompt.get("task"))
                    || context.kind() == Kind.TARGET && (!"targets".equals(prompt.get("purpose")) || prompt.get("librarySearch") != null)) return null;
        }
        return context;
    }

    static Set<Reference> publicReferences(List<?> players, Set<MatchPresence.Subject> subjects) {
        var result = new HashSet<Reference>();
        for (var subject : subjects) result.add(new Reference(subject.kind() == MatchPresence.Kind.CARD
                ? ReferenceKind.CARD : ReferenceKind.STACK, subject.id()));
        for (var value : players) if (value instanceof Map<?, ?> player && player.get("id") instanceof Number id
                && id.intValue() >= 0 && id.doubleValue() == id.intValue() && !Boolean.TRUE.equals(player.get("eliminated")))
            result.add(new Reference(ReferenceKind.PLAYER, Integer.toString(id.intValue())));
        return Set.copyOf(result);
    }

    static Domain combatDomain(Kind kind, Map<?, ?> combat, List<?> players, Set<Reference> publicReferences) {
        if (combat == null || kind == Kind.TARGET) return null;
        var cards = new HashMap<String, Reference>();
        for (var value : players) if (value instanceof Map<?, ?> player && player.get("zones") instanceof List<?> zones)
            for (var zoneValue : zones) if (zoneValue instanceof Map<?, ?> zone && "Battlefield".equals(zone.get("name"))
                    && zone.get("cards") instanceof List<?> faces)
                for (var face : faces) if (face instanceof Map<?, ?> card && card.get("combatId") instanceof String id
                        && card.get("visualId") instanceof String visual) {
                    var reference = new Reference(ReferenceKind.CARD, visual);
                    if (publicReferences.contains(reference)) cards.put(id, reference);
                }
        var targets = new HashMap<Reference, Set<Reference>>();
        if (kind == Kind.ATTACK && combat.get("attackOptions") instanceof List<?> offers)
            for (var value : offers) if (value instanceof Map<?, ?> offer && offer.get("cardId") instanceof String id
                    && offer.get("defenders") instanceof List<?> defenders) {
                var source = cards.get(id);
                if (source == null) continue;
                var destinations = new HashSet<Reference>();
                for (var defender : defenders) if (defender instanceof Map<?, ?> destination) {
                    Reference target = "player".equals(destination.get("kind")) && destination.get("id") instanceof Number player
                            ? new Reference(ReferenceKind.PLAYER, Integer.toString(player.intValue()))
                            : cards.get(destination.get("id"));
                    if (target != null && publicReferences.contains(target)) destinations.add(target);
                }
                if (!destinations.isEmpty()) targets.put(source, destinations);
            }
        if (kind == Kind.BLOCK && combat.get("attackers") instanceof List<?> attacks)
            for (var value : attacks) if (value instanceof Map<?, ?> attack && attack.get("cardId") instanceof String id
                    && attack.get("eligibleBlockerIds") instanceof List<?> blockers) {
                var target = cards.get(id);
                if (target == null) continue;
                for (var blocker : blockers) {
                    var source = cards.get(blocker);
                    if (source != null) targets.computeIfAbsent(source, ignored -> new HashSet<>()).add(target);
                }
            }
        return targets.isEmpty() ? null : new Domain(kind, null, targets);
    }

    private static boolean same(Object value, long expected) {
        return value instanceof Number number && number.longValue() == expected && number.doubleValue() == expected;
    }
    private static Preview preview(JsonElement node) {
        if (node == null || node.isJsonNull()) return null;
        if (!node.isJsonObject()) throw new IllegalArgumentException("Invalid public choice preview");
        var object = node.getAsJsonObject();
        var source = reference(object.get("source"));
        if (source == null || source.kind() == ReferenceKind.PLAYER) throw new IllegalArgumentException("Invalid public choice source");
        return new Preview(source, reference(object.get("target")));
    }
    private static Reference reference(JsonElement node) {
        if (node == null || node.isJsonNull()) return null;
        if (!node.isJsonObject()) throw new IllegalArgumentException("Invalid public choice reference");
        var object = node.getAsJsonObject();
        var kind = switch (text(object.get("kind"))) {
            case "card" -> ReferenceKind.CARD; case "stack" -> ReferenceKind.STACK; case "player" -> ReferenceKind.PLAYER;
            default -> throw new IllegalArgumentException("Unknown public choice reference kind");
        };
        String id = text(object.get("id"));
        if (!validId(id) || kind == ReferenceKind.PLAYER && !validPlayer(id)) throw new IllegalArgumentException("Invalid public choice reference");
        return new Reference(kind, id);
    }
    private static boolean validPlayer(String id) {
        try { return Integer.toString(Integer.parseInt(id)).equals(id) && Integer.parseInt(id) >= 0; }
        catch (NumberFormatException invalid) { return false; }
    }
    private static String contextId(JsonElement node) {
        if (node == null || node.isJsonNull()) return null;
        String id = text(node);
        if (!validId(id)) throw new IllegalArgumentException("Invalid public choice context");
        return id;
    }
    private static String text(JsonElement node) {
        if (node == null || !node.isJsonPrimitive() || !node.getAsJsonPrimitive().isString()) throw new IllegalArgumentException("Invalid public choice value");
        return node.getAsString();
    }
    private static boolean validId(String value) {
        return value != null && !value.isBlank() && value.length() <= 128 && value.chars().noneMatch(Character::isISOControl);
    }
}
