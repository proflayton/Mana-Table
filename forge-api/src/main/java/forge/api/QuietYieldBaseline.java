package forge.api;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.UUID;

/** A private permission for an unchanged, already-sanitized public situation.
 * Neither an engine view nor a hidden card is accepted by this boundary. */
final class QuietYieldBaseline {
    private static final List<String> PUBLIC_STATE = List.of("turn", "activePlayerId", "players", "stack", "combat", "states", "result");
    private static final List<String> PLAYER_STATS = List.of("life", "maxHandSize", "landPlaysUsed", "landPlayLimit", "counters", "states");
    private final Map<String, Object> entities = new LinkedHashMap<>();
    private Object state;
    private long eventEpoch;
    private long observedEpoch = -1;
    private long activitySequence = -1;
    private String token = "";

    synchronized String observe(Map<String, Object> publicSnapshot, long sequence) {
        var next = new LinkedHashMap<String, Object>();
        for (String key : PUBLIC_STATE) next.put(key, key.equals("combat")
                ? declaredCombat(publicSnapshot.get(key)) : copy(publicSnapshot.get(key)));
        if (!Objects.equals(state, next) || observedEpoch != eventEpoch || activitySequence != sequence) {
            state = next;
            observedEpoch = eventEpoch;
            activitySequence = sequence;
            token = UUID.randomUUID().toString();
        }
        entities.clear();
        seedEntities(next);
        return token;
    }

    synchronized void invalidate() { eventEpoch++; }

    synchronized boolean permits(String suppliedToken, long sequence) {
        return suppliedToken != null && !suppliedToken.isEmpty() && token.equals(suppliedToken)
                && observedEpoch == eventEpoch && activitySequence == sequence;
    }

    /** A refresh notification alone is not a mutation. Compare only copied
     * public characteristics; a change and restoration still advance the epoch. */
    synchronized void observeCard(Map<String, Object> publicCard) {
        observeEntity(cardId(publicCard), copy(publicCard));
    }

    synchronized void observePlayer(int id, Map<String, Object> publicStats) {
        observeEntity("player:" + id, playerStats(publicStats));
    }

    synchronized void observeMana(int id, Map<String, Integer> publicMana) {
        observeEntity("mana:" + id, copy(publicMana));
    }

    /** Advance only the expected public counts of an explicitly tagged turn
     * draw. Visible faces and library top information remain in the baseline. */
    @SuppressWarnings("unchecked")
    synchronized void observeTurnBasedDraw(int id) {
        if (!(state instanceof Map<?, ?> baseline) || !Objects.equals(baseline.get("activePlayerId"), id)
                || !(baseline.get("players") instanceof List<?> players)) return;
        for (Object value : players) {
            if (!(value instanceof Map<?, ?> player) || !Objects.equals(player.get("id"), id)
                    || !(player.get("zones") instanceof List<?> zones)) continue;
            Map<String, Object> hand = null, library = null;
            for (Object item : zones) {
                if (!(item instanceof Map<?, ?> zone)) continue;
                if (Objects.equals(zone.get("name"), "Hand")) hand = (Map<String, Object>) zone;
                else if (Objects.equals(zone.get("name"), "Library")) library = (Map<String, Object>) zone;
            }
            if (hand != null && library != null && hand.get("count") instanceof Number handCount
                    && library.get("count") instanceof Number libraryCount && libraryCount.intValue() > 0) {
                hand.put("count", handCount.intValue() + 1);
                library.put("count", libraryCount.intValue() - 1);
            }
            return;
        }
    }

    private void observeEntity(String id, Object value) {
        if (!Objects.equals(entities.put(id, value), value)) invalidate();
    }

    private void seedEntities(Object value) {
        if (value instanceof Map<?, ?> source) {
            if (source.containsKey("visualId") && source.containsKey("faceDown")) entities.put(cardId(source), source);
            if (source.containsKey("zones") && source.containsKey("id")) {
                entities.put("player:" + source.get("id"), playerStats(source));
                entities.put("mana:" + source.get("id"), source.get("mana"));
            }
            for (var item : source.values()) seedEntities(item);
        } else if (value instanceof List<?> source) {
            for (var item : source) seedEntities(item);
        }
    }

    private static String cardId(Map<?, ?> card) { return "card:" + card.get("combatId"); }

    private static Map<String, Object> playerStats(Map<?, ?> player) {
        var result = new LinkedHashMap<String, Object>();
        for (String key : PLAYER_STATS) result.put(key, copy(player.get(key)));
        return result;
    }

    /** Combat setup and legal-input options are not declared combat. Keep the
     * actual attackers, their defenders and assigned blocks in the permission. */
    private static Object declaredCombat(Object value) {
        if (!(value instanceof Map<?, ?> combat) || !(combat.get("attackers") instanceof List<?> attackers)
                || attackers.isEmpty()) return null;
        var declared = new ArrayList<>();
        for (Object attacker : attackers) {
            if (!(attacker instanceof Map<?, ?> fields)) { declared.add(copy(attacker)); continue; }
            var actual = new LinkedHashMap<String, Object>();
            for (var field : fields.entrySet()) {
                String key = (String) field.getKey();
                if (!key.equals("eligibleBlockerIds")) actual.put(key, copy(field.getValue()));
            }
            declared.add(actual);
        }
        return Map.of("attackingPlayerId", combat.get("attackingPlayerId"), "attackers", declared);
    }

    private static Object copy(Object value) {
        if (value instanceof Map<?, ?> source) {
            var result = new LinkedHashMap<String, Object>();
            boolean card = source.containsKey("visualId") && source.containsKey("faceDown");
            boolean player = source.containsKey("zones") && source.containsKey("id");
            for (var entry : source.entrySet()) {
                String key = (String) entry.getKey();
                // Only presentation/input fields are omitted. In particular a
                // public mechanic descriptor's "key" remains meaningful state.
                if (card && (key.equals("key") || key.equals("selectable") || key.equals("highlighted"))) continue;
                if (player && key.equals("priority")) continue;
                result.put(key, copy(entry.getValue()));
            }
            return result;
        }
        if (value instanceof List<?> source) {
            var result = new ArrayList<>();
            for (var item : source) result.add(copy(item));
            return result;
        }
        return value;
    }
}
