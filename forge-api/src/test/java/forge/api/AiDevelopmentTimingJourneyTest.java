package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Controlled rules counterfactuals, not an AI policy evaluator or a strategy verdict. */
public class AiDevelopmentTimingJourneyTest {
    private static final String MARCH = "March of the Multitudes";
    // Match the process wire's explicit null members; strict native decoding
    // must not infer required nullable fields omitted by an evidence exporter.
    private static final com.google.gson.Gson JSON = new GsonBuilder().setPrettyPrinting().serializeNulls().create();
    private record Consequence(String phase, int announcedX, int lands, int tappedLands, int tokens,
                               int spentMana, boolean creaturesPreserved, boolean landPlayed) { }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void ownUpkeepDevelopmentCanWaitForAnActuallyOfferedLandBeforeInvesting() throws Exception {
        var early = run(false);
        var later = run(true);
        assertEquals(early.phase(), "UPKEEP");
        assertEquals(later.phase(), "MAIN1");
        assertEquals(early.announcedX(), 1);
        assertEquals(later.announcedX(), 2);
        assertEquals(early.lands(), 4);
        assertEquals(later.lands(), 5);
        assertEquals(early.spentMana(), 4);
        assertEquals(later.spentMana(), 5);
        assertEquals(early.tokens(), 1);
        assertEquals(later.tokens(), 2);
        assertTrue(early.creaturesPreserved() && later.creaturesPreserved(),
                "Both legal routes leave the same two creatures untapped instead of treating convoke as free mana");
        assertFalse(early.landPlayed());
        assertTrue(later.landPlayed());
        // More tokens in this declared comparison does not establish the best X,
        // a globally better play, a deterministic future draw, or policy quality.
    }

