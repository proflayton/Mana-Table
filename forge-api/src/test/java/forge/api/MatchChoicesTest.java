package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.api.sync.SyncTransport;
import org.testng.annotations.Test;
import java.util.*;
import java.util.concurrent.atomic.AtomicLong;
import static org.testng.Assert.*;

public class MatchChoicesTest {
    private static final Gson JSON = new Gson();
    private static final MatchChoices.Reference SOURCE = card("source"), TARGET = card("target");
    private static final MatchChoices.Reference PLAYER = new MatchChoices.Reference(MatchChoices.ReferenceKind.PLAYER, "47");

    @Test public void targetContextsFollowTheActualChoiceAndAudienceWithoutBorrowingPriorityOrPrivateReading() {
        var context = new MatchChoices.Context("choice", 9, 29, MatchChoices.Kind.TARGET, SOURCE);
        var frame = new HashMap<String, Object>(Map.of("status", "playing", "actionClass", "selection", "boardRevision", 9L,
                "priorityPlayerId", 47, "decisionOwnerId", 29, "viewerId", 47,
                "players", List.of(Map.of("id", 29), Map.of("id", 47))));
        assertSame(MatchChoices.projectContext(frame, context, Set.of(SOURCE)), context,
                "Required choice ownership is distinct from priority ownership");
        var actor = new HashMap<>(frame); actor.put("viewerId", 29);
        actor.put("prompt", Map.of("task", "selection", "purpose", "targets"));
        assertSame(MatchChoices.projectContext(actor, context, Set.of(SOURCE)), context);
        for (String status : List.of("resolving", "finished", "error", "starting")) {
            var changed = new HashMap<>(frame); changed.put("status", status);
            assertNull(MatchChoices.projectContext(changed, context, Set.of(SOURCE)));
        }
        for (var prompt : List.of(Map.of("task", "selection"), Map.of("task", "selection", "purpose", "targets", "librarySearch", Map.of()),
                Map.of("task", "attack"))) {
            var changed = new HashMap<>(actor); changed.put("prompt", prompt);
            assertNull(MatchChoices.projectContext(changed, context, Set.of(SOURCE)));
        }
        assertNull(MatchChoices.projectContext(frame, context, Set.of()), "Own private reader material is not public-source authority");
        var departed = new HashMap<>(frame); departed.put("players", List.of(Map.of("id", 29, "eliminated", true), Map.of("id", 47)));
        assertNull(MatchChoices.projectContext(departed, context, Set.of(SOURCE)));
        var invalidCombat = new MatchChoices.Context("choice", 9, 29, MatchChoices.Kind.ATTACK, SOURCE);
        assertNull(MatchChoices.projectContext(frame, invalidCombat, Set.of(SOURCE)));
    }

    @Test public void latestAimedPairHasOneLeaseAndCannotMutateItsPublishedDomainOrReplayAfterRetirement() {
        var clock = new AtomicLong(1000); var presence = new MatchPresence("game", clock::get);
        var targets = new HashSet<>(Set.of(TARGET, PLAYER));
        var offers = new HashMap<MatchChoices.Reference, Set<MatchChoices.Reference>>(); offers.put(SOURCE, targets);
        var domain = new MatchChoices.Domain(MatchChoices.Kind.TARGET, SOURCE, offers);
        targets.clear(); offers.clear();
        var context = new MatchChoices.Context("choice", 9, 29, MatchChoices.Kind.TARGET, SOURCE);
        var visible = Set.of(SOURCE, TARGET, PLAYER);
        var request = update(1, "choice", SOURCE, TARGET); request.addProperty("playerId", 47);
        var first = exchange(presence, 29, request, context, domain, visible);
        assertEquals(choices(first).size(), 1); assertEquals(choices(first).get(0).get("playerId").getAsInt(), 29);
        assertEquals(text(choices(first).get(0).getAsJsonObject("preview").getAsJsonObject("target"), "id"), "target");
        clock.addAndGet(250);
        var changed = exchange(presence, 29, update(2, "choice", SOURCE, PLAYER), context, domain, visible);
        assertEquals(text(choices(changed).get(0).getAsJsonObject("preview").getAsJsonObject("target"), "kind"), "player");
        assertEquals(choices(exchange(presence, 29, update(1, "choice", SOURCE, TARGET), context, domain, visible)), choices(changed));
        assertEquals(text(choices(first).get(0).getAsJsonObject("preview").getAsJsonObject("target"), "id"), "target", "Earlier frames remain detached");
        assertTrue(choices(exchange(presence, 47, update(1, "choice", SOURCE, PLAYER), context, domain, visible)).stream()
                .noneMatch(cue -> cue.get("playerId").getAsInt() == 47), "The public context does not authorize another sender");
        clock.addAndGet(1500);
        assertTrue(choices(exchange(presence, 47, update(2, null, null, null), context, domain, visible)).isEmpty());
        clock.addAndGet(250);
        assertEquals(choices(exchange(presence, 29, update(3, "choice", SOURCE, null), context, domain, visible)).size(), 1);
        var replacement = new MatchChoices.Context("replacement", 9, 29, MatchChoices.Kind.TARGET, SOURCE);
        assertTrue(choices(exchange(presence, 29, update(4, "choice", SOURCE, TARGET), replacement, domain, visible)).isEmpty());
        presence.disconnected(29);
        assertEquals(choices(exchange(presence, 29, update(1, "replacement", SOURCE, TARGET), replacement, domain, visible)).size(), 1);
    }

