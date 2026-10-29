package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.card.CardView;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.LinkedHashMap;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class PromptScopeRegressionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @DataProvider public Object[][] decisions() {
        return new Object[][] {{"starting-player"}, {"mulligan"}, {"priority"}};
    }

    @Test(dataProvider = "decisions", timeOut = 90000)
    public void unrelatedGuiCallbacksKeepAnUnansweredDecisionUsable(String decision) throws Exception {
        try (var game = scenario("prompt-scope-" + decision)) {
            var input = game.until(next -> decision.equals("starting-player")
                    ? text(next.prompt(), "inputType").equals("InputSelectEntitiesFromList")
                    : decision.equals("mulligan") ? text(next.prompt(), "inputType").equals("InputConfirmMulligan")
                    : next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            var retained = game.table.seat(input.seat()).state();
            String originalId = text(input.prompt(), "id");
            var handles = handles(input.state());
            var session = game.table.seat(input.seat());

            // HeadlessPlatform publishes every seat after each GUI runnable.
            // This deliberately refreshes the display without answering input.
            HeadlessPlatform.later(() -> {});
            HeadlessPlatform.later(() -> session.gui().showPromptMessage(
                    game.table.game.getRegisteredPlayers().get(input.seat()).getView(), "Refreshed decision text", (CardView) null));
            flushGui();
            var refreshed = game.state(input.seat());
            assertEquals(text(refreshed.getAsJsonObject("prompt"), "id"), originalId);
            assertEquals(text(refreshed.getAsJsonObject("prompt"), "message"), "Refreshed decision text",
                    "Keeping an input scope must still refresh its visible feedback");
            assertEquals(handles(refreshed), handles, "Unrelated refreshes preserve the original card handles");
            assertEquals(((Map<?, ?>) retained.get("prompt")).get("id"), originalId);
            assertEquals(((Map<?, ?>) retained.get("prompt")).get("message"), text(input.prompt(), "message"),
                    "Refreshing the current prompt cannot mutate retained snapshots");

            Map<String, ?> answer = decision.equals("starting-player")
                    ? Map.of("action", "player", "playerId", game.table.game.getRegisteredPlayers().get(0).getId())
                    : decision.equals("priority") ? land(input.state()) : Map.of("action", "ok");
            assertTrue(game.answer(input, answer), "The originally displayed decision remains answerable");
            assertThrows(IllegalArgumentException.class, () -> session.action(request(input, answer)));
            var advanced = game.until(next -> !text(next.prompt(), "id").equals(originalId));
            assertNotEquals(text(advanced.prompt(), "id"), originalId, "An actual answer creates a fresh decision scope");
            if (decision.equals("priority")) assertEquals(forests(game.state(0)), 1L, "The original card gesture plays one land");
        }
    }

    @Test(timeOut = 90000)
    public void anAcceptedQueuedGestureCannotBeRepublishedBeforeItExecutes() throws Exception {
        try (var game = scenario("prompt-scope-queued-action")) {
            game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            var entered = new CountDownLatch(1);
            var releaseFirst = new CountDownLatch(1);
            var releaseProbe = new CountDownLatch(1);
            var observed = new CompletableFuture<JsonObject>();
            try {
                HeadlessPlatform.later(() -> { entered.countDown(); await(releaseFirst); });
                assertTrue(entered.await(5, TimeUnit.SECONDS));
                var heldState = game.state(0);
                var held = new RulesScenario.Input(0, heldState, heldState.getAsJsonObject("prompt"));
                // Both callbacks are ahead of the action in the same real GUI
                // queue. The probe runs after the first callback's publication.
                HeadlessPlatform.later(() -> {});
                HeadlessPlatform.later(() -> {
                    observed.complete(JSON.toJsonTree(game.table.seat(0).state()).getAsJsonObject());
                    await(releaseProbe);
                });
                var answer = land(held.state());
                assertTrue(game.answer(held, answer));
                releaseFirst.countDown();
                var queued = observed.get(5, TimeUnit.SECONDS);
                assertEquals(text(queued, "status"), "resolving");
                assertTrue(queued.get("prompt").isJsonNull(), "An accepted input stays consumed while its gesture is queued");
                assertEquals(forests(queued), 0L, "The probe must precede the actual card gesture");
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(held, answer)));
                releaseProbe.countDown();
                var advanced = game.until(next -> next.seat() == 0 && priority(next) && forests(next.state()) == 1);
                assertNotEquals(text(advanced.prompt(), "id"), text(held.prompt(), "id"));
                assertEquals(forests(advanced.state()), 1L);
                assertEquals(count(viewer(advanced.state()), "Hand"), count(viewer(held.state()), "Hand") - 1);
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(held, answer)));
            } finally { releaseFirst.countDown(); releaseProbe.countDown(); }
        }
    }

    @Test(timeOut = 90000)
    public void aDepartureCannotReassignSurvivingOrRemovedCardHandles() throws Exception {
        try (var game = new RulesScenario("prompt-scope-departure", Map.of(
                0, List.of("Spellbook", "Memnite", "Ornithopter"), 1, List.of("Spellbook"),
                2, List.of("Spellbook"), 3, List.of("Spellbook")))) {
            // Seat zero's visible cards precede seat one's own hand in its
            // projection. Removing them would shift an ordinal key allocator.
            var input = game.until(next -> next.seat() == 1 && priority(next)
                    && text(next.state(), "phaseKey").equals("MAIN1")
                    && next.state().get("activePlayerId").equals(next.state().get("viewerId")));
            String originalId = text(input.prompt(), "id");
            var ownCard = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var departed = player(input.state(), game.table.game.getRegisteredPlayers().get(0).getId());
            var removed = zone(departed, "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            String originalKey = text(ownCard, "key"), ownIdentity = text(ownCard, "visualId");
            String removedKey = text(removed, "key");
            assertTrue(ownCard.get("selectable").getAsBoolean(), "The retained handle is a legal own-turn land play");
            assertNotEquals(input.state().get("activePlayerId").getAsInt(), departed.get("id").getAsInt());
            var concede = new JsonObject();
            concede.addProperty("sessionId", text(input.state(), "id"));
            game.table.seat(0).concede(concede);
            awaitConcession(game.table, game.table.seat(0));
            flushGui();
            var after = game.state(1);
            assertEquals(text(after.getAsJsonObject("prompt"), "id"), originalId);
            assertEquals(handles(after).get(ownIdentity), originalKey, "The retained own handle still denotes the same physical occurrence");
            assertFalse(handles(after).containsValue(removedKey), "A removed card's key cannot be reassigned to another visible card");
            var rejected = expectThrows(IllegalArgumentException.class, () -> game.table.seat(1).action(
                    request(input, Map.of("action", "card", "key", removedKey))));
            assertEquals(rejected.getMessage(), "Card is not visible in this prompt");
            assertEquals(text(game.state(1).getAsJsonObject("prompt"), "id"), originalId,
                    "Rejecting a removed handle cannot consume the surviving decision");
            assertTrue(game.answer(input, Map.of("action", "card", "key", originalKey)));
            var played = game.until(next -> next.seat() == 1 && priority(next)
                    && zone(viewer(next.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(card -> text(card.getAsJsonObject(), "visualId").equals(ownIdentity)));
            assertEquals(forests(played.state()), 1L, "The original handle played its exact Forest once");
        }
    }

    private static RulesScenario scenario(String name) throws Exception {
        return new RulesScenario(name, Map.of(0, List.of("Spellbook"), 1, List.of("Spellbook"),
                2, List.of("Spellbook"), 3, List.of("Spellbook")));
    }

    private static void flushGui() throws Exception {
        var done = new CompletableFuture<Void>();
        HeadlessPlatform.later(() -> done.complete(null));
        done.get(5, TimeUnit.SECONDS);
    }

    private static void await(CountDownLatch latch) {
        var game = forge.game.GameAction.currentGame();
        try (var suspended = game == null ? null : game.getAction().suspendState()) {
            if (!latch.await(10, TimeUnit.SECONDS)) throw new IllegalStateException("The regression GUI barrier did not release");
        }
        catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); throw new IllegalStateException(interrupted); }
    }

    private static boolean priority(RulesScenario.Input input) {
        return text(input.prompt(), "inputType").equals("InputPassPriority");
    }

    private static Map<String, String> handles(JsonObject state) {
        var handles = new LinkedHashMap<String, String>();
        for (var player : state.getAsJsonArray("players")) for (var zone : player.getAsJsonObject().getAsJsonArray("zones"))
            for (var value : zone.getAsJsonObject().getAsJsonArray("cards")) {
                var card = value.getAsJsonObject();
                String identity = text(card, "visualId"), key = text(card, "key");
                assertFalse(identity.isEmpty(), "Every projected occurrence has a visual identity");
                assertFalse(key.isEmpty(), "An active input provides an opaque card handle");
                assertNull(handles.put(identity, key), "The same occurrence cannot appear twice in one projection");
            }
        return handles;
    }

    private static long forests(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream()
                .filter(card -> text(card.getAsJsonObject(), "name").equals("Forest")).count();
    }

    private static Map<String, ?> land(JsonObject state) {
        var card = zone(viewer(state), "Hand").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals("Forest") && value.get("selectable").getAsBoolean()).findFirst().orElseThrow();
        return Map.of("action", "card", "key", text(card, "key"));
    }

    private static JsonObject request(RulesScenario.Input input, Map<String, ?> answer) {
        var request = JSON.toJsonTree(answer).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id"));
        request.addProperty("promptId", text(input.prompt(), "id"));
        return request;
    }
}
