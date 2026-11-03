package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.*;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual cast-state classification and committed untaps, not inferred board differences. */
public class TraxosExecutionJourneyTest {
    private static final String TRAXOS = "Traxos, Scourge of Kroog";
    private static final com.google.gson.Gson JSON = new GsonBuilder().setPrettyPrinting().serializeNulls().create();

    @DataProvider public Object[][] untapResults() { return new Object[][] {{false}, {true}}; }

    @Test(groups = "canonical-interaction", dataProvider = "untapResults", timeOut = 90000)
    public void actualHistoricCastOwnsItsUntapWhilePreventedAndAlreadyUntappedResultsStayAbsent(boolean prevent) throws Exception {
        var lands = new ArrayList<>(Collections.nCopies(8, "Wastes"));
        lands.addAll(Collections.nCopies(6, "Island"));
        var library = List.of("Mind Stone", "Mind Stone", "Mind Stone", "Impede Momentum",
                "Impede Momentum", "Impede Momentum", "Wastes", "Wastes");
        // Passive seats can pass a full round without introducing unrelated
        // cleanup choices while the control reaches the actor's next untap.
        var battlefield = Map.of(0, lands, 1, List.of("Reliquary Tower"),
                2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower"));
        var scenario = prevent
                ? new RulesScenario("traxos-untap-prevented", battlefield, "Mind Stone", TRAXOS, Map.of(0, library))
                : new RulesScenario("traxos-untap-resolved", battlefield, "Mind Stone", TRAXOS);
        try (var game = scenario) {
            var current = main(game);
            start(game, current, card(viewer(current.state()), "Command", TRAXOS), ignored -> true);
            pay(game);
            current = settled(game);
            var traxos = card(viewer(current.state()), "Battlefield", TRAXOS);
            String targetId = text(traxos, "visualId");
            assertTrue(traxos.get("tapped").getAsBoolean(), "Traxos entered tapped through the actual rules");
            if (prevent) {
                start(game, current, card(viewer(current.state()), "Hand", "Impede Momentum"), ignored -> true);
                var target = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
                assertTrue(game.answer(target, Map.of("action", "card", "key",
                        text(card(viewer(target.state()), "Battlefield", TRAXOS), "key"))));
                pay(game);
                current = settled(game);
                assertEquals(counters(card(viewer(current.state()), "Battlefield", TRAXOS), "Stun"), 3);
            }
            long before = lastActivity(current.state());
            start(game, current, card(viewer(current.state()), "Hand", "Mind Stone"), ignored -> true);
            pay(game);
            current = settled(game);
            var cast = newestCast(current.state(), before);
            assertEquals(cast.get("historicSpell").getAsBoolean(), true);
            String castId = text(cast, "executionId");
            var trigger = occurrences(current.state()).stream().filter(value -> text(value, "kind").equals("trigger")
                    && text(value, "sourceName").equals(TRAXOS) && text(value, "parentId").equals(castId)).findFirst().orElseThrow();
            String triggerId = text(trigger, "id");
            assertEquals(text(trigger, "step"), "complete");
            assertEquals(text(trigger, "outcome"), "resolved");
            assertNotEquals(text(trigger, "budgetRootId"), castId, "A trigger has its own allowance and keeps the actual cast parent");
            var untaps = activities(current.state()).stream().filter(value -> value.get("id").getAsLong() > before
                    && text(value, "cardId").equals(targetId) && untapped(value)).toList();
            if (prevent) {
                assertTrue(untaps.isEmpty(), "A replaced untap has no committed tap-state mutation to project");
                assertTrue(card(viewer(current.state()), "Battlefield", TRAXOS).get("tapped").getAsBoolean());
                assertEquals(counters(card(viewer(current.state()), "Battlefield", TRAXOS), "Stun"), 2);
            } else {
                assertEquals(untaps.size(), 1);
                var untap = untaps.get(0);
                assertEquals(text(untap, "executionId"), triggerId);
                assertEquals(text(untap, "executionStep"), "resolution");
                assertEquals(untap.get("playerId").getAsInt(), current.state().get("viewerId").getAsInt());
                assertTrue(untap.get("cause").isJsonNull(), "The projection does not invent a producing cause from the target");
                assertFalse(card(viewer(current.state()), "Battlefield", TRAXOS).get("tapped").getAsBoolean());
                long alreadyUntapped = lastActivity(current.state());
                start(game, current, card(viewer(current.state()), "Hand", "Mind Stone"), ignored -> true);
                pay(game);
                current = settled(game);
                assertTrue(activities(current.state()).stream().noneMatch(value -> value.get("id").getAsLong() > alreadyUntapped
                        && text(value, "cardId").equals(targetId) && untapped(value)),
                        "A second historic trigger resolving on an already untapped Traxos does not manufacture another result");
                int turn = current.state().get("turn").getAsInt();
                current = game.until(input -> input.seat() == 0 && priority(input) && input.state().get("turn").getAsInt() > turn
                        && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt()
                        && text(input.state(), "phaseKey").equals("UPKEEP"));
                assertTrue(activities(current.state()).stream().anyMatch(value -> untapped(value)
                        && text(value, "cardName").equals("Wastes") && text(value, "phaseKey").equals("UNTAP")
                        && !text(value, "executionId").equals(triggerId)),
                        "Ordinary untap-step changes remain actual facts under their own provenance");
            }
            Files.writeString(game.evidence.resolve("untap-result.json"), JSON.toJson(current.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void adventureAndLegendaryCreatureRoutesUseTheirActualCastStateRatherThanThePhysicalFrontFace() throws Exception {
        var lands = new ArrayList<>(Collections.nCopies(5, "Tropical Island"));
        lands.addAll(Collections.nCopies(5, "Volcanic Island"));
        lands.addAll(Collections.nCopies(5, "Taiga"));
        try (var game = new RulesScenario("traxos-adventure-cast-state", Map.of(0, lands), "Beluna Grandsquall", TRAXOS)) {
            var current = main(game);
            start(game, current, card(viewer(current.state()), "Command", TRAXOS), ignored -> true);
            pay(game);
            current = settled(game);
            assertTrue(card(viewer(current.state()), "Battlefield", TRAXOS).get("tapped").getAsBoolean());
            long beforeAdventure = lastActivity(current.state());
            start(game, current, card(viewer(current.state()), "Hand", "Beluna Grandsquall"),
                    route -> text(route, "text").contains("Mill"));
            pay(game);
            current = settled(game);
            var adventure = newestCast(current.state(), beforeAdventure);
            assertEquals(adventure.get("historicSpell").getAsBoolean(), false,
                    "The instant Adventure is not historic even though its physical front face is legendary");
            assertTrue(card(viewer(current.state()), "Battlefield", TRAXOS).get("tapped").getAsBoolean());
            assertTrue(occurrences(current.state()).stream().noneMatch(value -> text(value, "sourceName").equals(TRAXOS)
                    && text(value, "parentId").equals(text(adventure, "executionId")) && text(value, "kind").equals("trigger")));
            long beforeCreature = lastActivity(current.state());
            start(game, current, card(viewer(current.state()), "Exile", "Beluna Grandsquall"), ignored -> true);
            pay(game);
            current = settled(game);
            var creature = newestCast(current.state(), beforeCreature);
            assertEquals(creature.get("historicSpell").getAsBoolean(), true,
                    "The actual legendary creature route supplies its own historic fact");
            assertFalse(card(viewer(current.state()), "Battlefield", TRAXOS).get("tapped").getAsBoolean());
            assertTrue(occurrences(current.state()).stream().anyMatch(value -> text(value, "sourceName").equals(TRAXOS)
                    && text(value, "parentId").equals(text(creature, "executionId")) && text(value, "kind").equals("trigger")));
            Files.writeString(game.evidence.resolve("cast-state-result.json"), JSON.toJson(current.state()));
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input settled(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject owner, String name, String face) {
        return zone(owner, name).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(face)).findFirst().orElseThrow();
    }
    private static int counters(JsonObject card, String type) {
        return card.getAsJsonObject("counters").has(type) ? card.getAsJsonObject("counters").get(type).getAsInt() : 0;
    }
    private static List<JsonObject> activities(JsonObject state) {
        return state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static List<JsonObject> occurrences(JsonObject state) {
        return state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static long lastActivity(JsonObject state) {
        return activities(state).stream().mapToLong(value -> value.get("id").getAsLong()).max().orElse(0);
    }
    private static JsonObject newestCast(JsonObject state, long before) {
        return activities(state).stream().filter(value -> value.get("id").getAsLong() > before && text(value, "kind").equals("cast")
                && value.get("playerId").getAsInt() == state.get("viewerId").getAsInt()).findFirst().orElseThrow();
    }
    private static boolean untapped(JsonObject entry) {
        if (!text(entry, "kind").equals("tapped") || !entry.has("change") || entry.get("change").isJsonNull()) return false;
        var change = entry.getAsJsonObject("change");
        return text(change, "kind").equals("state") && text(change, "property").equals("tapped")
                && change.get("before").getAsInt() == 1 && change.get("after").getAsInt() == 0;
    }
    private static void start(RulesScenario game, Input input, JsonObject source, Predicate<JsonObject> route) {
        var catalog = JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input,
                Map.of("key", text(source, "key"))))).getAsJsonObject();
        var selected = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals("cast") && route.test(value)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                "actionId", text(selected, "id"))));
    }
    private static void pay(RulesScenario game) throws Exception {
        var payment = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "The human fixture has actual automatic funding");
        assertTrue(game.answer(payment, Map.of("action", "ok")));
    }
}
