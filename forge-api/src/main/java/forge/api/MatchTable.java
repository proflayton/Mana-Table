package forge.api;

import forge.deck.Deck;
import forge.game.*;
import forge.game.player.RegisteredPlayer;
import forge.gui.interfaces.IGuiGame;
import forge.player.LobbyPlayerHuman;
import forge.player.PlayerControllerHuman;
import java.nio.file.Path;
import java.util.*;

/** The sole owner of a Forge game. Seats, projections and decisions never depend on transport. */
final class MatchTable implements ManaTableSession, AutoCloseable {
    final String id = UUID.randomUUID().toString();
    final Object gate = new Object();
    final Game game;
    final String format;
    final MatchIdentities identities = new MatchIdentities();
    private final Match match;
    private final Map<Integer, MatchSession> seats = new LinkedHashMap<>();
    private final ThreadLocal<MatchSession> dialogOwner = new ThreadLocal<>();
    private long boardRevision;
    private boolean closed;

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile) {
        HeadlessPlatform.initialize(resources, profile);
        this.format = format;
        var rules = new GameRules(GameType.Constructed);
        if (format.equals("Commander")) rules.addAppliedVariant(GameType.Commander);
        rules.setGamesPerMatch(1);
        rules.setWarnAboutAICards(false);
        match = new Match(rules, players, "Mana Table");
        game = match.createGame();
        game.subscribeToEvents(identities);
        for (int i = 0; i < game.getPlayers().size(); i++) {
            var player = game.getPlayers().get(i);
            if (player.getController() instanceof PlayerControllerHuman human) seats.put(i, new MatchSession(this, human));
            player.updateOpponentsForView();
        }
        if (seats.isEmpty()) throw new IllegalArgumentException("A table needs at least one human seat");
    }

    static RegisteredPlayer human(Deck deck, String format, String name) {
        return (format.equals("Commander") ? RegisteredPlayer.forCommander(new Deck(deck)) : new RegisteredPlayer(new Deck(deck)))
                .setPlayer(new LobbyPlayerHuman(name));
    }

    MatchSession seat(int index) {
        var seat = seats.get(index);
        if (seat == null) throw new IllegalArgumentException("No human in this seat");
        return seat;
    }
    List<Map<String, Object>> testViews() {
        synchronized (gate) { return seats.values().stream().map(MatchSession::state).toList(); }
    }

    void start() {
        HeadlessPlatform.activate(this);
        game.getAction().invoke(() -> {
            try {
                match.startGame(game);
                synchronized (gate) { for (var seat : seats.values()) seat.clearDecision(); publish(); }
            } catch (Throwable failure) { if (!closed) fail(failure); }
            finally {
                game.unsubscribeFromEvents(identities);
                for (var seat : seats.values()) seat.detach();
            }
        });
    }

    // Only called at controller input boundaries, while the game is waiting.
    void publish() {
        synchronized (gate) {
            long revision = ++boardRevision;
            for (var seat : seats.values()) seat.publishView(revision);
        }
    }

    void enter(MatchSession seat) { dialogOwner.set(seat); }
    void stopIfNoHumans() {
        if (!game.isGameOver() && seats.values().stream().allMatch(MatchSession::eliminated)) {
            game.setGameOver(GameEndReason.AllHumansLost);
            for (var seat : seats.values()) seat.releaseInput();
        }
    }
    @Override public IGuiGame gui() { return owner().gui(); }
    private MatchSession owner() {
        var current = dialogOwner.get();
        if (current != null) return current;
        if (seats.size() == 1) return seats.values().iterator().next();
        throw new IllegalStateException("A platform dialog must identify its controller");
    }
    @Override public Object platformDialog(String name, Object[] args) { return owner().platformDialog(name, args); }
    @Override public void publishInput() { for (var seat : seats.values()) seat.publishInput(); }
    @Override public void fail(Throwable error) { synchronized (gate) { for (var seat : seats.values()) seat.fail(error); } }
    @Override public void close() {
        synchronized (gate) {
            if (closed) return;
            closed = true;
            for (var seat : seats.values()) seat.cancelDecision();
            if (!game.isGameOver()) game.setGameOver(GameEndReason.AllHumansLost);
            for (var seat : seats.values()) seat.releaseInput();
        }
    }
}
