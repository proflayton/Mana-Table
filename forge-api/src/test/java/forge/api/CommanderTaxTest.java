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
                int tappedBeforeCast = tappedMountains(input.state(), ownerId);
                var commander = card(input.state(), ownerId, "Command", COMMANDER);
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(commander, "key"))));
                input = game.until(i -> i.seat() == 0);
                int castingSteps = 0;
                while (!text(input.prompt(), "inputType").equals("InputPassPriority")) {
                    assertTrue(++castingSteps <= 16, "The commander cast must finish within its bounded payment decisions");
                    if (text(input.prompt(), "kind").equals("choice")) {
                        assertTrue(game.answer(input, Map.of("choices", List.of(0))));
                    } else if (text(input.prompt(), "inputType").startsWith("InputPayMana")) {
                        var mountain = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Mountain") && !c.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                        if (!game.answer(input, Map.of("action", "card", "key", text(mountain, "key")))) {
                            // The prior mana-source action can finish payment while
                            // this published input is being inspected. Never replay
                            // the old card handle against a new decision.
                            var fresh = game.state(input.seat()).get("prompt");
                            assertTrue(fresh == null || fresh.isJsonNull()
                                    || !text(fresh.getAsJsonObject(), "id").equals(text(input.prompt(), "id")),
                                    "A rejected mana-source payment must have a superseding prompt");
                        }
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
                assertEquals(tappedMountains(input.state(), ownerId) - tappedBeforeCast, (cast - 1) * 2,
                        "Each recast must consume exactly its published commander tax in Mountain mana");
                assertEquals(viewer(input.state()).getAsJsonObject("mana").entrySet().stream()
                        .mapToInt(entry -> entry.getValue().getAsInt()).sum(), 0,
                        "Payment must consume the mana produced for this cast");
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

    private static int tappedMountains(JsonObject state, int owner) {
        return (int) RulesScenario.zone(player(state, owner), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Mountain") && c.get("tapped").getAsBoolean()).count();
    }
}
