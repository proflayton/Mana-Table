package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.GameEntityCounterTable;
import forge.game.card.CounterEnumType;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Ordinary source reads classify shortcuts; every invocation still uses the
 * actual source action and the existing rules-owned output witness. */
public class QuickManaSourceTest {
    private static final com.google.gson.Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();

    @DataProvider public Object[][] ownedContexts() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "ownedContexts", timeOut = 90000)
    public void ownedAndOrdinaryOffersShareExactManaCapabilitiesWithoutAddingCostPreviews(boolean payment) throws Exception {
        var sources = List.of("Wastes", "Forest", "Command Tower", "Simic Growth Chamber", "Crystal Vein", "Ancient Tomb");
        try (var game = new RulesScenario("owned-mana-capability-" + payment, Map.of(0, sources), "Foundry Inspector", "Progenitus")) {
            var input = main(game);
            if (payment) {
                var spell = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(spell, "key"))));
                game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            }
            HeadlessPlatform.query(game.table, () -> {
                var before = current(game);
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var previews = new java.util.concurrent.atomic.AtomicInteger();
                try (var boundary = new AutomatedObservationTest.DuringDiscovery(actor, ZoneType.Battlefield,
                        ManaCostPreviewProjection.class, "describe", previews::incrementAndGet)) {
                    var owned = JSON.toJsonTree(game.session(0).decisionObservation()).getAsJsonObject();
                    var domain = owned.getAsJsonObject("domain");
                    assertEquals(text(domain, "coverage"), "Complete");
                    assertEquals(previews.get(), 0, "Adding output capabilities cannot add on-demand funding previews to owned discovery");
                    var catalogs = new java.util.LinkedHashMap<String, JsonObject>();
                    for (String name : sources) {
                        var source = card(before.state(), name);
                        var ownedSource = domain.getAsJsonArray("sources").asList().stream().map(value -> value.getAsJsonObject())
                                .filter(value -> text(value, "sourceCardKey").equals(text(source, "key"))).findFirst().orElseThrow();
                        var ordinary = query(game, before, source); catalogs.put(name, ownedSource);
                        assertEquals(ownedSource.getAsJsonArray("actions").size(), ordinary.getAsJsonArray("actions").size());
                        for (var value : ownedSource.getAsJsonArray("actions")) {
                            var action = value.getAsJsonObject();
                            var ordinaryAction = ordinary.getAsJsonArray("actions").asList().stream().map(row -> row.getAsJsonObject())
                                    .filter(row -> text(row, "id").equals(text(action, "id"))).findFirst().orElseThrow();
                            assertEquals(action.get("quickMana"), ordinaryAction.get("quickMana"), "Both readers certify the same exact route output");
                            assertFalse(action.has("costPreview"), "Owned catalogs retain capability facts without optional cost previews");
                        }
                    }
                    assertEquals(outputs(catalogs.get("Wastes")), List.of(Map.of("C", 1)));
                    assertEquals(outputs(catalogs.get("Forest")), List.of(Map.of("G", 1)));
                    assertEquals(Set.copyOf(outputs(catalogs.get("Command Tower"))), colored(1));
                    assertEquals(outputs(catalogs.get("Simic Growth Chamber")), List.of(Map.of("G", 1, "U", 1)));
                    assertTrue(outputs(catalogs.get("Ancient Tomb")).isEmpty(), "An additional damage effect is never a quick capability");
                    var crystal = catalogs.get("Crystal Vein");
                    assertEquals(outputs(crystal), List.of(Map.of("C", 1)));
                    var consuming = crystal.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                            .filter(action -> !action.has("quickMana")).findFirst().orElseThrow();
                    var semantics = domain.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                            .filter(action -> text(action, "actionId").equals(text(consuming, "id"))).findFirst().orElseThrow();
                    assertEquals(semantics.getAsJsonObject("production"), JSON.toJsonTree(Map.of("knowledge", "Known",
                            "alternatives", List.of(Map.of("colors", Map.of("C", 2))))));
                    assertEquals(current(game).state(), before.state(), "Neither capability reader pays, taps, sacrifices or advances the owned input");
                    if (!payment) {
                        var spell = zone(viewer(before.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
                        query(game, before, spell);
                        assertEquals(previews.get(), 1, "The same preview hook still witnesses an explicit ordinary cost query");
                    }
                    Files.writeString(game.evidence.resolve("owned-and-query-capabilities.json"), JSON.toJson(Map.of("owned", owned, "catalogs", catalogs)));
                }
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void currentSourceFactsDistinguishOutputChoicesFromAdditionalCostsAndEffects() throws Exception {
        var sources = List.of("Forest", "Breeding Pool", "Command Tower", "High Market", "Lotus Field",
                "Simic Growth Chamber", "Ancient Tomb", "Crystal Vein", "Cavern of Souls", "Victory Chimes",
                "Mossfire Valley", "Mana Confluence", "Llanowar Elves");
        try (var game = new RulesScenario("quick-mana-classification", Map.of(0, sources), "Wastes", "Progenitus")) {
            main(game);
            var observed = new java.util.LinkedHashMap<String, JsonObject>();
            for (String name : sources) observed.put(name, query(game, name));
            Files.writeString(game.evidence.resolve("ordinary-source-classifications.json"), JSON.toJson(observed));
            assertEquals(outputs(observed.get("Forest")), List.of(Map.of("G", 1)));
            assertEquals(Set.copyOf(outputs(observed.get("Breeding Pool"))), Set.of(Map.of("G", 1), Map.of("U", 1)));
            assertEquals(observed.get("Command Tower").getAsJsonArray("actions").size(), 1,
                    "One action can still require an output chooser");
            assertEquals(Set.copyOf(outputs(observed.get("Command Tower"))), colored(1));
            assertEquals(observed.get("Lotus Field").getAsJsonArray("actions").size(), 1);
            assertEquals(Set.copyOf(outputs(observed.get("Lotus Field"))), colored(3));
            assertEquals(outputs(observed.get("Simic Growth Chamber")), List.of(Map.of("G", 1, "U", 1)),
                    "A simultaneous mixed bundle is one output, not two alternatives");
            assertEquals(outputs(observed.get("High Market")), List.of(Map.of("C", 1)));
            assertEquals(observed.get("High Market").getAsJsonArray("actions").size(), 2,
                    "Its nonmana sacrifice-for-life action remains an ordinary offer");
            assertEquals(outputs(observed.get("Crystal Vein")), List.of(Map.of("C", 1)));
            assertEquals(observed.get("Crystal Vein").getAsJsonArray("actions").size(), 2,
                    "A destructive mana alternative cannot become a quick tap");
            assertEquals(outputs(observed.get("Cavern of Souls")), List.of(Map.of("C", 1)),
                    "Restricted colored production is not an unrestricted shortcut");
            for (String name : List.of("Ancient Tomb", "Victory Chimes", "Mossfire Valley", "Mana Confluence")) {
                assertFalse(observed.get(name).getAsJsonArray("actions").isEmpty(), "Reviewable offer remains available: " + name);
                assertTrue(outputs(observed.get(name)).isEmpty(), "Extra effect, recipient, mana or life cost requires ordinary interaction: " + name);
            }
        }
    }

    @Test(timeOut = 90000)
    public void aCurrentCounterBasedSourceKeepsItsDistinctFixedAndVariableOutputModes() throws Exception {
        try (var game = new RulesScenario("quick-mana-elevator", Map.of(0, List.of("The Eternity Elevator")), "Wastes", "Progenitus")) {
            main(game);
            var first = query(game, "The Eternity Elevator");
            assertEquals(outputs(first), List.of(Map.of("C", 3)));
            // Controlled real counter placement prepares the rules-granted
            // station mode; this test does not claim to perform a station gesture.
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var source = actor.getCardsIn(ZoneType.Battlefield).stream()
                        .filter(card -> card.getName().equals("The Eternity Elevator")).findFirst().orElseThrow();
                var counters = new GameEntityCounterTable();
                source.addCounter(CounterEnumType.CHARGE, 20, actor, counters);
                counters.replaceCounterEffect(game.table.game, source.getFirstSpellAbility());
                game.table.game.getAction().checkStaticAbilities(false); game.table.publish();
                return null;
            });
            var charged = query(game, "The Eternity Elevator");
            var expected = new java.util.HashSet<>(colored(20)); expected.add(Map.of("C", 3));
            assertEquals(Set.copyOf(outputs(charged)), expected,
                    "Three colorless and X of a chosen color are distinct current options");
            assertEquals(outputs(first), List.of(Map.of("C", 3)), "A retained query cannot gain newly granted modes");
            Files.writeString(game.evidence.resolve("counter-mode-queries.json"), JSON.toJson(List.of(first, charged)));
        }
    }

    @DataProvider public Object[][] retention() { return new Object[][] {{false}, {true}}; }

    @DataProvider public Object[][] manaChanges() { return new Object[][] {{"Mana Flare"}, {"Zhur-Taa Ancient"}}; }

    @Test(dataProvider = "manaChanges", timeOut = 90000)
    public void anActiveManaReplacementOrTapTriggerKeepsTheOrdinaryActionWithoutCertifyingAShortcut(String effect) throws Exception {
        try (var game = new RulesScenario("quick-mana-global-" + effect.replaceAll("[^A-Za-z]", ""),
                Map.of(0, List.of("Forest", effect)), "Wastes", "Progenitus")) {
            main(game);
            var source = query(game, "Forest");
            assertFalse(source.getAsJsonArray("actions").isEmpty(), "The ordinary source reader remains available");
            assertTrue(outputs(source).isEmpty(), "Unmodeled global tap/mana effects require the existing manual path");
        }
    }

    @Test(dataProvider = "retention", timeOut = 90000)
    public void aManualExactSourceTapReturnsPriorityBeforeAnyExplicitPassAndHonorsManaRetention(boolean retained) throws Exception {
        var field = retained ? List.of("Forest", "Forest", "Omnath, Locus of Mana") : List.of("Forest", "Forest");
        try (var game = new RulesScenario("quick-mana-manual-" + retained,
                Map.of(0, field, 1, List.of("Spellbook"), 2, List.of("Spellbook"), 3, List.of("Spellbook")), "Wastes", "Progenitus")) {
            main(game); var before = current(game);
            var forests = zone(viewer(before.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Forest")).toList();
            var source = forests.get(0); var other = forests.get(1);
            var catalog = query(game, before, source);
            var action = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            var command = Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                    "actionId", text(action, "id"), "manaOutput", Map.of("colors", Map.of("G", 1)));
            var wrong = Map.of("action", "useSourceAction", "key", text(other, "key"),
                    "actionId", text(action, "id"), "manaOutput", Map.of("colors", Map.of("G", 1)));
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(before, wrong)));
            assertTrue(game.answer(before, command));
            var floated = awaitFloating(game);
            assertEquals(floated.state().get("turn"), before.state().get("turn"));
            assertEquals(text(floated.state(), "phaseKey"), "MAIN1");
            assertTrue(floated.state().getAsJsonArray("stack").isEmpty());
            assertFalse(floated.prompt().get("canAutoPass").getAsBoolean(), "Manual mana keeps the explicit correction window");
            assertTrue(cardById(floated.state(), text(source, "visualId")).get("tapped").getAsBoolean());
            assertFalse(cardById(floated.state(), text(other, "visualId")).get("tapped").getAsBoolean());
            assertEquals(pool(floated.state(), "G"), 1);
            if (retained) assertEquals(card(floated.state(), "Omnath, Locus of Mana").get("power").getAsInt(), 2);
            var stable = current(game).state();
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(before, command)));
            assertEquals(current(game).state(), stable, "Old output/source handles cannot replay mana production");
            assertTrue(game.answer(floated, Map.of("action", "passPriority")), "Only this explicit gesture leaves the manual window");
            var next = game.until(input -> input.seat() == 0 && priority(input)
                    && !text(input.state(), "phaseKey").equals("MAIN1"));
            assertEquals(next.state().get("turn"), before.state().get("turn"));
            assertEquals(pool(next.state(), "G"), retained ? 1 : 0,
                    "The actual retention rule, not a UI shortcut, determines mana at the next step");
            assertEquals(pool(before.state(), "G"), 0, "Old committed snapshots remain unchanged");
            Files.writeString(game.evidence.resolve("manual-source-journey.json"), JSON.toJson(List.of(before.state(), floated.state(), next.state())));
            assertEquals(outputs(catalog), List.of(Map.of("G", 1)), "The existing manual command's exact output is also the shortcut's advertised bundle");
        }
    }

    private static Set<Map<String, Integer>> colored(int amount) {
        return Set.of(Map.of("W", amount), Map.of("U", amount), Map.of("B", amount), Map.of("R", amount), Map.of("G", amount));
    }
    private static List<Map<String, Integer>> outputs(JsonObject catalog) {
        var result = new ArrayList<Map<String, Integer>>();
        for (var value : catalog.getAsJsonArray("actions")) {
            var action = value.getAsJsonObject();
            if (!action.has("quickMana")) continue;
            assertEquals(text(action, "kind"), "activate");
            for (var output : action.getAsJsonObject("quickMana").getAsJsonArray("outputs")) {
                var colors = new java.util.TreeMap<String, Integer>();
                output.getAsJsonObject().getAsJsonObject("colors").entrySet().forEach(entry -> colors.put(entry.getKey(), entry.getValue().getAsInt()));
                result.add(Map.copyOf(colors));
            }
        }
        return List.copyOf(result);
    }
    private static JsonObject query(RulesScenario game, String name) { var input = current(game); return query(game, input, card(input.state(), name)); }
    private static JsonObject query(RulesScenario game, Input input, JsonObject source) {
        var before = current(game);
        assertEquals(before.state(), input.state());
        var result = JSON.toJsonTree(game.session(0).sourceActions(request(input,
                Map.of("key", text(source, "key"), "expectedRevision", input.state().get("revision"))))).getAsJsonObject();
        assertEquals(current(game).state(), before.state(), "A descriptor read cannot tap, choose or advance the input");
        return result;
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input current(RulesScenario game) {
        var state = HeadlessPlatform.query(game.table, () -> game.state(0));
        return new Input(0, state, state.get("prompt").isJsonNull() ? null : state.getAsJsonObject("prompt"));
    }
    private static Input awaitFloating(RulesScenario game) throws Exception {
        long deadline = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(5);
        while (System.nanoTime() < deadline) {
            var input = current(game);
            if (input.prompt() != null && priority(input) && pool(input.state(), "G") == 1) return input;
            Thread.sleep(5);
        }
        throw new AssertionError("The explicit source action did not return its actual floating-mana priority");
    }
    private static boolean priority(Input input) { return input.prompt() != null && text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static int pool(JsonObject state, String color) { return viewer(state).getAsJsonObject("mana").get(color).getAsInt(); }
    private static JsonObject card(JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject cardById(JsonObject state, String id) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "visualId").equals(id)).findFirst().orElseThrow();
    }
}
