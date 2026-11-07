package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The rules zone and the delayed GUI cache must never supply different halves of a publication. */
public class CurrentZonePublicationTest {
    private static final com.google.gson.Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();

    @DataProvider public Object[][] depths() { return new Object[][] {{1}, {2}}; }

    @Test(dataProvider = "depths", timeOut = 90000)
    public void anOwnedInputPublishesOneCommittedZoneCutThroughNestedViewFreezes(int depth) throws Exception {
        exercise(depth, false);
    }

    @Test(dataProvider = "depths", timeOut = 90000)
    public void anIncidentalRefreshRetainsThePriorCohortUntilAnActualInputBoundary(int depth) throws Exception {
        exercise(depth, true);
    }

    @Test(dataProvider = "depths", timeOut = 90000)
    public void aCommittedFrozenLossCannotReopenTheDepartedPlayersCurrentInput(int depth) throws Exception {
        try (var scenario = new RulesScenario("current-loss-publication-" + depth, Map.of(0, List.of("Grizzly Bears")))) {
            scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            HeadlessPlatform.query(scenario.table, () -> {
                var game = scenario.table.game;
                var actor = game.getPlayer(0);
                var tracker = game.getTracker();
                for (int index = 0; index < depth; index++) tracker.freeze();
                try {
                    actor.concede(); game.onPlayerLost(actor);
                    assertTrue(actor.hasLost());
                    assertFalse(actor.getView().getHasLost(), "The actual committed loss is still delayed in the GUI cache");
                    scenario.table.publishInput();
                    var state = JSON.toJsonTree(scenario.table.seat(0).state()).getAsJsonObject();
                    Files.writeString(scenario.evidence.resolve("owned-frozen-loss.json"), JSON.toJson(state));
                    assertTrue(player(state, actor.getId()).get("eliminated").getAsBoolean(), "The owned cut copies the actual loss before choosing a live-input branch");
                    assertTrue(state.get("prompt").isJsonNull(), "A lost controller cannot regain a current selectable prompt after the flush");
                    assertTrue(zone(player(state, actor.getId()), "Battlefield").getAsJsonArray("cards").isEmpty());
                    assertEquals(game.getPlayers().size(), 3, "This is a departure, not a manufactured table outcome");
                    assertFalse(game.isGameOver());
                    assertTrue(tracker.isFrozen());
                } finally {
                    for (int index = 0; index < depth; index++) tracker.unfreeze();
                }
                assertFalse(tracker.isFrozen());
                return null;
            });
        }
    }

    @Test(dataProvider = "depths", timeOut = 90000)
    public void aCompletedRulesOutcomePublishesWithoutWaitingForAnotherDecisionOrOuterUnfreeze(int depth) throws Exception {
        try (var scenario = new RulesScenario("current-terminal-publication-" + depth, 2)) {
            scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            HeadlessPlatform.query(scenario.table, () -> {
                var game = scenario.table.game;
                var tracker = game.getTracker();
                for (int index = 0; index < depth; index++) tracker.freeze();
                try {
                    var loser = game.getPlayer(1);
                    loser.concede(); game.onPlayerLost(loser);
                    game.getAction().checkGameOverCondition();
                    assertTrue(game.isGameOver()); assertNotNull(game.getOutcome());
                    scenario.table.publish();
                    for (int seat = 0; seat < 2; seat++) {
                        var state = JSON.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject();
                        Files.writeString(scenario.evidence.resolve("frozen-terminal-seat-" + seat + ".json"), JSON.toJson(state));
                        assertEquals(text(state, "status"), "finished", "An actual completed rules outcome is its own committed publication boundary");
                        assertTrue(state.get("prompt").isJsonNull());
                        assertEquals(text(state.getAsJsonObject("outcome"), "kind"), "win");
                        assertEquals(state.getAsJsonObject("outcome").getAsJsonArray("winningPlayerIds").get(0).getAsInt(), 0);
                        assertEquals(text(state, "result"), seat == 0 ? "Victory" : "Defeat");
                        assertTrue(player(state, loser.getId()).get("eliminated").getAsBoolean());
                        assertCurrentBattlefieldIdentities(state);
                    }
                    assertTrue(tracker.isFrozen());
                } finally {
                    for (int index = 0; index < depth; index++) tracker.unfreeze();
                }
                assertFalse(tracker.isFrozen());
                return null;
            });
        }
    }

