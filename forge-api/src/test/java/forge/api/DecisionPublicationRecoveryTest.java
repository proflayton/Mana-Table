package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import forge.StaticData;
import forge.card.MagicColor;
import forge.game.card.Card;
import forge.game.mana.Mana;
import forge.game.event.GameEventPlayerStatsChanged;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A rejected mixed read must not leave Forge waiting for an invisible input. */
public class DecisionPublicationRecoveryTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] readers() {
        return new Object[][] {{false, false}, {true, false}, {false, true}, {true, true}};
    }

    @DataProvider public Object[][] alternateFaces() {
        return new Object[][] {{"Bala Ged Recovery", false}, {"Springheart Nantuko", false},
                {"Bala Ged Recovery", true}, {"Springheart Nantuko", true}};
    }

    @Test(dataProvider = "alternateFaces", timeOut = 90000)
    public void alternateFaceInspectionCannotEmitTransientLandPlayChanges(String name, boolean unlimited) throws Exception {
        try (var game = RulesScenario.automated("landfall-query-events-" + name.replace(' ', '-') + "-" + unlimited,
                Map.of(0, List.of("Aesi, Tyrant of Gyre Strait", unlimited ? "Fastbond" : "Oracle of Mul Daya",
                        "Elven Chorus", "Forest", "Forest", "Island")), "Forest", "Rhys the Redeemed")) {
            game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getPlayer(0);
                // Controlled card membership keeps the baseline reproduction
                // independent of the very publication path being tested.
                var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor);
                actor.getZone(ZoneType.Hand).add(source);
                int lands = actor.getMaxLandPlays(); boolean infinite = actor.getMaxLandPlaysInfinite();
                var stats = new PlayerStats(); game.table.game.subscribeToEvents(stats);
                try {
                    assertFalse(source.getSourceActionAbilities(actor).isEmpty());
                    assertEquals(stats.events, 0, "Inspecting an alternate face cannot publish temporary land-play removals and additions");
                    assertEquals(actor.getMaxLandPlays(), lands); assertEquals(actor.getMaxLandPlaysInfinite(), infinite);
                    game.table.game.getAction().checkStaticAbilities();
                    assertTrue(stats.events > 0, "Real continuous-effect updates must still notify observers");
                } finally { game.table.game.unsubscribeFromEvents(stats); }
                return null;
            });
        }
    }

    @Test(dataProvider = "alternateFaces", timeOut = 90000)
    public void queryingAlternateFacesWithExtraLandPlaysKeepsAUsableDecision(String card, boolean unlimited) throws Exception {
        try (var game = RulesScenario.automated("landfall-alternate-query-" + card.replace(' ', '-') + "-" + unlimited,
                Map.of(0, List.of("Aesi, Tyrant of Gyre Strait", unlimited ? "Fastbond" : "Oracle of Mul Daya", "Elven Chorus", "Forest", "Forest", "Island")),
                card, "Rhys the Redeemed")) {
            var main = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(game.table, () -> null);
            var session = game.session(0);
            var before = game.state(0);
            var observation = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
            assertEquals(text(observation.getAsJsonObject("state"), "status"), "playing");
            assertEquals(text(observation.getAsJsonObject("state").getAsJsonObject("prompt"), "id"), text(main.prompt(), "id"));
            assertEquals(JSON.toJsonTree(session.automatedObservation()), observation);
            assertEquals(game.state(0), before, "Alternate-face inspection must not revoke the waiting input");
            var stats = new PlayerStats();
            game.table.game.subscribeToEvents(stats);
            try {
                HeadlessPlatform.query(game.table, () -> {
                    game.table.game.getAction().checkStaticAbilities(false);
                    assertEquals(stats.events, 0, "Speculative rebuilding and restoration must not publish player changes");
                    game.table.game.getAction().checkStaticAbilities();
                    assertTrue(stats.events > 0, "The event scope must restore ordinary live player notifications");
                    return null;
                });
            } finally { game.table.game.unsubscribeFromEvents(stats); }
        }
    }

    private static final class PlayerStats {
        int events;
        @Subscribe public void changed(GameEventPlayerStatsChanged event) { events++; }
    }

    @DataProvider public Object[][] otherPlayerStatics() {
        return new Object[][] {{"Leyline of Sanctity"}, {"Brago's Representative"}, {"Ballot Broker"}, {"The Valeyard"}};
    }

    @Test(dataProvider = "otherPlayerStatics", timeOut = 90000)
    public void alternateFaceInspectionCannotEmitOtherTransientPlayerChanges(String name) throws Exception {
        try (var game = RulesScenario.automated("player-static-query-" + name.replaceAll("[^A-Za-z]", "-"),
                Map.of(0, List.of(name, "Forest", "Forest", "Island")), "Forest", "Rhys the Redeemed")) {
            game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getPlayer(0);
                var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Bala Ged Recovery"), actor);
                actor.getZone(ZoneType.Hand).add(source);
                var stats = new PlayerStats(); game.table.game.subscribeToEvents(stats);
                try {
                    assertFalse(source.getSourceActionAbilities(actor).isEmpty());
                    assertEquals(stats.events, 0, "A speculative player effect cannot invalidate the current decision");
                    game.table.game.getAction().checkStaticAbilities();
                    assertTrue(stats.events > 0, "Actual player effect notifications remain enabled");
                } finally { game.table.game.unsubscribeFromEvents(stats); }
                return null;
            });
        }
    }

    @Test(dataProvider = "readers", timeOut = 90000)
    public void anUnpublishedRulesEventCannotStrandAnUnansweredDecision(boolean automated, boolean duringRead) throws Exception {
        String name = "decision-publication-recovery-" + automated + "-" + duringRead;
        var battlefield = Map.of(0, List.of("Forest", "Forest", "Forest"));
        try (var game = automated ? RulesScenario.automated(name, battlefield, "Manalith", "Rhys the Redeemed")
                : new RulesScenario(name, battlefield, "Manalith", "Rhys the Redeemed")) {
            var main = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(game.table, () -> null);
            var session = game.session(0);
            var actor = game.table.game.getPlayer(0);
            var controller = (PlayerControllerHuman)actor.getController();
            var input = (InputPassPriority)controller.getInputQueue().getInput();
            var before = game.state(0);
            var forestBefore = zone(viewer(before), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var oldCatalog = JSON.toJsonTree(session.sourceActions(RulesScenario.request(main,
                    Map.of("key", text(forestBefore, "key"))))).getAsJsonObject();
            String oldAction = text(oldCatalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id");
            var old = duringRead ? null : JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
            Runnable change = () -> {
                var forest = actor.getCardsIn(ZoneType.Battlefield).get(0);
                actor.getManaPool().addMana(new Mana(MagicColor.GREEN, forest,
                        forest.getManaAbilities().get(0).getManaPart(), actor));
            };
            HeadlessPlatform.query(game.table, () -> {
                if (duringRead) {
                    // Invalidate a cold source discovery at the same boundary as
                    // the observed real-game query, using a real rules event.
                    try (var hook = new AutomatedObservationTest.DuringDiscovery(actor, change)) {
                        assertThrows(StaleObservationException.class, session::decisionObservation);
                    }
                } else {
                    change.run();
                    var retired = JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
                    assertNull(retired.getAsJsonObject("state").getAsJsonObject("prompt"),
                            "A read cannot authorize the old board after a rules event");
                }
                assertSame(controller.getInputQueue().getInput(), input);
                assertTrue(input.isPriorityActive(), "The rejected read must not answer the input");
                return null;
            });
            // Drain the production input owner, without explicitly publishing
            // the table or supplying a replacement decision on the test's behalf.
            HeadlessPlatform.query(game.table, () -> null);
            var recovered = JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
            var state = recovered.getAsJsonObject("state");
            assertEquals(text(state, "status"), "playing", "The still-owned decision must be republished");
            assertNotNull(state.getAsJsonObject("prompt"), "Forge is waiting for this exact input");
            assertEquals(text(state.getAsJsonObject("prompt"), "id"), text(main.prompt(), "id"));
            assertTrue(state.get("boardRevision").getAsLong() > before.get("boardRevision").getAsLong());
            assertEquals(viewer(state).getAsJsonObject("mana").get("G").getAsInt(), 1);
            assertEquals(text(recovered.getAsJsonObject("domain"), "coverage"), "Complete");
            assertSame(controller.getInputQueue().getInput(), input); assertTrue(input.isPriorityActive());
            assertNull(session.state().get("error"));
            assertThrows(IllegalArgumentException.class, () -> session.action(RulesScenario.request(main, Map.of(
                    "action", "useSourceAction", "key", text(forestBefore, "key"), "actionId", oldAction))));
            var recoveredInput = new Input(0, state, state.getAsJsonObject("prompt"));
            var forest = zone(viewer(state), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(session.sourceActions(RulesScenario.request(recoveredInput,
                    Map.of("key", text(forest, "key"))))).getAsJsonObject();
            assertTrue(game.answer(recoveredInput, Map.of("action", "useSourceAction", "key", text(forest, "key"),
                    "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
            var acted = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 2);
            assertTrue(zone(viewer(acted.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(card -> card.getAsJsonObject().get("tapped").getAsBoolean()));
            Files.writeString(game.evidence.resolve("recovered-decision.json"), JSON.toJson(Map.of(
                    "before", before, "oldObservation", old == null ? Map.of() : old, "recovered", recovered, "acted", acted.state())));
        }
    }
}
