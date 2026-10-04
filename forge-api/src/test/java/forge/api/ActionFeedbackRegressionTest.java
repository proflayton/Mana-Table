package forge.api;

import java.util.*;
import org.testng.annotations.Test;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class ActionFeedbackRegressionTest {
    @Test(timeOut = 90000) public void ravenformProjectsItsTargetAndReportsExileAndBird() throws Exception {
        try (var game = new RulesScenario("ravenform-feedback", Map.of(0, List.of("Island", "Island", "Island"), 1, List.of("Grizzly Bears")), "Ravenform", "Rhys the Redeemed")) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertEquals(text(spell, "name"), "Ravenform");
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(spell, "key"))));
            RulesScenario.Input window = null;
            String targetId = "";
            for (int step = 0; step < 12; step++) {
                var input = game.until(i -> !i.state().getAsJsonArray("stack").isEmpty() || i.seat() == 0 &&
                        (text(i.prompt(), "kind").equals("choice") || text(i.prompt(), "inputType").startsWith("InputPayMana") || text(i.prompt(), "inputType").equals("InputSelectTargets")));
                if (!input.state().getAsJsonArray("stack").isEmpty()) { window = input; break; }
                if (text(input.prompt(), "inputType").equals("InputSelectTargets")) {
                    var target = input.state().getAsJsonArray("players").asList().stream().map(p -> p.getAsJsonObject())
                            .flatMap(p -> zone(p, "Battlefield").getAsJsonArray("cards").asList().stream()).map(c -> c.getAsJsonObject())
                            .filter(c -> text(c, "name").equals("Grizzly Bears")).findFirst().orElseThrow();
                    targetId = text(target, "visualId");
                    assertTrue(game.answer(input, Map.of("action", "card", "key", text(target, "key"))));
                } else if (text(input.prompt(), "kind").equals("choice")) {
                    var choice = input.prompt().getAsJsonArray("choices").asList().stream().map(c -> c.getAsJsonObject())
                            .filter(c -> !text(c, "label").toLowerCase(Locale.ROOT).contains("foretell")).findFirst().orElseThrow();
                    assertTrue(game.answer(input, Map.of("choices", List.of(choice.get("index").getAsInt()))));
                } else assertTrue(game.answer(input, Map.of("action", "ok")));
            }
            assertNotNull(window, "The spell must publish before resolution");
            var stack = window.state().getAsJsonArray("stack").get(0).getAsJsonObject();
            var target = stack.getAsJsonArray("targets").get(0).getAsJsonObject();
            assertEquals(text(target, "id"), targetId);
            assertEquals(text(target, "name"), "Grizzly Bears");
            assertEquals(text(target, "kind"), "card");
            assertTrue(text(stack, "text").contains("Grizzly Bears"));
            final String removedId = targetId;
            var resolved = game.until(i -> i.state().getAsJsonArray("stack").isEmpty() &&
                    i.state().getAsJsonArray("activity").asList().stream().map(e -> e.getAsJsonObject())
                            .anyMatch(e -> text(e, "kind").equals("resolved") && text(e, "cardName").equals("Ravenform")));
            var history = resolved.state().getAsJsonArray("activity").asList().stream().map(e -> e.getAsJsonObject()).toList();
            assertTrue(history.stream().anyMatch(e -> text(e, "kind").equals("moved") && text(e, "cardId").equals(removedId) && text(e, "message").contains("exile")));
            assertTrue(history.stream().anyMatch(e -> text(e, "kind").equals("arrived") && text(e, "message").toLowerCase(Locale.ROOT).contains("bird token entered")));
            assertTrue(history.stream().anyMatch(e -> text(e, "kind").equals("resolved") && text(e, "detail").contains("Grizzly Bears")), "Keep the actual resolved spell description for review");
            assertTrue(history.stream().anyMatch(e -> text(e, "kind").equals("resolved") && text(e, "detail").contains("1/1 blue Bird")), "The explanation includes the token creation clause");
            var victim = player(resolved.state(), game.state(1).get("viewerId").getAsInt());
            assertEquals(count(victim, "Exile"), 1);
            var bird = zone(victim, "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertEquals(text(bird, "name"), "Bird Token");
            assertEquals(bird.get("power").getAsInt(), 1);
        }
    }
}