    @Test public void publicObjectsNeedBothTheCurrentActorsLegalPairAndRecipientDisclosure() {
        var presence = new MatchPresence("game", () -> 1000);
        var context = new MatchChoices.Context("choice", 9, 29, MatchChoices.Kind.BLOCK, null);
        var domain = new MatchChoices.Domain(MatchChoices.Kind.BLOCK, null, Map.of(SOURCE, Set.of(TARGET)));
        assertTrue(choices(exchange(presence, 29, update(1, "choice", TARGET, SOURCE), context, domain, Set.of(SOURCE, TARGET))).isEmpty());
        assertTrue(choices(exchange(presence, 29, update(2, "choice", SOURCE, PLAYER), context, domain, Set.of(SOURCE, TARGET, PLAYER))).isEmpty());
        assertTrue(choices(exchange(presence, 29, update(3, "choice", SOURCE, TARGET), context, domain, Set.of(SOURCE))).isEmpty());
        assertTrue(choices(exchange(presence, 29, update(4, "choice", SOURCE, TARGET), null, domain, Set.of(SOURCE, TARGET))).isEmpty());
        var stale = update(5, "choice", SOURCE, TARGET); stale.addProperty("boardRevision", 8);
        assertTrue(choices(exchange(presence, 29, stale, context, domain, Set.of(SOURCE, TARGET))).isEmpty());
        assertThrows(UnsupportedOperationException.class, domain.targets()::clear);
        assertThrows(UnsupportedOperationException.class, domain.targets().get(SOURCE)::clear);
    }

    @Test public void malformedAndNoncanonicalReferencesAreRejectedBeforeEitherPresenceLedgerChanges() {
        var presence = new MatchPresence("game", () -> 1000);
        var context = new MatchChoices.Context("choice", 9, 29, MatchChoices.Kind.TARGET, SOURCE);
        var domain = new MatchChoices.Domain(MatchChoices.Kind.TARGET, SOURCE, Map.of(SOURCE, Set.of(PLAYER)));
        for (String target : List.of("{\"kind\":\"hand\",\"id\":\"secret\"}", "{\"kind\":0,\"id\":\"target\"}",
                "{\"kind\":\"player\",\"id\":\"047\"}", "{\"kind\":\"player\",\"id\":47}", "{\"kind\":\"player\",\"id\":\"-1\"}")) {
            var request = update(1, "choice", SOURCE, PLAYER);
            request.getAsJsonObject("choice").add("target", com.google.gson.JsonParser.parseString(target));
            assertThrows(IllegalArgumentException.class, () -> exchange(presence, 29, request, context, domain, Set.of(SOURCE, PLAYER)));
        }
        assertEquals(choices(exchange(presence, 29, update(1, "choice", SOURCE, PLAYER), context, domain, Set.of(SOURCE, PLAYER))).size(), 1,
                "Malformed updates cannot consume the real sender's sequence or rate allowance");
    }

    @Test(timeOut = 10000) public void theRealHostNegotiationPreservesOldInspectionWithoutForwardingNewChoiceFields() throws Exception {
        for (boolean supported : List.of(false, true)) {
            Set<String> capabilities = supported ? Set.of("table-presence", "table-choices-v1") : Set.of("table-presence");
            try (var server = new SyncTransport.Server((seat, method, args) -> Map.of("choice", args.has("choice"),
                    "choiceContext", args.has("choiceContextId"), "inspection", args.has("subject")), () -> 1, ignored -> { }, capabilities);
                 var remote = new SeatConnection(new SyncTransport.Remote("127.0.0.1:" + server.listen()))) {
                var request = update(1, "choice", SOURCE, TARGET); request.add("subject", JSON.toJsonTree(Map.of("kind", "card", "id", "public")));
                var result = remote.presence(request);
                assertEquals(result.get("choice"), supported); assertEquals(result.get("choiceContext"), supported);
                assertEquals(result.get("inspection"), true); assertTrue(request.has("choice"), "Negotiation does not rewrite the caller's value");
            }
        }
    }

    private static MatchChoices.Reference card(String id) { return new MatchChoices.Reference(MatchChoices.ReferenceKind.CARD, id); }
    private static JsonObject update(long sequence, String context, MatchChoices.Reference source, MatchChoices.Reference target) {
        var request = new JsonObject(); request.addProperty("gameId", "game"); request.addProperty("boardRevision", 9L); request.addProperty("sequence", sequence);
        if (context != null) request.addProperty("choiceContextId", context);
        if (source != null) request.add("choice", JSON.toJsonTree(new MatchChoices.Preview(source, target)));
        return request;
    }
    private static Map<String, Object> exchange(MatchPresence presence, int player, JsonObject request, MatchChoices.Context context,
            MatchChoices.Domain domain, Set<MatchChoices.Reference> visible) {
        return presence.exchange(player, request, 9, null, Set.of(), Set.of(), context, domain, visible);
    }
    private static List<JsonObject> choices(Map<String, Object> frame) {
        return JSON.toJsonTree(frame).getAsJsonObject().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static String text(JsonObject object, String key) { return object.get(key).getAsString(); }
}
