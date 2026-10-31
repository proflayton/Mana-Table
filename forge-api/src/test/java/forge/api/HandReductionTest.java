package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectHandSizeDiscard;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.IntStream;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class HandReductionTest {
    @Test(timeOut = 90000)
    public void aThirtyFiveCardHandCommitsOneDiscardSetAndPreservesEveryRealTrigger() throws Exception {
        try (var game = RulesScenario.largeHand("hand-reduction-35-to-7", Map.of(0, List.of("Bag of Holding")), 34)) {
            var cleanup = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectHandSizeDiscard"));
            var prompt = cleanup.prompt();
            assertEquals(text(prompt, "kind"), "choice"); assertEquals(text(prompt, "task"), "choice");
            assertEquals(prompt.get("min").getAsInt(), 28); assertEquals(prompt.get("max").getAsInt(), 28);
            var context = prompt.getAsJsonObject("handReduction");
            assertEquals(context.get("playerId"), cleanup.state().get("viewerId"));
            assertEquals(context.get("handSize").getAsInt(), 35); assertEquals(context.get("maximumHandSize").getAsInt(), 7);
            var choices = prompt.getAsJsonArray("choices"); assertEquals(choices.size(), 35);
            var identities = new HashSet<String>();
            for (int index = 0; index < choices.size(); index++) {
                var option = choices.get(index).getAsJsonObject(); var face = option.getAsJsonObject("card");
                assertEquals(option.get("index").getAsInt(), index); assertEquals(text(face, "name"), "Forest");
                assertTrue(identities.add(text(option, "cardId")), "Same-printing cards retain distinct current occurrences");
                assertEquals(text(option, "cardId"), text(face, "combatId"));
                assertEquals(text(face, "key"), ""); assertFalse(face.get("selectable").getAsBoolean());
                assertFalse(face.get("highlighted").getAsBoolean());
            }
            for (int seat = 1; seat < 4; seat++) {
                var other = game.state(seat);
                assertTrue(other.get("prompt").isJsonNull(), "Only the deciding seat receives the private hand choice");
                assertTrue(zone(player(other, context.get("playerId").getAsInt()), "Hand").getAsJsonArray("cards").isEmpty());
            }
            var controller = (PlayerControllerHuman)game.table.game.getRegisteredPlayers().get(0).getController();
            var actual = HeadlessPlatform.query(game.table,
                    () -> (InputSelectHandSizeDiscard)controller.getInputQueue().getInput());
            var discard = IntStream.range(7, 35).boxed().toList();
            var duplicate = new ArrayList<>(discard); duplicate.set(27, duplicate.get(0));
            game.rejected(cleanup, Map.of("choices", duplicate), "Invalid or duplicate discard choice");
            var outside = new ArrayList<>(discard); outside.set(27, 35);
            game.rejected(cleanup, Map.of("choices", outside), "Invalid or duplicate discard choice");
            game.rejected(cleanup, Map.of("choices", discard.subList(0, 27)), "Choose the required number of cards to discard");
            var fractional = new ArrayList<Number>(discard); fractional.set(27, 34.5);
            game.rejected(cleanup, Map.of("choices", fractional), "Invalid or duplicate discard choice");
            HeadlessPlatform.query(game.table, () -> {
                assertTrue(actual.getSelected().isEmpty()); assertTrue(actual.isSelectionActive());
                assertEquals(controller.getPlayer().getCardsIn(ZoneType.Hand).size(), 35);
                return null;
            });
            var waits = new AtomicInteger(); var original = controller.getGui();
            controller.setGui(ManyPublicationTest.intercept(original, (name, args) -> {
                if (name.equals("awaitNextInput")) {
                    assertFalse(actual.isSelectionActive(), "The batch retires before the waiting transition"); waits.incrementAndGet();
                }
                if (name.equals("clearSelectables")) assertFalse(actual.isSelectionActive());
            }));
            try {
                assertTrue(game.answer(cleanup, Map.of("choices", discard)), "One command submits the entire complement of the seven kept cards");
                HeadlessPlatform.query(game.table, () -> {
                    assertFalse(actual.isSelectionActive()); assertEquals(actual.getSelected().size(), 28);
                    assertEquals(waits.get(), 1); return null;
                });
            } finally { controller.setGui(original); }
            RulesScenario.Input triggered;
            while (true) {
                triggered = game.until(input -> text(input.state(), "phaseKey").equals("CLEANUP")
                        && input.state().getAsJsonArray("stack").size() == 28 || text(input.prompt(), "kind").equals("choice"));
                if (triggered.state().getAsJsonArray("stack").size() == 28) break;
                assertTrue(triggered.prompt().get("ordered").getAsBoolean(), "Only the engine's trigger ordering may intervene");
                assertTrue(game.answer(triggered, Map.of("choices", IntStream.range(0,
                        triggered.prompt().getAsJsonArray("choices").size()).boxed().toList())));
            }
            int owner = context.get("playerId").getAsInt();
            assertEquals(count(player(triggered.state(), owner), "Hand"), 7);
            assertEquals(count(player(triggered.state(), owner), "Graveyard"), 28);
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(cleanup, Map.of("choices", discard))));
            assertEquals(count(viewer(game.state(0)), "Hand"), 7, "A stale replay cannot select or discard another card");
            var responders = new HashSet<Integer>();
            game.until(input -> {
                if (text(input.state(), "phaseKey").equals("CLEANUP") && !input.state().getAsJsonArray("stack").isEmpty())
                    responders.add(input.seat());
                return input.state().get("turn").getAsInt() == 2;
            });
            assertEquals(responders.size(), 4, "Every seat retains its genuine cleanup response windows");
            assertEquals(count(viewer(game.state(0)), "Hand"), 7);
            assertEquals(count(viewer(game.state(0)), "Graveyard"), 0);
            assertEquals(count(viewer(game.state(0)), "Exile"), 28, "All twenty-eight Bag of Holding triggers resolve through Forge");
        }
    }

    @Test(timeOut = 20000)
    public void aBulkInputRejectsForeignCardsChangedHandsAndReplacedOwnersBeforeMutation() throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var players = IntStream.range(0, 4).mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Policy " + seat)).toList();
        try (var table = new MatchTable("Constructed", players, resources, ApiTestWorkspace.evidence("hand-reduction-scope"), true)) {
            HeadlessPlatform.activate(table);
            var player = table.game.getRegisteredPlayers().get(0);
            var controller = (PlayerControllerHuman)player.getController();
            var cards = new ArrayList<Card>();
            for (int index = 0; index < 35; index++) {
                var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Forest"), player);
                player.getZone(ZoneType.Hand).add(card); cards.add(card);
            }
            var foreignOwner = table.game.getRegisteredPlayers().get(1);
            var foreign = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Forest"), foreignOwner);
            foreignOwner.getZone(ZoneType.Hand).add(foreign);
            var input = new InputSelectHandSizeDiscard(controller, 28);
            controller.getInputQueue().setInput(input);
            HeadlessPlatform.query(table, () -> {
                var offer = input.getHandReductionOffer(); assertNotNull(offer);
                var selected = new ArrayList<>(cards.subList(7, 35));
                assertTrue(input.acceptsSelection(offer, selected));
                selected.set(27, foreign); assertFalse(input.submitSelection(offer, selected));
                selected.set(27, cards.get(7)); assertFalse(input.submitSelection(offer, selected));
                selected.set(27, cards.get(34));
                var replacement = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Forest"), player);
                player.getZone(ZoneType.Hand).remove(cards.get(0)); player.getZone(ZoneType.Hand).add(replacement);
                assertFalse(input.submitSelection(offer, selected), "A changed unselected occurrence invalidates the entire original hand offer");
                assertTrue(input.getSelected().isEmpty()); assertTrue(input.isSelectionActive());
                assertEquals(player.getCardsIn(ZoneType.Hand).size(), 35);
                var next = new InputSelectHandSizeDiscard(controller, 28);
                controller.getInputQueue().setInput(next);
                assertFalse(input.submitSelection(offer, selected), "A replaced input can never regain authority from its old offer");
                assertTrue(input.getSelected().isEmpty()); assertTrue(next.getSelected().isEmpty());
                return null;
            });
        }
    }
}
