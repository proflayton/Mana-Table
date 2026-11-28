package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class IcetillExplorerRegressionTest {
    @DataProvider public Object[][] battlefields() {
        return new Object[][] {{0, false}, {60, true}};
    }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream()
                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals(name))
                .findFirst().orElseThrow(() -> new AssertionError(name + " missing from " + zone));
    }

    @Test(dataProvider = "battlefields", timeOut = 90000)
    public void millingVisibleHydraReturnsPriorityAndUpdatesLibraryForEverySeat(int creatures, boolean departures) throws Exception {
        // Oboro supplies a land play without adding random non-Hydras to the library.
        var battlefield = new ArrayList<>(List.of("Icetill Explorer", "Elven Chorus", "Oboro, Palace in the Clouds"));
        battlefield.addAll(Collections.nCopies(creatures, "Grizzly Bears"));
        if (creatures > 0) battlefield.addAll(List.of("Scute Swarm", "Tireless Tracker", "Fervor"));
        try (var game = new RulesScenario("icetill-elven-chorus-" + creatures, Map.of(0, battlefield),
                "Mossborn Hydra", "Rhys the Redeemed")) {
            game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            if (departures) for (int seat : List.of(2, 3)) {
                var request = new JsonObject();
                request.addProperty("sessionId", text(game.state(seat), "id"));
                game.table.seat(seat).concede(request);
                awaitConcession(game.table, game.table.seat(seat));
            }
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            var before = zone(viewer(main.state()), "Library");
            var oldTop = before.getAsJsonObject("topCard");
            assertEquals(text(oldTop, "name"), "Mossborn Hydra");
            int librarySize = before.get("count").getAsInt();
            int graveyardSize = count(viewer(main.state()), "Graveyard");
            var oboro = card(main.state(), "Battlefield", "Oboro, Palace in the Clouds");
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(oboro, "key"))));
            var ability = game.until(i -> i.seat() == 0);
            assertEquals(text(ability.prompt(), "kind"), "choice");
            var bounce = ability.prompt().getAsJsonArray("choices").asList().stream()
                    .map(c -> c.getAsJsonObject()).filter(c -> text(c, "label").contains("Return"))
                    .findFirst().orElseThrow();
            assertTrue(game.answer(ability, Map.of("choices", List.of(bounce.get("index").getAsInt()))));
            var payment = game.until(i -> i.seat() == 0);
            assertEquals(text(payment.prompt(), "inputType"), "InputPayManaOfCostPayment");
            oboro = card(payment.state(), "Battlefield", "Oboro, Palace in the Clouds");
            assertTrue(game.answer(payment, Map.of("action", "card", "key", text(oboro, "key"))));
            RulesScenario.Input returned;
            long paymentDeadline = System.nanoTime() + java.time.Duration.ofSeconds(10).toNanos();
            do {
                returned = game.until(i -> i.seat() == 0 && (text(i.prompt(), "inputType").equals("InputPayManaOfCostPayment")
                        || text(i.prompt(), "inputType").equals("InputPassPriority")
                        && i.state().getAsJsonArray("stack").isEmpty()
                        && zone(viewer(i.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .anyMatch(c -> text(c.getAsJsonObject(), "name").equals("Oboro, Palace in the Clouds"))));
                if (!text(returned.prompt(), "inputType").equals("InputPayManaOfCostPayment")) break;
                assertTrue(System.nanoTime() < paymentDeadline, "The queued mana activation must complete");
                Thread.sleep(5);
            } while (true);
            oboro = card(returned.state(), "Hand", "Oboro, Palace in the Clouds");
            assertTrue(game.answer(returned, Map.of("action", "card", "key", text(oboro, "key"))));
            RulesScenario.Input resolved;
            do {
                resolved = game.until(i -> text(i.prompt(), "kind").equals("choice")
                        || text(i.prompt(), "inputType").equals("InputPassPriority")
                        && count(player(i.state(), main.state().get("viewerId").getAsInt()), "Graveyard") > graveyardSize
                        && i.state().getAsJsonArray("stack").isEmpty());
                if (!text(resolved.prompt(), "kind").equals("choice")) break;
                assertTrue(resolved.prompt().get("ordered").getAsBoolean(), "Only trigger ordering is expected");
                var order = resolved.prompt().getAsJsonArray("choices").asList().stream()
                        .map(c -> c.getAsJsonObject().get("index").getAsInt()).toList();
                assertTrue(game.answer(resolved, Map.of("choices", order)));
            } while (true);
            int owner = main.state().get("viewerId").getAsInt();
            for (int seat = 0; seat < 4; seat++) {
                var state = game.state(seat);
                var player = player(state, owner);
                assertEquals(count(player, "Library"), librarySize - 1);
                assertEquals(count(player, "Graveyard"), graveyardSize + 1);
                var library = zone(player, "Library");
                if (seat == 0) {
                    var newTop = library.getAsJsonObject("topCard");
                    assertEquals(text(newTop, "name"), "Mossborn Hydra");
                    assertNotEquals(text(newTop, "visualId"), text(oldTop, "visualId"));
                } else {
                    assertTrue(library.get("topCard").isJsonNull(), "Elven Chorus reveals only to its controller");
                    assertTrue(library.getAsJsonArray("cards").isEmpty());
                }
            }
            assertTrue(game.answer(resolved, Map.of("action", "ok")), "Priority remains usable after milling");
        }
    }
}
