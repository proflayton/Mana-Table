package forge.api;

import com.google.gson.JsonObject;
import com.google.common.eventbus.Subscribe;
import forge.game.event.GameEventGameFinished;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.Map;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class PregameConcessionTest {
    @DataProvider public Object[][] decisions() {
        return new Object[][] {{"starting-player"}, {"keep-hand"}, {"opening-choice"}, {"opening-payment"}};
    }

    @Test(dataProvider = "decisions", timeOut = 90000)
    public void oneDepartureDoesNotStrandTheSurvivingPlayers(String decision) throws Exception {
        String mainCard = decision.equals("opening-payment") ? "Gemstone Caverns"
                : decision.equals("opening-choice") ? "Leyline of Sanctity" : "Forest";
        try (var scenario = new RulesScenario("pregame-concession-" + decision, Map.of(), mainCard, "Rhys the Redeemed")) {
            var departure = scenario.until(input -> decision.equals("starting-player")
                    ? text(input.prompt(), "inputType").equals("InputSelectEntitiesFromList")
                    : decision.equals("keep-hand") ? text(input.prompt(), "inputType").equals("InputConfirmMulligan")
                    : text(input.prompt(), "kind").equals("choice"));
            if (decision.equals("opening-payment")) {
                int owner = departure.seat();
                assertTrue(scenario.answer(departure, Map.of("choices", List.of(0))));
                departure = scenario.until(input -> input.seat() == owner
                        && text(input.prompt(), "inputType").equals("InputSelectEntitiesFromList")
                        && text(input.prompt(), "sourceZone").equals("Battlefield"));
                assertEquals(text(departure.prompt().getAsJsonObject("sourceCard"), "name"), "Gemstone Caverns");
                assertFalse(departure.prompt().get("okEnabled").getAsBoolean());
                assertFalse(departure.prompt().get("cancelEnabled").getAsBoolean());
                assertTrue(zone(viewer(departure.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .anyMatch(card -> card.getAsJsonObject().get("selectable").getAsBoolean()));
                assertEquals(count(viewer(departure.state()), "Battlefield"), 1,
                        "Gemstone Caverns entered before its required exile choice");
            }
            int departed = departure.seat();
            int departedId = departure.state().get("viewerId").getAsInt();
            assertEquals(departure.state().get("turn").getAsInt(), 0);
            var concede = new JsonObject();
            concede.addProperty("sessionId", text(departure.state(), "id"));
            scenario.table.seat(departed).concede(concede);

            var after = scenario.state(departed);
            assertTrue(viewer(after).get("eliminated").getAsBoolean());
            assertTrue(after.get("prompt").isJsonNull(), "A departed player cannot retain a decision");
            var stale = new JsonObject();
            stale.addProperty("sessionId", text(departure.state(), "id"));
            stale.addProperty("promptId", text(departure.prompt(), "id"));
            stale.addProperty("action", "ok");
            assertThrows(IllegalArgumentException.class, () -> scenario.table.seat(departed).action(stale));

            RulesScenario.Input main;
            while (true) {
                main = scenario.until(input -> text(input.prompt(), "kind").equals("choice")
                        || text(input.state(), "phaseKey").equals("MAIN1"));
                if (text(main.state(), "phaseKey").equals("MAIN1")) break;
                assertTrue(scenario.answer(main, Map.of("choices", List.of())));
            }
            assertNotEquals(main.state().get("activePlayerId").getAsInt(), departedId);
            assertEquals(scenario.table.game.getPlayers().size(), 3);
            for (int seat = 0; seat < 4; seat++) {
                var state = scenario.state(seat);
                assertEquals(viewer(state).get("eliminated").getAsBoolean(), seat == departed);
                assertEquals(state.get("turn").getAsInt(), 1);
                if (seat == departed) assertTrue(state.get("prompt").isJsonNull());
            }
        }
    }

    @Test(timeOut = 90000)
    public void aDepartingRevealViewerDoesNotCancelAnotherPlayersOpeningAbility() throws Exception {
        try (var scenario = new RulesScenario("pregame-concession-reveal-viewer", Map.of(), "Chancellor of the Annex", "Rhys the Redeemed")) {
            var opening = scenario.until(input -> text(input.prompt(), "kind").equals("choice"));
            assertTrue(scenario.answer(opening, Map.of("choices", List.of(0))));
            var reveal = scenario.until(input -> text(input.prompt(), "kind").equals("reveal"));
            assertNotEquals(reveal.seat(), opening.seat(), "The reveal is acknowledged by another player");
            var concede = new JsonObject();
            concede.addProperty("sessionId", text(reveal.state(), "id"));
            scenario.table.seat(reveal.seat()).concede(concede);

            RulesScenario.Input main;
            while (true) {
                main = scenario.until(input -> text(input.prompt(), "kind").equals("choice")
                        || text(input.prompt(), "kind").equals("reveal") || text(input.state(), "phaseKey").equals("MAIN1"));
                if (text(main.state(), "phaseKey").equals("MAIN1")) break;
                assertNotEquals(main.seat(), reveal.seat(), "Departed viewers receive no further decisions");
                assertTrue(scenario.answer(main, text(main.prompt(), "kind").equals("reveal")
                        ? Map.of() : Map.of("choices", List.of())));
            }
            assertEquals(scenario.table.game.getPlayers().size(), 3);
            assertEquals(main.state().get("activePlayerId").getAsInt(), opening.state().get("viewerId").getAsInt());
            assertTrue(viewer(scenario.state(reveal.seat())).get("eliminated").getAsBoolean());
            assertTrue(scenario.state(reveal.seat()).get("prompt").isJsonNull());
        }
    }

    @Test(timeOut = 90000)
    public void gameEndingDuringOpeningChoiceReleasesTheGameThread() throws Exception {
        try (var scenario = new RulesScenario("pregame-concession-terminal-choice", Map.of(), "Leyline of Sanctity", "Rhys the Redeemed")) {
            var choice = scenario.until(input -> text(input.prompt(), "kind").equals("choice"));
            var finished = new CountDownLatch(1);
            var listener = new Object() {
                @Subscribe public void gameFinished(GameEventGameFinished event) { finished.countDown(); }
            };
            scenario.table.game.subscribeToEvents(listener);
            try {
                for (int seat = 0; seat < 4; seat++) {
                    if (seat == choice.seat()) continue;
                    var concede = new JsonObject();
                    concede.addProperty("sessionId", text(choice.state(), "id"));
                    scenario.table.seat(seat).concede(concede);
                }
                assertTrue(finished.await(5, TimeUnit.SECONDS), "The cancelled dialog must release the game thread");
                for (int seat = 0; seat < 4; seat++) {
                    var state = scenario.table.seat(seat).state();
                    assertEquals(state.get("status"), "finished");
                    assertEquals(state.get("result"), seat == choice.seat() ? "Victory" : "Defeat");
                    assertNull(state.get("prompt"));
                }
            } finally { scenario.table.game.unsubscribeFromEvents(listener); }
        }
    }
}
