package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.Test;
import java.util.*;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class CommanderTaxTest {
    private static final String COMMANDER = "Rograkh, Son of Rohgahh";

    @Test(timeOut = 90000)
    public void castsAndDeathsPublishTaxToEverySeat() throws Exception {
        var permanents = new ArrayList<>(Collections.nCopies(6, "Mountain"));
        permanents.addAll(Collections.nCopies(2, "Seal of Fire"));
        try (var game = new RulesScenario("commander-tax", Map.of(0, permanents), "Mountain", COMMANDER)) {
            var input = priority(game);
            int ownerId = input.state().get("viewerId").getAsInt();
            assertEquals(card(input.state(), ownerId, "Command", COMMANDER).get("commanderTax").getAsInt(), 0);
            for (int cast = 1; cast <= 3; cast++) {
                var commander = card(input.state(), ownerId, "Command", COMMANDER);
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(commander, "key"))));
                input = game.until(i -> i.seat() == 0);
                while (!text(input.prompt(), "inputType").equals("InputPassPriority")) {
                    if (text(input.prompt(), "kind").equals("choice")) {
                        assertTrue(game.answer(input, Map.of("choices", List.of(0))));
                    } else if (text(input.prompt(), "inputType").startsWith("InputPayMana")) {
                        var mountain = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Mountain") && !c.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                        assertTrue(game.answer(input, Map.of("action", "card", "key", text(mountain, "key"))));
                    } else {
                        assertTrue(input.prompt().get("okEnabled").getAsBoolean(), input.prompt().toString());
                        assertTrue(game.answer(input, Map.of("action", "ok")));
                    }
                    input = game.until(i -> i.seat() == 0);
                }
                if (!input.state().getAsJsonArray("stack").isEmpty()) {
                    assertEquals(input.state().getAsJsonArray("stack").get(0).getAsJsonObject().getAsJsonObject("card").get("commanderTax").getAsInt(), cast * 2);
                    assertTrue(game.answer(input, Map.of("action", "ok")));
                    input = priority(game);
                }
                for (int seat = 0; seat < 4; seat++) {
                    var view = game.state(seat);
                    assertEquals(card(view, ownerId, "Battlefield", COMMANDER).get("commanderTax").getAsInt(), cast * 2);
                    var other = view.getAsJsonArray("players").asList().stream().map(p -> p.getAsJsonObject())
                            .filter(p -> p.get("id").getAsInt() != ownerId).findFirst().orElseThrow();
                    assertEquals(zone(other, "Command").getAsJsonArray("cards").get(0).getAsJsonObject().get("commanderTax").getAsInt(), 0);
                }
                if (cast == 3) break;
                var seal = card(input.state(), ownerId, "Battlefield", "Seal of Fire");
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(seal, "key"))));
                var target = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectTargets"));
                commander = card(target.state(), ownerId, "Battlefield", COMMANDER);
                assertTrue(game.answer(target, Map.of("action", "card", "key", text(commander, "key"))));
                input = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputPassPriority")
                        && count(viewer(i.state()), "Command") == 1);
                assertEquals(card(input.state(), ownerId, "Command", COMMANDER).get("commanderTax").getAsInt(), cast * 2,
                        "Dying and returning to command do not add tax");
            }
        }
    }

    private static RulesScenario.Input priority(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1")
                && text(i.prompt(), "inputType").equals("InputPassPriority") && i.state().getAsJsonArray("stack").isEmpty());
    }

    private static JsonObject card(JsonObject state, int owner, String zone, String name) {
        return RulesScenario.zone(player(state, owner), zone).getAsJsonArray("cards").asList().stream()
                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals(name)).findFirst().orElseThrow();
    }
}
