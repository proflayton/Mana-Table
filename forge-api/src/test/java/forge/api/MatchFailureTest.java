package forge.api;

import org.testng.annotations.Test;
import java.util.List;
import java.util.Map;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class MatchFailureTest {
    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void creatureTypeChoiceCanRankTokenProducersAndFinishPlayingTheLand() throws Exception {
        try (var game = new RulesScenario("creature-type-token-prototypes", Map.of(0, List.of("Adeline, Resplendent Cathar")),
                "Cavern of Souls", "Isamaru, Hound of Konda")) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1")
                    && text(i.prompt(), "inputType").equals("InputPassPriority"));
            var land = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(land, "key"))));
            var choice = game.until(i -> text(i.prompt(), "message").toLowerCase(java.util.Locale.ROOT).contains("creature type"));
            var human = choice.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(option -> text(option, "label").equals("Human")).findFirst().orElseThrow();
            assertTrue(game.answer(choice, Map.of("choices", List.of(human.get("index").getAsInt()))));
            var settled = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1")
                    && text(i.prompt(), "inputType").equals("InputPassPriority"));
            assertEquals(text(settled.state(), "status"), "playing");
            var cavern = game.table.game.getPlayers().get(0).getCardsIn(forge.game.zone.ZoneType.Battlefield).stream()
                    .filter(card -> card.getName().equals("Cavern of Souls")).findFirst().orElseThrow();
            assertEquals(cavern.getChosenType(), "Human");
        }
    }

    @Test(timeOut = 90000)
    public void failureRetainsTheLastBoardWithoutPublishingCleanupAsADefeat() throws Exception {
        try (var game = new RulesScenario("interrupted-is-not-defeat", Map.of())) {
            game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1")
                    && text(i.prompt(), "inputType").equals("InputPassPriority"));
            var before = game.table.testViews();
            game.table.fail(new IllegalStateException("Controlled engine failure"));
            game.table.publish(); // The same publication performed by the rules-thread finalizer.
            var after = game.table.testViews();
            for (int seat = 0; seat < after.size(); seat++) {
                var stopped = after.get(seat);
                assertEquals(stopped.get("status"), "error");
                assertNull(stopped.get("result"), "Shutdown concessions must not become a rules outcome");
                assertNull(stopped.get("prompt"));
                assertEquals(stopped.get("players"), before.get(seat).get("players"), "Preserve the last synchronized board for inspection");
            }
        }
    }
}