    private static void exercise(int depth, boolean incidental) throws Exception {
        try (var scenario = new RulesScenario("current-zone-publication-" + depth + "-" + incidental,
                Map.of(0, List.of("Grizzly Bears")))) {
            scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            HeadlessPlatform.query(scenario.table, () -> {
                var game = scenario.table.game;
                var actor = game.getRegisteredPlayers().get(0);
                var leaving = actor.getCardsIn(ZoneType.Battlefield).stream()
                        .filter(card -> card.getName().equals("Grizzly Bears")).findFirst().orElseThrow();
                var priorView = leaving.getView();
                var tracker = game.getTracker();
                var before = JSON.toJsonTree(scenario.table.seat(0).state()).getAsJsonObject();
                for (int index = 0; index < depth; index++) tracker.freeze();
                try {
                    var arrived = game.getAction().moveToHand(leaving, null);
                    assertSame(arrived.getZone(), actor.getZone(ZoneType.Hand), "The actual rules move is already committed");
                    assertFalse(CurrentCardViews.isCurrent(game, priorView), "The departing view is historical despite its delayed zone cache");
                    assertEquals(priorView.getZone(), ZoneType.Battlefield, "This is a real frozen GUI cache, not a fabricated missing ID");
                    if (incidental) {
                        var delayedZone = arrived.getView().getZone();
                        scenario.table.publish();
                        var retained = JSON.toJsonTree(scenario.table.seat(0).state()).getAsJsonObject();
                        assertTrue(retained.equals(before), "An incidental frozen refresh must retain the entire prior coherent cohort");
                        assertEquals(arrived.getView().getZone(), delayedZone, "Incidental refreshes cannot flush speculative or delayed view changes");
                    }
                    // This is the actual still-owned InputQueue/InputProxy
                    // boundary, not an ordinary polling read or a rules event.
                    scenario.table.publishInput();
                    assertTrue(tracker.isFrozen(), "Publishing a required input preserves the engine's outer freeze lifetime");
                    for (int seat = 0; seat < 4; seat++) {
                        var state = JSON.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject();
                        Files.writeString(scenario.evidence.resolve("owned-cut-seat-" + seat + ".json"), JSON.toJson(state));
                        var owner = player(state, actor.getId());
                        assertEquals(zone(owner, "Battlefield").get("count").getAsInt(), 0,
                                "The board count comes from the same committed zone cut as its faces");
                        assertTrue(zone(owner, "Battlefield").getAsJsonArray("cards").isEmpty(),
                                "An obsolete battlefield portrait cannot be published as an identity-free current permanent");
                        assertEquals(zone(owner, "Hand").get("count").getAsInt(), actor.getCardsIn(ZoneType.Hand).size());
                        if (seat == 0) {
                            var current = zone(owner, "Hand").getAsJsonArray("cards").asList().stream()
                                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Grizzly Bears"))
                                    .findFirst().orElse(null);
                            assertNotNull(current, "The owned decision must include the actual moved hand face, not a stale mixed zone cut");
                            assertFalse(text(current, "visualId").isEmpty(), "The entitled current hand occurrence remains identifiable");
                        } else assertTrue(zone(owner, "Hand").getAsJsonArray("cards").isEmpty(),
                                "Refreshing a committed cut cannot disclose the moved hand card to another audience");
                        assertCurrentBattlefieldIdentities(state);
                    }
                } finally {
                    for (int index = 0; index < depth; index++) tracker.unfreeze();
                }
                assertFalse(tracker.isFrozen(), "Publication did not add or consume an outer freeze");
                return null;
            });
        }
    }

    private static void assertCurrentBattlefieldIdentities(JsonObject state) {
        for (var value : state.getAsJsonArray("players")) {
            var owner = value.getAsJsonObject();
            for (var card : zone(owner, "Battlefield").getAsJsonArray("cards")) {
                var face = card.getAsJsonObject();
                assertTrue(!text(face, "visualId").isBlank() || !text(face, "combatId").isBlank(),
                        "Every actual public battlefield occurrence retains an identity");
            }
        }
    }
}
