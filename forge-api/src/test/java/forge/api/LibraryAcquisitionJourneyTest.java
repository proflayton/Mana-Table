package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.game.card.Card;
import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Provider-only controls: manual option submission verifies current context,
 * actual selected occurrences and rules movement. C# policy continuation has its
 * own journey; these synthetic-game captures are not natural strength results. */
public class LibraryAcquisitionJourneyTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();

    @Test(timeOut = 90000) public void directBattlefieldSelectionResolvesItsActualOfferedLand() throws Exception { play("direct-own-battlefield", LibraryAcquisitionFixtureEngine.Mode.DIRECT_BATTLEFIELD); }
    @Test(timeOut = 90000) public void directHandSelectionResolvesItsActualOfferedLand() throws Exception { play("direct-own-hand", LibraryAcquisitionFixtureEngine.Mode.DIRECT_HAND); }
    @Test(timeOut = 90000) public void bundleSelectionResolvesOneActualLandToFieldAndOneToHand() throws Exception { play("bundle-select", LibraryAcquisitionFixtureEngine.Mode.BUNDLE); }
    @Test(timeOut = 90000) public void renamedBundleKeepsIdenticalMechanicsAndActualDestinations() throws Exception { play("renamed-bundle-select", LibraryAcquisitionFixtureEngine.Mode.RENAMED_BUNDLE); }

    private static void play(String label, LibraryAcquisitionFixtureEngine.Mode mode) throws Exception {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        var paper = LibraryAcquisitionFixtureEngine.paper(mode);
        StaticData.instance().getCommonCards().addCard(paper);
        boolean bundle = mode == LibraryAcquisitionFixtureEngine.Mode.BUNDLE || mode == LibraryAcquisitionFixtureEngine.Mode.RENAMED_BUNDLE;
        try (var game = RulesScenario.automated(label, Map.of(0, List.of(paper.getName())), "Plains", "Rhys the Redeemed")) {
            var main = game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
            var source = zone(viewer(main.state()), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(card -> text(card, "name").equals(paper.getName())).findFirst().orElseThrow();
            var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(main, Map.of("key", text(source, "key"))))).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"), "actionId", text(action, "id"))));
            var choosing = game.until(input -> input.seat() == 0 && text(input.prompt(), "kind").equals("choice")
                    && input.prompt().has("libraryAcquisition"));
            var context = choosing.prompt().getAsJsonObject("libraryAcquisition");
            assertEquals(text(context, "stage"), bundle ? "select-bundle" : "direct");
            assertEquals(text(context, "currentDestination"), bundle ? "Library" : mode == LibraryAcquisitionFixtureEngine.Mode.DIRECT_HAND ? "Hand" : "Battlefield");
            int actualPlayer = choosing.state().get("viewerId").getAsInt();
            assertEquals(context.get("actorPlayerId").getAsInt(), actualPlayer);
            assertEquals(context.get("libraryOwnerId").getAsInt(), actualPlayer);
            assertEquals(context.get("recipientPlayerId").getAsInt(), actualPlayer);
            assertTrue(context.get("additionalCostKnownAbsent").getAsBoolean());
            var owned = game.session(0).automatedObservation();
            assertEquals(AttachmentCapabilitiesTest.map(owned.get("semantics")).get("intent"), "Unknown",
                    "The new context must not incidentally change the frozen baseline's legacy intent");
            Files.writeString(game.evidence.resolve("owned-observation.json"), JSON.toJson(owned));
            Files.writeString(game.evidence.resolve("selection-state.json"), JSON.toJson(choosing.state()));
            var library = zone(viewer(choosing.state()), "Library");
            assertTrue(library.getAsJsonArray("cards").isEmpty(), "A search choice does not inject cards into the ordinary hidden library projection");
            for (var option : choosing.prompt().getAsJsonArray("choices")) {
                var value = option.getAsJsonObject(); var face = value.getAsJsonObject("card");
                assertFalse(value.has("cardId")); assertFalse(face.has("key")); assertFalse(face.has("visualId")); assertFalse(face.has("combatId"));
            }
            for (int seat = 1; seat < 4; seat++) assertTrue(game.state(seat).get("prompt").isJsonNull());
            var actual = HeadlessPlatform.query(game.table, () -> game.table.game.getRegisteredPlayers().get(0).getCardsIn(ZoneType.Library).stream()
                    .sorted(Comparator.comparing((Card card) -> card.getView().getCurrentState().getName(), String.CASE_INSENSITIVE_ORDER)
                            .thenComparingInt(card -> card.getView().getId())).toList());
            int selectedCount = bundle ? 2 : 1;
            var selected = actual.subList(0, selectedCount).stream().map(Card::getId).toList();
            int beforeHand = count(viewer(choosing.state()), "Hand");
            assertTrue(game.answer(choosing, Map.of("choices", bundle ? List.of(0, 1) : List.of(0))));
            if (bundle) {
                var distribute = untilAcknowledgingReveals(game, input -> input.seat() == 0 && text(input.prompt(), "kind").equals("choice"));
                var distribution = distribute.prompt().getAsJsonObject("libraryAcquisition"); assertNotNull(distribution);
                assertEquals(text(distribution, "stage"), "distribute-bundle"); assertEquals(text(distribution, "currentDestination"), "Battlefield");
                Files.writeString(game.evidence.resolve("distribution-owned-observation.json"), JSON.toJson(game.session(0).automatedObservation()));
                assertTrue(game.answer(distribute, Map.of("choices", List.of(0))));
            }
            var resolved = untilAcknowledgingReveals(game, input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            var movement = HeadlessPlatform.query(game.table, () -> {
                var player = game.table.game.getRegisteredPlayers().get(0);
                return selected.stream().map(id -> {
                    var card = game.table.game.getCardState(actual.stream().filter(value -> value.getId() == id).findFirst().orElseThrow());
                    assertNotNull(card); assertEquals(card.getZone().getPlayer(), player);
                    return Map.of("actualCardId", id, "actualDestination", card.getZone().getZoneType().name());
                }).toList();
            });
            long fields = movement.stream().filter(item -> item.get("actualDestination").equals("Battlefield")).count();
            long hands = movement.stream().filter(item -> item.get("actualDestination").equals("Hand")).count();
            assertEquals(fields, mode == LibraryAcquisitionFixtureEngine.Mode.DIRECT_HAND ? 0L : 1L);
            assertEquals(hands, bundle || mode == LibraryAcquisitionFixtureEngine.Mode.DIRECT_HAND ? 1L : 0L);
            assertEquals(count(viewer(resolved.state()), "Hand"), beforeHand + (bundle || mode == LibraryAcquisitionFixtureEngine.Mode.DIRECT_HAND ? 1 : 0));
            assertFalse(resolved.prompt().has("libraryAcquisition"));
            var controller = (PlayerControllerHuman)game.table.game.getRegisteredPlayers().get(0).getController();
            assertNull(controller.getLibraryAcquisitionOffer(), "A completed callback cannot lend its context to the next decision");
            Files.writeString(game.evidence.resolve("resolved-state.json"), JSON.toJson(resolved.state()));
            Files.writeString(game.evidence.resolve("actual-movement-proof.json"), JSON.toJson(Map.of("kind", "provider-only-synthetic-game",
                    "manualSelection", true, "executionId", text(choosing.prompt(), "executionId"), "selectedActualEngineCards", movement)));
        }
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static Input untilAcknowledgingReveals(RulesScenario game, java.util.function.Predicate<Input> stop) throws Exception {
        for (int acknowledged = 0; acknowledged < 16; acknowledged++) {
            var next = game.until(input -> stop.test(input) || text(input.prompt(), "kind").equals("reveal"));
            if (stop.test(next)) return next;
            assertTrue(game.answer(next, Map.of("choices", List.of())));
        }
        throw new AssertionError("The bounded acquisition fixture exceeded its reveal acknowledgements");
    }
}
