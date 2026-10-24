package forge.api;

import forge.deck.Deck;
import forge.ai.LobbyPlayerAi;
import forge.game.*;
import forge.game.player.RegisteredPlayer;
import forge.gui.interfaces.IGuiGame;
import forge.player.LobbyPlayerHuman;
import forge.player.PlayerControllerHuman;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.CancellationException;

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
            // Cosmetics are per registered deck; rules and live characteristics
            // still come from the exact token definition used by the effect.
            player.getRegisteredPlayer().getDeck().setPreferLatestTokenArt(true);
            if (player.getController() instanceof PlayerControllerHuman human) seats.put(i, new MatchSession(this, human));
            player.updateOpponentsForView();
        }
        if (seats.isEmpty()) throw new IllegalArgumentException("A table needs at least one human seat");
    }

    static RegisteredPlayer human(Deck deck, String format, String name) {
        return (format.equals("Commander") ? RegisteredPlayer.forCommander(new Deck(deck)) : new RegisteredPlayer(new Deck(deck)))
                .setPlayer(new LobbyPlayerHuman(name));
    }

    static RegisteredPlayer ai(Deck deck, String format, String name) {
        var player = new LobbyPlayerAi(name, Set.of());
        player.setAiProfile("Default");
        return (format.equals("Commander") ? RegisteredPlayer.forCommander(new Deck(deck)) : new RegisteredPlayer(new Deck(deck)))
                .setPlayer(player);
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
            } catch (Throwable failure) {
                if (!closed && !(failure instanceof CancellationException && game.isGameOver())) fail(failure);
            }
            finally {
                synchronized (gate) { for (var seat : seats.values()) seat.clearDecision(); publish(); }
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
            refreshDecisionState();
        }
    }

    /** Public decision ownership only; a seat's options and prompt remain private. */
    void refreshDecisionState() {
        synchronized (gate) {
            var decisions = seats.values().stream().map(MatchSession::publicDecision).filter(Objects::nonNull).toList();
            Integer owner = decisions.size() == 1 ? decisions.get(0).ownerId() : null;
            String actionClass = decisions.isEmpty() ? "resolving" : decisions.size() == 1 ? decisions.get(0).actionClass() : "simultaneous";
            for (var seat : seats.values()) seat.updatePublicDecision(owner, actionClass);
        }
    }

    void enter(MatchSession seat) { dialogOwner.set(seat); }
    void stopIfNoHumans() {
        if (finishIfNoHumans()) releaseInputs();
    }
    // A controller boundary publishes the terminal board before waking the game.
    boolean finishIfNoHumans() {
        if (!game.isGameOver() && seats.values().stream().allMatch(MatchSession::eliminated)) {
            game.setGameOver(GameEndReason.AllHumansLost);
            return true;
        }
        return false;
    }
    void releaseInputs() { for (var seat : seats.values()) seat.releaseInput(); }
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
