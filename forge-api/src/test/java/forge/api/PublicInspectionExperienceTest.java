package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.api.sync.SyncTransport;
import forge.card.MagicColor;
import forge.game.mana.Mana;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real rules inputs and two authenticated TCP seats; no fabricated game state. */
public class PublicInspectionExperienceTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void aNewHostsInactiveMatchIsSupersededRatherThanAnAbsentPresenceCapability() throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var evidence = ApiTestWorkspace.evidence("public-inspection-inactive-host");
        var lobby = new SyncLobby(resources, evidence);
        try {
            lobby.host("Commander", 2, false, "local");
            var retired = expectThrows(SyncTransport.CodedFailure.class, () -> lobby.match().presence(new JsonObject()));
            assertEquals(retired.code(), MatchPresence.SUPERSEDED);
            for (String method : List.of("tablePresence", "matchAction")) {
                var request = new JsonObject(); request.addProperty("id", 1); request.addProperty("method", method);
                var reply = DesktopEngine.errorReply(request, retired);
                assertEquals(reply.has("errorCode"), method.equals("tablePresence"));
            }
        } finally { lobby.close(); }
    }

    @Test(timeOut = 90000)
    public void unpublishedRulesChangesAndRetiredPublicationSetsCannotCertifyAnInspection() throws Exception {
        try (var game = new RulesScenario("public-inspection-unpublished-state", Map.of(0, List.of("Forest")))) {
            main(game);
            HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0); long board = before.get("boardRevision").getAsLong();
                String cardId = text(card(before, 0, "Forest"), "visualId");
                assertEquals(cues(game.session(0).presence(update(before, 1, "card", cardId))).size(), 1);
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var forest = actor.getCardsIn(ZoneType.Battlefield).get(0);
                actor.getManaPool().addMana(new Mana(MagicColor.GREEN, forest, forest.getManaAbilities().get(0).getManaPart(), actor));
                assertEquals(game.state(0), before, "The detached publication remains the actual old committed value");
                assertTrue(game.session(0).publicInspectionSubjects(board).isEmpty(), "An unpublished source event retires that cached membership set");
                assertTrue(cues(game.session(0).presence(update(before, 2, "card", cardId))).isEmpty());
                game.table.publish();
                var current = game.state(0);
                assertTrue(game.session(0).publicInspectionSubjects(board).isEmpty(), "A previous board cannot borrow the new publication's detached subjects");
                assertNotEquals(current.get("publicInspection"), before.get("publicInspection"));
                // Use a fresh ledger after explicit connection retirement to
                // avoid confusing the independent 4Hz publication cap with scope.
                game.session(0).revokeFiniteYieldForConnection(); current = game.state(0);
                assertEquals(cues(game.session(0).presence(update(current, 3, "card", cardId))).size(), 1);
                Files.writeString(game.evidence.resolve("public-inspection-unpublished-state.json"), JSON.toJson(Map.of("before", before, "after", current)));
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void inspectionWaitsForTheExistingRulesBoundaryAndRechecksRetiredInputAfterRelease() throws Exception {
        try (var game = new RulesScenario("public-inspection-held-state", Map.of(0, List.of("Forest")))) {
            var input = main(game); var source = card(input.state(), 0, "Forest");
            var update = update(input.state(), 1, "card", text(source, "visualId"));
            var entered = new CountDownLatch(1); var release = new CountDownLatch(1); var queryEntered = new CountDownLatch(1);
            var holder = CompletableFuture.runAsync(() -> game.table.game.getAction().withState(() -> {
                entered.countDown();
                try { if (!release.await(5, TimeUnit.SECONDS)) throw new AssertionError("Fixture did not release its owned state boundary"); }
                catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); throw new AssertionError(interrupted); }
                game.session(0).revokeFiniteYieldForConnection();
            }));
            assertTrue(entered.await(5, TimeUnit.SECONDS));
            var reading = CompletableFuture.supplyAsync(() -> { queryEntered.countDown(); return game.session(0).presence(update); });
            try {
                assertTrue(queryEntered.await(5, TimeUnit.SECONDS));
                try { reading.get(50, TimeUnit.MILLISECONDS); fail("Presence read escaped the held rules-state boundary"); }
                catch (TimeoutException expected) { /* The owned rules boundary still excludes this read. */ }
            } finally { release.countDown(); }
            holder.get(5, TimeUnit.SECONDS);
            assertTrue(cues(reading.get(5, TimeUnit.SECONDS)).isEmpty(), "The old context cannot be admitted after connection/input retirement");
            assertNotEquals(game.state(0).get("publicInspection"), input.state().get("publicInspection"));
        }
    }

    @Test(timeOut = 90000)
    public void twoTcpSeatsShareOnlyCertifiedPublicInspectionWithoutChangingRulesOrDecisionTime() throws Exception {
        var clock = new AtomicLong(1000);
        try (var game = new RulesScenario("public-inspection-tcp", Map.of(0, List.of("Forest"), 1, List.of("Sol Ring")),
                DecisionTempo.Settings.off(), clock::get);
             var server = new SyncTransport.Server((seat, method, args) -> switch (method) {
                 case "observe" -> game.session(seat).state(); case "presence" -> game.session(seat).presence(args);
                 case "sourceActions" -> game.session(seat).sourceActions(args); case "reply" -> game.session(seat).action(args);
                 default -> throw new IllegalArgumentException("Unknown test route");
             }, () -> 1, seat -> game.session(seat).revokeFiniteYieldForConnection(), Set.of("table-presence"));
             var host = new SeatConnection(server.remoteHost());
             var guestTransport = new SyncTransport.Remote("127.0.0.1:" + server.listen());
             var guest = new SeatConnection(guestTransport)) {
            var input = main(game);
            var original = JSON.toJsonTree(host.state()).getAsJsonObject();
            var context = original.getAsJsonObject("publicInspection"); assertNotNull(context);
            assertEquals(game.state(1).get("publicInspection"), context, "All audiences receive the same public context");
            assertEquals(JSON.toJsonTree(host.state().get("publicInspection")), JSON.toJsonTree(game.session(0).state().get("publicInspection")));
            var source = card(original, 0, "Forest");
            var update = update(original, 1, "card", text(source, "visualId")); update.addProperty("playerId", game.state(1).get("viewerId").getAsInt());
            var local = host.presence(update);
            assertEquals(cues(local).size(), 1);
            var shared = guest.presence(clear(game.state(1), 1));
            assertEquals(JSON.toJsonTree(cues(shared)), JSON.toJsonTree(cues(local)));
            assertEquals(((Number)cues(shared).get(0).get("playerId")).intValue(), original.get("viewerId").getAsInt(), "Authenticated sender defeats a forged actor claim");
            assertEquals(JSON.toJsonTree(host.state()), original, "Presence changes no board/publication/history/input/timer facts");
            var hidden = zone(viewer(original), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(cues(host.presence(update(original, 2, "card", text(hidden, "visualId")))).isEmpty(), "An owned readable hand face is still private");
            clock.addAndGet(250);
            assertEquals(cues(host.presence(update(original, 3, "card", text(source, "visualId")))).size(), 1);
            guestTransport.reconnect(); guest.state();
            assertTrue(cues(guest.presence(clear(game.state(1), 2))).isEmpty(), "Reconnect retires the shared old context and cannot replay its cue");
            var current = main(game);
            var catalog = JSON.toJsonTree(host.sourceActions(request(current, Map.of("key", text(card(current.state(), 0, "Forest"), "key"))))).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            host.action(request(current, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                    "actionId", text(action, "id"), "manaOutput", Map.of("colors", Map.of("G", 1)))));
            var floated = game.until(i -> i.seat() == 0 && priority(i) && viewer(i.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            assertNotEquals(floated.state().get("publicInspection"), context);
            assertTrue(card(floated.state(), 0, "Forest").get("tapped").getAsBoolean());
            assertTrue(cues(host.presence(update(original, 4, "card", text(source, "visualId")))).isEmpty(), "Old input/publication context cannot restore a cue after an actual action");
            Files.writeString(game.evidence.resolve("public-inspection-tcp.json"), JSON.toJson(Map.of("before", original,
                    "shared", shared, "afterMana", floated.state(), "publicCue", local)));
        }
    }

    @Test(timeOut = 90000)
    public void aPublicBattlefieldTargetDuringRequiredChoiceStaysPrivateAndCommittedStackInspectionReturns() throws Exception {
        try (var game = new RulesScenario("public-inspection-choice", Map.of(0, List.of("Forest", "Grizzly Bears"), 1, List.of("Grizzly Bears")),
                "Battlegrowth", "Rhys the Redeemed")) {
            var input = main(game);
            assertNotNull(input.state().getAsJsonObject("publicInspection"));
            var source = card(input.state(), 0, "Forest");
            var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                    "actionId", text(action, "id"), "manaOutput", Map.of("colors", Map.of("G", 1)))));
            input = game.until(i -> i.seat() == 0 && priority(i) && viewer(i.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            var certified = input.state();
            var spell = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(input, Map.of("action", "card", "key", text(spell, "key"))));
            var targeting = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectTargets"));
            var target = card(targeting.state(), 1, "Grizzly Bears");
            for (int seat = 0; seat < 4; seat++) assertTrue(game.state(seat).get("publicInspection") == null
                    || game.state(seat).get("publicInspection").isJsonNull(), "Every audience revokes public inspection for the required target choice");
            assertTrue(cues(game.session(0).presence(update(certified, 1, "card", text(target, "visualId")))).isEmpty(),
                    "Public card identity cannot disclose tentative targeting under an old priority context");
            assertTrue(game.answer(targeting, Map.of("action", "card", "key", text(target, "key"))));
            Input stack = null;
            for (int step = 0; step < 4; step++) {
                var next = game.until(i -> text(i.prompt(), "inputType").startsWith("InputPayMana") || priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
                if (priority(next)) { stack = next; break; }
                assertTrue(game.answer(next, Map.of("action", "ok")));
            }
            assertNotNull(stack); assertNotNull(stack.state().getAsJsonObject("publicInspection"));
            int actor = stack.seat();
            // Casting can publish another coherent board after the first input
            // observation. Read the actual current scope for this positive case;
            // the separate stale-scope journey must continue to reject old ones.
            var attempt = game.table.game.getAction().readState(() -> {
                var current = game.state(actor);
                String stackId = text(current.getAsJsonArray("stack").get(0).getAsJsonObject(), "id");
                return Map.of("state", current, "frame", game.session(actor).presence(update(current, 2, "stack", stackId)));
            });
            @SuppressWarnings("unchecked") var frame = (Map<String, Object>)attempt.get("frame");
            Files.writeString(game.evidence.resolve("stack-inspection-attempt.json"), JSON.toJson(attempt));
            assertEquals(cues(frame).size(), 1);
            assertEquals(((Map<?, ?>)cues(frame).get(0).get("subject")).get("kind"), "stack");
            Files.writeString(game.evidence.resolve("public-inspection-required-choice.json"), JSON.toJson(Map.of(
                    "priority", certified, "targetChoice", targeting.state(), "stack", attempt.get("state"), "frame", frame)));
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, int seat, String name) {
        return zone(state.getAsJsonArray("players").get(seat).getAsJsonObject(), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject clear(JsonObject state, long sequence) {
        var update = new JsonObject(); update.addProperty("gameId", text(state, "id"));
        update.addProperty("boardRevision", state.get("boardRevision").getAsLong()); update.addProperty("sequence", sequence); return update;
    }
    private static JsonObject update(JsonObject state, long sequence, String kind, String id) {
        var request = clear(state, sequence); request.addProperty("contextId", text(state.getAsJsonObject("publicInspection"), "id"));
        request.add("subject", JSON.toJsonTree(Map.of("kind", kind, "id", id))); return request;
    }
    @SuppressWarnings("unchecked") private static List<Map<String, Object>> cues(Map<String, Object> frame) {
        return (List<Map<String, Object>>)frame.get("cues");
    }
}
