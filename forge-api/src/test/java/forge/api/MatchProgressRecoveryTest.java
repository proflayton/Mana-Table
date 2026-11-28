package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.gson.JsonElement;
import com.google.gson.JsonPrimitive;
import com.google.gson.JsonNull;
import forge.api.sync.SyncTransport;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicBoolean;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class MatchProgressRecoveryTest {
    private static final Gson JSON = new Gson();
    @DataProvider public Object[][] seats() { return new Object[][] {{false, false}, {true, false}, {false, true}, {true, true}}; }

    @Test(dataProvider = "seats", timeOut = 90000)
    public void localAndTcpRepairAnActualUnpublishedInputWithoutAnsweringIt(boolean automated, boolean tcp) throws Exception {
        var battlefield = Map.of(0, List.of("Forest", "Forest", "Forest"));
        String name = "progress-control-" + automated + "-" + tcp;
        try (var game = automated ? RulesScenario.automated(name, battlefield, "Manalith", "Rhys the Redeemed")
                : new RulesScenario(name, battlefield, "Manalith", "Rhys the Redeemed")) {
            var main = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(game.table, () -> null);
            var session = game.session(0);
            var controller = (PlayerControllerHuman)game.table.game.getPlayer(0).getController();
            var input = (InputPassPriority)controller.getInputQueue().getInput();
            var beforeWait = game.state(0).getAsJsonArray("matchWaits").get(0).getAsJsonObject();
            long generation = beforeWait.get("generation").getAsLong();
            assertTrue(beforeWait.get("published").getAsBoolean());
            HeadlessPlatform.query(game.table, () -> {
                // Fault injection removes only adapter publication ownership.
                // Forge's actual input and waiting latch remain untouched.
                session.clearDecision(); game.table.publish();
                assertNull(session.state().get("prompt"));
                assertTrue(input.isPriorityActive());
                var missing = game.state(0).getAsJsonArray("matchWaits").get(0).getAsJsonObject();
                assertFalse(missing.get("published").getAsBoolean());
                assertEquals(missing.get("generation").getAsLong(), generation, "Losing a publication cannot invent a new input episode");
                return null;
            });
            var request = new JsonObject(); request.addProperty("sessionId", game.table.id);
            try (var server = new SyncTransport.Server((seat, method, args) -> switch (method) {
                case "reconcileProgress" -> session.reconcileProgress(args);
                case "observe" -> session.state();
                default -> throw new IllegalArgumentException("Unexpected recovery command");
            }, () -> 0, ignored -> { }, Set.of("match-progress-recovery-v1"));
                 var connection = tcp ? new SeatConnection(new SyncTransport.Remote("127.0.0.1:" + server.listen())) : new SeatConnection(server.local(0))) {
                var receipt = connection.reconcileProgress(request);
                assertEquals(receipt.get("gameId"), game.table.id); assertEquals(receipt.get("status"), "reconciled");
                HeadlessPlatform.query(game.table, () -> null);
                var restored = game.state(0);
                assertNotNull(restored.getAsJsonObject("prompt"));
                var restoredWait = restored.getAsJsonArray("matchWaits").get(0).getAsJsonObject();
                assertTrue(restoredWait.get("published").getAsBoolean());
                assertEquals(restoredWait.get("generation").getAsLong(), generation, "Recovery restores the same actual controller wait");
                assertNotEquals(text(restored.getAsJsonObject("prompt"), "id"), text(main.prompt(), "id"), "The lost scope cannot regain old offers");
                assertSame(controller.getInputQueue().getInput(), input); assertSame(controller.getInputProxy().getInput(), input);
                assertTrue(input.isPriorityActive(), "Repair must never pass the waiting priority input");
                assertEquals(game.table.game.getPlayer(0).getManaPool().totalMana(), 0);
                assertEquals(restored.get("turn"), main.state().get("turn")); assertEquals(restored.get("phaseKey"), main.state().get("phaseKey"));
                assertEquals(restored.get("stack"), main.state().get("stack"));
                for (int reader = 1; reader < 4; reader++) assertTrue(game.state(reader).get("prompt").isJsonNull(), "Recovery cannot disclose another seat's decision");
                var before = game.state(0);
                assertEquals(connection.reconcileProgress(request).get("status"), "unchanged");
                assertEquals(game.state(0), before, "A healthy repeated recovery call creates no publication or input authority");
                var wrong = request.deepCopy(); wrong.addProperty("sessionId", "retired-game");
                assertThrows(RuntimeException.class, () -> connection.reconcileProgress(wrong));
                assertEquals(game.state(0), before, "Late recovery from a retired game cannot change this match");
                var observation = JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
                assertEquals(text(observation.getAsJsonObject("domain"), "coverage"), "Complete");
                var current = new Input(0, restored, restored.getAsJsonObject("prompt"));
                var forest = zone(viewer(restored), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
                var catalog = JSON.toJsonTree(session.sourceActions(RulesScenario.request(current, Map.of("key", text(forest, "key"))))).getAsJsonObject();
                assertTrue(game.answer(current, Map.of("action", "useSourceAction", "key", text(forest, "key"),
                        "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
                var acted = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                        && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
                assertEquals(viewer(acted.state()).getAsJsonObject("mana").get("G").getAsInt(), 1,
                        "A fresh recovered offer must execute the real engine ability");
            }
        }
    }

    @Test(timeOut = 90000)
    public void repairCannotReopenAnAlreadyQueuedCommand() throws Exception {
        try (var game = new RulesScenario("progress-queued-command", Map.of())) {
            var main = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            var session = game.session(0);
            HeadlessPlatform.query(game.table, () -> {
                session.action(RulesScenario.request(main, Map.of("action", "passPriority")));
                var request = new JsonObject(); request.addProperty("sessionId", game.table.id);
                assertEquals(session.reconcileProgress(request).get("status"), "unchanged");
                assertNull(session.state().get("prompt"), "An admitted command retains its exclusive input ownership");
                return null;
            });
            var next = game.until(input -> input.seat() != 0 && text(input.prompt(), "inputType").equals("InputPassPriority"));
            assertNotEquals(text(next.prompt(), "id"), text(main.prompt(), "id"));
        }
    }

    @Test(timeOut = 10000)
    public void aLegacyNetworkHostReceivesNoUnnegotiatedRecoveryRequest() throws Exception {
        var calls = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, request) -> { calls.incrementAndGet(); return Map.of(); }, () -> 0);
             var connection = new SeatConnection(new SyncTransport.Remote("127.0.0.1:" + server.listen()))) {
            assertThrows(SyncTransport.CodedFailure.class, () -> connection.reconcileProgress(new JsonObject()));
            assertEquals(calls.get(), 0);
        }
    }

    @Test(timeOut = 90000)
    public void networkRecoveryFailureDoesNotDiscloseAnotherSeatsPrivateCapture() throws Exception {
        try (var game = new RulesScenario("progress-private-failure", Map.of())) {
            game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            var session = game.session(0);
            HeadlessPlatform.query(game.table, () -> { session.clearDecision(); game.table.publish(); return null; });
            var armed = new AtomicBoolean();
            session.observePlaytest(frame -> { if (armed.get()) throw new IllegalStateException("PRIVATE-SEAT-CAPTURE-CANARY"); });
            try (var server = new SyncTransport.Server((seat, method, args) -> session.reconcileProgress(args), () -> 0,
                    ignored -> { }, Set.of("match-progress-recovery-v1"));
                 var connection = new SeatConnection(new SyncTransport.Remote("127.0.0.1:" + server.listen()))) {
                var request = new JsonObject(); request.addProperty("sessionId", game.table.id);
                armed.set(true);
                try {
                    connection.reconcileProgress(request);
                    fail("The controlled capture failure must reject reconciliation");
                } catch (RuntimeException failure) {
                    assertTrue(failure.getMessage().contains("could not restore its current decision"));
                    assertFalse(failure.getMessage().contains("PRIVATE-SEAT-CAPTURE-CANARY"));
                } finally { armed.set(false); }
            }
        }
    }

    @Test(timeOut = 90000)
    public void hostRecoveryRejectsMalformedUnownedAndRetiredScopesWithoutChangingTheInput() throws Exception {
        try (var fixture = new ConfiguredSimulationTest.Fixture()) {
            var setup = new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE, List.of(
                    new MatchSetupSpecification.Seat(0, 17, "party"), new MatchSetupSpecification.Seat(1, 17, "party"),
                    new MatchSetupSpecification.Seat(2, 42, "opposition"), new MatchSetupSpecification.Seat(3, 42, "opposition")));
            var accepted = fixture.start(fixture.request(setup));
            String game = text(accepted.getAsJsonObject("state"), "id");
            int actor = accepted.getAsJsonArray("participants").get(0).getAsJsonObject().get("playerId").getAsInt();
            var input = fixture.main(actor);
            var request = JSON.toJsonTree(Map.of("sessionId", game, "seatId", actor)).getAsJsonObject();
            assertEquals(JSON.toJsonTree(fixture.engine.dispatch("automatedReconcileProgress", request)).getAsJsonObject()
                    .get("status").getAsString(), "unchanged");
            for (JsonElement invalid : List.of(new JsonPrimitive("0"), new JsonPrimitive(0.5), JsonNull.INSTANCE, new JsonPrimitive(Integer.MAX_VALUE))) {
                var malformed = request.deepCopy(); malformed.add("seatId", invalid);
                assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedReconcileProgress", malformed));
            }
            var foreign = request.deepCopy(); foreign.addProperty("sessionId", "other-game");
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedReconcileProgress", foreign));
            var current = fixture.observations().asList().stream().map(JsonElement::getAsJsonObject)
                    .filter(row -> row.get("seatId").getAsInt() == actor).findFirst().orElseThrow();
            assertEquals(current.getAsJsonObject("state"), input.getAsJsonObject("state"), "Control reads cannot mint or consume an input scope");
            var replacement = fixture.start(fixture.request(setup));
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedReconcileProgress", request));
            fixture.engine.dispatch("automatedSimulationStop", JSON.toJsonTree(Map.of("sessionId", text(replacement.getAsJsonObject("state"), "id"))).getAsJsonObject());

            var humanSetup = JSON.toJsonTree(fixture.engine.dispatch("matchSetup",
                    JSON.toJsonTree(Map.of("deckId", fixture.decks.get(0).substring(5))).getAsJsonObject())).getAsJsonObject();
            var human = JSON.toJsonTree(fixture.engine.dispatch("matchStart", JSON.toJsonTree(Map.of("deckId", text(humanSetup, "deckId"),
                    "revision", humanSetup.get("revision").getAsLong(), "opponents", List.of("green", "red"))).getAsJsonObject())).getAsJsonObject();
            var humanRequest = JSON.toJsonTree(Map.of("sessionId", text(human, "id"), "seatId", human.get("viewerId").getAsInt())).getAsJsonObject();
            assertThrows(IllegalArgumentException.class, () -> fixture.engine.dispatch("automatedReconcileProgress", humanRequest));
        }
    }
}
