package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.Game;
import forge.game.GameAction;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.player;
import static org.testng.Assert.*;

/** Multiple audiences and controller waits share one per-game rules-state boundary. */
public class GameStateBoundaryTest {
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static final Gson JSON = new Gson();

    private static MatchTable simulation() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        return new MatchTable("Commander", List.of(
                MatchTable.automated(new Deck(), "Commander", "Policy 1"),
                MatchTable.automated(new Deck(), "Commander", "Policy 2")), RESOURCES,
                Files.createTempDirectory("atomic-rules-observation"), true);
    }

    @Test(timeOut = 60000)
    public void aSharedBoardRevisionCannotObserveHalfAnEngineMutation() throws Exception {
        var workers = Executors.newFixedThreadPool(2);
        var changedFirstPlayer = new CountDownLatch(1);
        var finishMutation = new CountDownLatch(1);
        var requestedObservation = new CountDownLatch(1);
        var mutatingThread = new AtomicLong();
        var observingThread = new AtomicReference<Thread>();
        try (var table = simulation()) {
            var first = table.game.getPlayers().get(0);
            var second = table.game.getPlayers().get(1);
            var mutation = workers.submit(() -> table.game.getAction().withState(() -> {
                mutatingThread.set(Thread.currentThread().getId());
                first.setLife(17, null);
                changedFirstPlayer.countDown();
                try { assertTrue(finishMutation.await(5, TimeUnit.SECONDS)); }
                catch (InterruptedException error) { throw new AssertionError(error); }
                second.setLife(29, null);
            }));
            assertTrue(changedFirstPlayer.await(5, TimeUnit.SECONDS));
            var observation = workers.submit(() -> {
                observingThread.set(Thread.currentThread());
                requestedObservation.countDown();
                table.publish();
                return table.spectatorState();
            });
            assertTrue(requestedObservation.await(5, TimeUnit.SECONDS));
            StateBoundaryContention.await(observingThread.get(), mutatingThread.get());
            assertFalse(observation.isDone(), "The actual game-state boundary holds publication until mutation ends");
            finishMutation.countDown();
            mutation.get(5, TimeUnit.SECONDS);
            var publicState = JSON.toJsonTree(observation.get(5, TimeUnit.SECONDS)).getAsJsonObject();
            assertEquals(player(publicState, first.getId()).get("life").getAsInt(), 17);
            assertEquals(player(publicState, second.getId()).get("life").getAsInt(), 29);
            for (var seat : table.automatedSeats()) {
                JsonObject privateState = JSON.toJsonTree(seat.state()).getAsJsonObject();
                assertEquals(privateState.get("boardRevision"), publicState.get("boardRevision"));
                assertEquals(player(privateState, first.getId()).get("life"), player(publicState, first.getId()).get("life"));
                assertEquals(player(privateState, second.getId()).get("life"), player(publicState, second.getId()).get("life"));
            }
        } finally { finishMutation.countDown(); workers.shutdownNow(); }
    }

    @Test(timeOut = 60000)
    public void aRequiredChoiceReleasesAllNestedOwnershipAndRestoresItAfterAnswering() throws Exception {
        var workers = Executors.newFixedThreadPool(2);
        var enteredChoice = new CountDownLatch(1);
        var answer = new CompletableFuture<Integer>();
        try (var table = simulation()) {
            var action = table.game.getAction();
            var waitingController = workers.submit(() -> action.readState(() -> action.readState(() -> {
                enteredChoice.countDown();
                try { return action.awaitState(answer); }
                catch (InterruptedException | ExecutionException error) { throw new AssertionError(error); }
            })));
            assertTrue(enteredChoice.await(5, TimeUnit.SECONDS));
            var answeringController = workers.submit(() -> action.readState(() -> {
                table.publish();
                answer.complete(7);
                return true;
            }));
            assertTrue(answeringController.get(5, TimeUnit.SECONDS));
            assertEquals(waitingController.get(5, TimeUnit.SECONDS), Integer.valueOf(7));
            assertEquals(action.readState(() -> 11), Integer.valueOf(11),
                    "Nested ownership was restored and fully released after the controller returned");
        } finally { answer.completeExceptionally(new CancellationException("Test ended")); workers.shutdownNow(); }
    }

    @Test(timeOut = 60000)
    public void nestedAndAsynchronousActionsKeepTheirActualGameContext() throws Exception {
        try (var first = simulation(); var second = simulation()) {
            assertNull(GameAction.currentGame());
            first.game.getAction().withState(() -> {
                assertSame(GameAction.currentGame(), first.game);
                assertSame(second.game.getAction().readState(GameAction::currentGame), second.game);
                assertSame(GameAction.currentGame(), first.game, "A nested game's scope restores its caller");
                try {
                    second.game.getAction().callState(() -> { throw new IllegalStateException("Controlled context exit"); });
                    fail("The controlled exception must propagate");
                } catch (Exception error) { assertEquals(error.getMessage(), "Controlled context exit"); }
                assertSame(GameAction.currentGame(), first.game, "Exceptional exit also restores its caller");
            });
            assertNull(GameAction.currentGame(), "A completed game task cannot contaminate its worker's next task");
            var observed = new CompletableFuture<Game>();
            first.game.getAction().invoke(() -> observed.complete(GameAction.currentGame()));
            assertSame(observed.get(5, TimeUnit.SECONDS), first.game,
                    "A child rules task identifies its game without a mutable global active table");
        }
    }
}
