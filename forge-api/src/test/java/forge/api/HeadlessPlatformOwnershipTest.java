package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.Game;
import forge.game.GameAction;
import forge.gui.GuiBase;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static org.testng.Assert.*;

/** A delayed callback keeps its originating game even after another table becomes active. */
public class HeadlessPlatformOwnershipTest {
    @Test(timeOut = 60000)
    public void gameOwnedQueuedSynchronousAndExplicitQueryWorkCannotMigrateToTheNextTable() throws Exception {
        Path resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var players = List.of(MatchTable.human(new Deck(), "Constructed", "First"),
                MatchTable.human(new Deck(), "Constructed", "Second"));
        try (var first = new MatchTable("Constructed", players, resources, Files.createTempDirectory("headless-owner-first"));
             var next = new MatchTable("Constructed", players, resources, Files.createTempDirectory("headless-owner-next"))) {
            HeadlessPlatform.activate(first);
            var queued = new CompletableFuture<Game>();
            first.game.getAction().withState(() -> {
                HeadlessPlatform.activate(next);
                HeadlessPlatform.later(() -> queued.complete(GameAction.currentGame()));
            });
            assertSame(queued.get(3, TimeUnit.SECONDS), first.game,
                    "Queued work belongs to its originating game, not the most recently activated table");
            assertSame(HeadlessPlatform.query(first, GameAction::currentGame), first.game,
                    "An explicit old-table observation acquires that table's own state boundary");
            var synchronous = new AtomicReference<Game>();
            first.game.getAction().withState(() -> GuiBase.getInterface().invokeInEdtAndWait(
                    () -> synchronous.set(GameAction.currentGame())));
            assertSame(synchronous.get(), first.game,
                    "Synchronous GUI work releases/restores the correct game boundary while waiting");
            assertNull(GameAction.currentGame(), "The caller does not retain either game's ownership after completion");
        }
    }
}
