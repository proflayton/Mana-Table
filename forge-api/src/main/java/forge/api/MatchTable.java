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
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import java.util.function.LongSupplier;

/** The sole owner of a Forge game. Seats, projections and decisions never depend on transport. */
final class MatchTable implements ManaTableSession, AutoCloseable {
    enum CompletionPolicy { RULES_OUTCOME, STOP_AFTER_LAST_HUMAN }
    final String id = UUID.randomUUID().toString();
    final Object gate = new Object();
    final Game game;
    final String format;
    final MatchIdentities identities = new MatchIdentities();
    final StackInterruption stackInterruption = new StackInterruption();
    private final Match match;
    private final Map<Integer, MatchSession> seats = new LinkedHashMap<>();
    private final Map<Integer, MatchSession> automated = new LinkedHashMap<>();
    private final Set<Integer> humanSeats = new LinkedHashSet<>();
    private final boolean automatedOnly;
    private final CompletionPolicy completionPolicy;
    private final EncounterContext encounter;
    private final Map<Integer, EncounterContext.Participant> encounterParticipants = new LinkedHashMap<>();
    private volatile boolean encounterRetirementAllowed;
    private final ThreadLocal<MatchSession> dialogOwner = new ThreadLocal<>();
    private long boardRevision;
    private Map<String, Object> spectatorState;
    private String spectatorError;
    private boolean closed;
    private boolean faulted;
    private boolean startQueued;
    private final CompletableFuture<Void> startupCompletion = new CompletableFuture<>();
    final DecisionTempo tempo;
    private ScheduledExecutorService tempoWorker;

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile) {
        this(format, players, resources, profile, DecisionTempo.Settings.off(), () -> System.nanoTime() / 1_000_000);
    }

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile, CompletionPolicy completionPolicy) {
        this(format, players, resources, profile, DecisionTempo.Settings.off(), () -> System.nanoTime() / 1_000_000,
                false, null, completionPolicy);
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
        this(format, players, resources, profile, timing, monotonicMillis, automatedOnly, null);
    }

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile,
               DecisionTempo.Settings timing, LongSupplier monotonicMillis, boolean automatedOnly, EncounterContext encounter) {
        this(format, players, resources, profile, timing, monotonicMillis, automatedOnly, encounter, CompletionPolicy.RULES_OUTCOME);
    }

    MatchTable(String format, List<RegisteredPlayer> players, Path resources, Path profile,
               DecisionTempo.Settings timing, LongSupplier monotonicMillis, boolean automatedOnly, EncounterContext encounter,
               CompletionPolicy completionPolicy) {
        this.completionPolicy = Objects.requireNonNull(completionPolicy);
        if ((automatedOnly || encounter != null) && completionPolicy != CompletionPolicy.RULES_OUTCOME)
            throw new IllegalArgumentException("Simulations and encounters require an actual rules outcome");
        if (automatedOnly && (players.size() < 2 || players.stream().anyMatch(player -> !(player.getPlayer() instanceof AutomatedLobbyPlayer))))
            throw new IllegalArgumentException("A simulation requires at least two externally automated seats");
        this.automatedOnly = automatedOnly;
        this.encounter = encounter;
        HeadlessPlatform.initialize(resources, profile);
        tempo = new DecisionTempo(timing, monotonicMillis);
        this.format = format;
        var rules = new GameRules(GameType.Constructed);
        if (format.equals("Commander")) rules.addAppliedVariant(GameType.Commander);
        rules.setGamesPerMatch(1);
        rules.setWarnAboutAICards(false);
        match = new Match(rules, players, "Mana Table");
        game = match.createGame();
        if (encounter != null) for (var participant : encounter.participants()) {
            var registered = players.get(participant.index());
            var actual = game.getRegisteredPlayers().stream().filter(player -> player.getRegisteredPlayer() == registered).findFirst().orElseThrow();
            if (actual.getTeam() != participant.teamId()) throw new IllegalStateException("The engine did not preserve the configured team");
            encounterParticipants.put(actual.getId(), participant);
        }
        game.getAction().enableStateBoundary();
        game.getReversibleManaAction().enable();
        game.subscribeToEvents(identities);
        game.subscribeToEvents(stackInterruption);
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

    String partyId() { return encounter == null ? null : encounter.partyId(); }
    String encounterId() { return encounter == null ? null : encounter.encounterId(); }
    String participantId(int playerId) {
        var participant = encounterParticipants.get(playerId);
        return participant == null ? "" : participant.participantId();
    }
    EncounterProgressStore.Receipt terminalReceipt() {
        var captured = new java.util.concurrent.atomic.AtomicReference<EncounterProgressStore.Receipt>();
        game.getAction().withState(() -> { synchronized (gate) {
            if (encounter == null || faulted) return;
            var outcome = MatchOutcomeProjection.of(this, null);
            if (outcome == null) return;
            var participants = encounterParticipants.entrySet().stream().map(entry -> {
                var participant = entry.getValue();
                return new EncounterProgressStore.Participant(participant.participantId(), participant.index(), entry.getKey(), participant.teamId(), participant.role(), participant.deckSelectionId());
            }).sorted(Comparator.comparingInt(EncounterProgressStore.Participant::index)).toList();
            captured.set(new EncounterProgressStore.Receipt(encounter.encounterId(), encounter.partyId(), encounter.encounterId(), id,
                    encounter.specification().rulesetId(), encounter.specification().partyTeam(), outcome, participants, java.time.Instant.now().toString()));
        } });
        return captured.get();
    }
    void allowEncounterRetirement() { encounterRetirementAllowed = true; }

    /** Private child-process host access. These are never network seat capabilities. */
    List<MatchSession> automatedSeats() { return List.copyOf(automated.values()); }
    boolean retired() { synchronized (gate) { return closed && !faulted; } }
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

    /** Completion belongs to the actual rules worker, not another executor task.
     * The game executor can run queued tasks in either order. */
    CompletableFuture<Void> start() {
        synchronized (gate) {
            if (startQueued) return startupCompletion;
            startQueued = true;
        }
        // invoke can run inline on a rules thread; do not retain the table
        // monitor while entering a game that can wait for controller input.
        try { game.getAction().invoke(this::startInState); }
        catch (Throwable failure) {
            // Submission can fail before there is a rules worker to report or
            // complete anything. Use the same typed fault and retirement path.
            try { reportStartupFailure(failure); }
            finally { completeStart(failure); }
        }
        return startupCompletion;
    }

    private void startInState() {
        Throwable unexpected = null;
        try {
            synchronized (gate) {
                if (closed || game.isGameOver()) return;
                HeadlessPlatform.activate(this);
                if (tempo.enabled()) {
                    tempoWorker = Executors.newSingleThreadScheduledExecutor(task -> {
                        var worker = new Thread(task, "mana-table-tempo"); worker.setDaemon(true); return worker;
                    });
                    tempoWorker.scheduleWithFixedDelay(() -> {
                        try { tickTempo(); } catch (Throwable failure) { fail(failure); stopTempo(); }
                    }, 100, 100, TimeUnit.MILLISECONDS);
                }
            }
            match.startGame(game);
        } catch (Throwable failure) {
            if (!(failure instanceof CancellationException && game.isGameOver())) {
                unexpected = failure;
                reportStartupFailure(failure);
            }
        } finally { completeStart(unexpected); }
    }

    private void completeStart(Throwable unexpected) {
        try {
            stopTempo();
            synchronized (gate) {
                for (var seat : seats.values()) seat.clearDecision();
                if (!faulted) publish();
            }
        } catch (Throwable failure) {
            if (unexpected == null) {
                unexpected = failure;
                reportStartupFailure(failure);
            } else unexpected.addSuppressed(failure);
        } finally {
            try {
                game.unsubscribeFromEvents(identities);
                game.unsubscribeFromEvents(stackInterruption);
                for (var seat : seats.values()) seat.detach();
            } catch (Throwable failure) {
                if (unexpected == null) {
                    unexpected = failure;
                    reportStartupFailure(failure);
                } else unexpected.addSuppressed(failure);
            } finally {
                if (unexpected == null) startupCompletion.complete(null);
                else startupCompletion.completeExceptionally(unexpected);
            }
        }
    }

    private void reportStartupFailure(Throwable failure) {
        synchronized (gate) {
            // Explicit closure revokes the table's public projection, but must
            // not hide an unrelated worker/cleanup fault from host diagnostics.
            if (closed) failure.printStackTrace(System.err);
            else fail(failure);
        }
    }

    // Only called at controller input boundaries, while the game is waiting.
    void publish() {
        game.getAction().withState(() -> { synchronized (gate) {
            if (faulted) return;
            observeStackChoices();
            long revision = ++boardRevision;
            boolean publicationClosed = closed;
            // Rules rollback can restore a card directly to a hidden zone
            // without a movement event. Revoke every audience's previous view
            // before any seat allocates IDs for this publication, regardless
            // of which seat owns the newly private occurrence.
            for (var seat : seats.values()) seat.refreshVisibility();
            for (var seat : seats.values()) {
                if (!seat.publishView(revision) || !currentPublication(revision)) return;
            }
            refreshDecisionState();
            if (!currentPublication(revision)) return;
            if (automatedOnly) {
                var spectator = SpectatorObservation.project(this, revision, spectatorError);
                if (currentPublication(revision)) spectatorState = spectator;
            }
            // Observers can inspect every audience or publish reentrantly. No
            // normal completion may expose a partially projected table, and a
            // newer publication owns all remaining callbacks and queued passes.
            BooleanSupplier current = () -> currentPublication(revision) && !faulted && closed == publicationClosed;
            for (var seat : seats.values()) {
                if (!current.getAsBoolean()) return;
                seat.completePublication(current);
            }
        } });
    }

    /** Called inside the rules-state/table boundary. A reentrant publication
     * supersedes the whole earlier cohort, including its public observer. */
    boolean currentPublication(long revision) { return boardRevision == revision; }

    /** Public decision ownership only; a seat's options and prompt remain private. */
    void refreshDecisionState() {
        synchronized (gate) {
            observeStackChoices();
            var decisions = seats.values().stream().map(MatchSession::publicDecision).filter(Objects::nonNull).toList();
            Integer owner = decisions.size() == 1 ? decisions.get(0).ownerId() : null;
            String actionClass = decisions.isEmpty() ? "resolving" : decisions.size() == 1 ? decisions.get(0).actionClass() : "simultaneous";
            for (var seat : seats.values()) seat.updatePublicDecision(owner, actionClass);
            for (var seat : seats.values()) seat.observeTempo();
        }
    }

    /** Admission revokes a reversal before queued work can create a new event.
     * Copies update authority only; the last committed board stays immutable. */
    void invalidateUndo(String reason) {
        game.getReversibleManaAction().invalidate(reason);
        for (var seat : seats.values()) seat.refreshUndoProjection();
    }

    void restoredUndo(forge.game.ReversibleManaAction.Restoration restored) {
        stackInterruption.action();
        for (var seat : seats.values()) seat.reconcileUndo(restored);
    }

    /** Both admission and every audience publication share the state/table
     * boundary. Failed publication retains complete immutable prior views,
     * then interrupts before any observer can receive a partial cohort. */
    void publishUndo(Runnable restoreAndPublish) {
        var previous = new LinkedHashMap<MatchSession, Map<String, Object>>();
        for (var seat : seats.values()) previous.put(seat, seat.committedView());
        var publicPrevious = spectatorState;
        try { restoreAndPublish.run(); }
        catch (Throwable failure) {
            previous.forEach(MatchSession::retainCommittedView);
            spectatorState = publicPrevious;
            fail(failure);
        }
    }

    private void observeStackChoices() {
        var choices = seats.values().stream().map(MatchSession::requiredChoiceEpisode)
                .filter(Objects::nonNull).toList();
        stackInterruption.observeChoices(choices);
        if (!choices.isEmpty()) game.getReversibleManaAction().invalidate("required-choice");
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
        if (completionPolicy == CompletionPolicy.STOP_AFTER_LAST_HUMAN && !game.isGameOver()
                && humanSeats.stream().map(seats::get).allMatch(MatchSession::eliminated)) {
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
            faulted = true;
            game.getReversibleManaAction().invalidate("table-failed");
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
            for (var seat : seats.values()) {
                try { seat.fail(failure); }
                catch (Throwable observerFailure) { observerFailure.printStackTrace(System.err); }
            }
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
                stopped.put("result", null); stopped.put("outcome", null);
                spectatorState = Collections.unmodifiableMap(stopped);
            }
        } });
    }
    @Override public void close() {
        game.getAction().withState(() -> { synchronized (gate) {
            if (closed) return;
            // A natural outcome can race a request to leave an active table.
            // Decide retirement at the rules boundary before transport teardown.
            if (encounter != null && !encounterRetirementAllowed && MatchOutcomeProjection.of(this, faulted ? "fault" : null) != null)
                throw new IllegalStateException("Save encounter progress before retiring this match.");
            closed = true;
            invalidateUndo("table-closed");
            stopTempo();
            for (var seat : seats.values()) seat.cancelDecision();
            if (!game.isGameOver()) game.setGameOver(GameEndReason.AllHumansLost);
            for (var seat : seats.values()) seat.releaseInput();
        } });
    }
}
