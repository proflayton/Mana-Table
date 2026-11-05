package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real selection, payment and resolution. No event injection or board mutation
 * supplies the announced source, selected mode or target in these journeys. */
public class AnnouncedTargetHistoryJourneyTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();

    @Test(timeOut = 90000)
    public void restorationMagicCuraRetainsItsOpponentsLandAfterActualResolution() throws Exception {
        try (var game = new RulesScenario("announced-restoration-cura", Map.of(
                0, List.of("Plains", "Plains", "Reliquary Tower"), 1, List.of("Forest")),
                "Restoration Magic", "Rhys the Redeemed")) {
            var main = main(game);
            int actor = main.state().get("viewerId").getAsInt();
            int recipient = game.state(1).get("viewerId").getAsInt();
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertEquals(text(spell, "name"), "Restoration Magic");
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(spell, "key"))));
            var mode = game.until(input -> input.seat() == 0 && text(input.prompt(), "kind").equals("choice"));
            var cura = mode.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> value.get("index").getAsInt() == 1)
                    .findFirst().orElseThrow();
            assertTrue(text(cura, "label").contains("3"), "The real card's second offered mode grants three life");
            assertTrue(game.answer(mode, Map.of("choices", List.of(cura.get("index").getAsInt()))));
            var targeting = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var forest = card(targeting.state(), recipient, "Battlefield", "Forest");
            String targetId = text(forest, "visualId");
            assertFalse(targetId.isBlank());
            assertTrue(game.answer(targeting, Map.of("action", "card", "key", text(forest, "key"))));
            payAndStack(game);
            var first = announced(game, 0, "Restoration Magic");
            assertEquals(first.size(), 1, "One actual committed cast must create one announcement");
            var record = first.get(0); var fact = record.getAsJsonObject("announcement");
            assertEquals(text(fact, "kind"), "announced"); assertEquals(text(fact, "targetKnowledge"), "Known");
            assertEquals(fact.getAsJsonArray("targets").size(), 1);
            var target = fact.getAsJsonArray("targets").get(0).getAsJsonObject();
            assertEquals(text(target, "kind"), "card"); assertEquals(text(target, "cardId"), targetId);
            assertEquals(text(target, "name"), "Forest"); assertTrue(text(target, "type").contains("Land"));
            assertEquals(text(target, "zone"), "Battlefield");
            assertEquals(target.get("controllerId").getAsInt(), recipient);
            assertEquals(target.get("ownerId").getAsInt(), recipient);
            assertTrue(target.get("groupIndex").getAsInt() > 0, "Cura targets are in the actual selected submode, not the targetless Charm root");
            String execution = text(record, "executionId"); assertFalse(execution.isBlank());
            for (int seat = 1; seat < 4; seat++)
                assertEquals(publicFacts(announced(game, seat, "Restoration Magic")), publicFacts(first),
                        "Public descriptors agree while each audience retains its own ledger ordinals");
            var settled = game.until(input -> input.seat() == 0 && priority(input)
                    && input.state().getAsJsonArray("stack").isEmpty()
                    && player(input.state(), actor).get("life").getAsInt() == 43);
            assertEquals(count(player(settled.state(), actor), "Graveyard"), 1);
            assertEquals(text(card(settled.state(), recipient, "Battlefield", "Forest"), "visualId"), targetId);
            assertEquals(announced(game, 0, "Restoration Magic"), first,
                    "Resolution and later current snapshots do not rewrite the cast's retained target");
            var legacy = history(game.session(0), game.table.id, 1);
            var fallback = legacy.stream().filter(row -> row.get("sequence").equals(record.get("sequence"))).findFirst().orElseThrow();
            assertFalse(fallback.has("announcement"), "The negotiated v2 payload must not appear as even a null field in strict v1");
            assertEquals(text(fallback.getAsJsonObject("activity"), "kind"), "announcement");
            assertEquals(text(fallback, "executionId"), execution);
            Files.writeString(game.evidence.resolve("announced-cura-v2.json"), JSON.toJson(first));
            Files.writeString(game.evidence.resolve("announced-cura-v1.json"), JSON.toJson(fallback));
        }
    }

    @Test(timeOut = 90000)
    public void actualActivatedAndTriggeredTargetsAreRetainedSeparatelyFromTargetlessCasts() throws Exception {
        try (var game = new RulesScenario("announced-activation-trigger", Map.of(
                0, List.of("Plains", "Plains", "Plains", "Plains", "Plains", "Plains", "Dragon Blood", "Grizzly Bears", "Reliquary Tower"),
                1, List.of("Grizzly Bears")), "Fiend Hunter", "Rhys the Redeemed")) {
            var first = main(game); int actor = first.state().get("viewerId").getAsInt();
            assertTrue(game.answer(first, Map.of("action", "card", "key", text(card(first.state(), actor, "Battlefield", "Dragon Blood"), "key"))));
            var targeting = game.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            assertTrue(game.answer(targeting, Map.of("action", "card", "key", text(card(targeting.state(), actor, "Battlefield", "Grizzly Bears"), "key"))));
            payAndStack(game);
            assertEquals(announced(game, 0, "Dragon Blood").size(), 1);
            var resolved = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            var next = resolved;
            var hunter = zone(viewer(next.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(next, Map.of("action", "card", "key", text(hunter, "key"))));
            payAndStack(game);
            var trigger = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            int opponent = game.state(1).get("viewerId").getAsInt();
            assertTrue(game.answer(trigger, Map.of("action", "card", "key", text(card(trigger.state(), opponent, "Battlefield", "Grizzly Bears"), "key"))));
            game.until(input -> priority(input) && input.state().getAsJsonArray("stack").size() == 1);
            var hunterFacts = announced(game, 0, "Fiend Hunter");
            assertEquals(hunterFacts.size(), 2, "The actual permanent spell and its later ETB trigger are different occurrences");
            assertTrue(hunterFacts.get(0).getAsJsonObject("announcement").getAsJsonArray("targets").isEmpty());
            assertEquals(text(hunterFacts.get(0).getAsJsonObject("announcement"), "targetKnowledge"), "Known");
            assertEquals(hunterFacts.get(1).getAsJsonObject("announcement").getAsJsonArray("targets").size(), 1);
            assertNotEquals(text(hunterFacts.get(0), "executionId"), text(hunterFacts.get(1), "executionId"));
            game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            for (int seat = 1; seat < 4; seat++) assertEquals(publicFacts(announced(game, seat, "Fiend Hunter")), publicFacts(hunterFacts));
        }
    }

    static List<JsonObject> history(MatchEndpoint endpoint, String gameId, int schema) {
        var result = new ArrayList<JsonObject>(); long after = 0;
        for (int chunk = 0; chunk < 128; chunk++) {
            var query = new JsonObject(); query.addProperty("gameId", gameId); query.addProperty("afterSequence", after);
            query.addProperty("limit", 128); query.addProperty("historySchemaVersion", schema);
            var value = JSON.toJsonTree(endpoint.history(query)).getAsJsonObject();
            int returnedSchema = value.get("schemaVersion").getAsInt();
            assertTrue(returnedSchema >= 1 && returnedSchema <= schema,
                    "A negotiated maximum can explicitly retain older NotReported history");
            value.getAsJsonArray("records").forEach(row -> result.add(row.getAsJsonObject()));
            after = value.get("nextSequence").getAsLong();
            if (!value.get("hasMore").getAsBoolean()) return List.copyOf(result);
        }
        throw new AssertionError("History exceeded its bounded retained record count");
    }
    private static List<JsonObject> announced(RulesScenario game, int seat, String name) {
        return history(game.session(seat), game.table.id, 2).stream()
                .filter(row -> text(row, "sourceName").equals(name) && row.has("announcement") && !row.get("announcement").isJsonNull()).toList();
    }
    private static List<Object> publicFacts(List<JsonObject> records) {
        return records.stream().map(row -> (Object)List.of(row.get("executionId"), row.get("sourceName"), row.get("announcement"))).toList();
    }
    private static boolean priority(RulesScenario.Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static RulesScenario.Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static RulesScenario.Input payAndStack(RulesScenario game) throws Exception {
        var payment = game.until(input -> text(input.prompt(), "inputType").startsWith("InputPayMana"));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
        assertTrue(game.answer(payment, Map.of("action", "ok")));
        return game.until(input -> priority(input) && !input.state().getAsJsonArray("stack").isEmpty());
    }
    private static JsonObject card(JsonObject state, int owner, String zone, String name) {
        return zone(player(state, owner), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
}
