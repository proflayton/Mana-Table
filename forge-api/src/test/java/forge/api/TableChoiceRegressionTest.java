package forge.api;

import java.util.*;
import org.testng.annotations.Test;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class TableChoiceRegressionTest {
    @Test(timeOut = 90000) public void openingLeylinesLinkTheExactHandCardsAndPutOnlyTheChosenCopiesIntoPlay() throws Exception {
        try (var game = new RulesScenario("table-choice-leylines", Map.of(), "Leyline of Sanctity", "Rhys the Redeemed")) {
            var choice = game.until(i -> text(i.prompt(), "kind").equals("choice"));
            assertEquals(choice.seat(), 0);
            var ids = new HashMap<Integer, String>();
            for (var entry : choice.prompt().getAsJsonArray("choices")) {
                var item = entry.getAsJsonObject();
                String id = text(item, "cardId");
                assertFalse(id.isEmpty(), "A table choice must link an existing occurrence");
                assertFalse(ids.containsValue(id), "Identically named cards must keep distinct identities");
                ids.put(item.get("index").getAsInt(), id);
                assertTrue(zone(viewer(choice.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .anyMatch(c -> text(c.getAsJsonObject(), "visualId").equals(id)), "Choice is present in the same hand snapshot");
                assertFalse(item.getAsJsonObject("card").has("visualId"), "Gallery faces themselves remain anonymous");
            }
            assertEquals(ids.size(), 7);
            assertTrue(game.answer(choice, Map.of("choices", List.of(1, 3))));
            RulesScenario.Input main;
            while (true) {
                main = game.until(i -> text(i.prompt(), "kind").equals("choice") || text(i.state(), "phaseKey").equals("MAIN1"));
                if (text(main.state(), "phaseKey").equals("MAIN1")) break;
                assertTrue(game.answer(main, Map.of("choices", List.of())));
            }
            var battlefield = zone(viewer(game.state(0)), "Battlefield").getAsJsonArray("cards");
            assertEquals(battlefield.size(), 2);
            assertEquals(battlefield.asList().stream().map(c -> text(c.getAsJsonObject(), "visualId")).collect(java.util.stream.Collectors.toSet()), Set.of(ids.get(1), ids.get(3)));
        }
    }
}
