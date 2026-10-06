package forge.api;

import forge.deck.Deck;
import forge.game.Game;
import forge.game.GameRules;
import forge.game.GameType;
import forge.game.Match;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.player.RegisteredPlayer;
import forge.player.LobbyPlayerHuman;
import forge.player.PlayerControllerHuman;
import forge.trackable.TrackableCollection;
import forge.trackable.TrackableProperty;
import org.testng.annotations.Test;

import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

import static org.testng.Assert.*;

public class NetworkSnapshotConcurrencyTest {
    @Test(timeOut = 10000)
    public void pollingCannotObserveACollectionWhileFullStateRebuildsIt() throws Exception {
        forge.util.Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages");
        forge.util.Lang.createInstance("en-US");
        var seats = List.of(seat("Host"), seat("Guest"));
        var rules = new GameRules(GameType.Constructed);
        var game = new Game(seats, rules, new Match(rules, seats, "Snapshot regression"));
        var rebuilding = new CountDownLatch(1);
        var resume = new CountDownLatch(1);
        var players = new TrackableCollection<PlayerView>(game.getView().getPlayers()) {
            @Override public void clear() {
                super.clear();
                rebuilding.countDown();
                try {
                    if (!resume.await(5, TimeUnit.SECONDS)) throw new AssertionError("Snapshot test did not resume the update");
                } catch (InterruptedException e) { Thread.currentThread().interrupt(); throw new AssertionError(e); }
            }
        };
        game.getView().set(TrackableProperty.Players, players);
        var session = new NetworkMatchSession();
        session.setGameView(game.getView());
        assertEquals(((List<?>) session.state().get("players")).size(), 2);
        var workers = Executors.newFixedThreadPool(2);
        try {
            // Full-state synchronization resolves collection references in place.
            // Hold that update between clear and refill while a renderer polls.
            var update = workers.submit(() -> session.setGameView(game.getView()));
            assertTrue(rebuilding.await(3, TimeUnit.SECONDS));
            var polling = new CountDownLatch(1);
            var read = workers.submit(() -> { polling.countDown(); return session.state(); });
            assertTrue(polling.await(3, TimeUnit.SECONDS));
            try {
                var partial = read.get(150, TimeUnit.MILLISECONDS);
                fail("Polling exposed an unfinished view: " + partial.get("players"));
            } catch (TimeoutException expected) { /* Reads wait for the complete update. */ }
            resume.countDown();
            update.get(3, TimeUnit.SECONDS);
            assertEquals(((List<?>) read.get(3, TimeUnit.SECONDS).get("players")).size(), 2);
        } finally {
            resume.countDown();
            workers.shutdownNow();
        }
    }

    private static RegisteredPlayer seat(String name) {
        return new RegisteredPlayer(new Deck()).setPlayer(new LobbyPlayerHuman(name) {
            @Override public Player createIngamePlayer(Game game, int id) {
                var player = new Player(getName(), game, id);
                player.setFirstController(new PlayerControllerHuman(game, player, this));
                return player;
            }
        });
    }
}