    private static Consequence run(boolean waitForMain) throws Exception {
        String id = "ai-development-timing-" + (waitForMain ? "main" : "upkeep");
        // This deliberately small test library guarantees at least one of each
        // relevant hand card and one genuine draw. It is not a valid shipping
        // Commander list and supplies no campaign deck qualification evidence.
        var library = List.of(MARCH, MARCH, MARCH, MARCH, "Plains", "Plains", "Plains", "Plains");
        try (var game = RulesScenario.automated(id, Map.of(0,
                List.of("Plains", "Plains", "Forest", "Forest", "Grizzly Bears", "Memnite",
                        "Rhys the Redeemed", "Concordant Crossroads")),
                "Forest", "Rhys the Redeemed", Map.of(0, library))) {
            var opening = game.until(input -> input.seat() == 0 && priority(input)
                    && input.state().get("activePlayerId").getAsInt() == 0 && text(input.state(), "phaseKey").equals("UPKEEP"));
            assertTrue(opening.state().getAsJsonArray("stack").isEmpty());
            assertEquals(viewer(opening.state()).get("landPlaysUsed").getAsInt(), 0);
            assertTrue(cards(opening, "Hand").stream().anyMatch(card -> text(card, "name").equals("Plains")));
            assertTrue(catalog(game, opening, card(opening, "Hand", MARCH)).getAsJsonArray("actions").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "kind").equals("cast")),
                    "The same declared upkeep state actually offers the developmental instant");
            assertTrue(catalog(game, opening, card(opening, "Hand", "Plains")).getAsJsonArray("actions").isEmpty(),
                    "The known land cannot be played during upkeep");
            assertTrue(catalog(game, opening, card(opening, "Battlefield", "Rhys the Redeemed")).getAsJsonArray("actions").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "kind").equals("activate")),
                    "Rhys has an actual competing upkeep activation, so deferring only the instant would not preserve planning options");
            var actualUpkeep = JSON.toJsonTree(game.session(0).automatedObservation());
            var before = opening;
            boolean landPlayed = false;
            if (waitForMain) {
                before = game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
                assertEquals(count(viewer(before.state()), "Hand"), count(viewer(opening.state()), "Hand") + 1,
                        "Waiting includes the real draw step rather than an authored future card");
                var land = card(before, "Hand", "Plains");
                String landId = text(land, "visualId");
                var offer = catalog(game, before, land).getAsJsonArray("actions").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "kind").equals("play")).findFirst().orElseThrow();
                invoke(game, before, land, offer);
                before = game.until(input -> input.seat() == 0 && priority(input)
                        && viewer(input.state()).get("landPlaysUsed").getAsInt() == 1);
                assertTrue(cards(before, "Battlefield").stream().anyMatch(value -> text(value, "visualId").equals(landId)));
                assertEquals(lands(before).size(), 5);
                landPlayed = true;
            }
            int invested = waitForMain ? 2 : 1;
            var spell = card(before, "Hand", MARCH);
            String spellId = text(spell, "visualId");
            var cast = catalog(game, before, spell).getAsJsonArray("actions").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "kind").equals("cast")).findFirst().orElseThrow();
            invoke(game, before, spell, cast);
            var number = game.until(input -> input.seat() == 0 && text(input.prompt(), "kind").equals("number"));
            assertTrue(game.answer(number, Map.of("action", "number", "value", invested)));
            var reduction = game.until(input -> input.seat() == 0
                    && text(input.prompt(), "inputType").equals("InputSelectCardsForConvokeOrImprovise"));
            assertTrue(reduction.prompt().get("okEnabled").getAsBoolean(), "The real convoke choice permits keeping creatures untapped");
            assertTrue(cards(reduction, "Battlefield").stream().filter(value -> text(value, "name").equals("Grizzly Bears")
                    || text(value, "name").equals("Memnite")).allMatch(value -> value.get("selectable").getAsBoolean()),
                    "The experiment retains actual available convoke choices without claiming ordinary lands are the maximum");
            assertTrue(game.answer(reduction, Map.of("action", "ok")));
            var payment = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            String executionId = text(payment.prompt(), "executionId");
            assertFalse(executionId.isEmpty());
            // Automated seats receive the same real cost but do not acquire
            // the human client's Auto gesture. Fund through their own current
            // source actions and wait for each mana child's actual completion.
            for (int mana = 0; mana < invested + 3; mana++) {
                var land = lands(payment).stream().filter(value -> !value.get("tapped").getAsBoolean())
                        .sorted(java.util.Comparator.comparing(value -> text(value, "name").equals("Plains") ? 0 : 1))
                        .findFirst().orElseThrow();
                String landId = text(land, "visualId");
                var route = catalog(game, payment, land).getAsJsonArray("actions").get(0).getAsJsonObject();
                invoke(game, payment, land, route);
                payment = awaitManaProgress(game, executionId, landId);
            }
            if (text(payment.prompt(), "inputType").startsWith("InputPayMana")) {
                assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "The manually funded owning bill can complete");
                assertTrue(game.answer(payment, Map.of("action", "ok")));
            }
            var settled = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            var occurrence = settled.state().getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "id").equals(executionId)).findFirst().orElseThrow();
            assertEquals(text(occurrence, "outcome"), "resolved");
            assertTrue(cards(settled, "Graveyard").stream().anyMatch(value -> text(value, "visualId").equals(spellId)));
            var tokens = cards(settled, "Battlefield").stream().filter(value -> text(value, "gamePieceType").equals("token")).toList();
            assertEquals(tokens.size(), invested);
            assertTrue(tokens.stream().allMatch(value -> text(value, "type").contains("Soldier")
                    && value.get("power").getAsInt() == 1 && value.get("toughness").getAsInt() == 1));
            var history = history(game);
            var spent = history.stream().filter(record -> executionId.equals(record.executionId()) && record.payment() != null
                    && record.payment().kind().equals("spent")).toList();
            assertFalse(spent.isEmpty(), "Actual payment receipts, not tapped snapshots, prove spending");
            assertTrue(spent.stream().allMatch(record -> record.payment().payerId() == 0));
            int spentMana = spent.stream().mapToInt(record -> record.payment().spent().values().stream().mapToInt(Integer::intValue).sum()).sum();
            assertEquals(spentMana, invested + 3);
            assertTrue(spent.stream().anyMatch(record -> record.payment().billAfter().isEmpty()), "The owning bill really reaches zero");
            boolean creaturesPreserved = cards(settled, "Battlefield").stream().filter(value -> text(value, "name").equals("Grizzly Bears")
                    || text(value, "name").equals("Memnite")).allMatch(value -> !value.get("tapped").getAsBoolean());
            var result = new Consequence(text(settled.state(), "phaseKey"), invested, lands(settled).size(),
                    (int) lands(settled).stream().filter(value -> value.get("tapped").getAsBoolean()).count(),
                    tokens.size(), spentMana, creaturesPreserved, landPlayed);
            assertEquals(result.tappedLands(), invested + 3);
            Files.writeString(game.evidence.resolve("counterfactual.json"), JSON.toJson(Map.of(
                    "scope", "Scripted actual-rules routes from declared pregame inputs; no native policy execution, best-X or strategic qualification claim.",
                    "opening", opening.state(), "actualUpkeep", actualUpkeep, "beforeCast", before.state(), "after", settled.state(),
                    "history", history, "consequence", result)));
            return result;
        }
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static List<JsonObject> cards(Input input, String name) {
        return zone(viewer(input.state()), name).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static JsonObject card(Input input, String zone, String name) {
        return cards(input, zone).stream().filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static List<JsonObject> lands(Input input) {
        return cards(input, "Battlefield").stream().filter(value -> text(value, "type").contains("Land")).toList();
    }
    private static JsonObject catalog(RulesScenario game, Input input, JsonObject source) {
        return JSON.toJsonTree(game.session(0).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
    }
    private static void invoke(RulesScenario game, Input input, JsonObject source, JsonObject action) {
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id"))));
    }
    private static Input awaitManaProgress(RulesScenario game, String owner, String sourceId) throws Exception {
        long deadline = System.nanoTime() + java.time.Duration.ofSeconds(5).toNanos();
        while (System.nanoTime() < deadline) {
            var state = game.state(0);
            if (state.get("prompt").isJsonObject()) {
                var input = new Input(0, state, state.getAsJsonObject("prompt"));
                boolean sourceComplete = state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream()
                        .map(value -> value.getAsJsonObject()).anyMatch(value -> text(value, "sourceCardId").equals(sourceId)
                                && text(value, "parentId").equals(owner) && text(value, "kind").equals("mana")
                                && text(value, "outcome").equals("resolved"));
                if (sourceComplete && (priority(input)
                        || text(input.prompt(), "inputType").startsWith("InputPayMana") && text(input.prompt(), "executionId").equals(owner)))
                    return input;
            }
            Thread.sleep(2);
        }
        throw new AssertionError("The real mana child did not settle back to its owning payment: " + game.state(0));
    }
    private static List<MatchHistory.Record> history(RulesScenario game) {
        var result = new ArrayList<MatchHistory.Record>();
        long after = 0;
        for (int chunkIndex = 0; chunkIndex < 8; chunkIndex++) {
            var request = new JsonObject();
            request.addProperty("gameId", game.table.id); request.addProperty("afterSequence", after); request.addProperty("limit", 128);
            request.addProperty("historySchemaVersion", 2);
            var chunk = game.session(0).history(request);
            // Decode the wire projection instead of depending on in-process record objects.
            var records = JSON.fromJson(JSON.toJson(chunk.get("records")), MatchHistory.Record[].class);
            java.util.Collections.addAll(result, records); after = ((Number) chunk.get("nextSequence")).longValue();
            if (!Boolean.TRUE.equals(chunk.get("hasMore"))) return List.copyOf(result);
        }
        throw new AssertionError("The small timing experiment exceeded its bounded history read");
    }
}
