package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.api.AutomatedPaymentJourneyTest.Driver;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.card.CardCollectionView;
import forge.game.player.Player;
import forge.game.zone.PlayerZone;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputConfirm;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Superseded private reads are discarded; no stale capability is promoted. */
public class AutomatedObservationTest {
    private static final Gson JSON = new Gson();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test(timeOut = 90000)
    public void aControllerReplacementDuringDiscoveryDiscardsTheWholeReadWithoutFailingOrAnsweringTheGame() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human"), "Commander", "Human");
        var bot = MatchTable.automated(deck("Policy"), "Commander", "Policy");
        bot.addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Forest")));
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES,
                Files.createTempDirectory("automated-superseded-observation"))) {
            var actor = table.game.getPlayers().get(1);
            int actorId = actor.getId();
            var session = table.automatedSeat(actorId);
            var driver = new Driver(table, actorId); table.start();
            var main = driver.until(input -> input.playerId() == actorId
                    && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1")
                    && input.state().get("activePlayerId").getAsInt() == actorId);
            HeadlessPlatform.query(table, () -> null);
            var controller = (PlayerControllerHuman) actor.getController();
            var original = controller.getInputQueue().getInput();
            var forest = zone(viewer(main.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(session.sourceActions(request(main.state(), main.prompt(),
                    Map.of("key", text(forest, "key"))))).getAsJsonObject();
            var oldAction = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            var confirm = new InputConfirm(controller, "A required engine choice", "Continue", "Cancel", true);
            var worker = Executors.newSingleThreadExecutor();
            try (var boundary = new DuringDiscovery(actor, () -> {
                try {
                    // InputQueue publishes its new real controller identity
                    // before its queued display callback can obtain state.
                    worker.submit(() -> controller.getInputQueue().setInput(confirm)).get(3, TimeUnit.SECONDS);
                } catch (Exception failure) { throw new AssertionError(failure); }
                assertSame(controller.getInputQueue().getInput(), confirm);
                assertSame(controller.getInputProxy().getInput(), confirm);
            })) {
                assertThrows(StaleObservationException.class, session::automatedObservation);
                assertTrue(boundary.fired, "The replacement must occur inside actual source discovery");
            } finally { worker.shutdownNow(); }

            HeadlessPlatform.query(table, () -> null);
            var choice = driver.until(input -> input.playerId() == actorId
                    && text(input.prompt(), "inputType").equals("InputConfirm"));
            var observed = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
            assertEquals(text(observed.getAsJsonObject("domain"), "coverage"), "NotApplicable");
            assertTrue(observed.getAsJsonObject("domain").getAsJsonArray("sources").isEmpty());
            assertSame(controller.getInputQueue().getRootInput(), original, "Discovery did not pass the priority input");
            assertEquals(actor.getManaPool().totalMana(), 0);
            assertFalse(actor.getCardsIn(ZoneType.Battlefield).get(0).isTapped());
            assertFalse(table.game.isGameOver()); assertNull(session.state().get("error"));
            assertThrows(IllegalArgumentException.class, () -> session.action(request(main.state(), main.prompt(), Map.of(
                    "action", "useSourceAction", "key", text(forest, "key"), "actionId", text(oldAction, "id")))));

            driver.answer(choice, Map.of("action", "ok"));
            var resumed = driver.until(input -> input.playerId() == actorId
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            assertSame(controller.getInputQueue().getInput(), original);
            assertNotEquals(text(resumed.prompt(), "id"), text(main.prompt(), "id"));
            var fresh = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
            assertEquals(text(fresh.getAsJsonObject("domain"), "coverage"), "Complete");
            assertEquals(JSON.toJsonTree(session.automatedObservation()), fresh,
                    "A fresh normal observation retains stable cached authority without advancing rules");
            assertThrows(IllegalArgumentException.class, () -> session.action(request(main.state(), main.prompt(), Map.of(
                    "action", "useSourceAction", "key", text(forest, "key"), "actionId", text(oldAction, "id")))));
            assertEquals(actor.getManaPool().totalMana(), 0); assertNull(session.state().get("error"));
        }
    }

    @Test(timeOut = 90000)
    public void aHumanPreviewRechecksItsActualInputAfterItsLastFundingQuery() throws Exception {
        try (var game = new RulesScenario("human-source-preview-owner", Map.of(0, List.of("Forest", "Forest", "Forest")),
                "Manalith", "Rhys the Redeemed")) {
            var main = main(game);
            HeadlessPlatform.query(game.table, () -> null);
            var actor = game.table.game.getRegisteredPlayers().get(0);
            var controller = (PlayerControllerHuman)actor.getController();
            var session = game.session(0);
            var original = controller.getInputQueue().getInput();
            var before = game.state(0);
            var spell = card(before, "Hand", "Manalith");
            var sourceRequest = request(before, before.getAsJsonObject("prompt"), Map.of("key", text(spell, "key")));
            var offered = JSON.toJsonTree(session.sourceActions(sourceRequest)).getAsJsonObject();
            var oldAction = offered.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).findFirst().orElseThrow();
            assertNotNull(oldAction.getAsJsonObject("costPreview"), "The warmed real catalog must run a mana funding preview");
            var confirm = new InputConfirm(controller, "An actual replacement during cost inspection", "Continue", "Cancel", true);
            var worker = Executors.newSingleThreadExecutor();
            try {
                HeadlessPlatform.query(game.table, () -> {
                    long revision = ((Number)session.state().get("revision")).longValue();
                    try (var boundary = new DuringDiscovery(actor, ZoneType.Battlefield, NumericDecisionProjection.class, "funding", () -> {
                        try { worker.submit(() -> controller.getInputQueue().setInput(confirm)).get(3, TimeUnit.SECONDS); }
                        catch (Exception failure) { throw new AssertionError(failure); }
                        assertSame(controller.getInputQueue().getInput(), confirm);
                        assertSame(controller.getInputProxy().getInput(), confirm);
                        assertEquals(((Number)session.state().get("revision")).longValue(), revision,
                                "The real queue replacement precedes its queued display; a revision-only fence cannot detect it");
                    })) {
                        assertThrows(IllegalArgumentException.class, () -> session.sourceActions(sourceRequest));
                        assertTrue(boundary.fired, "The warmed catalog must be replaced inside its final funding preview");
                        assertEquals(((Number)session.state().get("revision")).longValue(), revision);
                    }
                    return null;
                });
            } finally { worker.shutdownNow(); }
            var replacement = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm"));
            assertSame(controller.getInputQueue().getRootInput(), original);
            assertEquals(actor.getManaPool().totalMana(), 0);
            assertTrue(actor.getCardsIn(ZoneType.Battlefield).stream().noneMatch(forest -> forest.isTapped()));
            assertThrows(IllegalArgumentException.class, () -> session.action(request(before, before.getAsJsonObject("prompt"), Map.of(
                    "action", "useSourceAction", "key", text(spell, "key"), "actionId", text(oldAction, "id")))));
            assertTrue(game.answer(replacement, Map.of("action", "ok")));
            var resumed = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority"));
            assertSame(controller.getInputQueue().getInput(), original);
            assertNotEquals(text(resumed.prompt(), "id"), text(main.prompt(), "id"));
            var freshSpell = card(resumed.state(), "Hand", "Manalith");
            var fresh = JSON.toJsonTree(session.sourceActions(request(resumed.state(), resumed.prompt(),
                    Map.of("key", text(freshSpell, "key"))))).getAsJsonObject();
            assertTrue(fresh.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(action -> text(action, "kind").equals("cast") && !text(action, "id").equals(text(oldAction, "id"))));
            assertEquals(actor.getManaPool().totalMana(), 0);
            assertTrue(actor.getCardsIn(ZoneType.Battlefield).stream().noneMatch(forest -> forest.isTapped()));
            assertTrue(resumed.state().getAsJsonArray("stack").isEmpty());
            assertNull(session.state().get("error"));
            Files.writeString(game.evidence.resolve("human-preview-resumption.json"), JSON.toJson(Map.of(
                    "before", before, "replacement", replacement.state(), "resumed", resumed.state(), "fresh", fresh)));
        }
    }

    @Test(timeOut = 90000)
    public void aNestedSourceQueryKeepsItsCompletedDomainAndHandlesWhenTheOuterReadIsDiscarded() throws Exception {
        try (var game = RulesScenario.automated("nested-source-query-owner", Map.of(0, List.of("Forest")),
                "Manalith", "Rhys the Redeemed")) {
            main(game);
            HeadlessPlatform.query(game.table, () -> null);
            var actor = game.table.game.getRegisteredPlayers().get(0);
            var controller = (PlayerControllerHuman)actor.getController();
            var session = game.session(0);
            var original = controller.getInputQueue().getInput();
            var inner = new AtomicReference<Map<String, Object>>();
            var retained = HeadlessPlatform.query(game.table, () -> {
                var before = JSON.toJsonTree(session.state());
                try (var boundary = new DuringDiscovery(actor, () -> inner.set(session.automatedObservation()))) {
                    assertThrows(StaleObservationException.class, session::automatedObservation);
                    assertTrue(boundary.fired, "The inner read must run during the outer input's actual source discovery");
                }
                assertNotNull(inner.get());
                var recovered = session.automatedObservation();
                assertSame(recovered.get("domain"), inner.get().get("domain"),
                        "The first rejected read cannot delete or replace the already completed inner query");
                assertEquals(JSON.toJsonTree(recovered), JSON.toJsonTree(inner.get()));
                assertEquals(JSON.toJsonTree(session.state()), before);
                assertSame(controller.getInputQueue().getInput(), original);
                assertSame(controller.getInputProxy().getInput(), original);
                assertEquals(actor.getManaPool().totalMana(), 0);
                assertFalse(actor.getCardsIn(ZoneType.Battlefield).get(0).isTapped());
                return JSON.toJsonTree(recovered).getAsJsonObject();
            });
            var state = retained.getAsJsonObject("state");
            var prompt = state.getAsJsonObject("prompt");
            var forest = card(state, "Battlefield", "Forest");
            var source = retained.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "sourceCardId").equals(text(forest, "visualId")))
                    .findFirst().orElseThrow();
            var action = source.getAsJsonArray("actions").get(0).getAsJsonObject();
            var command = Map.of("action", "useSourceAction", "key", text(source, "sourceCardKey"), "actionId", text(action, "id"));
            assertTrue(game.answer(new Input(0, state, prompt), command), "The exact inner query's retained handle remains executable");
            var paid = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            assertTrue(card(paid.state(), "Battlefield", "Forest").get("tapped").getAsBoolean());
            assertEquals(actor.getManaPool().totalMana(), 1);
            assertTrue(paid.state().getAsJsonArray("stack").isEmpty());
            assertThrows(IllegalArgumentException.class, () -> session.action(request(state, prompt, command)));
            assertNull(session.state().get("error"));
            Files.writeString(game.evidence.resolve("nested-source-query-resumption.json"), JSON.toJson(Map.of(
                    "inner", retained, "paid", paid.state())));
        }
    }

    @Test
    public void onlyTheTypedPrivateObservationFailureReceivesARecoverableWireCode() {
        var observation = new JsonObject(); observation.addProperty("id", 17); observation.addProperty("method", "automatedState");
        var typed = DesktopEngine.errorReply(observation, new StaleObservationException());
        assertEquals(text(typed, "errorCode"), "observation-superseded"); assertEquals(typed.get("id").getAsInt(), 17);
        assertFalse(typed.has("result"), "A superseded batch carries no partial observations or authority");
        assertFalse(DesktopEngine.errorReply(observation,
                new IllegalArgumentException("The automated input changed during discovery")).has("errorCode"),
                "Display text cannot promote another error into a retryable read");
        var action = observation.deepCopy(); action.addProperty("method", "automatedAction");
        assertFalse(DesktopEngine.errorReply(action, new StaleObservationException()).has("errorCode"),
                "A command outcome must never be treated as a superseded observation");
    }

    private static Deck deck(String name) {
        var deck = new Deck(name); deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed")); return deck;
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                && text(next.state(), "phaseKey").equals("MAIN1") && next.state().get("activePlayerId").getAsInt() == 0);
    }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject request(JsonObject state, JsonObject prompt, Map<String, ?> answer) {
        var request = JSON.toJsonTree(answer).getAsJsonObject();
        request.addProperty("sessionId", text(state, "id")); request.addProperty("promptId", text(prompt, "id")); return request;
    }

    /** One-shot timing hook on an existing zone read; card membership and all
     * ordinary input operations use their actual production implementations. */
    private static final class DuringDiscovery implements AutoCloseable {
        private final Map<ZoneType, PlayerZone> zones;
        private final PlayerZone original;
        private final ZoneType zone;
        private boolean fired;
        DuringDiscovery(Player actor, Runnable replacement) throws Exception {
            this(actor, ZoneType.Hand, SourceActions.class, "discover", replacement);
        }
        @SuppressWarnings("unchecked")
        DuringDiscovery(Player actor, ZoneType zone, Class<?> caller, String method, Runnable replacement) throws Exception {
            var field = Player.class.getDeclaredField("zones"); field.setAccessible(true);
            this.zone = zone;
            zones = (Map<ZoneType, PlayerZone>) field.get(actor); original = zones.get(zone);
            zones.put(zone, new PlayerZone(zone, actor) {
                @Override public CardCollectionView getCards(boolean filter) {
                    var actual = original.getCards(filter);
                    if (!fired && StackWalker.getInstance().walk(frames -> frames.anyMatch(frame ->
                            frame.getClassName().equals(caller.getName()) && frame.getMethodName().equals(method)))) {
                        fired = true; close(); replacement.run();
                    }
                    return actual;
                }
            });
        }
        @Override public void close() { zones.put(zone, original); }
    }
}
