package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.zone.ZoneType;
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
        Path evidence = Path.of("target/rules-regressions/target-selection-progress"); Files.createDirectories(evidence);
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
            var again = queuedTargetChange(table, answered, undone, controller);
            assertEquals(episode(table), episode);
            answer(table, answered, again, Map.of("action", "card", "key", text(cards(again.state(), 2).get(0), "key")));
            var subset = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets")
                    && cards(input.state(), 2).get(0).get("highlighted").getAsBoolean());
            assertEquals(subset.prompt().get("max").getAsInt(), 3); assertTrue(subset.prompt().get("okEnabled").getAsBoolean());
            assertEquals(episode(table), episode);
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
