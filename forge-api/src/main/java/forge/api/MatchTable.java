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
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.function.LongSupplier;

/** The sole owner of a Forge game. Seats, projections and decisions never depend on transport. */
final class MatchTable implements ManaTableSession, AutoCloseable {
    final String id = UUID.randomUUID().toString();
    final Object gate = new Object();
    final Game game;
    final String format;
    final MatchIdentities identities = new MatchIdentities();
    private final Match match;
    private final Map<Integer, MatchSession> seats = new LinkedHashMap<>();
    private final Map<Integer, MatchSession> automated = new LinkedHashMap<>();
    private final Set<Integer> humanSeats = new LinkedHashSet<>();
    private final boolean automatedOnly;
    private final ThreadLocal<MatchSession> dialogOwner = new ThreadLocal<>();
    private long boardRevision;
    private Map<String, Object> spectatorState;
    private String spectatorError;
    private boolean closed;
    final DecisionTempo tempo;
    private ScheduledExecutorService tempoWorker;

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile) {
        this(format, players, resources, profile, DecisionTempo.Settings.off(), () -> System.nanoTime() / 1_000_000);
    }

    /** Explicit simulation composition; live-table construction retains its human requirement. */
    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile, boolean automatedOnly) {
        this(format, players, resources, profile, DecisionTempo.Settings.off(), () -> System.nanoTime() / 1_000_000, automatedOnly);
    }

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile,
               DecisionTempo.Settings timing, LongSupplier monotonicMillis) {
        this(format, players, resources, profile, timing, monotonicMillis, false);
    }

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile,
               DecisionTempo.Settings timing, LongSupplier monotonicMillis, boolean automatedOnly) {
        if (automatedOnly && (players.size() < 2 || players.stream().anyMatch(player -> !(player.getPlayer() instanceof AutomatedLobbyPlayer))))
            throw new IllegalArgumentException("A simulation requires at least two externally automated seats");
        this.automatedOnly = automatedOnly;
        HeadlessPlatform.initialize(resources, profile);
        tempo = new DecisionTempo(timing, monotonicMillis);
        this.format = format;
        var rules = new GameRules(GameType.Constructed);
        if (format.equals("Commander")) rules.addAppliedVariant(GameType.Commander);
        rules.setGamesPerMatch(1);
        rules.setWarnAboutAICards(false);
        match = new Match(rules, players, "Mana Table");
        game = match.createGame();
        game.getAction().enableStateBoundary();
        game.subscribeToEvents(identities);
        for (int i = 0; i < game.getPlayers().size(); i++) {
            var player = game.getPlayers().get(i);
            // Cosmetics are per registered deck; rules and live characteristics
            // still come from the exact token definition used by the effect.
            player.getRegisteredPlayer().getDeck().setPreferLatestTokenArt(true);
            if (player.getController() instanceof PlayerControllerHuman human) {
                var session = new MatchSession(this, human);
                seats.put(i, session);
                if (isAutomated(player)) automated.put(player.getId(), session);
                else humanSeats.add(i);
            }
            player.updateOpponentsForView();
        }
        if (!automatedOnly && humanSeats.isEmpty()) throw new IllegalArgumentException("A table needs at least one human seat");
        if (automatedOnly) spectatorState = SpectatorObservation.project(this, 0, null);
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

    /** Explicitly selected before game creation; never falls back to Forge strategy. */
    static RegisteredPlayer automated(Deck deck, String format, String name) {
        return (format.equals("Commander") ? RegisteredPlayer.forCommander(new Deck(deck)) : new RegisteredPlayer(new Deck(deck)))
                .setPlayer(new AutomatedLobbyPlayer(name));
    }

    static boolean isAutomated(forge.game.player.Player player) {
        return player.getOriginalLobbyPlayer() instanceof AutomatedLobbyPlayer;
    }

    /** Private child-process host access. These are never network seat capabilities. */
    List<MatchSession> automatedSeats() { return List.copyOf(automated.values()); }
    boolean libraryInspectionActive() { return seats.values().stream().anyMatch(MatchSession::libraryInspectionActive); }

    MatchSession automatedSeat(int playerId) {
        var session = automated.get(playerId);
        if (session == null) throw new IllegalArgumentException("No automated player with this ID");
        return session;
    }

    MatchSession seat(int index) {
        var seat = seats.get(index);
        if (seat == null || !humanSeats.contains(index)) throw new IllegalArgumentException("No human in this seat");
        return seat;
    }
    List<Map<String, Object>> testViews() {
        synchronized (gate) { return humanSeats.stream().map(seats::get).map(MatchSession::state).toList(); }
    }

    /** Published public audience, never an individual bot's observation. */
    Map<String, Object> spectatorState() {
        synchronized (gate) {
            if (!automatedOnly) throw new IllegalStateException("This table is not a spectator simulation");
            return spectatorState;
        }
    }

    void start() {
        HeadlessPlatform.activate(this);
        if (tempo.enabled()) {
            tempoWorker = Executors.newSingleThreadScheduledExecutor(task -> {
                var worker = new Thread(task, "mana-table-tempo"); worker.setDaemon(true); return worker;
            });
            tempoWorker.scheduleWithFixedDelay(() -> {
                try { tickTempo(); } catch (Throwable failure) { fail(failure); stopTempo(); }
            }, 100, 100, TimeUnit.MILLISECONDS);
        }
        game.getAction().invoke(() -> {
            try {
                match.startGame(game);
            } catch (Throwable failure) {
                if (!closed && !(failure instanceof CancellationException && game.isGameOver())) fail(failure);
            }
            finally {
                stopTempo();
                synchronized (gate) { for (var seat : seats.values()) seat.clearDecision(); publish(); }
                game.unsubscribeFromEvents(identities);
                for (var seat : seats.values()) seat.detach();
            }
        });
    }

    // Only called at controller input boundaries, while the game is waiting.
    void publish() {
        game.getAction().withState(() -> { synchronized (gate) {
            long revision = ++boardRevision;
            for (var seat : seats.values()) seat.publishView(revision);
            refreshDecisionState();
            if (automatedOnly) spectatorState = SpectatorObservation.project(this, revision, spectatorError);
        } });
    }

    /** Public decision ownership only; a seat's options and prompt remain private. */
    void refreshDecisionState() {
        synchronized (gate) {
            var decisions = seats.values().stream().map(MatchSession::publicDecision).filter(Objects::nonNull).toList();
            Integer owner = decisions.size() == 1 ? decisions.get(0).ownerId() : null;
            String actionClass = decisions.isEmpty() ? "resolving" : decisions.size() == 1 ? decisions.get(0).actionClass() : "simultaneous";
            for (var seat : seats.values()) seat.updatePublicDecision(owner, actionClass);
            for (var seat : seats.values()) seat.observeTempo();
        }
    }

    void tickTempo() {
        game.getAction().withState(() -> { synchronized (gate) {
            if (closed || game.isGameOver()) return;
            for (var expiry : tempo.expired()) {
                for (var seat : seats.values()) seat.expireTempo(expiry);
            }
        } });
    }
    private void stopTempo() { if (tempoWorker != null) tempoWorker.shutdownNow(); }

    void enter(MatchSession seat) { dialogOwner.set(seat); }
    void stopIfNoHumans() {
        if (finishIfNoHumans()) releaseInputs();
    }
    // A controller boundary publishes the terminal board before waking the game.
    boolean finishIfNoHumans() {
        if (!automatedOnly && !game.isGameOver() && humanSeats.stream().map(seats::get).allMatch(MatchSession::eliminated)) {
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
    @Override public void inState(Runnable action) { game.getAction().withState(action); }
    @Override public <T> T callInState(java.util.concurrent.Callable<T> action) throws Exception { return game.getAction().callState(action); }
    @Override public void awaitUi(java.util.concurrent.Future<?> completion) throws InterruptedException, java.util.concurrent.ExecutionException {
        game.getAction().awaitState(completion);
    }
    @Override public <T> T awaitUi(java.util.concurrent.Future<T> completion, long timeout, TimeUnit unit)
            throws InterruptedException, java.util.concurrent.ExecutionException, java.util.concurrent.TimeoutException {
        return game.getAction().awaitState(completion, timeout, unit);
    }
    @Override public void fail(Throwable error) {
        game.getAction().withState(() -> { synchronized (gate) {
            if (closed) return;
            closed = true;
            stopTempo();
            // A rules failure can contain a bot's private card or choice. Keep
            // the original in host diagnostics, outside every seat projection.
            error.printStackTrace(System.err);
            Throwable publicError = error;
            if (!automated.isEmpty()) {
                publicError = new IllegalStateException(
                        "The rules engine could not continue this game. See the host diagnostics.");
            }
            String failure = publicError.getMessage() == null ? publicError.getClass().getSimpleName() : publicError.getMessage();
            for (var seat : seats.values()) seat.fail(failure);
            // A host interruption is not a rules concession. Conceding a player
            // can reveal their face-down cards to surviving seats and wait for
            // new acknowledgements while this fault request owns the table.
            // Revoke every seat first, then terminalize and release all waits.
            if (!game.isGameOver()) game.setGameOver(GameEndReason.AllHumansLost);
            releaseInputs();
            if (automatedOnly && spectatorState != null) {
                // Fault text can include an internal card/choice. The public
                // observer receives a stable diagnostic and its last board.
                spectatorError = "The simulation stopped because an automated decision failed.";
                var stopped = new LinkedHashMap<>(spectatorState);
                stopped.put("status", "error"); stopped.put("error", spectatorError);
                spectatorState = Collections.unmodifiableMap(stopped);
            }
        } });
    }
    @Override public void close() {
        game.getAction().withState(() -> { synchronized (gate) {
            if (closed) return;
            closed = true;
            stopTempo();
            for (var seat : seats.values()) seat.cancelDecision();
            if (!game.isGameOver()) game.setGameOver(GameEndReason.AllHumansLost);
            for (var seat : seats.values()) seat.releaseInput();
        } });
    }
}
