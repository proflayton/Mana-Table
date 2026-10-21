package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.card.mana.ManaAtom;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.time.Duration;
import java.util.*;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Exact public mana at real production, payment, priority and step boundaries. */
public class ManaPoolLifecycleTest {
    private static final com.google.gson.Gson JSON = new GsonBuilder().setPrettyPrinting().serializeNulls().create();
    private static final Set<String> COLORS = Set.of("W", "U", "B", "R", "G", "C");

    @DataProvider public Object[][] retention() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "retention", timeOut = 90000)
    public void publicFloatingManaSurvivesPriorityPaysCostsAndHonorsStepRetention(boolean keepMana) throws Exception {
        var permanents = keepMana ? List.of("Sol Ring", "Upwelling") : List.of("Sol Ring");
        try (var game = new RulesScenario("mana-pool-" + (keepMana ? "retained" : "ordinary"),
                Map.of(0, permanents), "Expedition Map", "Rhys the Redeemed")) {
            var stages = new LinkedHashMap<String, Object>();
            var main = game.until(input -> input.seat() == 0 && priority(input)
                    && text(input.state(), "phaseKey").equals("MAIN1") && input.state().getAsJsonArray("stack").isEmpty());
            int owner = main.state().get("viewerId").getAsInt();
            int turn = main.state().get("turn").getAsInt();
            observe(game, stages, "empty", owner, 0);

            String originalPrompt = text(main.prompt(), "id");
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(card(main.state(), "Battlefield", "Sol Ring"), "key"))));
            var floating = game.until(input -> input.seat() == 0 && priority(input)
                    && !text(input.prompt(), "id").equals(originalPrompt));
            assertEquals(text(floating.state(), "phaseKey"), "MAIN1");
            assertTrue(floating.state().getAsJsonArray("stack").isEmpty(), "The real mana ability resolves without using the stack");
            assertTrue(card(floating.state(), "Battlefield", "Sol Ring").get("tapped").getAsBoolean());
            assertEquals(game.table.game.getPlayers().get(0).getManaPool().getAmountOfColor((byte) ManaAtom.COLORLESS), 2,
                    "The engine has actually produced two colorless mana");
            assertEquals(colorless(floating.state(), owner), 2, "The public projection must use the colorless mana atom, not the absence-of-color card mask");
            observe(game, stages, "floated", owner, 2);
            var retained = game.table.seat(0).state();
            String retainedJson = JSON.toJson(retained);

            String mapId = text(card(floating.state(), "Hand", "Expedition Map"), "visualId");
            int handBefore = count(viewer(floating.state()), "Hand");
            assertTrue(game.answer(floating, Map.of("action", "card", "key", text(card(floating.state(), "Hand", "Expedition Map"), "key"))));
            var casting = game.until(input -> input.seat() == 0 && (text(input.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(input) && mapOnStack(input.state())));
            if (text(casting.prompt(), "inputType").startsWith("InputPayMana")) {
                assertTrue(game.answer(casting, Map.of("action", "ok")), "One Auto payment spends the already-floating mana");
                // Observe the next semantic decision before issuing another input.
                // Intermediate Auto-payment publications are not a second payment.
                casting = awaitCastPriority(game, text(casting.prompt(), "id"));
            }
            assertEquals(colorless(casting.state(), owner), 1, "Casting a one-mana artifact consumes exactly one of the two colorless mana");
            assertEquals(count(viewer(casting.state()), "Hand"), handBefore - 1);
            observe(game, stages, "paid", owner, 1);

            assertTrue(game.answer(casting, Map.of("action", "ok")));
            var opponent = game.until(input -> input.seat() == 1 && priority(input) && mapOnStack(input.state()));
            assertEquals(text(opponent.state(), "phaseKey"), "MAIN1");
            assertEquals(opponent.state().get("turn").getAsInt(), turn);
            observe(game, stages, "opponent-priority", owner, 1);

            String castPrompt = text(casting.prompt(), "id");
            var resolved = game.until(input -> input.seat() == 0 && priority(input)
                    && !text(input.prompt(), "id").equals(castPrompt) && input.state().getAsJsonArray("stack").isEmpty()
                    && hasOccurrence(input.state(), "Battlefield", mapId));
            assertEquals(text(resolved.state(), "phaseKey"), "MAIN1", "Resolving the spell does not end the step or empty the pool");
            assertEquals(resolved.state().get("turn").getAsInt(), turn);
            assertTrue(resolved.state().getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(event -> text(event, "kind").equals("resolved") && text(event, "cardName").equals("Expedition Map")));
            observe(game, stages, "resolved", owner, 1);

            var nextStep = game.until(input -> input.seat() == 0 && priority(input)
                    && !text(input.state(), "phaseKey").equals("MAIN1") && input.state().get("turn").getAsInt() == turn);
            assertTrue(nextStep.state().getAsJsonArray("stack").isEmpty());
            observe(game, stages, "next-step", owner, keepMana ? 1 : 0);
            assertEquals(JSON.toJson(retained), retainedJson, "A later payment and step transition cannot mutate the retained floating-mana snapshot");
            assertEquals(colorless(JSON.toJsonTree(retained).getAsJsonObject(), owner), 2);
            Files.writeString(game.evidence.resolve("mana-pool-observations.json"), JSON.toJson(stages));
        }
    }

    private static void observe(RulesScenario game, Map<String, Object> stages, String stage, int owner, int expectedColorless) {
        var views = new ArrayList<JsonObject>();
        for (int seat = 0; seat < 4; seat++) {
            var state = game.state(seat);
            int viewer = state.get("viewerId").getAsInt();
            for (var entry : state.getAsJsonArray("players")) {
                var player = entry.getAsJsonObject();
                int id = player.get("id").getAsInt();
                var mana = player.getAsJsonObject("mana");
                assertEquals(mana.keySet(), COLORS, "The public pool has all six actual mana types, including colorless");
                for (String color : COLORS) assertEquals(mana.get(color).getAsInt(), id == owner && color.equals("C") ? expectedColorless : 0,
                        stage + ": seat " + seat + " sees exact " + color + " mana for player " + id);
                var hand = zone(player, "Hand");
                assertTrue(hand.get("count").getAsInt() > 0);
                if (id == viewer) assertEquals(hand.getAsJsonArray("cards").size(), hand.get("count").getAsInt());
                else assertTrue(hand.getAsJsonArray("cards").isEmpty(), "Public mana does not reveal another player's hand");
                var library = zone(player, "Library");
                assertTrue(library.getAsJsonArray("cards").isEmpty(), "Mana observations do not reveal library faces");
                assertTrue(library.get("topCard").isJsonNull());
            }
            views.add(state);
        }
        assertEquals(views.stream().map(state -> state.get("boardRevision").getAsLong()).distinct().count(), 1L,
                "All four seats share the same controller-boundary publication");
        stages.put(stage, views);
    }

    private static boolean priority(RulesScenario.Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static RulesScenario.Input awaitCastPriority(RulesScenario game, String paymentPrompt) throws InterruptedException {
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        JsonObject state;
        do {
            state = game.state(0);
            var value = state.get("prompt");
            if (value != null && !value.isJsonNull()) {
                var prompt = value.getAsJsonObject();
                if (text(prompt, "inputType").equals("InputPassPriority") && !text(prompt, "id").equals(paymentPrompt)
                        && mapOnStack(state)) return new RulesScenario.Input(0, state, prompt);
            }
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        throw new AssertionError("The accepted Auto payment must finish and publish fresh casting priority before another input: " + state);
    }
    private static int colorless(JsonObject state, int owner) { return player(state, owner).getAsJsonObject("mana").get("C").getAsInt(); }
    private static boolean mapOnStack(JsonObject state) {
        return state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(item -> text(item, "name").equals("Expedition Map") && !item.get("ability").getAsBoolean());
    }
    private static JsonObject card(JsonObject state, String zoneName, String name) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow(() -> new AssertionError(name + " absent from " + zoneName));
    }
    private static boolean hasOccurrence(JsonObject state, String zoneName, String id) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(value -> text(value, "visualId").equals(id));
    }
}
