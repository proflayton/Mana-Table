package forge.api;

import com.google.gson.Gson;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectTargets;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

import static org.testng.Assert.*;

/** An interrupted host cannot start rules-driven loss/reveal choices during teardown. */
public class MatchFaultAbortTest {
    @DataProvider public Object[][] endings() { return new Object[][] { { "automation" }, { "session" }, { "stop" } }; }

    @Test(dataProvider = "endings", timeOut = 90000)
    public void aPendingTargetAndConcealedPermanentCannotBlockFaultOrExplicitStop(String route) throws Exception {
        boolean fault = !route.equals("stop");
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var workers = Executors.newCachedThreadPool(task -> {
            var thread = new Thread(task, "fault-abort-regression"); thread.setDaemon(true); return thread;
        });
        var players = java.util.stream.IntStream.range(0, 4)
                .mapToObj(index -> MatchTable.automated(new Deck(), "Constructed", "Policy " + index)).toList();
        var table = new MatchTable("Constructed", players, resources, Files.createTempDirectory("fault-abort"), true);
        Future<?> waiting = null, stopping = null;
        try {
            HeadlessPlatform.activate(table);
            var owner = table.game.getRegisteredPlayers().get(0);
            var actor = table.game.getRegisteredPlayers().get(2);
            var concealed = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), owner);
            owner.getZone(ZoneType.Battlefield).add(concealed);
            assertTrue(concealed.turnFaceDown(true));
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Flame Slash"), actor);
            actor.getZone(ZoneType.Hand).add(source);
            var ability = source.getSpellAbilities().stream().filter(part -> part.getApi() == ApiType.DealDamage).findFirst().orElseThrow();
            ability.setActivatingPlayer(actor);
            var controller = (PlayerControllerHuman) actor.getController();
            var input = new InputSelectTargets(controller, List.of(concealed), ability, false, null, null, target -> true, false);
            waiting = workers.submit(() -> table.game.getAction().withState(input::showAndWait));
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
            while (table.automatedSeat(actor.getId()).state().get("prompt") == null && System.nanoTime() < deadline)
                Thread.sleep(5);
            var before = table.automatedSeats().stream().map(MatchSession::state).toList();
            var prompt = (Map<?, ?>) before.get(2).get("prompt");
            assertNotNull(prompt, "The actual target-selection input must be published before interruption");
            assertEquals(prompt.get("inputType"), "InputSelectTargets");
            stopping = workers.submit(() -> {
                if (route.equals("automation")) table.automatedSeat(actor.getId()).automationFault();
                else if (route.equals("session")) table.automatedSeat(actor.getId()).fail(new IllegalStateException("PRIVATE_PROVIDER_FAULT_SENTINEL"));
                else table.close();
            });
            stopping.get(3, TimeUnit.SECONDS);
            waiting.get(3, TimeUnit.SECONDS);
            HeadlessPlatform.query(table, () -> null); // Drain callbacks queued before the interruption.
            if (fault) table.publish(); // The same publication performed by the rules-thread finalizer.
            assertTrue(table.game.isGameOver());
            assertFalse(table.game.getRegisteredPlayers().stream().anyMatch(player -> player.conceded()),
                    "A provider fault or stop is not a player's rules concession");
            assertTrue(concealed.isFaceDown(), "Teardown cannot reveal a private original face");
            assertTrue(concealed.isInZone(ZoneType.Battlefield), "Loss cleanup must not remove the preserved board");
            if (fault) for (int index = 0; index < before.size(); index++) {
                var state = table.automatedSeats().get(index).state();
                assertEquals(state.get("status"), "error");
                assertNull(state.get("prompt"), "No new reveal acknowledgement can appear during fault handling");
                assertNull(state.get("result"), "A backend interruption cannot be published as a victory or defeat");
                assertFalse(String.valueOf(state.get("error")).contains("PRIVATE_PROVIDER_FAULT_SENTINEL"));
                assertEquals(state.get("players"), before.get(index).get("players"));
            }
            assertFalse(new Gson().toJson(table.spectatorState()).contains("Grizzly Bears"));
            if (fault) assertNull(table.spectatorState().get("result"), "A spectator cannot mistake an aborted game for a rules outcome");
            workers.submit(table::close).get(3, TimeUnit.SECONDS);
        } finally {
            if (stopping != null) stopping.cancel(true);
            if (waiting != null) waiting.cancel(true);
            workers.shutdownNow();
            table.close();
        }
    }
}
