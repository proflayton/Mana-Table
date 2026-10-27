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
    private static JsonObject request(JsonObject state, JsonObject prompt, Map<String, ?> answer) {
        var request = JSON.toJsonTree(answer).getAsJsonObject();
        request.addProperty("sessionId", text(state, "id")); request.addProperty("promptId", text(prompt, "id")); return request;
    }

    /** One-shot timing hook on an existing zone read; card membership and all
     * ordinary input operations use their actual production implementations. */
    private static final class DuringDiscovery implements AutoCloseable {
        private final Map<ZoneType, PlayerZone> zones;
        private final PlayerZone original;
        private boolean fired;
        @SuppressWarnings("unchecked")
        DuringDiscovery(Player actor, Runnable replacement) throws Exception {
            var field = Player.class.getDeclaredField("zones"); field.setAccessible(true);
            zones = (Map<ZoneType, PlayerZone>) field.get(actor); original = zones.get(ZoneType.Hand);
            zones.put(ZoneType.Hand, new PlayerZone(ZoneType.Hand, actor) {
                @Override public CardCollectionView getCards(boolean filter) {
                    var actual = original.getCards(filter);
                    if (!fired && StackWalker.getInstance().walk(frames -> frames.anyMatch(frame ->
                            frame.getClassName().equals(SourceActions.class.getName()) && frame.getMethodName().equals("discover")))) {
                        fired = true; close(); replacement.run();
                    }
                    return actual;
                }
            });
        }
        @Override public void close() { zones.put(ZoneType.Hand, original); }
    }
}
