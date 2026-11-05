package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;
import java.util.*;
import java.util.concurrent.atomic.AtomicLong;
import static org.testng.Assert.*;

public class MatchPresenceTest {
    private static final Gson JSON = new Gson();
    private static final MatchPresence.Subject CARD = new MatchPresence.Subject(MatchPresence.Kind.CARD, "public-card");
    private static final MatchPresence.Subject STACK = new MatchPresence.Subject(MatchPresence.Kind.STACK, "7");

    @Test public void latestInspectionExpiresCoalescesAndCannotBeReplayedOrAttributedToAnotherPlayer() {
        var clock = new AtomicLong(1000);
        var presence = new MatchPresence("game", clock::get);
        var context = new MatchPresence.Context("context", 9, 29);
        var publicSubjects = Set.of(CARD, STACK);
        var forged = update(1, "context", CARD); forged.addProperty("playerId", 47);
        var first = presence.exchange(29, forged, 9, context, publicSubjects, publicSubjects);
        assertEquals(cues(first).size(), 1); assertEquals(cues(first).get(0).get("playerId"), 29);
        assertEquals(cues(first).get(0).get("subject"), CARD.wire());
        clock.addAndGet(100);
        var capped = presence.exchange(29, update(2, "context", STACK), 9, context, publicSubjects, publicSubjects);
        assertEquals(cues(capped).get(0).get("subject"), CARD.wire(), "Sub-250ms updates cannot produce a new cue");
        clock.addAndGet(150);
        var changed = presence.exchange(29, update(3, "context", STACK), 9, context, publicSubjects, publicSubjects);
        assertEquals(cues(changed).get(0).get("subject"), STACK.wire());
        assertEquals(cues(first).get(0).get("subject"), CARD.wire(), "Earlier frames remain detached");
        var replayed = presence.exchange(29, update(2, "context", CARD), 9, context, publicSubjects, publicSubjects);
        assertEquals(cues(replayed).get(0).get("subject"), STACK.wire());
        clock.addAndGet(1500);
        assertTrue(cues(presence.exchange(47, update(1, null, null), 9, context, publicSubjects, publicSubjects)).isEmpty());
        assertTrue(cues(presence.exchange(47, update(2, "context", CARD), 9, context, publicSubjects, publicSubjects)).isEmpty(),
                "A guest cannot publish for the current actor even with the public context ID");
        presence.disconnected(29);
        assertEquals(cues(presence.exchange(29, update(1, "context", CARD), 9, context, publicSubjects, publicSubjects)).size(), 1);
        assertTrue(cues(presence.exchange(47, update(3, null, null), 9,
                new MatchPresence.Context("replacement", 9, 29), publicSubjects, publicSubjects)).isEmpty());
    }

    @Test public void bothAdmissionAndRecipientDisclosureAreRequiredWithoutRecoveringPrivateSubjects() {
        var presence = new MatchPresence("game", () -> 1000);
        var context = new MatchPresence.Context("context", 9, 29);
        assertTrue(cues(presence.exchange(29, update(1, "context", CARD), 9, context, Set.of(), Set.of(CARD))).isEmpty());
        assertTrue(cues(presence.exchange(29, update(2, "context", CARD), 9, context, Set.of(CARD), Set.of())).isEmpty());
        assertTrue(cues(presence.exchange(47, update(1, null, null), 10, null, Set.of(CARD), Set.of(CARD))).isEmpty());
        assertTrue(cues(presence.exchange(29, update(3, "context", CARD), 9, null, Set.of(CARD), Set.of(CARD))).isEmpty(),
                "A public card during a required or secret choice is insufficient");
        var players = List.of(Map.of("zones", List.of(
                Map.of("name", "Battlefield", "cards", List.of(card("public-card", false), card("concealed-card", true))),
                Map.of("name", "Hand", "cards", List.of(card("private-hand", false))),
                Map.of("name", "Library", "cards", List.of(card("revealed-library", false))))));
        var stack = List.of(Map.of("id", 7, "name", "Public spell", "card", card("spell-card", false)),
                Map.of("id", 8, "name", "Face-down spell", "card", card("face-down-spell", true)));
        assertEquals(MatchPresence.subjects(players, stack), Set.of(CARD, STACK));
    }

    @Test public void malformedKindsAndUnboundedOrNonIntegralScopesNeverEnterTheLedger() {
        var presence = new MatchPresence("game", () -> 1000);
        var context = new MatchPresence.Context("context", 9, 29);
        for (String invalid : List.of("{\"kind\":\"hand\",\"id\":\"secret\"}", "{\"kind\":0,\"id\":\"public-card\"}",
                "{\"kind\":\"card\"}", "{\"kind\":\"card\",\"id\":\"" + "a".repeat(129) + "\"}")) {
            var request = update(1, "context", CARD); request.add("subject", com.google.gson.JsonParser.parseString(invalid));
            assertThrows(IllegalArgumentException.class, () -> presence.exchange(29, request, 9, context, Set.of(CARD), Set.of(CARD)));
        }
        for (String invalid : List.of("1.5", "9223372036854775808", "\"1\"", "0", "-1")) {
            var request = update(1, "context", CARD); request.add("sequence", com.google.gson.JsonParser.parseString(invalid));
            assertThrows(IllegalArgumentException.class, () -> presence.exchange(29, request, 9, context, Set.of(CARD), Set.of(CARD)));
        }
    }

    private static Map<String, Object> card(String id, boolean faceDown) { return Map.of("visualId", id, "faceDown", faceDown); }
    static JsonObject update(long sequence, String context, MatchPresence.Subject subject) {
        var value = new JsonObject(); value.addProperty("gameId", "game"); value.addProperty("boardRevision", 9);
        value.addProperty("sequence", sequence); value.addProperty("contextId", context);
        value.add("subject", subject == null ? com.google.gson.JsonNull.INSTANCE : JSON.toJsonTree(subject.wire())); return value;
    }
    @SuppressWarnings("unchecked") private static List<Map<String, Object>> cues(Map<String, Object> frame) {
        return (List<Map<String, Object>>)frame.get("cues");
    }
}
