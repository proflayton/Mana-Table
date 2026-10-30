package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.gamemodes.match.input.InputSelectTargets;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.ActionOccurrenceCoherenceTest.until;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Current add-target gates and selected-card undo come from the same rules input. */
public class TargetSelectionProgressTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 120000)
    public void crackleWithPowerConfirmsOneOfTwoTargetsPaysTripleXAndDealsTenDamage() throws Exception {
        try (var game = new RulesScenario("crackle-power-subset",
                Map.of(0, java.util.Collections.nCopies(8, "Mountain")), "Crackle with Power", "Rhys the Redeemed")) {
            var main = game.until(input -> input.seat() == 0 && priority(input)
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            int actor = main.state().get("viewerId").getAsInt();
            int recipient = game.state(1).get("viewerId").getAsInt();
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(game.table.seat(0).sourceActions(request(main,
                    Map.of("key", text(spell, "key"))))).getAsJsonObject();
            Files.writeString(game.evidence.resolve("00-cast-source.json"), JSON.toJson(catalog));
            assertEquals(text(spell, "name"), "Crackle with Power");
            assertEquals(catalog.getAsJsonArray("actions").size(), 1);
            var action = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                    "actionId", text(action, "id"))));
            var number = game.until(input -> input.seat() == 0 && text(input.prompt(), "kind").equals("number"));
            Files.writeString(game.evidence.resolve("01-announce-x.json"), JSON.toJson(number.state()));
            assertEquals(text(number.prompt().getAsJsonObject("sourceCard"), "name"), "Crackle with Power");
            assertTrue(game.answer(number, Map.of("action", "number", "value", 2)));
            var selection = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            Files.writeString(game.evidence.resolve("02-targets.json"), JSON.toJson(selection.state()));
            assertEquals(selection.prompt().get("min").getAsInt(), 0);
            assertEquals(selection.prompt().get("max").getAsInt(), 2);
            assertTrue(selection.prompt().getAsJsonArray("playerChoices").asList().stream()
                    .anyMatch(value -> value.getAsInt() == recipient));
            assertTrue(game.answer(selection, Map.of("action", "player", "playerId", recipient)));
            var subset = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            Files.writeString(game.evidence.resolve("03-selected-subset.json"), JSON.toJson(subset.state()));
            HeadlessPlatform.query(game.table, () -> {
                var controller = (PlayerControllerHuman)game.table.game.getRegisteredPlayers().get(0).getController();
                var current = (InputSelectTargets)controller.getInputQueue().getInput();
                assertEquals(current.getSpellAbility().getXManaCostPaid(), Integer.valueOf(2));
                assertEquals(java.util.stream.StreamSupport.stream(current.getSpellAbility().getTargets().getTargetPlayers().spliterator(), false)
                                .map(player -> player.getId()).toList(),
                        List.of(recipient), "One legal player is the actual current target, below the maximum of two");
                assertTrue(current.isTargetSelectionActive());
                return null;
            });
            assertTrue(subset.prompt().get("okEnabled").getAsBoolean(), "A valid subset must expose its explicit completion");
            assertTrue(game.answer(subset, Map.of("action", "ok")));
            var payment = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            Files.writeString(game.evidence.resolve("04-payment.json"), JSON.toJson(payment.state()));
            HeadlessPlatform.query(game.table, () -> {
                var controller = (PlayerControllerHuman)game.table.game.getRegisteredPlayers().get(0).getController();
                var current = (InputPayMana)controller.getInputQueue().getInput();
                assertEquals(current.getOriginalManaCost().getConvertedManaCost(), 8);
                assertEquals(current.getRemainingManaCost().getConvertedManaCost(), 8);
                assertEquals(current.getPaidForAbility().getXManaCostPaid(), Integer.valueOf(2));
                return null;
            });
            assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "Auto can fund six generic and two red from the eight Mountains");
            assertEquals(cards(payment.state(), 0).stream().filter(card -> text(card, "name").equals("Mountain")
                    && !card.get("tapped").getAsBoolean()).count(), 8L);
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var stacked = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").size() == 1);
            Files.writeString(game.evidence.resolve("05-stacked.json"), JSON.toJson(stacked.state()));
            assertEquals(cards(stacked.state(), 0).stream().filter(card -> text(card, "name").equals("Mountain")
                    && card.get("tapped").getAsBoolean()).count(), 8L);
            assertEquals(text(stacked.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "name"), "Crackle with Power");
            var resolved = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            Files.writeString(game.evidence.resolve("06-resolved.json"), JSON.toJson(resolved.state()));
            assertEquals(player(resolved.state(), recipient).get("life").getAsInt(), 30,
                    "The real spell resolves for five times the announced X against its one confirmed player");
            for (var value : resolved.state().getAsJsonArray("players")) {
                var player = value.getAsJsonObject();
                if (player.get("id").getAsInt() != recipient) assertEquals(player.get("life").getAsInt(), 40);
            }
            assertEquals(viewer(resolved.state()).get("id").getAsInt(), actor);
            assertTrue(zone(viewer(resolved.state()), "Graveyard").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Crackle with Power")));
        }
    }

    @Test(timeOut = 120000)
    public void actualPerOpponentTargetsExcludeUnproductiveSiblingsPreserveUndoAndCompleteAValidSubset() throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var players = java.util.stream.IntStream.range(0, 4).mapToObj(seat -> {
            var deck = new Deck("Targeting " + seat);
            deck.getMain().add(StaticData.instance().getCommonCards().getCard(seat == 0 ? "Bronzebeak Foragers" : "Forest"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
            return MatchTable.automated(deck, "Commander", "Policy " + seat);
        }).toList();
        for (var entry : Map.of(0, List.of("Plains", "Plains", "Plains", "Plains"),
                1, List.of("Grizzly Bears", "Grizzly Bears"), 2, List.of("Grizzly Bears")).entrySet())
            players.get(entry.getKey()).addExtraCardsOnBattlefield(entry.getValue().stream()
                    .map(name -> (forge.item.IPaperCard)StaticData.instance().getCommonCards().getCard(name)).toList());
        Path evidence = ApiTestWorkspace.evidence("target-selection-progress");
        try (var table = new MatchTable("Commander", players, resources, evidence, true)) {
            table.start(); var answered = new HashMap<Integer, String>();
            var main = until(table, answered, 0, input -> input.seat() == 0 && priority(input)
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            use(table, answered, main, zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject());
            for (int mana = 0; mana < 4; mana++) {
                var payment = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
                var plains = cards(payment.state(), 0).stream().filter(card -> text(card, "name").equals("Plains") && !card.get("tapped").getAsBoolean())
                        .findFirst().orElseThrow();
                use(table, answered, payment, plains);
            }
            var selection = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var controller = (PlayerControllerHuman)table.game.getRegisteredPlayers().get(0).getController();
            var actual = (InputSelectTargets)controller.getInputQueue().getInput();
            String episode = episode(table);
            assertFalse(episode.isEmpty());
            assertPublishedTargets(table, selection, episode);
            var first = cards(selection.state(), 1).get(0);
            answer(table, answered, selection, Map.of("action", "card", "key", text(first, "key")));
            var chosen = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets")
                    && cards(input.state(), 1).stream().anyMatch(card -> card.get("highlighted").getAsBoolean()));
            assertSame(controller.getInputQueue().getInput(), actual);
            assertNotEquals(text(chosen.prompt(), "id"), text(selection.prompt(), "id"));
            assertEquals(episode(table), episode, "Refreshing a gesture scope cannot reset the actual input's work allowance");
            var selected = cards(chosen.state(), 1).stream().filter(card -> card.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
            var sibling = cards(chosen.state(), 1).stream().filter(card -> !card.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
            assertTrue(selected.get("selectable").getAsBoolean(), "A selected target remains available for undo");
            assertFalse(sibling.get("selectable").getAsBoolean(), "The existing different-controller commitment check excludes an unproductive sibling");
            assertTrue(cards(chosen.state(), 2).get(0).get("selectable").getAsBoolean(), "Another opponent's target remains productive");
            assertPublishedTargets(table, chosen, episode);
            Files.writeString(evidence.resolve("private-productive-other-observation.json"), JSON.toJson(table.automatedSeat(0).automatedObservation()));
            table.game.getAction().withState(() -> {
                var before = JSON.toJsonTree(table.automatedSeat(0).automatedObservation());
                var selectedCard = controller.getCard(actual.getSpellAbility().getTargets().getTargetCards().get(0).getView());
                assertTrue(actual.canSelectCard(selectedCard)); assertFalse(actual.canAddTarget(selectedCard));
                var other = table.game.getRegisteredPlayers().get(1).getCardsIn(ZoneType.Battlefield).stream().filter(card -> card != selectedCard).findFirst().orElseThrow();
                for (int repeat = 0; repeat < 8; repeat++) assertFalse(actual.canAddTarget(other));
                assertEquals(JSON.toJsonTree(table.automatedSeat(0).automatedObservation()), before, "Eligibility queries do not select, publish, tap, open dialogs or change the episode");
                assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(0).action(request(chosen,
                        Map.of("action", "card", "key", text(sibling, "key")))));
                assertEquals(JSON.toJsonTree(table.automatedSeat(0).automatedObservation()), before);
            });
            answer(table, answered, chosen, Map.of("action", "card", "key", text(selected, "key")));
            var undone = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets")
                    && cards(input.state(), 1).stream().noneMatch(card -> card.get("highlighted").getAsBoolean()));
            assertTrue(cards(undone.state(), 1).stream().allMatch(card -> card.get("selectable").getAsBoolean()));
            assertEquals(episode(table), episode);
            assertPublishedTargets(table, undone, episode);
            var again = queuedTargetChange(table, answered, undone, controller);
            assertEquals(episode(table), episode);
            assertPublishedTargets(table, again, episode);
            answer(table, answered, again, Map.of("action", "card", "key", text(cards(again.state(), 2).get(0), "key")));
            var subset = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets")
                    && cards(input.state(), 2).get(0).get("highlighted").getAsBoolean());
            assertEquals(subset.prompt().get("max").getAsInt(), 3); assertTrue(subset.prompt().get("okEnabled").getAsBoolean());
            assertEquals(episode(table), episode);
            assertPublishedTargets(table, subset, episode);
            assertTrue(subset.state().getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                    .flatMap(player -> zone(player, "Battlefield").getAsJsonArray("cards").asList().stream())
                    .map(value -> value.getAsJsonObject()).noneMatch(card -> card.get("selectable").getAsBoolean() && !card.get("highlighted").getAsBoolean()));
            Files.writeString(evidence.resolve("private-valid-subset-observation.json"), JSON.toJson(table.automatedSeat(0).automatedObservation()));
            answer(table, answered, subset, Map.of("action", "ok"));
            var resolved = until(table, answered, 0, input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(cards(resolved.state(), 1).size(), 1); assertEquals(cards(resolved.state(), 2).size(), 0);
            assertEquals(count(player(resolved.state(), 1), "Exile"), 1); assertEquals(count(player(resolved.state(), 2), "Exile"), 1);
            assertNotEquals(episode(table), episode, "A new actual controller input receives its own episode");
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(0).action(request(subset, Map.of("action", "ok"))));
        }
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static List<JsonObject> cards(JsonObject state, int seat) {
        return zone(player(state, seat), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static String episode(MatchTable table) {
        return text(JSON.toJsonTree(table.automatedSeat(0).automatedObservation()).getAsJsonObject().getAsJsonObject("semantics"), "episodeId");
    }
    private static void assertPublishedTargets(MatchTable table, Input current, String episode) {
        HeadlessPlatform.query(table, () -> {
            var session = table.automatedSeat(0);
            var observation = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
            var state = observation.getAsJsonObject("state");
            assertEquals(state, JSON.toJsonTree(session.state()), "Human-compatible state and AI facts share the current publication");
            var prompt = state.getAsJsonObject("prompt");
            assertEquals(prompt.get("id"), current.prompt().get("id"));
            assertEquals(prompt.get("min"), current.prompt().get("min"));
            assertEquals(prompt.get("max"), current.prompt().get("max"));
            var semantics = observation.getAsJsonObject("semantics");
            assertEquals(text(semantics, "episodeId"), episode);
            assertEquals(text(semantics, "intent"), "Exile");
            assertTrue(semantics.getAsJsonArray("selectedPlayers").isEmpty());
            int active = semantics.get("operationId").getAsInt();
            var operation = semantics.getAsJsonObject("operationGraph").getAsJsonArray("operations").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> value.get("id").getAsInt() == active).findFirst().orElseThrow();
            var actualRecipients = operation.getAsJsonObject("recipients").getAsJsonArray("entities").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "cardId")).filter(value -> !value.isEmpty())
                    .collect(java.util.stream.Collectors.toSet());
            var highlighted = state.getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                    .flatMap(player -> zone(player, "Battlefield").getAsJsonArray("cards").asList().stream())
                    .map(value -> value.getAsJsonObject()).filter(card -> card.get("highlighted").getAsBoolean())
                    .map(card -> text(card, "combatId")).collect(java.util.stream.Collectors.toSet());
            assertEquals(actualRecipients, highlighted,
                    "Each refreshed captured graph follows the exact current selected-card highlights, including deselection and queued rejection");
            assertEquals(JSON.toJsonTree(session.automatedObservation()), observation,
                    "The same-cut target read cannot mutate the graph, source, choices or highlights");
            return null;
        });
    }
    private static Input queuedTargetChange(MatchTable table, Map<Integer, String> answered, Input current,
                                             PlayerControllerHuman controller) throws Exception {
        var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
        var first = cards(current.state(), 1).get(0);
        var second = cards(current.state(), 1).get(1);
        var actualFirst = table.game.getRegisteredPlayers().get(1).getCardsIn(ZoneType.Battlefield).get(0).getView();
        HeadlessPlatform.later(() -> {
            entered.countDown();
            try (var suspended = table.game.getAction().suspendState()) {
                if (!release.await(5, TimeUnit.SECONDS)) throw new AssertionError("Queued target barrier expired");
            } catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
        });
        assertTrue(entered.await(3, TimeUnit.SECONDS));
        try {
            // A real controller gesture is already queued ahead of the newly
            // accepted command. It makes the latter's sibling unproductive.
            HeadlessPlatform.later(() -> controller.selectCard(actualFirst, null, null));
            answer(table, answered, current, Map.of("action", "card", "key", text(second, "key")));
        } finally { release.countDown(); }
        answered.remove(0); // The exact owner is intentionally restored, with a fresh publication.
        var restored = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets")
                && cards(input.state(), 1).stream().anyMatch(card -> card.get("highlighted").getAsBoolean()));
        assertEquals(text(restored.prompt(), "id"), text(current.prompt(), "id"));
        assertTrue(restored.prompt().get("okEnabled").getAsBoolean());
        var selected = cards(restored.state(), 1).stream().filter(card -> card.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
        assertEquals(text(selected, "visualId"), text(first, "visualId"));
        assertTrue(selected.get("selectable").getAsBoolean());
        assertFalse(cards(restored.state(), 1).stream().filter(card -> !card.get("highlighted").getAsBoolean()).findFirst().orElseThrow()
                .get("selectable").getAsBoolean(), "The queued rejection republishes the current add-target set, never stale candidate flags");
        assertTrue(restored.state().getAsJsonArray("notices").toString().contains("That target changed"));
        return restored;
    }
    private static void use(MatchTable table, Map<Integer, String> answered, Input input, JsonObject source) {
        var catalog = JSON.toJsonTree(table.automatedSeat(0).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        answer(table, answered, input, Map.of("action", "useSourceAction", "key", text(source, "key"),
                "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id")));
    }
    private static void answer(MatchTable table, Map<Integer, String> answered, Input input, Map<String, ?> answer) {
        table.automatedSeat(0).action(request(input, answer)); answered.put(0, text(input.prompt(), "id"));
    }
}
