package forge.api;

import com.google.gson.JsonObject;
import com.google.gson.Gson;
import org.testng.annotations.Test;
import java.time.Duration;
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
                        if (game.answer(input, Map.of("action", "card", "key", text(mountain, "key")))) {
                            awaitManaProgress(game, input, ownerId, text(mountain, "visualId"),
                                    tappedBeforeCast + (cast - 1) * 2);
                        } else {
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

    private static void awaitManaProgress(RulesScenario game, RulesScenario.Input answered,
                                          int ownerId, String sourceId, int expectedTapped) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        JsonObject fresh;
        do {
            fresh = game.state(answered.seat());
            if (manaProgressed(answered, fresh, ownerId, sourceId, expectedTapped)) return;
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        fail("The accepted mana-source action must finish before another payment decision: " + fresh);
    }

    private static boolean manaProgressed(RulesScenario.Input answered, JsonObject fresh,
                                          int ownerId, String sourceId, int expectedTapped) {
        // Activating a mana source queues game-thread work. A new prompt UUID can
        // arrive after the source taps but before that work pays the remaining cost.
        // Wait for the cost display to refresh; after the last required tap, wait
        // for payment to retire rather than trying to find a seventh Mountain.
        var source = zone(player(fresh, ownerId), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "visualId").equals(sourceId)).findFirst();
        if (source.isEmpty() || !source.get().get("tapped").getAsBoolean()) return false;
        int tapped = tappedMountains(fresh, ownerId);
        assertTrue(tapped <= expectedTapped, "Payment must not tap more sources than this cast costs");
        var value = fresh.get("prompt");
        if (value == null || value.isJsonNull()) {
            return fresh.getAsJsonArray("stack").asList().stream().map(item -> item.getAsJsonObject())
                    .anyMatch(item -> text(item, "name").equals(COMMANDER));
        }
        var prompt = value.getAsJsonObject();
        if (!text(prompt, "inputType").startsWith("InputPayMana")) return true;
        return tapped < expectedTapped
                && !text(prompt, "id").equals(text(answered.prompt(), "id"))
                && !text(prompt, "message").equals(text(answered.prompt(), "message"));
    }

    @Test
    public void manaSourceProgressRejectsIntermediatePaymentPublications() {
        var before = paymentFrame("InputPayManaOfCostPayment", "before", "Pay Mana Cost: {1}", 5);
        var answered = new RulesScenario.Input(0, before, before.getAsJsonObject("prompt"));
        var tappedButUnpaid = paymentFrame("InputPayManaOfCostPayment", "new-id", "Pay Mana Cost: {1}", 6);
        assertEquals(tappedMountains(tappedButUnpaid, 0), 6,
                "The failing publication has no untapped source for the old fixture to select");
        assertFalse(manaProgressed(answered, tappedButUnpaid, 0, "mountain-5", 6),
                "A changed prompt ID and all sources tapped do not prove payment has finished");
        assertTrue(manaProgressed(answered,
                paymentFrame("InputPassPriority", "priority", "Priority", 6), 0, "mountain-5", 6));

        before = paymentFrame("InputPayManaOfCostPayment", "before", "Pay Mana Cost: {2}", 4);
        answered = new RulesScenario.Input(0, before, before.getAsJsonObject("prompt"));
        assertFalse(manaProgressed(answered,
                paymentFrame("InputPayManaOfCostPayment", "new-id", "Pay Mana Cost: {2}", 5), 0, "mountain-4", 6),
                "An intermediate tap must not trigger a second source action before its cost is applied");
        assertTrue(manaProgressed(answered,
                paymentFrame("InputPayManaOfCostPayment", "new-id", "Pay Mana Cost: {1}", 5), 0, "mountain-4", 6));
    }

    private static JsonObject paymentFrame(String type, String id, String message, int tapped) {
        var mountains = new ArrayList<Map<String, Object>>();
        for (int index = 0; index < 6; index++) mountains.add(Map.of(
                "name", "Mountain", "visualId", "mountain-" + index, "tapped", index < tapped));
        return new Gson().toJsonTree(Map.of("players", List.of(Map.of("id", 0,
                        "zones", List.of(Map.of("name", "Battlefield", "cards", mountains)))),
                "prompt", Map.of("id", id, "inputType", type, "message", message), "stack", List.of())).getAsJsonObject();
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
