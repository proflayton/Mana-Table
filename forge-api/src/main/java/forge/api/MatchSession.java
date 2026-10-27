package forge.api;

import com.google.gson.JsonObject;
import com.google.common.eventbus.Subscribe;
import forge.StaticData;
import forge.card.MagicColor;
import forge.card.CardStateName;
import forge.card.GamePieceType;
import forge.card.mana.ManaAtom;
import forge.deck.Deck;
import forge.game.*;
import forge.game.event.GameEventTurnPhase;
import forge.game.event.GameEvent;
import forge.game.event.GameEventManaPool;
import forge.game.event.GameEventCardTapped;
import forge.game.event.GameEventCardPhased;
import forge.game.event.GameEventCardAttachment;
import forge.game.event.GameEventCardChangeZone;
import forge.game.event.GameEventCardDrawn;
import forge.game.event.GameEventPlayerCounters;
import forge.game.event.GameEventSpeedChanged;
import forge.game.event.GameEventDayTimeChanged;
import forge.game.event.GameEventCardStatsChanged;
import forge.game.event.GameEventPlayerStatsChanged;
import forge.game.event.GameEventSnapshotRestored;
import forge.game.card.CardView;
import forge.game.card.Card;
import forge.game.card.CounterType;
import forge.game.combat.CombatUtil;
import forge.game.player.*;
import forge.game.spellability.SpellAbilityView;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.StackItemView;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.*;
import forge.gui.control.PlaybackSpeed;
import forge.gui.interfaces.IGuiGame;
import forge.item.PaperCard;
import forge.player.PlayerControllerHuman;
import forge.player.HumanCostDecision;
import forge.player.PlayerZoneUpdates;
import forge.util.FSerializableFunction;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Proxy;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;

/** One seat in an authoritative match, independent of its connection. Mutable engine objects never leave this adapter. */
public final class MatchSession implements ManaTableSession, MatchEndpoint {
    private static final forge.util.ITriggerEvent CARD_CLICK = new forge.util.ITriggerEvent() {
        public int getButton() { return 1; }
        public int getX() { return 0; }
        public int getY() { return 0; }
    };
    private final String id;
    private final MatchTable table;
    private final Game game;
    private final String format;
    private final PlayerControllerHuman human;
    private final PlayerView viewer;
    private final MatchActivity activity;
    private final QuietYieldBaseline quietYield = new QuietYieldBaseline();
    private final PlayAccessProjection playAccess = new PlayAccessProjection();
    private final CombatCardIds combatIds;
    private final IGuiGame gui;
    private final Object gate;
    private final Set<CardView> selectable = new HashSet<>();
    private final Set<CardView> actionable = new HashSet<>();
    private final Set<GameEntityView> highlighted = new HashSet<>();
    private final List<String> notices = new ArrayList<>();
    private volatile Map<String, Object> latest;
    private volatile Pending pending;
    private boolean inputActionQueued;
    private final AtomicLong sourceActionVersion = new AtomicLong();
    private volatile boolean closed;
    private volatile String error;
    private long revision;
    private long activityRevision;
    private String message = "Preparing the match…", ok = "Continue", cancel = "Cancel";
    private boolean okEnabled, cancelEnabled;
    private Input displayedInput;
    private CardView promptSource;
    private boolean conceded;
    private final List<java.util.function.Consumer<PlaytestFrame>> playtestObservers = new ArrayList<>();

    public record PlaytestFrame(String gameId, String status, String result, Map<String, Integer> observations) { }

    void observePlaytest(java.util.function.Consumer<PlaytestFrame> observer) {
        synchronized (gate) { playtestObservers.add(observer); observer.accept(playtestFrame()); }
    }

    private PlaytestFrame playtestFrame() {
        String status = error != null ? "error" : conceded ? "conceded" : game.isGameOver() ? "completed" : closed ? "interrupted" : "running";
        String result = error != null ? error : gameResult();
        if (result == null && conceded) result = "Conceded";
        return new PlaytestFrame(id, status, result, activity.observations());
    }

    private String gameResult() {
        if (error != null || !game.isGameOver() || game.getOutcome() == null) return null;
        var outcome = game.getOutcome();
        return viewer.getHasLost() ? "Defeat" : outcome.isDraw() ? "Draw"
                : outcome.getWinningLobbyPlayer() == human.getLobbyPlayer() ? "Victory" : "Defeat";
    }

    private void notifyPlaytest() {
        if (!playtestObservers.isEmpty()) {
            var frame = playtestFrame();
            for (var observer : playtestObservers) observer.accept(frame);
        }
    }

    private static final class Pending {
        final String id = UUID.randomUUID().toString();
        final String kind;
        final Input input;
        final Map<String, CardView> cards = new LinkedHashMap<>();
        final Map<String, CardView> additionalCards = new LinkedHashMap<>();
        final List<Map<String, Object>> additionalSources = new ArrayList<>();
        final Map<Integer, String> cardKeys = new HashMap<>();
        final Map<String, SourceActions.Catalog> sourceActions = new HashMap<>();
        final Set<String> poolManaColors = new HashSet<>();
        List<Map<String, Object>> optionSemantics = List.of();
        SpellAbility announcedAbility;
        InputPayMana colorPaymentOwner;
        String announcedSymbol = "";
        Map<String, Object> cachedDomain;
        long cachedDomainVersion = -1;
        int nextCardKey;
        final Set<Integer> players = new HashSet<>();
        final Set<String> blockPairs = new HashSet<>();
        final Set<String> attackPairs = new HashSet<>();
        final CompletableFuture<JsonObject> response = new CompletableFuture<>();
        Map<String, Object> prompt;
        int size, min, max;
        int amount;
        boolean atLeastOne, numeric, maySkip;
        List<Integer> limits = List.of();
        java.util.function.Consumer<List<Integer>> validateOrder;
        int defenderIndex = -1;
        List<Integer> lethal = List.of();
        Pending(String kind, Input input) { this.kind = kind; this.input = input; }
    }

    static MatchSession solo(Deck deck, String format, List<String> opponents, Path resources, Path profile) {
        return solo(deck, format, opponents, resources, profile, false);
    }
    static MatchSession solo(Deck deck, String format, List<String> opponents, Path resources, Path profile, boolean automated) {
        validateOpponents(format, opponents);
        boolean commander = format.equals("Commander");
        var assigned = new ArrayList<OpponentDeck>();
        for (String opponent : opponents) {
            var preset = commander && opponent.startsWith("preset:") ? DeckPresets.find(opponent.substring(7)) : null;
            String name = preset != null ? preset.name() : opponent.equals("red") ? "Cinder" : "Verdant";
            assigned.add(new OpponentDeck(aiDeck(opponent, format), name));
        }
        return soloDecks(deck, format, assigned, resources, profile, automated);
    }
    record OpponentDeck(Deck deck, String name) { }
    static MatchSession soloDecks(Deck deck, String format, List<OpponentDeck> opponents, Path resources, Path profile, boolean automated) {
        if (opponents.isEmpty() || opponents.size() >= (format.equals("Commander") ? 6 : 2))
            throw new IllegalArgumentException("Choose a valid number of opponents for this format");
        var seats = new ArrayList<RegisteredPlayer>();
        seats.add(MatchTable.human(deck, format, "You"));
        for (var opponent : opponents) {
            String name = opponent.name() + " · AI" + (opponents.size() > 1 ? " " + seats.size() : "");
            seats.add(automated ? MatchTable.automated(opponent.deck(), format, name) : MatchTable.ai(opponent.deck(), format, name));
        }
        var table = new MatchTable(format, seats, resources, profile);
        table.start();
        return table.seat(0);
    }

    MatchSession(MatchTable table, PlayerControllerHuman human) {
        this.table = table; this.id = table.id; this.gate = table.gate;
        this.game = table.game; this.format = table.format; this.human = human;
        viewer = human.getPlayer().getView();
        activity = new MatchActivity(viewer, table.identities);
        combatIds = new CombatCardIds(viewer, activity, table.identities);
        game.subscribeToEvents(activity);
        game.subscribeToEvents(combatIds);
        game.subscribeToEvents(this);
        gui = (IGuiGame) Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
            table.enter(this);
            if (method.getName().equals("chooseColor")) return chooseColor((String) args[0], (CardView) args[1], (List<MagicColor.Color>) args[2]);
            if (method.getName().equals("getIntegerForAnnouncement")) return announcedNumber((String) args[0],
                    (SpellAbilityView) args[1], (String) args[2], (int) args[3], (int) args[4]);
            if (method.getName().equals("chooseCounterTypeForPayment")) return chooseCounterTypeForPayment((String) args[0],
                    (CardView) args[1], (int) args[2], (List<CounterType>) args[3],
                    (Map<CounterType, Integer>) args[4]);
            if (method.isDefault()) return InvocationHandler.invokeDefault(proxy, method, args);
            return invokeGui(method.getName(), args == null ? new Object[0] : args);
        });
        human.setGui(gui);
        for (var player : game.getPlayers()) player.updateOpponentsForView();
        latest = Collections.unmodifiableMap(map("id", id, "revision", 0L, "boardRevision", 0L,
                "status", "starting", "message", message, "format", format, "viewerId", viewer.getId(),
                "playerCount", game.getPlayers().size(), "players", List.of(), "stack", List.of(),
                "turn", 0, "phase", "Pregame", "phaseKey", "PREGAME", "activePlayerId", null,
                "priorityPlayerId", null, "decisionOwnerId", null, "actionClass", "starting",
                "prompt", null, "combat", null, "result", null, "error", null, "activity", List.of(), "notices", List.of()));
    }

    void detach() { game.unsubscribeFromEvents(activity); game.unsubscribeFromEvents(combatIds); game.unsubscribeFromEvents(this); }
    void clearDecision() { pending = null; }
    void cancelDecision() {
        closed = true;
        notifyPlaytest();
        if (pending != null) pending.response.completeExceptionally(new CancellationException("Match closed"));
        pending = null;
    }
    void releaseInput() { human.getInputQueue().onGameOver(true); }
    boolean eliminated() { return viewer.getHasLost(); }
    void closeTable() { table.close(); }
    List<MatchSession> automatedSeats() { return table.automatedSeats(); }
    MatchSession automatedSeat(int playerId) { return table.automatedSeat(playerId); }
    void automationFault() { table.fail(new IllegalStateException("An automated player could not complete its decision. The game has stopped; see the host diagnostics.")); }
    boolean libraryInspectionActive() { return pending != null && "librarySearch".equals(pending.prompt.get("context")); }

    @Override
    public IGuiGame gui() { return gui; }
    public Map<String, Object> state() {
        synchronized (gate) {
            // During AI work, publish only event-time copies. Never read a live board from IPC.
            if ("resolving".equals(latest.get("status"))) {
                var frame = activity.frame();
                if (frame.revision() != activityRevision) {
                    activityRevision = frame.revision();
                    var next = new LinkedHashMap<>(latest);
                    next.put("revision", ++revision);
                    next.put("turn", frame.turn()); next.put("phase", frame.phase()); next.put("phaseKey", frame.phaseKey());
                    next.put("activePlayerId", frame.activePlayerId()); next.put("activity", frame.entries());
                    latest = Collections.unmodifiableMap(next);
                }
            }
            if (!table.tempo.enabled()) return latest;
            var timed = new LinkedHashMap<>(latest);
            timed.put("tempo", table.tempo.view(!finished()));
            return Collections.unmodifiableMap(timed);
        }
    }
    public boolean finished() { return closed || error != null || game.isGameOver(); }

    static void validateOpponents(String format, List<String> opponents) {
        int maximum = format.equals("Commander") ? 5 : 1;
        if (opponents.isEmpty() || opponents.size() > maximum) throw new IllegalArgumentException(
                format.equals("Commander") ? "Choose 1–5 AI opponents for a 2–6 player Commander table" : "Constructed supports one AI opponent");
        var allowed = opponents(format).stream().map(opponent -> opponent.get("id")).toList();
        for (String opponent : opponents) if (!allowed.contains(opponent)) throw new IllegalArgumentException("Unknown opponent: " + opponent);
    }

    @Subscribe
    public void onPhase(GameEventTurnPhase event) {
        table.stopIfNoHumans();
    }

    // Any rules mutation, including a hidden one, revokes the previous action
    // capability without disclosing which fact changed.
    @Subscribe public void onSourceActionEvent(GameEvent event) { sourceActionVersion.incrementAndGet(); }

    @Subscribe
    public void onQuietYieldEvent(GameEvent event) {
        // These public mutations need not create history entries and may be
        // reversed before another snapshot. Never inspect a hidden card here.
        boolean changed = event instanceof GameEventPlayerCounters
                || event instanceof GameEventSpeedChanged || event instanceof GameEventDayTimeChanged
                || event instanceof GameEventSnapshotRestored;
        if (event instanceof GameEventCardDrawn e && !viewer.equals(e.player())
                && e.origin() == GameEventCardChangeZone.Origin.TURN_BASED_DRAW)
            quietYield.observeTurnBasedDraw(e.player().getId());
        if (event instanceof GameEventManaPool e) quietYield.observeMana(e.player().getId(), publicMana(e.player()));
        else if (event instanceof GameEventCardTapped e) changed = publicQuietCard(e.card());
        else if (event instanceof GameEventCardPhased e) changed = publicQuietCard(e.card());
        else if (event instanceof GameEventCardAttachment e) changed = publicQuietCard(e.equipment());
        else if (event instanceof GameEventCardStatsChanged e) {
            for (var card : e.cards()) if (publicQuietCard(card)) quietYield.observeCard(cardState(card, null));
        } else if (event instanceof GameEventPlayerStatsChanged e) {
            for (var player : e.players()) {
                var actual = game.getRegisteredPlayers().stream().filter(p -> p.getId() == player.getId()).findFirst().orElse(null);
                quietYield.observePlayer(player.getId(), map("life", player.getLife(),
                        "maxHandSize", player.hasUnlimitedHandSize() ? null : player.getMaxHandSize(),
                        "landPlaysUsed", player.getNumLandThisTurn(), "landPlayLimit", player.hasUnlimitedLandPlay() ? null : player.getMaxLandPlay(),
                        "counters", publicCounters(player), "states", actual == null ? List.of() : PublicPlayerStates.of(actual)));
            }
        }
        if (changed) quietYield.invalidate();
    }

    private boolean publicQuietCard(CardView card) {
        return card != null && card.getZone() != null && card.canBeShownTo(viewer)
                && (card.getZone() != ZoneType.Hand || viewer.equals(card.getController()))
                && (!card.isFaceDown() || card.getZone() == ZoneType.Battlefield);
    }

    public static List<Map<String, String>> opponents(String format) {
        if (format.equals("Commander")) {
            var opponents = new ArrayList<Map<String, String>>();
            for (var preset : DeckPresets.list()) opponents.add(Map.of("id", "preset:" + preset.id(), "name", preset.name(),
                    "description", preset.commanders().get(0).name() + " · " + preset.theme() + " · Commander · 100 cards"));
            opponents.add(Map.of("id", "green", "name", "Goreclaw · Verdant", "description", "Goreclaw, Terror of Qal Sisma · Green ramp and big creatures · Commander · 100 cards"));
            opponents.add(Map.of("id", "red", "name", "Torbran · Cinder", "description", "Torbran, Thane of Red Fell · Red creatures and damage · Commander · 100 cards"));
            return List.copyOf(opponents);
        }
        return List.of(Map.of("id", "green", "name", "Verdant", "description", "Green creatures, mana ramp, and combat tricks · Constructed · 60 cards"),
                Map.of("id", "red", "name", "Cinder", "description", "Red creatures and direct damage · Constructed · 60 cards"));
    }

    /** Both solo games and hosted AI seats use these engine-owned, format-compatible choices. */
    static Deck aiDeck(String opponent, String format) {
        validateOpponents(format, List.of(opponent));
        boolean commander = format.equals("Commander");
        var preset = commander && opponent.startsWith("preset:") ? DeckPresets.find(opponent.substring(7)) : null;
        return preset != null ? DeckPresets.create(preset.id()) : commander ? CommanderOpponents.create(opponent) : opponentDeck(opponent);
    }

    private static Deck opponentDeck(String key) {
        String[] lines = switch (key) {
            case "green" -> new String[]{"24 Forest", "4 Llanowar Elves", "4 Elvish Mystic", "4 Grizzly Bears", "4 Elvish Visionary", "4 Centaur Courser", "4 Giant Growth", "4 Colossal Dreadmaw", "4 Rumbling Baloth", "4 Sentinel Spider"};
            case "red" -> new String[]{"24 Mountain", "4 Monastery Swiftspear", "4 Ghitu Lavarunner", "4 Shock", "4 Lightning Bolt", "4 Lightning Strike", "4 Goblin Arsonist", "4 Borderland Marauder", "4 Viashino Pyromancer", "4 Chandra's Pyrohelix"};
            default -> throw new IllegalArgumentException("Unknown opponent");
        };
        Deck deck = new Deck(key);
        for (String line : lines) {
            int split = line.indexOf(' ');
            PaperCard card = StaticData.instance().getCommonCards().getCard(line.substring(split + 1));
            if (card == null) throw new IllegalStateException("Missing opponent card: " + line);
            deck.getMain().add(card, Integer.parseInt(line.substring(0, split)));
        }
        return deck;
    }

    /** Called only after an input-display runnable completes, while the game awaits that input. */
    @Override
    public void publishInput() {
        game.getAction().withState(this::publishInputInState);
    }
    private void publishInputInState() {
        synchronized (gate) {
            if (closed || error != null) return;
            if (game.isGameOver()) { releaseInput(); return; }
            if (inputActionQueued) return;
            if (pending != null && !pending.kind.equals("input")) return;
            Input current = human.getInputQueue().getInput();
            if (current == null || current != displayedInput || current != human.getInputProxy().getInput() || game.isGameOver()) return;
            if (viewer.getHasLost()) {
                // A loss while resolving a spell can leave priority pointing at
                // the departed controller. Hiding that input strands its latch,
                // so the phase loop never reaches its normal departure cleanup.
                // The game is waiting at this exact input: copy the loss before
                // releasing it, and let surviving human seats keep playing.
                pending = null;
                table.finishIfNoHumans();
                table.publish();
                if (game.isGameOver()) table.releaseInputs();
                else if (current instanceof InputSynchronized departed) departed.stop();
                else releaseInput();
                return;
            }
            if (current instanceof InputPayMana payment && !payment.isPaymentActive()) {
                // A selected mana source or Auto payment runs on a queued rules
                // task. Its input stays on the controller stack while locked,
                // but cannot accept another choice until that task finishes.
                // Unrelated GUI callbacks must not reopen its consumed scope.
                Pending consumed = pending;
                if (pending != null && pending.input == current) pending = null;
                if (latest.get("prompt") != null) markBusy(consumed);
                return;
            }
            // Unrelated GUI callbacks may refresh every seat. An unanswered
            // controller input keeps its scope and card handles through refreshes.
            Pending next = pending != null && pending.input == current ? pending : new Pending("input", current);
            // Cleanup's discard input (among others) is an anonymous subclass.
            // Preserve its actual input kind instead of publishing an empty name.
            Class<?> inputClass = current.getClass();
            while (inputClass.getSimpleName().isEmpty()) inputClass = inputClass.getSuperclass();
            var playerChoices = current instanceof InputSelectEntitiesFromList<?> selection
                    ? selection.getValidChoices().stream().filter(Player.class::isInstance).map(entity -> ((Player) entity).getId()).toList()
                    : current instanceof InputSelectTargets targeting
                    ? game.getPlayers().stream().filter(targeting::canSelectPlayer).map(Player::getId).toList() : List.<Integer>of();
            next.players.clear();
            next.players.addAll(playerChoices);
            next.prompt = map("id", next.id, "kind", "input", "inputType", inputClass.getSimpleName(),
                    "message", message, "ok", ok, "cancel", cancel, "okEnabled", okEnabled, "cancelEnabled", cancelEnabled,
                    "canAttackAll", current instanceof InputAttack, "playerChoices", playerChoices,
                    "canAutoPass", canAutoPass(current));
            next.prompt.put("priorityAlternative", priorityAlternative(current));
            if (current instanceof InputSelectTargets targeting) {
                next.prompt.put("min", targeting.getMinTargets());
                next.prompt.put("max", targeting.getMaxTargets());
                var effect = pendingEffect(targeting.getSpellAbility());
                if (effect != null) next.prompt.put("effect", effect);
                if (targeting.getSpellAbility().getTargetRestrictions().canTgtPlayer()) {
                    var search = librarySearch(LibrarySearchOffer.targeting(targeting.getSpellAbility()), targeting.getSpellAbility());
                    if (search != null) next.prompt.put("librarySearch", search);
                }
            }
            if (current instanceof InputConfirm confirmation && confirmation.getLibrarySearchOffer() != null) {
                next.prompt.put("librarySearch", librarySearch(confirmation.getLibrarySearchOffer(), confirmation.getSpellAbility()));
                next.prompt.put("ok", "Search library");
                next.prompt.put("cancel", "Don’t search");
            }
            if (current instanceof InputConfirmMulligan mulligan && mulligan.getOpeningHandOffer() != null) {
                var offer = mulligan.getOpeningHandOffer();
                next.prompt.put("openingHand", map("handSize", offer.handSize(), "nextHandSize", offer.nextHandSize(),
                        "cardsToReturn", offer.cardsToReturn(), "freeMulligan", offer.freeMulligan()));
            }
            if (current instanceof InputLondonMulligan mulligan) {
                next.prompt.put("min", mulligan.getCardsToReturn());
                next.prompt.put("max", mulligan.getCardsToReturn());
            }
            if (current instanceof HumanCostDecision.InputSelectCardToRemoveCounter counters) {
                var payment = counters.getCounterPayment();
                if (payment.amount() > 0) next.prompt.put("counterPayment", map("amount", payment.amount(), "remaining", payment.remaining()));
            }
            if (promptSource != null && promptSource.canBeShownTo(viewer)) {
                next.prompt.put("sourceCard", cardState(promptSource, null));
                next.prompt.put("sourceZone", promptSource.getZone() == null ? "" : promptSource.getZone().name());
                next.prompt.put("sourceControllerId", promptSource.getController() == null ? null : promptSource.getController().getId());
            }
            pending = next;
            publish(next);
        }
    }

    private boolean canAutoPass(Input input) {
        var view = game.getView();
        // The controller refreshes this conservative action scan before publishing
        // priority. It includes planned payments and unresolved manual routes in
        // playable zones. Timeouts remain conservative; mana abilities alone do not count.
        // Land plays and castable commanders count as actions too. A main phase
        // with no remaining play is eligible; the client applies saved phase stops.
        // Never infer this permission from card highlighting in the renderer.
        return input instanceof InputPassPriority priority && okEnabled && view.getTurn() > 0
                && priority.canAutoPass();
    }

    private Map<String, Object> pendingEffect(SpellAbility ability) {
        return PendingEffects.project(ability, viewer, table.identities, card -> {
            if (!publicQuietCard(card)) return null;
            var context = cardState(card, null);
            // This explains a captured mutation; it is never a new action or
            // selection handle, even when the same permanent is actionable.
            context.put("key", "");
            context.put("selectable", false);
            context.put("highlighted", false);
            return context;
        });
    }

    private Map<String, Object> librarySearch(LibrarySearchOffer offer, SpellAbility ability) {
        if (offer == null) return null;
        CardView source = ability == null ? null : ability.getHostCard().getView();
        boolean publicSource = source != null && source.canBeShownTo(viewer) && !source.isFaceDown();
        return map("libraryOwnerId", offer.libraryOwnerId(), "destination", offer.destination().name(),
                "cardName", publicSource ? offer.cardName() : "");
    }

    private boolean canQuietYield(Input input) {
        var view = game.getView();
        return input instanceof InputPassPriority priority && priority.isPriorityActive() && okEnabled
                && !closed && error == null && !viewer.getHasLost() && !game.isGameOver()
                && human.getInputQueue().getInput() == input && human.getInputProxy().getInput() == input
                && view.getTurn() > 0 && view.getPlayerTurn() != null && !viewer.equals(view.getPlayerTurn())
                && view.getStack().isEmpty() && game.getStack().isEmpty();
    }

    private boolean permitsQuietYield(Pending prompt, String token) {
        return canQuietYield(prompt.input) && quietYield.permits(token, activity.quietSequence());
    }

    @Override
    public Map<String, Object> sourceActions(JsonObject request) {
        // Never hold the table gate while waiting for its input owner.
        return HeadlessPlatform.query(table, () -> {
            synchronized (gate) {
                requireSession(request);
                long observedRevision = revision;
                if (request.has("expectedRevision")) {
                    var expected = request.get("expectedRevision");
                    try {
                        if (!expected.isJsonPrimitive() || !expected.getAsJsonPrimitive().isNumber()
                                || expected.getAsBigDecimal().longValueExact() != observedRevision)
                            throw new IllegalArgumentException("The table changed. Read the current card again.");
                    } catch (ArithmeticException | NumberFormatException invalid) {
                        throw new IllegalArgumentException("The table changed. Read the current card again.");
                    }
                }
                Pending current = pending;
                if (current == null || !current.id.equals(string(request, "promptId")))
                    throw new IllegalArgumentException("That choice has changed. Use the current prompt.");
                if (!sourceInputCurrent(current)) throw new IllegalArgumentException("Source actions are not available in this decision");
                String key = string(request, "key");
                CardView view = current.cards.containsKey(key) ? current.cards.get(key) : current.additionalCards.get(key);
                Card source = sourceCard(view);
                if (source == null) throw new IllegalArgumentException("Card is not visible in this prompt");
                long version = sourceActionVersion.get();
                var catalog = current.sourceActions.get(key);
                if (view.isFaceDown() || catalog == null || !catalog.matches(source, version)) {
                    catalog = view.isFaceDown() ? SourceActions.empty(source, key, version)
                            : SourceActions.describe(current.input, source, key, version, this::visibleActionHost);
                    if (!sourceInputCurrent(current) || sourceActionVersion.get() != version)
                        throw new IllegalArgumentException("The source actions changed. Read the current card again.");
                    current.sourceActions.put(key, catalog);
                }
                var result = sourceCatalogState(current, catalog);
                if (revision != observedRevision) throw new IllegalArgumentException("The table changed. Read the current card again.");
                return result;
            }
        });
    }

    private Map<String, Object> sourceCatalogState(Pending current, SourceActions.Catalog catalog) {
        return sourceCatalogState(current, catalog, true);
    }

    private Map<String, Object> sourceCatalogState(Pending current, SourceActions.Catalog catalog, boolean readOnlyFacts) {
        var actions = catalog.actions().stream().filter(action -> actionHostVisible(action.host())).map(action -> {
            var description = map("id", action.id(), "kind", action.kind(), "text", action.text());
            if (readOnlyFacts) {
                var costPreview = ManaCostPreviewProjection.describe(action.ability(), human.getPlayer(), this::semanticCardId);
                if (costPreview != null) description.put("costPreview", costPreview);
            }
            if (action.host() != catalog.source()) {
                var preview = cardState(action.host().getView(), null);
                preview.put("selectable", false); preview.put("highlighted", false);
                description.put("card", preview); description.put("zone", action.zone().name());
            }
            return description;
        }).toList();
        var result = map("gameId", id, "decisionId", current.id, "sourceCardKey", catalog.key(),
                "sourceCardId", catalog.source().isFaceDown() && human.isExternallyAutomated()
                        ? combatIds.id(catalog.source().getView()) : activity.visualId(catalog.source().getView()), "actions", actions);
        if (readOnlyFacts) result.put("revision", revision);
        return Collections.unmodifiableMap(result);
    }

    // Only the process owner calls this. No network seat can enumerate bot
    // observations, source catalogs, or unpublished input choices.
    Map<String, Object> automatedObservation() {
        if (!human.isExternallyAutomated()) throw new IllegalArgumentException("This is not an automated seat");
        return HeadlessPlatform.query(table, () -> {
            synchronized (gate) {
                Pending current = pending;
                // A controller can finish its input before the queued display
                // callback publishes its successor. Revoke that old authority;
                // never pair a stale priority prompt with a non-action domain.
                if (current != null && current.kind.equals("input")
                        && (human.getInputQueue().getInput() != current.input || human.getInputProxy().getInput() != current.input
                            || (current.input instanceof InputPassPriority || current.input instanceof InputPayMana) && !sourceInputCurrent(current))) {
                    if (latest.get("prompt") != null && !finished()) markBusy(current);
                    current = null;
                }
                var domain = map("version", sourceActionVersion.get(), "coverage", "NotApplicable", "sources", List.of(), "actions", List.of());
                if (current != null && sourceInputCurrent(current)) {
                    long version = sourceActionVersion.get();
                    if (current.cachedDomain != null && current.cachedDomainVersion == version)
                        return map("seatId", viewer.getId(), "state", automatedState(current), "domain", current.cachedDomain,
                                "semantics", automatedSemantics(current));
                    var discovery = SourceActions.discover(current.input, human.getPlayer(), this::actionHostVisible);
                    boolean complete = discovery.complete();
                    var sources = new ArrayList<Object>();
                    var semantics = new ArrayList<Object>();
                    current.additionalCards.clear(); current.additionalSources.clear();
                    for (Card source : discovery.sources()) {
                        String key = current.cardKeys.get(source.getId());
                        if (source.getZone().getZoneType() == ZoneType.Stack || source.getZone().getZoneType() == ZoneType.Sideboard) {
                            var face = cardState(source.getView(), current);
                            key = String.valueOf(face.get("key"));
                            face.put("selectable", false); face.put("highlighted", false);
                            current.additionalCards.put(key, source.getView());
                            current.additionalSources.add(Collections.unmodifiableMap(map("decisionId", current.id,
                                    "zone", source.getZone().getZoneType().name(), "playerId", source.getController().getId(),
                                    "card", Collections.unmodifiableMap(face))));
                        } else if (key == null || !current.cards.containsKey(key)) { complete = false; continue; }
                        var catalog = current.sourceActions.get(key);
                        if (catalog == null || !catalog.matches(source, version)) {
                            catalog = SourceActions.describe(current.input, source, key, version, this::actionHostVisible);
                            current.sourceActions.put(key, catalog);
                        }
                        sources.add(sourceCatalogState(current, catalog, false));
                        for (var action : catalog.actions()) semantics.add(ActionSemantics.describe(action, current.input,
                                human.getPlayer(), this::semanticCardId));
                    }
                    domain = map("version", version, "coverage", complete ? "Complete" : "Unsupported",
                            "sources", complete ? sources : List.of(), "actions", complete ? semantics : List.of());
                    current.poolManaColors.clear();
                    if (current.input instanceof InputPayMana mana) {
                        current.poolManaColors.addAll(mana.getUsablePoolManaColors().stream().map(MagicColor::toShortString).toList());
                        domain.put("poolManaColors", List.copyOf(current.poolManaColors));
                    }
                    if (!sourceInputCurrent(current) || sourceActionVersion.get() != version)
                        throw new IllegalArgumentException("The automated input changed during discovery");
                    current.cachedDomain = Collections.unmodifiableMap(domain);
                    current.cachedDomainVersion = version;
                }
                return map("seatId", viewer.getId(), "state", automatedState(current), "domain", domain,
                        "semantics", automatedSemantics(current));
            }
        });
    }

    private Map<String, Object> automatedState(Pending current) {
        var snapshot = state();
        if (current == null || current.additionalSources.isEmpty() || snapshot.get("prompt") == null) return snapshot;
        var enriched = new LinkedHashMap<>(snapshot);
        enriched.put("actionSources", List.copyOf(current.additionalSources));
        return Collections.unmodifiableMap(enriched);
    }

    private Map<String, Object> automatedSemantics(Pending current) {
        var result = map("intent", "Unknown", "amount", null, "options", List.of(), "legalTextValues", List.of(), "selectionIsCost", false);
        if (current == null) return result;
        result.putAll(DecisionSemantics.describe(current.input, game, human.getPlayer(), this::semanticCardId));
        result.put("options", current.optionSemantics);
        if (current.announcedAbility != null)
            result.put("numeric", NumericDecisionProjection.describe(current.announcedAbility, current.announcedSymbol,
                    human.getPlayer(), card -> numericSourceId(card)));
        if (current.input instanceof InputPayMana payment) {
            var provenance = PaymentDecisionProjection.describe(payment, this::numericSourceId);
            if (provenance != null) result.put("payment", provenance);
        }
        if (current.input instanceof InputAttack || current.input instanceof InputBlock)
            result.put("episodeId", id + ":combat:" + game.getPhaseHandler().getTurn() + ":"
                    + game.getPhaseHandler().getNumCombat() + ":" + game.getPhaseHandler().getPhase().name() + ":" + viewer.getId());
        if (current.input instanceof InputSelectTargets targeting)
            result.put("selectedPlayers", java.util.stream.StreamSupport.stream(
                    targeting.getSpellAbility().getTargets().getTargetPlayers().spliterator(), false).map(Player::getId).toList());
        else if (current.input instanceof InputSelectManyBase<?> selection)
            result.put("selectedPlayers", selection.getSelected().stream().filter(Player.class::isInstance).map(entity -> ((Player) entity).getId()).toList());
        if (current.input instanceof InputLondonMulligan) result.put("intent", "Discard");
        if (current.prompt.containsKey("counterPayment") || current.prompt.containsKey("counterPaymentChoice")) {
            result.put("intent", "RemoveCounters"); result.put("selectionIsCost", true);
        }
        var payingInput = current.input instanceof InputPayMana ? current.input
                : "colorChoice".equals(current.prompt.get("context")) ? human.getInputQueue().getInput() : null;
        if (payingInput instanceof InputPayMana mana) result.put("unpaidMana", ManaPaymentFacts.unpaid(mana));
        return result;
    }

    private boolean sourceInputCurrent(Pending current) {
        return !closed && error == null && !viewer.getHasLost() && !game.isGameOver() && !inputActionQueued
                && current.kind.equals("input") && sourceInputActive(current.input)
                && human.getInputQueue().getInput() == current.input && human.getInputProxy().getInput() == current.input;
    }

    private String semanticCardId(CardView card) {
        ZoneType zone = card.getZone();
        if (zone == null || !card.canBeShownTo(viewer)) return "";
        if (zone == ZoneType.Library && table.libraryInspectionActive()) return "";
        if (List.of(ZoneType.Battlefield, ZoneType.Hand, ZoneType.Library, ZoneType.Graveyard, ZoneType.Exile, ZoneType.Command).contains(zone))
            return combatIds.id(card);
        if (zone == ZoneType.Stack && game.getView().getStack().stream().anyMatch(item -> card.equals(item.getSourceCard())))
            return combatIds.id(card);
        if (zone == ZoneType.Sideboard && pending != null && pending.additionalCards.containsValue(card)) return combatIds.id(card);
        return "";
    }

    private static boolean sourceInputActive(Input input) {
        return input instanceof InputPassPriority priority ? priority.isPriorityActive()
                : input instanceof InputPayMana payment && payment.isPaymentActive();
    }

    private Card sourceCard(CardView view) {
        if (view == null || view.getZone() == null || !isVisibleInZone(view, view.getZone(), viewer)) return null;
        Card source = human.getCard(view);
        return source == null || source.getZone() == null ? null : source;
    }

    private boolean visibleActionHost(Card card) {
        return card != null && card.getZone() != null && !card.isFaceDown()
                && isVisibleInZone(card.getView(), card.getZone().getZoneType(), viewer);
    }
    private boolean actionHostVisible(Card card) {
        return human.isExternallyAutomated() ? card != null && card.getZone() != null
                && isVisibleInZone(card.getView(), card.getZone().getZoneType(), viewer)
                && (!card.isFaceDown() || card.getView().canFaceDownBeShownTo(viewer)) : visibleActionHost(card);
    }

    private boolean sourceActionCurrent(Pending current, SourceActions.Catalog catalog, SourceActions.Action action) {
        // inputActionQueued is expected after acceptance, so recheck the exact
        // owned input directly rather than using sourceInputCurrent here.
        Card source = sourceCard(current.cards.containsKey(catalog.key()) ? current.cards.get(catalog.key()) : current.additionalCards.get(catalog.key()));
        return !closed && error == null && !viewer.getHasLost() && !game.isGameOver()
                && sourceInputActive(current.input)
                && human.getInputQueue().getInput() == current.input && human.getInputProxy().getInput() == current.input
                && catalog.matches(source, sourceActionVersion.get()) && actionHostVisible(source)
                && action.matches(source) && actionHostVisible(action.host()) && action.ability().canPlay(true);
    }

    public Map<String, Object> action(JsonObject request) {
        return action(request, true);
    }
    private Map<String, Object> action(JsonObject request, boolean playerDriven) {
        return game.getAction().readState(() -> actionInState(request, playerDriven));
    }
    private Map<String, Object> actionInState(JsonObject request, boolean playerDriven) {
        synchronized (gate) {
            requireSession(request);
            String action = string(request, "action");
            if (action.equals("setAbilityAutoPass")) return setAbilityAutoPass(request);
            Pending next = pending;
            if (next == null || !next.id.equals(string(request, "promptId"))) throw new IllegalArgumentException("That choice has changed. Use the current prompt.");
            if (request.has("expectedTask") && !semanticTask(next).equals(string(request, "expectedTask")))
                throw new IllegalArgumentException("That command does not answer the current decision.");
            if (action.equals("passIfNoResponse") && (!Boolean.TRUE.equals(next.prompt.get("canAutoPass")) || !canAutoPass(next.input)))
                throw new IllegalArgumentException("This response window needs your decision");
            String yieldToken = "";
            if (action.equals("passUntilPublicAction")) {
                var supplied = request.get("yieldToken");
                if (supplied == null || !supplied.isJsonPrimitive() || !supplied.getAsJsonPrimitive().isString()
                        || supplied.getAsString().isEmpty())
                    throw new IllegalArgumentException("The table has changed. Review the current decision before passing for now.");
                yieldToken = supplied.getAsString();
            }
            if (action.equals("passUntilPublicAction") && (!next.kind.equals("input")
                    || !Objects.equals(next.prompt.get("quietYieldToken"), yieldToken) || !permitsQuietYield(next, yieldToken)))
                throw new IllegalArgumentException("The table has changed. Review the current decision before passing for now.");
            if (!next.kind.equals("input")) {
                validateDialog(next, request);
                if (playerDriven) table.tempo.activity(viewer.getId());
                pending = null;
                markBusy(next);
                next.response.complete(request.deepCopy());
                return latest;
            }
            CardView card = null;
            CardView attacker = null;
            PlayerView target = null;
            String placementAbility = null;
            Integer placementHandId = null;
            SourceActions.Catalog sourceCatalog = null;
            SourceActions.Action sourceAction = null;
            ManaOutputChoice manaOutput = ManaOutputChoice.parse(request.get("manaOutput"));
            if (manaOutput != null && !action.equals("useSourceAction"))
                throw new IllegalArgumentException("A mana output belongs to an exact current source action");
            switch (action) {
                case "spendPoolMana" -> {
                    if (!(next.input instanceof InputPayMana mana)) throw new IllegalArgumentException("Mana can only pay the current payment");
                    String color = string(request, "manaColor");
                    if (!List.of("W", "U", "B", "R", "G", "C").contains(color)
                            || !next.poolManaColors.contains(color))
                        throw new IllegalArgumentException("That pool mana cannot pay this cost");
                }
                case "ok", "passIfNoResponse", "passUntilPublicAction" -> { if (!okEnabled) throw new IllegalArgumentException("Continue is not available"); }
                case "passPriority" -> {
                    if (!(next.input instanceof InputPassPriority priority) || !priority.isPriorityActive() || !okEnabled)
                        throw new IllegalArgumentException("Passing priority is not available");
                }
                case "priorityUndo", "priorityPassTurn" -> {
                    String alternative = action.equals("priorityUndo") ? "undo" : "passTurn";
                    if (!cancelEnabled || !Objects.equals(next.prompt.get("priorityAlternative"), alternative)
                            || !priorityAlternative(next.input).equals(alternative))
                        throw new IllegalArgumentException("That priority action changed. Use the current decision.");
                }
                case "cancel" -> { if (!cancelEnabled) throw new IllegalArgumentException("Cancel is not available"); }
                case "attackAll" -> { if (!(next.input instanceof InputAttack)) throw new IllegalArgumentException("Not declaring attackers"); }
                case "card" -> { card = next.cards.get(string(request, "key")); if (card == null) throw new IllegalArgumentException("Card is not visible in this prompt"); }
                case "useSourceAction" -> {
                    if (!sourceInputCurrent(next)) throw new IllegalArgumentException("Source actions are not available in this decision");
                    sourceCatalog = next.sourceActions.get(string(request, "key"));
                    String actionId = string(request, "actionId");
                    if (sourceCatalog != null) sourceAction = sourceCatalog.actions().stream()
                            .filter(candidate -> candidate.id().equals(actionId)).findFirst().orElse(null);
                    // IPC validation reads only the scoped cache. Live source
                    // resolution and canPlay run on the owned input thread.
                    if (sourceAction == null || sourceCatalog.version() != sourceActionVersion.get())
                        throw new IllegalArgumentException("That source action is no longer available. Read the current card again.");
                    // Read-only validation precedes consuming the decision or
                    // touching express choices, mana, or the physical source.
                    if (manaOutput != null) manaOutput.choicesFor(sourceAction.ability());
                }
                case "useHandPlacement" -> {
                    if (!(next.input instanceof InputPassPriority) || human.getInputQueue().getInput() != next.input
                            || closed || viewer.getHasLost() || game.isGameOver())
                        throw new IllegalArgumentException("Use a hand-placement option during your current priority");
                    var abilityId = request.get("abilityId");
                    if (abilityId == null || !abilityId.isJsonPrimitive() || !abilityId.getAsJsonPrimitive().isString()
                            || abilityId.getAsString().isBlank()) throw new IllegalArgumentException("Choose an available hand-placement ability");
                    var hand = next.cards.get(string(request, "handKey"));
                    if (hand == null || hand.getZone() != ZoneType.Hand || !viewer.getCards(ZoneType.Hand).contains(hand)
                            || !human.canUseHandPlacement(abilityId.getAsString(), hand.getId()))
                        throw new IllegalArgumentException("That hand-placement option is no longer available");
                    placementAbility = abilityId.getAsString();
                    placementHandId = hand.getId();
                }
                case "attack" -> {
                    if (!(next.input instanceof InputAttack)) throw new IllegalArgumentException("Not declaring attackers");
                    attacker = next.cards.get(string(request, "attackerKey"));
                    String defender;
                    if (request.has("defenderPlayerId") && !request.has("defenderKey")) {
                        int playerId = exactInt(string(request, "defenderPlayerId"));
                        target = game.getView().getPlayers().stream().filter(p -> p.getId() == playerId).findFirst().orElseThrow();
                        defender = "player:" + playerId;
                    } else {
                        card = next.cards.get(string(request, "defenderKey"));
                        defender = card == null || request.has("defenderPlayerId") ? "" : "card:" + card.getId();
                    }
                    if (attacker == null || !next.attackPairs.contains(attacker.getId() + ":" + defender))
                        throw new IllegalArgumentException("That creature cannot attack this defender");
                }
                case "block" -> {
                    if (!(next.input instanceof InputBlock)) throw new IllegalArgumentException("Not declaring blockers");
                    attacker = next.cards.get(string(request, "attackerKey"));
                    card = next.cards.get(string(request, "blockerKey"));
                    if (attacker == null || card == null || !next.blockPairs.contains(attacker.getId() + ":" + card.getId()))
                        throw new IllegalArgumentException("That creature cannot block this attacker");
                }
                case "player" -> {
                    int playerId = exactInt(string(request, "playerId"));
                    if ((next.input instanceof InputSelectTargets || next.input instanceof InputSelectEntitiesFromList<?>) && !next.players.contains(playerId))
                        throw new IllegalArgumentException("That player is not selectable in this decision");
                    target = game.getView().getPlayers().stream().filter(p -> p.getId() == playerId).findFirst().orElseThrow();
                }
                default -> throw new IllegalArgumentException("Unknown match action");
            }
            CardView chosenCard = card;
            CardView chosenAttacker = attacker;
            PlayerView chosenPlayer = target;
            String chosenPlacementAbility = placementAbility;
            Integer chosenPlacementHandId = placementHandId;
            String chosenYieldToken = yieldToken;
            SourceActions.Catalog chosenSourceCatalog = sourceCatalog;
            SourceActions.Action chosenSourceAction = sourceAction;
            if (playerDriven) table.tempo.activity(viewer.getId());
            pending = null;
            inputActionQueued = true;
            markBusy(next);
            HeadlessPlatform.later(() -> {
                boolean restorePriority = false;
                try {
                    if (closed || viewer.getHasLost() || game.isGameOver() || human.getInputQueue().getInput() != next.input) return;
                    switch (action) {
                        case "spendPoolMana" -> {
                            byte color = ManaAtom.fromName(string(request, "manaColor"));
                            if (((InputPayMana) next.input).getUsablePoolManaColors().contains(color)) human.useMana(color);
                            else restorePriority = true;
                        }
                        case "ok" -> human.selectButtonOk();
                        case "passPriority", "passIfNoResponse" -> ((InputPassPriority) next.input).passPriority();
                        case "priorityUndo" -> restorePriority = !((InputPassPriority) next.input).undoPriorityAction();
                        case "priorityPassTurn" -> restorePriority = !((InputPassPriority) next.input).passTurn();
                        case "passUntilPublicAction" -> {
                            synchronized (gate) {
                                // Recheck on the owned input thread. A queued
                                // permission must never consume a changed input.
                                restorePriority = !permitsQuietYield(next, chosenYieldToken);
                                if (!restorePriority) ((InputPassPriority) next.input).passPriority();
                            }
                        }
                        case "cancel" -> human.selectButtonCancel();
                        case "attackAll" -> human.alphaStrike();
                        case "card" -> human.selectCard(chosenCard, null, CARD_CLICK);
                        case "useSourceAction" -> {
                            synchronized (gate) {
                                restorePriority = !sourceActionCurrent(next, chosenSourceCatalog, chosenSourceAction);
                                if (!restorePriority) {
                                    var ability = chosenSourceAction.ability();
                                    var output = manaOutput == null ? null : ManaProductionProjection.outputChoices(ability, manaOutput.colors());
                                    restorePriority = manaOutput != null && output == null;
                                    if (!restorePriority) restorePriority = !(next.input instanceof InputPayMana payment
                                            ? payment.selectAbility(ability, output)
                                            : ((InputPassPriority)next.input).selectAbility(ability, output));
                                }
                            }
                        }
                        case "useHandPlacement" -> restorePriority = !human.useHandPlacement(chosenPlacementAbility, chosenPlacementHandId);
                        case "block" -> {
                            // One scoped gesture: select the attacker and toggle this exact
                            // block through the engine's normal input, then publish once.
                            human.selectCard(chosenAttacker, null, CARD_CLICK);
                            human.selectCard(chosenCard, null, CARD_CLICK);
                        }
                        case "attack" -> {
                            // One scoped drag chooses a defender and toggles the
                            // attacker through the normal engine input, atomically.
                            if (chosenPlayer != null) human.selectPlayer(chosenPlayer, null);
                            else human.selectCard(chosenCard, null, CARD_CLICK);
                            human.selectCard(chosenAttacker, null, CARD_CLICK);
                        }
                        case "player" -> human.selectPlayer(chosenPlayer, null);
                    }
                } finally {
                    synchronized (gate) {
                        inputActionQueued = false;
                        // A late engine recheck may revoke this exact option. Keep
                        // the unanswered input usable; never choose a replacement.
                        if (restorePriority && !closed && !viewer.getHasLost() && !game.isGameOver()
                                && human.getInputQueue().getInput() == next.input) {
                            pending = next;
                            notices.add(action.equals("passUntilPublicAction")
                                    ? "The table changed. Passing for now stopped; review the current decision."
                                    : action.equals("useSourceAction") ? "That source action changed. Read the current card again."
                                    : action.startsWith("priority") ? "That priority action changed. Use the current decision."
                                    : "That hand-placement option is no longer available. Use the current prompt.");
                            while (notices.size() > 12) notices.remove(0);
                        }
                    }
                    if (restorePriority) publishInput();
                }
            });
            return latest;
        }
    }

    private static String priorityAlternative(Input input) {
        if (!(input instanceof InputPassPriority priority)) return "none";
        return priority.canUndoPriorityAction() ? "undo" : priority.canPassTurn() ? "passTurn" : "none";
    }

    private static String semanticTask(Pending prompt) {
        if (!prompt.kind.equals("input"))
            return prompt.kind.equals("text") && Boolean.TRUE.equals(prompt.prompt.get("numeric")) ? "number" : prompt.kind;
        Input input = prompt.input;
        if (input instanceof InputConfirmMulligan) return "openingHand";
        if (input instanceof InputPassPriority) return "priority";
        if (input instanceof InputPayMana) return "mana";
        if (input instanceof InputAttack) return "attack";
        if (input instanceof InputBlock) return "block";
        if (input instanceof InputSelectTargets || input instanceof InputSelectManyBase<?> || input instanceof InputLondonMulligan)
            return "selection";
        return "confirmation";
    }

    /** Priority preferences never consume an input, activate an ability, or traverse a running board. */
    private Map<String, Object> setAbilityAutoPass(JsonObject request) {
        if (closed || viewer.getHasLost() || game.isGameOver()) throw new IllegalArgumentException("This match has ended");
        var value = request.get("ignored");
        if (value == null || !value.isJsonPrimitive() || !value.getAsJsonPrimitive().isBoolean())
            throw new IllegalArgumentException("Choose whether to pass for this ability");
        boolean ignored = value.getAsBoolean();
        var requestedAbility = request.get("abilityId");
        if (requestedAbility == null || !requestedAbility.isJsonPrimitive() || !requestedAbility.getAsJsonPrimitive().isString()
                || requestedAbility.getAsString().isBlank()) throw new IllegalArgumentException("Choose an available ability");
        String abilityId = requestedAbility.getAsString();
        Pending current = pending;
        if (ignored && (current == null || !current.id.equals(string(request, "promptId"))
                || !(current.input instanceof InputPassPriority) || human.getInputQueue().getInput() != current.input))
            throw new IllegalArgumentException("That choice has changed. Use the current prompt.");
        if (!human.setPriorityAbilityIgnored(abilityId, ignored))
            throw new IllegalArgumentException("That ability is no longer available");

        // Preserve the published board and prompt-scoped card handles. Undo is also
        // available during another player's priority or while the game is resolving.
        var next = new LinkedHashMap<>(latest);
        next.put("revision", ++revision);
        next.put("ignoredPriorityAbilities", priorityAbilities(human.getIgnoredPriorityAbilities(), current));
        if (current != null && current.input instanceof InputPassPriority) {
            var prompt = new LinkedHashMap<>(current.prompt);
            prompt.put("canAutoPass", canAutoPass(current.input));
            prompt.put("priorityAbilities", priorityAbilities(human.getPriorityAbilities(), current));
            current.prompt = prompt;
            next.put("prompt", prompt);
        }
        latest = Collections.unmodifiableMap(next);
        return latest;
    }

    private List<Map<String, Object>> priorityAbilities(List<PlayerControllerHuman.PriorityAbility> abilities, Pending prompt) {
        return abilities.stream().<Map<String, Object>>map(ability -> {
            String sourceKey = prompt == null ? "" : prompt.cards.entrySet().stream()
                    .filter(entry -> entry.getValue().getId() == ability.sourceCardId())
                    .map(Map.Entry::getKey).findFirst().orElse("");
            var handKeys = prompt == null || !(prompt.input instanceof InputPassPriority) ? List.<String>of()
                    : prompt.cards.entrySet().stream().filter(entry -> entry.getValue().getZone() == ZoneType.Hand
                            && viewer.getCards(ZoneType.Hand).contains(entry.getValue())
                            && ability.eligibleHandCardIds().contains(entry.getValue().getId()))
                    .map(Map.Entry::getKey).toList();
            return map("id", ability.id(), "sourceCardKey", sourceKey, "sourceName", ability.sourceName(),
                    "text", ability.text(), "ignoredUntilNextTurn", ability.ignoredUntilNextTurn(),
                    "eligibleHandKeys", handKeys, "handPlacementLimit", 1);
        }).toList();
    }

    public Map<String, Object> concede(JsonObject request) {
        return game.getAction().readState(() -> concedeInState(request));
    }
    private Map<String, Object> concedeInState(JsonObject request) {
        synchronized (gate) {
            requireSession(request);
            if (game.isGameOver()) return latest;
            Pending old = pending;
            pending = null;
            conceded = true;
            human.concede();
            // Publish the loss before releasing a dialog's waiter: pregame
            // orchestration must observe the departure when it handles cancellation.
            if (old != null) old.response.completeExceptionally(new CancellationException("Match conceded"));
            table.stopIfNoHumans();
            human.getInputQueue().onGameOver(true);
            publish(null);
            return latest;
        }
    }

    private void requireSession(JsonObject request) {
        if (!id.equals(string(request, "sessionId"))) throw new IllegalArgumentException("This match is no longer active");
        if (error != null) throw new IllegalStateException(error);
    }

    private void markBusy(Pending consumed) {
        var next = new LinkedHashMap<>(latest);
        next.put("revision", ++revision); next.put("prompt", null); next.put("status", "resolving");
        next.put("players", resolvingPlayers(consumed));
        next.put("playAccesses", PlayAccessProjection.withoutCurrentOffers(next.get("playAccesses")));
        latest = Collections.unmodifiableMap(next);
        table.refreshDecisionState();
    }

    @SuppressWarnings("unchecked")
    private List<Map<String, Object>> resolvingPlayers(Pending consumed) {
        var players = new ArrayList<Map<String, Object>>();
        for (var previous : (List<Map<String, Object>>) latest.get("players")) {
            var player = new LinkedHashMap<>(previous);
            var zones = new ArrayList<Map<String, Object>>();
            for (var oldZone : (List<Map<String, Object>>) previous.get("zones")) {
                var zone = new LinkedHashMap<>(oldZone);
                var zoneType = ZoneType.valueOf((String) zone.get("name"));
                var retained = new ArrayList<Map<String, Object>>();
                Object top = null;
                for (var oldCard : (List<Map<String, Object>>) oldZone.get("cards")) {
                    boolean hidden = zoneType == ZoneType.Library || zoneType == ZoneType.Hand
                            && !Objects.equals(previous.get("id"), viewer.getId())
                            || zoneType == ZoneType.Exile && Boolean.TRUE.equals(oldCard.get("faceDown"));
                    CardView view = consumed == null ? null : consumed.cards.get(oldCard.get("key"));
                    if (hidden && !visibleOutsideTemporaryDecision(view, zoneType)) continue;
                    var card = new LinkedHashMap<>(oldCard);
                    card.put("key", ""); card.put("selectable", false); card.put("highlighted", false);
                    var frozen = Collections.unmodifiableMap(card);
                    retained.add(frozen);
                    if (oldCard == oldZone.get("topCard")) top = frozen;
                }
                zone.put("cards", List.copyOf(retained));
                if (oldZone.containsKey("topCard")) zone.put("topCard", top);
                zones.add(Collections.unmodifiableMap(zone));
            }
            player.put("zones", List.copyOf(zones));
            players.add(Collections.unmodifiableMap(player));
        }
        return List.copyOf(players);
    }

    private boolean visibleOutsideTemporaryDecision(CardView view, ZoneType zone) {
        if (view == null || !isVisibleInZone(view, zone, viewer)) return false;
        Card current = human.getCard(view);
        if (current == null || !current.isInZone(zone)) return false;
        if (current.mayPlayerLookWithoutTemporary(human.getPlayer())) return true;
        // Preserve the engine's controlled-player viewing relationship without
        // borrowing a temporary peek from that player's completed decision.
        var controller = view.getController();
        return controller != null && viewer.equals(controller.getMindSlaveMaster())
                && (zone == ZoneType.Hand || current.mayPlayerLookWithoutTemporary(current.getController()));
    }

    record PublicDecision(int ownerId, String actionClass) { }

    private Object tempoEpisode() {
        if (publicDecision() == null || game.isGameOver() || inputActionQueued && pending.input != null) return null;
        // A nested ability menu/payment/confirmation must not refund the parent
        // decision when cancelled. Its current input still determines whether a
        // timeout can pass or must allow required-choice grace.
        var root = human.getInputQueue().getRootInput();
        return root != null ? root : pending;
    }
    void observeTempo() {
        table.tempo.observe(viewer.getId(), tempoEpisode(), pending != null && pending.input instanceof InputPassPriority priority
                && priority.isPriorityActive() && okEnabled);
    }
    public Map<String, Object> tempoPresented(JsonObject request) {
        synchronized (gate) {
            requireSession(request);
            if (finished()) return Map.of("accepted", false);
            return Map.of("accepted", table.tempo.presented(viewer.getId(), string(request, "clockId")));
        }
    }
    void expireTempo(DecisionTempo.Expired expiry) {
        if (viewer.getId() != expiry.player() || tempoEpisode() != expiry.episode()) return;
        if (expiry.outcome() == DecisionTempo.Outcome.WARNING) return;
        var request = new JsonObject(); request.addProperty("sessionId", id);
        if (expiry.outcome() == DecisionTempo.Outcome.LOSS) {
            notices.add("Decision time expired.");
            concede(request);
            if (viewer.getHasLost()) table.tempo.lost(viewer.getId());
        } else {
            request.addProperty("promptId", pending.id); request.addProperty("action", "passPriority");
            action(request, false);
        }
    }

    PublicDecision publicDecision() {
        Pending current = pending;
        if (current == null || closed || error != null || viewer.getHasLost()) return null;
        String actionClass = current.kind;
        if (current.input != null) {
            Input input = current.input;
            actionClass = input instanceof InputPassPriority ? "priority" : input instanceof InputPayMana ? "mana"
                    : input instanceof InputAttack ? "attack" : input instanceof InputBlock ? "block"
                    : input instanceof InputSelectTargets || input instanceof InputSelectEntitiesFromList<?> ? "selection"
                    : input instanceof InputLondonMulligan ? "mulligan" : input instanceof InputConfirm ? "confirm" : "choice";
        }
        return new PublicDecision(viewer.getId(), actionClass);
    }

    void updatePublicDecision(Integer owner, String actionClass) {
        // Only ownership/class is public. Another seat choosing attackers is
        // observed until an attack actually exists; choosing none stays quiet.
        // Other required prompts still revoke without exposing private options.
        boolean otherAttackChoice = actionClass.equals("attack") && owner != null && owner != viewer.getId();
        boolean requiredChoice = !actionClass.equals("priority") && !actionClass.equals("resolving") && !otherAttackChoice;
        if (requiredChoice) quietYield.invalidate();
        String status = (String) latest.get("status");
        if (status.equals("finished") || status.equals("error") || status.equals("starting")) { owner = null; actionClass = status; }
        boolean clearPriority = actionClass.equals("resolving") || actionClass.equals("finished") || actionClass.equals("error");
        if (Objects.equals(latest.get("decisionOwnerId"), owner) && Objects.equals(latest.get("actionClass"), actionClass)
                && (!clearPriority || latest.get("priorityPlayerId") == null)) return;
        var next = new LinkedHashMap<>(latest);
        next.put("revision", ++revision); next.put("decisionOwnerId", owner); next.put("actionClass", actionClass);
        if (!actionClass.equals("priority") || !Objects.equals(owner, viewer.getId())) {
            var current = pending;
            if (current != null && current.prompt != null && !Objects.equals(current.prompt.get("quietYieldToken"), "")) {
                var prompt = new LinkedHashMap<>(current.prompt);
                prompt.put("quietYieldToken", "");
                current.prompt = prompt;
                next.put("prompt", prompt);
            }
        }
        if (clearPriority) next.put("priorityPlayerId", null);
        if (!actionClass.equals("priority") || !Objects.equals(owner, viewer.getId()))
            next.put("playAccesses", PlayAccessProjection.withoutCurrentOffers(next.get("playAccesses")));
        latest = Collections.unmodifiableMap(next);
    }

    private void publish(Pending prompt) { pending = prompt; table.publish(); }

    void refreshVisibility() { activity.refreshVisibility(); }

    void publishView(long boardRevision) {
        // Failure retains the last published board. The table aborts rules work
        // and releases waiters without manufacturing player concessions.
        if (error != null) return;
        if (viewer.getHasLost() || game.isGameOver()) {
            Pending old = pending;
            pending = null;
            if (old != null) old.response.completeExceptionally(new CancellationException("Match ended"));
        }
        Pending prompt = pending;
        if (prompt != null) { prompt.cards.clear(); prompt.attackPairs.clear(); prompt.blockPairs.clear(); }
        var view = game.getView();
        var players = new ArrayList<Object>();
        var registeredPlayers = game.getRegisteredPlayers();
        var allPlayers = registeredPlayers.stream().map(Player::getView).toList();
        var publishedAccessCards = new HashMap<PlayAccessProjection.PublishedOccurrence, String>();
        var statesByPlayer = new LinkedHashMap<Integer, List<PublicPlayerStates.State>>();
        for (Player player : registeredPlayers) statesByPlayer.put(player.getId(), PublicPlayerStates.of(player));
        for (PlayerView player : allPlayers) {
            var states = statesByPlayer.get(player.getId());
            activity.observePlayerStates(player, states);
            var zones = new ArrayList<Object>();
            for (ZoneType zone : List.of(ZoneType.Battlefield, ZoneType.Hand, ZoneType.Library, ZoneType.Graveyard, ZoneType.Exile, ZoneType.Command)) {
                var visible = new ArrayList<Object>();
                var cards = player.getCards(zone);
                Map<String, Object> topCard = null;
                if (cards != null && !(zone == ZoneType.Library && table.libraryInspectionActive()))
                    for (CardView card : cards) if (isVisibleInZone(card, zone, viewer)) {
                    var state = cardState(card, prompt);
                    visible.add(state);
                    if ((zone == ZoneType.Library || zone == ZoneType.Graveyard || zone == ZoneType.Exile)
                            && !Boolean.TRUE.equals(state.get("faceDown")) && state.get("visualId") instanceof String visualId && !visualId.isBlank())
                        publishedAccessCards.put(new PlayAccessProjection.PublishedOccurrence(player.getId(), zone, card), visualId);
                    if (zone == ZoneType.Library && card.equals(cards.get(0))) topCard = state;
                }
                var zoneState = map("name", zone.name(), "count", player.getZoneSize(zone), "cards", visible);
                // A visible card elsewhere in the library is not necessarily its top card.
                if (zone == ZoneType.Library) zoneState.put("topCard", topCard);
                zones.add(zoneState);
            }
            var mana = publicMana(player);
            var commanderDamage = new ArrayList<Object>();
            if (format.equals("Commander")) for (PlayerView owner : allPlayers) {
                if (owner.getCommanders() == null) continue;
                for (CardView commander : owner.getCommanders()) commanderDamage.add(map("name", commander.isFaceDown() ? "Face-down commander" : commander.getCurrentState().getName(),
                        "ownerId", owner.getId(), "owner", owner.getName(), "damage", player.getCommanderDamage(commander)));
            }
            players.add(map("id", player.getId(), "name", player.equals(viewer) ? "You" : player.getName(), "human", player.equals(viewer),
                    "seat", allPlayers.indexOf(player) + 1, "eliminated", player.getHasLost(),
                    "life", player.getLife(), "maxHandSize", player.hasUnlimitedHandSize() ? null : player.getMaxHandSize(),
                    "landPlaysUsed", player.getNumLandThisTurn(), "landPlayLimit", player.hasUnlimitedLandPlay() ? null : player.getMaxLandPlay(),
                    "priority", player.getHasPriority(), "mana", mana, "counters", publicCounters(player), "states", states, "zones", zones, "commanderDamage", commanderDamage));
        }
        var stack = new ArrayList<Object>();
        var pendingEffects = new HashMap<Integer, Map<String, Object>>();
        var stackKinds = new HashMap<Integer, String>();
        for (var instance : game.getStack()) {
            var ability = instance.getSpellAbility();
            stackKinds.put(instance.getId(), ability.isTrigger() ? "triggered" : ability.isSpell() ? "spell"
                    : ability.isActivatedAbility() ? "activated" : "ability");
            var effect = pendingEffect(ability);
            if (effect != null) pendingEffects.put(instance.getId(), effect);
        }
        for (var item : view.getStack()) {
            CardView source = item.getSourceCard();
            boolean visible = source != null && source.canBeShownTo(viewer) && !source.isFaceDown();
            stack.add(map("id", item.getId(), "name", visible ? source.getCurrentState().getName() : "Face-down spell", "text", visible ? item.getText() : "",
                    "card", source != null && source.canBeShownTo(viewer) ? cardState(source, null) : null,
                    "ability", item.isAbility(),
                    "kind", visible ? stackKinds.getOrDefault(item.getId(), item.isAbility() ? "ability" : "spell")
                            : item.isAbility() ? "ability" : "spell",
                    "effect", visible ? pendingEffects.get(item.getId()) : null,
                    "targets", visible ? stackTargets(item) : List.of(),
                    "controller", item.getActivatingPlayer() == null ? "" : item.getActivatingPlayer().getName(),
                    "controllerId", item.getActivatingPlayer() == null ? null : item.getActivatingPlayer().getId()));
        }
        String result = gameResult();
        var activityFrame = activity.frame();
        activityRevision = activityFrame.revision();
        if (prompt != null) {
            var projectedPrompt = new LinkedHashMap<>(prompt.prompt);
            projectedPrompt.put("task", semanticTask(prompt));
            projectedPrompt.put("purpose", prompt.input instanceof InputConfirmMulligan ? "openingHand"
                    : prompt.input instanceof InputSelectTargets ? "targets"
                    : Objects.equals(prompt.prompt.get("context"), "playAbility") ? "abilities" : "");
            projectedPrompt.remove("payerId"); projectedPrompt.remove("unpaidMana");
            projectedPrompt.remove("unpaidManaParts");
            projectedPrompt.remove("fundingContext"); projectedPrompt.remove("usablePoolManaColors");
            projectedPrompt.remove("manaPaymentContext");
            prompt.poolManaColors.clear();
            if (prompt.input instanceof InputPayMana payment && sourceInputCurrent(prompt)) {
                projectedPrompt.putAll(ManaPaymentFacts.describe(payment, this::semanticCardId));
                prompt.poolManaColors.addAll(payment.getUsablePoolManaColors().stream().map(MagicColor::toShortString).toList());
            }
            if (prompt.colorPaymentOwner != null && prompt.kind.equals("choice")
                    && "colorChoice".equals(prompt.prompt.get("context"))
                    && human.getInputQueue().getInput() == prompt.colorPaymentOwner)
                projectedPrompt.put("manaPaymentContext", colorPaymentContext(prompt.colorPaymentOwner));
            if (prompt.input instanceof InputPassPriority) {
                projectedPrompt.put("priorityAbilities", priorityAbilities(human.getPriorityAbilities(), prompt));
                projectedPrompt.put("priorityAlternative", priorityAlternative(prompt.input));
            }
            prompt.prompt = projectedPrompt;
        }
        var snapshot = map("id", id, "revision", ++revision, "boardRevision", boardRevision, "format", format, "status", error != null ? "error" : game.isGameOver() ? "finished" : "playing",
                "error", error, "playerCount", allPlayers.size(), "viewerId", viewer.getId(), "turn", view.getTurn(), "phase", view.getPhase() == null ? "Pregame" : view.getPhase().nameForUi,
                "phaseKey", view.getPhase() == null ? "PREGAME" : view.getPhase().name(),
                "activePlayerId", view.getPlayerTurn() == null ? null : view.getPlayerTurn().getId(), "players", players,
                "priorityPlayerId", allPlayers.stream().filter(p -> !p.getHasLost() && p.getHasPriority()).map(PlayerView::getId).findFirst().orElse(null),
                "decisionOwnerId", null, "actionClass", "resolving",
                "stack", stack, "combat", combatState(prompt), "prompt", prompt == null ? null : prompt.prompt,
                "states", PublicStates.of(forge.game.PublicTableStates.snapshot(game)),
                "playAccesses", playAccess.project(human.getPlayer(), allPlayers,
                        prompt != null && prompt.input instanceof InputPassPriority && sourceInputCurrent(prompt) ? prompt.input : null,
                        human::getCard, activity::visualId, publishedAccessCards),
                "ignoredPriorityAbilities", priorityAbilities(human.getIgnoredPriorityAbilities(), prompt),
                "result", result, "notices", List.copyOf(notices), "activity", activityFrame.entries());
        String quietToken = quietYield.observe(snapshot, activity.quietSequence());
        if (prompt != null) {
            var projectedPrompt = new LinkedHashMap<>(prompt.prompt);
            projectedPrompt.put("quietYieldToken", canQuietYield(prompt.input) ? quietToken : "");
            prompt.prompt = projectedPrompt;
            snapshot.put("prompt", projectedPrompt);
        }
        latest = Collections.unmodifiableMap(snapshot);
        notifyPlaytest();
    }

    private static Map<String, Integer> publicMana(PlayerView player) {
        var mana = new LinkedHashMap<String, Integer>();
        // Colorless mana has its own atom; the card-color absence mask is zero.
        byte[] colors = ManaAtom.MANATYPES;
        String[] labels = {"W", "U", "B", "R", "G", "C"};
        for (int i = 0; i < colors.length; i++) mana.put(labels[i], player.getMana(colors[i]));
        return mana;
    }

    private List<Map<String, Object>> stackTargets(StackItemView root) {
        var targets = new LinkedHashMap<String, Map<String, Object>>();
        for (var item = root; item != null; item = item.getSubInstance()) {
            if (item.getTargetCards() != null) for (var card : item.getTargetCards()) {
                String visibleId = activity.visualId(card);
                if (visibleId == null) continue;
                targets.put("card:" + visibleId, map("kind", "card", "id", visibleId, "name", card.getCurrentState().getName(),
                        "playerId", card.getController() == null ? null : card.getController().getId()));
            }
            if (item.getTargetPlayers() != null) for (var player : item.getTargetPlayers())
                targets.put("player:" + player.getId(), map("kind", "player", "id", String.valueOf(player.getId()),
                        "name", player.equals(viewer) ? "You" : player.getName(), "playerId", player.getId()));
        }
        return List.copyOf(targets.values());
    }

    /** Copy combat only at a safe publish boundary, never from an IPC state read. */
    private Map<String, Object> combatState(Pending prompt) {
        var combat = game.getCombat();
        if (combat == null || game.isGameOver()) return null;
        boolean blocking = prompt != null && prompt.input instanceof InputBlock;
        boolean attacking = prompt != null && prompt.input instanceof InputAttack;
        var defenders = new ArrayList<Object>();
        GameEntity selectedDefender = null;
        for (var defender : combat.getDefenders()) {
            defenders.add(combatDefender(defender));
            if (attacking && highlighted.contains(GameEntityView.get(defender))) selectedDefender = defender;
        }
        var candidates = new ArrayList<String>();
        var attackOptions = new ArrayList<Object>();
        if (attacking) for (Card card : human.getPlayer().getCreaturesInPlay()) {
            if (combat.isAttacking(card) || selectedDefender != null && CombatUtil.canAttack(card, selectedDefender))
                candidates.add(combatIds.id(card.getView()));
            var legalDefenders = new ArrayList<Object>();
            for (GameEntity defender : combat.getDefenders()) {
                if (!CombatUtil.canAttack(card, defender) && !combat.isAttacking(card, defender)) continue;
                legalDefenders.add(combatDefender(defender));
                prompt.attackPairs.add(card.getId() + ":" + (defender instanceof Player ? "player:" : "card:") + defender.getId());
            }
            if (!legalDefenders.isEmpty()) attackOptions.add(map("cardId", combatIds.id(card.getView()), "defenders", legalDefenders));
        }
        var blockers = blocking ? human.getPlayer().getCreaturesInPlay() : List.<Card>of();
        var attacks = new ArrayList<Object>();
        for (Card attacker : combat.getAttackers()) {
            if (!attacker.getView().canBeShownTo(viewer)) continue;
            var assigned = combat.getBlockers(attacker);
            var eligible = new ArrayList<String>();
            for (Card blocker : blockers) {
                if (combat.isBlocking(blocker, attacker) || CombatUtil.canBlock(attacker, blocker, combat)) {
                    eligible.add(combatIds.id(blocker.getView()));
                    prompt.blockPairs.add(attacker.getId() + ":" + blocker.getId());
                }
            }
            attacks.add(map("cardId", combatIds.id(attacker.getView()),
                    "defender", combatDefender(combat.getDefenderByAttacker(attacker)),
                    "defendingPlayerId", combat.getDefenderPlayerByAttacker(attacker) == null ? null : combat.getDefenderPlayerByAttacker(attacker).getId(),
                    "blockerIds", assigned.stream().filter(card -> card.getView().canBeShownTo(viewer)).map(card -> combatIds.id(card.getView())).toList(),
                    "blocked", combat.isBlocked(attacker), "eligibleBlockerIds", eligible));
        }
        boolean attackValid = !attacking || CombatUtil.validateAttackers(combat);
        var repair = new ArrayList<Object>();
        if (attacking && !attackValid) for (var entry : combat.getAttackConstraints().getLegalAttackers().getLeft().entrySet())
            repair.add(map("cardId", combatIds.id(entry.getKey().getView()), "defender", combatDefender(entry.getValue())));
        return map("attackingPlayerId", combat.getAttackingPlayer().getId(), "attackers", attacks,
                "defenders", defenders, "selectedDefender", selectedDefender == null ? null : combatDefender(selectedDefender),
                "attackerCandidates", candidates,
                "attackOptions", attackOptions,
                "attackValid", attackValid, "attackRepair", repair,
                "blockerCandidates", blockers.stream().map(card -> combatIds.id(card.getView())).toList(),
                "blockProblem", blocking ? CombatUtil.validateBlocks(combat, human.getPlayer()) : null);
    }

    private Map<String, Object> combatDefender(GameEntity entity) {
        if (entity instanceof Player player) return map("kind", "player", "id", player.getId(),
                "name", player.getView().equals(viewer) ? "You" : player.getName(), "playerId", player.getId());
        if (entity instanceof Card card) {
            boolean visible = card.getView().canBeShownTo(viewer) && !card.isFaceDown();
            return map("kind", "card", "id", combatIds.id(card.getView()), "name", visible ? card.getView().getCurrentState().getName() : "Face-down permanent",
                    "playerId", card.isBattle() && card.getProtectingPlayer() != null ? card.getProtectingPlayer().getId() : card.getController().getId());
        }
        return null;
    }

    static boolean isVisibleInZone(CardView card, ZoneType zone, PlayerView viewer) {
        return card.getZone() == zone && card.canBeShownTo(viewer);
    }

    private Map<String, Object> cardState(CardView card, Pending prompt) {
        boolean hidden = card.isFaceDown();
        var face = card.getCurrentState();
        // Refreshes can remove or reorder visible cards without consuming the
        // input. Retain opaque identities separately from the current allowed
        // cards, so a removed card's handle can never select another card.
        String key = prompt == null ? "" : prompt.cardKeys.computeIfAbsent(card.getId(), ignored -> prompt.id + ":c" + prompt.nextCardKey++);
        if (prompt != null) prompt.cards.put(key, card);
        var counters = new LinkedHashMap<String, Integer>();
        if (card.getCounters() != null) for (var entry : card.getCounters().entrySet()) counters.put(entry.getElement().getName(), entry.getCount());
        var combat = game.getView().getCombat();
        var defender = combat == null ? null : combat.getDefender(card);
        boolean publicFace = !hidden || card.getZone() == ZoneType.Battlefield;
        var result = map("key", key, "visualId", activity.visualId(card), "combatId", combatIds.id(card), "name", hidden ? "Face-down card" : face.getName(),
                "type", hidden ? publicFace ? face.getType().toString() : "" : visibleType(card), "gamePieceType", gamePieceType(card, hidden),
                "manaCost", hidden ? "" : face.getManaCost().toString(), "commanderTax", commanderTax(card, hidden),
                "power", publicFace ? face.getPower() : null, "toughness", publicFace ? face.getToughness() : null,
                "text", hidden ? "" : CardRulesText.of(card), "tapped", card.isTapped(), "phasedOut", card.isPhasedOut(), "sick", card.isSick(), "damage", card.getDamage(),
                "attacking", card.isAttacking(), "blocking", card.isBlocking(), "counters", counters,
                "defenderId", defender instanceof PlayerView ? defender.getId() : null,
                "defender", defender == null ? null : defender instanceof CardView target && (!target.canBeShownTo(viewer) || target.isFaceDown()) ? "Face-down permanent" : defender.getName(),
                "selectable", selectable.contains(card) || actionable.contains(card) || linkedSourceActionable(card, prompt)
                        || prompt != null && prompt.input instanceof InputLondonMulligan && card.getController().equals(viewer) && card.getZone() == ZoneType.Hand,
                "highlighted", highlighted.contains(card), "faceDown", hidden,
                "combatKeywords", publicFace ? publicKeywords(face) : List.of(),
                "states", publicFace ? PublicStates.of(card.getPublicStates(viewer)) : List.of());
        AttachmentProjection.add(result, card, viewer, combatIds);
        addCardFaces(result, card, !hidden && card.canBeShownTo(viewer));
        addCardPrinting(result, card, !hidden && card.canBeShownTo(viewer));
        return result;
    }

    private boolean linkedSourceActionable(CardView card, Pending prompt) {
        if (card.isFaceDown() || card.getPreparedSpell() == null || prompt == null || !sourceInputCurrent(prompt)) return false;
        Card source = sourceCard(card);
        return source != null && SourceActions.hasLinkedAction(prompt.input, source, this::visibleActionHost);
    }

    private static int commanderTax(CardView card, boolean hidden) {
        return hidden || !card.isCommander() || card.getOwner() == null ? 0 : 2 * card.getOwner().getCommanderCast(card);
    }

    private static String gamePieceType(CardView card, boolean hidden) {
        return hidden ? "card" : Objects.requireNonNullElse(card.getGamePieceType(), GamePieceType.CARD).name().toLowerCase(Locale.ROOT);
    }

    private static String visibleType(CardView card) {
        String type = card.getCurrentState().getType().toString();
        return type.isBlank() && card.getGamePieceType() == GamePieceType.EFFECT ? "Effect" : type;
    }

    static Map<String, Integer> publicCounters(GameEntityView entity) {
        var counters = new LinkedHashMap<String, Integer>();
        if (entity.getCounters() != null) for (var entry : entity.getCounters().entrySet())
            if (entry.getCount() > 0) counters.put(entry.getElement().getName(), entry.getCount());
        return Collections.unmodifiableMap(counters);
    }

    private static List<String> publicKeywords(CardView.CardStateView face) {
        return face.getPublicStates().stream().map(forge.game.PublicState::key).distinct().toList();
    }

    static void addCardPrinting(Map<String, Object> result, CardView card, boolean visible) {
        if (visible) {
            // Prepared copies retain their creature's image key in Forge. That
            // printing identifies the wrong card face; use the live spell name.
            if (card.getCurrentState().getState() == CardStateName.PreparedSpell) {
                result.put("artName", card.getCurrentState().getName()); result.put("artFace", "front");
                return;
            }
            String imageKey = card.getCurrentState().getTrackableImageKey();
            String tokenImage = imageKey == null ? null : forge.ImageKeys.getTokenImageName(imageKey);
            if (card.isToken() && tokenImage != null) {
                String[] parts = tokenImage.split("\\|", -1);
                if (parts.length >= 3) {
                    var token = forge.StaticData.instance().getAllTokens().getPrinting(parts[0], parts[0] + "|" + parts[1] + "|" + parts[2]);
                    if (token != null) {
                        var printing = DeckTokens.describe(parts[0], token);
                        result.put("artName", printing.artName());
                        result.put("artFace", card.getCurrentState().getState() == CardStateName.Backside ? "back" : "front");
                        result.put("artSet", printing.artSet()); result.put("edition", printing.edition());
                        result.put("collectorNumber", printing.collectorNumber());
                    }
                }
                return;
            }
            var printing = forge.util.ImageUtil.getPaperCardFromImageKey(card.getCurrentState().getTrackableImageKey());
            if (printing != null) {
                result.put("artSet", CardCatalog.artSet(printing));
                result.put("edition", printing.getEdition());
                result.put("collectorNumber", printing.getCollectorNumber());
            }
        }
    }

    /** Only add alternate identities after the same visibility check as the current face. */
    static void addCardFaces(Map<String, Object> result, CardView card, boolean visible) {
        if (!visible || !card.isDoubleFacedCard() || !card.hasAlternateState()) return;
        var current = card.getCurrentState();
        var other = card.getAlternateState();
        boolean back = current.getState() == CardStateName.Backside || current.getState() == CardStateName.Meld;
        var front = back ? other : current;
        String artName = Objects.requireNonNullElse(front.getOracleName(), "");
        if (artName.isEmpty()) artName = front.getName();
        result.put("artName", artName);
        result.put("artFace", back ? "back" : "front");
        result.put("otherFace", map("name", other.getName(), "manaCost", other.getManaCost().toString(),
                "type", other.getType().toString(), "oracleText", other.getOracleText(),
                "power", other.getPower(), "toughness", other.getToughness(),
                "artName", artName, "artFace", back ? "front" : "back"));
    }

    @Override
    public void fail(Throwable failure) { table.fail(failure); }
    void fail(String failure) {
        synchronized (gate) {
            if (closed) return;
            error = failure;
            closed = true;
            if (pending != null) pending.response.completeExceptionally(new IllegalStateException(failure));
            pending = null;
            var next = new LinkedHashMap<>(latest);
            next.put("revision", ++revision); next.put("status", "error"); next.put("error", failure); next.put("prompt", null);
            next.put("result", null);
            next.put("priorityPlayerId", null); next.put("decisionOwnerId", null); next.put("actionClass", "error");
            latest = Collections.unmodifiableMap(next);
            notifyPlaytest();
        }
    }

    @SuppressWarnings("unchecked")
    private Object invokeGui(String name, Object[] a) {
        return switch (name) {
            case "getGameView" -> game.getView();
            case "isLibgdxPort", "isNetGame", "isGamePaused", "isUiSetToSkipPhase" -> false;
            case "getGameSpeed" -> PlaybackSpeed.NORMAL;
            case "getDayTime" -> null;
            case "isSelecting" -> !selectable.isEmpty();
            case "showPromptMessage" -> {
                message = String.valueOf(a[1]);
                Input nextInput = human.getInputProxy().getInput();
                // A target-error message can omit its card. Retain it only for
                // the same input; priority and subsequent choices must clear it.
                if (a.length > 2 && a[2] instanceof CardView card) promptSource = card;
                else if (nextInput != displayedInput) promptSource = null;
                displayedInput = nextInput;
                yield null;
            }
            case "updateButtons" -> { ok = (String)a[1]; cancel = (String)a[2]; okEnabled = (boolean)a[3]; cancelEnabled = (boolean)a[4]; yield null; }
            case "setSelectables" -> { selectable.clear(); ((Iterable<CardView>)a[0]).forEach(selectable::add); yield null; }
            case "clearSelectables" -> { selectable.clear(); yield null; }
            case "setWeaklySelectable" -> { actionable.clear(); ((Iterable<CardView>)a[0]).forEach(actionable::add); yield null; }
            case "clearWeaklySelectable" -> { actionable.clear(); yield null; }
            case "setHighlighted" -> { for (var value : (Iterable<GameEntityView>)a[0]) { if ((boolean)a[1]) highlighted.add(value); else highlighted.remove(value); } yield null; }
            case "openZones" -> new PlayerZoneUpdates();
            case "tempShowZones" -> a[1];
            case "getAbilityToPlay" -> {
                // Engine-driven choices (including triggers) have no pointer event and
                // are not activated abilities. Filtering them by canPlay silently drops triggers.
                var abilities = a[2] == null ? (List<SpellAbilityView>)a[1]
                        : ((List<SpellAbilityView>)a[1]).stream().filter(SpellAbilityView::canPlay).toList();
                yield abilities.size() == 1 && (a[2] == null || !abilities.get(0).promptIfOnlyPossibleAbility()) ? abilities.get(0)
                        : a[2] == null ? first(choose("Choose an ability of " + label(a[0]), 0, 1, abilities, false, null))
                        : chooseAbility((CardView)a[0], abilities);
            }
            case "one" -> first(choose((String)a[0], 1, 1, (List<?>)a[1], false, (FSerializableFunction<Object, String>)a[2]));
            case "oneOrNone" -> first(choose((String)a[0], 0, 1, (List<?>)a[1], false, null));
            case "getChoices" -> choose((String)a[0], (int)a[1], (int)a[2], (List<?>)a[3], false, (FSerializableFunction<Object, String>)a[5]);
            case "many" -> choose(a[0] + " · " + a[1], (int)a[2], (int)a[3], combine(a[4], a[5]), false, null);
            case "order" -> {
                List<?> choices = combine(a[4], a[5]);
                yield new IGuiGame.OrderResult<>(choose(a[0] + " · " + a[1], choices.size() - (int)a[3], choices.size() - (int)a[2], choices, true, null), false);
            }
            case "insertInList" -> { var choices = new ArrayList<Object>((List<?>)a[2]); choices.add(a[1]); yield choose((String)a[0], choices.size(), choices.size(), choices, true, null); }
            case "showConfirmDialog" -> first(choose(a[1] + "\n" + a[0], 1, 1, List.of(a[2], a[3]), false, null)).equals(a[2]);
            case "confirm" -> first(choose((String)a[1], 1, 1, (List<?>)a[3], false, null)).equals(((List<?>)a[3]).get(0));
            case "showOptionDialog" -> { var options = (List<?>)a[3]; yield options.indexOf(first(choose(a[1] + "\n" + a[0], 1, 1, options, false, null))); }
            case "getInteger" -> number((String)a[0], (int)a[1], (int)a[2]);
            case "showInputDialog" -> inputText(a);
            case "chooseSingleEntityForEffect" -> first(chooseForEffect((String)a[0], (boolean)a[3] ? 0 : 1, 1, (List<?>)a[1], (DelayedReveal)a[2]));
            case "chooseEntitiesForEffect" -> chooseForEffect((String)a[0], (int)a[2], (int)a[3], (List<?>)a[1], (DelayedReveal)a[4]);
            case "reveal" -> { reveal((String)a[0], (List<?>)a[1]); yield null; }
            case "message", "showErrorDialog" -> { synchronized (gate) { notices.add(String.valueOf(a[0])); while (notices.size() > 12) notices.remove(0); } yield null; }
            case "assignCombatDamage" -> combatDamage(a);
            case "assignGenericAmount" -> genericAmount(a);
            case "manipulateCardList" -> reorderCards(a);
            case "sideboard" -> null;
            case "setGameView", "setOriginalGameController", "setGameController", "setSpectator", "setCurrentPlayer", "openView",
                 "showCombat", "alertUser", "flashIncorrectAction", "updatePhase", "updateTurn", "updatePlayerControl", "enableOverlay", "disableOverlay",
                 "showManaPool", "hideManaPool", "updateStack", "notifyStackAddition", "notifyStackRemoval", "handleLandPlayed", "handleGameEvent",
                 "hideZones", "updateZones", "updateSingleCard", "updateCards", "updateRevealedCards", "refreshCardDetails", "refreshField",
                 "updateManaPool", "updateLives", "updateShards", "updateDependencies", "setPanelSelection", "setCard", "setPlayerAvatar",
                 "restoreOldZones", "awaitNextInput", "cancelAwaitNextInput", "showWaitingTimer", "updateAutoPassPrompt", "applyYieldUpdate",
                 "setGamePause", "setGameSpeed", "updateDayTime", "afterGameEnd", "finishGame" -> null;
            case "toString" -> "Mana Table match";
            default -> throw new UnsupportedOperationException("Match dialog is not implemented: " + name);
        };
    }

    @Override
    public Object platformDialog(String name, Object[] a) {
        if (name.equals("getChoices")) return choose((String)a[0], (int)a[1], (int)a[2], new ArrayList<>((Collection<?>)a[3]), false, (FSerializableFunction<Object, String>)a[5]);
        if (name.equals("chooseCard")) return first(choose(a[0] + "\n" + a[1], 1, 1, (List<?>)a[2], false, null));
        if (name.equals("order")) {
            var choices = combine(a[4], a[5]);
            return choose(a[0] + "\n" + a[1], choices.size() - (int)a[3], choices.size() - (int)a[2], choices, true, null);
        }
        return invokeGui(name, a);
    }

    private void revealDelayed(DelayedReveal reveal) { if (reveal != null) reveal(reveal.getMessagePrefix(), reveal.getCards()); }
    private void reveal(String title, List<?> choices) {
        try {
            if (!libraryCards(choices)) { dialog("reveal", title, choices, 0, 0, false, null); return; }
            var next = choicePrompt("reveal", title, List.of(), 0, 0, false, null);
            libraryPrompt(next, List.of(), choices, ((CardView)choices.get(0)).getController(), false);
            await(next);
        } catch (CancellationException cancellation) {
            // A departed observer cannot acknowledge a read-only reveal. Their
            // departure does not cancel the ability another player is resolving.
            if (!viewer.getHasLost() && !game.isGameOver()) { throw cancellation; }
        }
    }

    private static boolean libraryCards(List<?> choices) {
        return !choices.isEmpty() && choices.stream().allMatch(item -> item instanceof CardView card && card.getZone() == ZoneType.Library)
                && choices.stream().map(item -> ((CardView)item).getController()).distinct().count() == 1;
    }

    private List<?> chooseForEffect(String title, int min, int max, List<?> choices, DelayedReveal reveal) {
        boolean library = reveal == null ? libraryCards(choices) : reveal.getZone().equals(Set.of(ZoneType.Library)) && choices.stream().allMatch(CardView.class::isInstance);
        if (!library) {
            revealDelayed(reveal);
            if (choices.isEmpty()) return List.of();
            var next = choicePrompt("choice", title, choices, Math.max(0, min), Math.min(choices.size(), max < 0 ? choices.size() : max), false, null);
            if (reveal != null) {
                // The rules controller keeps the delayed window open through
                // this choice. Noneligible faces are current read-only facts,
                // not selection options or permission borrowed from an earlier
                // reveal acknowledgement. Never attach occurrence/action IDs.
                var disclosed = new ArrayList<Map<String, Object>>();
                for (CardView card : new LinkedHashSet<>(reveal.getCards())) {
                    if (choices.contains(card)) continue;
                    var portrait = choiceCard(card, viewer);
                    disclosed.add(map("label", portrait.get("name"), "card", portrait));
                }
                disclosed.sort(Comparator.comparing(item -> String.valueOf(item.get("label")), String.CASE_INSENSITIVE_ORDER));
                if (!disclosed.isEmpty()) next.prompt.put("disclosedCards", List.copyOf(disclosed));
            }
            var selected = new ArrayList<Object>();
            for (var index : await(next).getAsJsonArray("choices")) selected.add(choices.get(index.getAsInt()));
            return selected;
        }
        if (choices.isEmpty()) return List.of();
        // Search handles must not encode shuffled positions. Both the offered
        // indices and the response lookup use this detached canonical order.
        choices = choices.stream().sorted(Comparator.comparing(item -> label(item), String.CASE_INSENSITIVE_ORDER)
                .thenComparing(item -> item instanceof CardView card ? card.getId() : 0)).toList();
        var next = choicePrompt("choice", title, choices, Math.max(0, min), Math.min(choices.size(), max < 0 ? choices.size() : max), false, null);
        libraryPrompt(next, choices, reveal == null ? choices : reveal.getCards(),
                reveal == null ? ((CardView)choices.get(0)).getController() : reveal.getOwner(), true);
        var selected = new ArrayList<Object>();
        for (var index : await(next).getAsJsonArray("choices")) selected.add(choices.get(index.getAsInt()));
        return selected;
    }

    private void libraryPrompt(Pending next, List<?> choices, List<?> revealed, PlayerView owner, boolean choosing) {
        String library = owner.equals(viewer) ? "your library" : owner.getName() + "’s library";
        next.prompt.put("context", "librarySearch");
        next.prompt.put("title", (choosing ? "Search " : "Look at ") + library);
        var permitted = new LinkedHashSet<Object>(revealed);
        permitted.addAll(choices);
        var cards = new ArrayList<Map<String, Object>>();
        for (Object item : permitted.stream().sorted(Comparator.comparing(this::label, String.CASE_INSENSITIVE_ORDER)
                .thenComparing(value -> value instanceof CardView card ? card.getId() : 0)).toList()) {
            if (!(item instanceof CardView card)) continue;
            var details = choiceCard(card, viewer);
            int index = choices.indexOf(card);
            cards.add(map("index", index < 0 ? null : index, "label", details.get("name"), "card", details));
        }
        // A search grants access to these cards, not a catalog query or a new
        // permission to inspect another hidden zone. Do not expose deck order.
        cards.sort(Comparator.comparing(card -> String.valueOf(card.get("label")), String.CASE_INSENSITIVE_ORDER));
        next.prompt.put("libraryCards", cards);
    }

    static Map<String, Object> choiceCard(CardView card, PlayerView viewer) {
        boolean hidden = card.isFaceDown() || !card.canBeShownTo(viewer);
        var face = card.getCurrentState();
        var result = map("name", hidden ? "Face-down or hidden card" : face.getName(), "faceDown", hidden,
                "type", hidden ? "" : visibleType(card), "gamePieceType", gamePieceType(card, hidden), "manaCost", hidden ? "" : face.getManaCost().toString(),
                "commanderTax", commanderTax(card, hidden),
                "text", hidden ? "" : CardRulesText.of(card), "power", hidden ? null : face.getPower(), "toughness", hidden ? null : face.getToughness(),
                "combatKeywords", hidden ? List.of() : publicKeywords(face),
                "states", hidden ? List.of() : PublicStates.of(card.getPublicStates(viewer)));
        addCardFaces(result, card, !hidden);
        return result;
    }
    private static Object first(List<?> choices) { return choices.isEmpty() ? null : choices.get(0); }
    private static List<?> combine(Object source, Object destination) {
        var combined = new ArrayList<Object>((Collection<?>)source);
        if (destination != null) combined.addAll((Collection<?>)destination);
        return combined;
    }

    private SpellAbilityView chooseAbility(CardView host, List<SpellAbilityView> abilities) {
        if (abilities.isEmpty()) return null;
        boolean visible = host != null && host.canBeShownTo(viewer) && !host.isFaceDown();
        String name = visible ? host.getCurrentState().getName() : "this card";
        boolean activating = host != null && host.getZone() == ZoneType.Battlefield;
        var next = choicePrompt("choice", (activating ? "Choose an ability of " : "Choose how to play ") + name + ". Costs and any targets come next.",
                abilities, 0, 1, false, ability -> visible ? String.valueOf(ability) : "Card ability");
        next.prompt.put("context", "playAbility");
        next.prompt.put("title", (activating ? "Activate " : "Play ") + name);
        if (host != null && host.canBeShownTo(viewer)) {
            next.prompt.put("sourceCard", cardState(host, null));
            next.prompt.put("sourceZone", host.getZone() == null ? "" : host.getZone().name());
        }
        var items = new ArrayList<Object>();
        for (int i = 0; i < abilities.size(); i++) {
            var ability = abilities.get(i);
            String description = visible ? ability.getDescription() : "Card ability";
            String title = description;
            String detail = "";
            if (visible && ability.isSpell() && description.startsWith("Bestow ")) {
                title = "Bestow — cast as an Aura";
                detail = description;
            } else if (visible && ability.isSpell() && description.startsWith(name + " - Creature")) {
                title = "Cast as a creature";
                detail = host.getCurrentState().getManaCost() + " · " + description.substring(name.length() + 3);
            }
            items.add(map("index", i, "label", title, "detail", detail));
        }
        next.prompt.put("choices", items);
        var chosen = await(next).getAsJsonArray("choices");
        return chosen.isEmpty() ? null : abilities.get(chosen.get(0).getAsInt());
    }

    private MagicColor.Color chooseColor(String message, CardView source, List<MagicColor.Color> colors) {
        var next = choicePrompt("choice", message, colors, 1, 1, false, color -> ((MagicColor.Color) color).getTranslatedName());
        next.prompt.put("context", "colorChoice");
        if (human.getInputQueue().getInput() instanceof InputPayMana payment) next.colorPaymentOwner = payment;
        boolean visible = source != null && source.canBeShownTo(viewer);
        String name = visible && !source.isFaceDown() ? source.getCurrentState().getName() : "this card";
        next.prompt.put("title", "Choose a color for " + name);
        if (visible) {
            next.prompt.put("sourceCard", cardState(source, null));
            next.prompt.put("sourceZone", source.getZone() == null ? "" : source.getZone().name());
        }
        next.prompt.put("choices", java.util.stream.IntStream.range(0, colors.size()).mapToObj(i ->
                map("index", i, "label", colors.get(i).getTranslatedName(), "mana", "{" + colors.get(i).getShortName() + "}")).toList());
        var answer = await(next);
        return colors.get(answer.getAsJsonArray("choices").get(0).getAsInt());
    }

    private Map<String, Object> colorPaymentContext(InputPayMana payment) {
        var ability = payment.getPaidForAbility();
        var source = ability == null || ability.getHostCard() == null ? null : ability.getHostCard().getView();
        boolean visible = source != null && source.canBeShownTo(viewer);
        Map<String, Object> card = visible ? cardState(source, null) : null;
        if (card != null) {
            card.put("key", ""); card.put("selectable", false); card.put("highlighted", false);
        }
        String sourceId = card == null ? "" : Objects.toString(card.get("visualId"), "");
        if (sourceId.isEmpty() && card != null) sourceId = Objects.toString(card.get("combatId"), "");
        return map("payerId", payment.getPayer().getId(), "unpaidManaParts", ManaPaymentFacts.parts(payment.getRemainingManaCost()),
                "source", map("cardId", sourceId, "card", card,
                        "controllerId", visible && source.getController() != null ? source.getController().getId() : null,
                        "zone", visible && source.getZone() != null ? source.getZone().name() : ""));
    }

    private CounterType chooseCounterTypeForPayment(String message, CardView permanent, int amount,
            List<CounterType> types, Map<CounterType, Integer> available) {
        if (types.size() <= 1) return types.isEmpty() ? null : types.get(0);
        var next = choicePrompt("choice", message, types, 1, 1, false, type -> ((CounterType) type).getName());
        if (promptSource != null && promptSource.canBeShownTo(viewer)) {
            next.prompt.put("sourceCard", counterPaymentCard(promptSource));
            next.prompt.put("sourceZone", promptSource.getZone() == null ? "" : promptSource.getZone().name());
            next.prompt.put("sourceControllerId", promptSource.getController() == null ? null : promptSource.getController().getId());
        }
        if (permanent != null && permanent.getZone() == ZoneType.Battlefield && isAllocationOccurrence(permanent, viewer)) {
            var affected = map("cardId", combatIds.id(permanent), "card", counterPaymentCard(permanent),
                    "controllerId", permanent.getController() == null ? null : permanent.getController().getId(),
                    "zone", permanent.getZone().name());
            var options = new ArrayList<Object>();
            for (int index = 0; index < types.size(); index++) {
                var type = types.get(index);
                options.add(map("index", index, "type", type.toString(), "label", type.getName(), "available", available.get(type)));
            }
            next.prompt.put("counterPaymentChoice", map("amount", amount, "permanent", affected, "options", options));
        }
        var answer = await(next);
        return types.get(answer.getAsJsonArray("choices").get(0).getAsInt());
    }

    private Map<String, Object> counterPaymentCard(CardView card) {
        var details = allocationCard(card);
        // A public face-down position has a combat ID, not a disclosed identity.
        if (details.get("visualId") == null) details.remove("visualId");
        if (details.get("combatId") == null) details.remove("combatId");
        return details;
    }

    private List<?> choose(String title, int min, int max, List<?> choices, boolean ordered, FSerializableFunction<Object, String> display) {
        if (choices.isEmpty()) return List.of();
        var answer = dialog("choice", title, choices, Math.max(0, min), Math.min(choices.size(), max < 0 ? choices.size() : max), ordered, display);
        var selected = new ArrayList<Object>();
        for (var index : answer.getAsJsonArray("choices")) selected.add(choices.get(index.getAsInt()));
        return selected;
    }

    private JsonObject dialog(String kind, String title, List<?> choices, int min, int max, boolean ordered, FSerializableFunction<Object, String> display) {
        return await(choicePrompt(kind, title, choices, min, max, ordered, display));
    }

    private Pending choicePrompt(String kind, String title, List<?> choices, int min, int max, boolean ordered, FSerializableFunction<Object, String> display) {
        Pending next = new Pending(kind, null);
        next.optionSemantics = DecisionSemantics.options(choices, human::getDecisionAbility, human.getPlayer(), this::semanticCardId);
        next.size = choices.size(); next.min = min; next.max = max;
        var items = new ArrayList<Object>();
        for (int i = 0; i < choices.size(); i++) {
            Object choice = choices.get(i);
            String label = display == null ? label(choice) : display.apply(choice);
            var item = map("index", i, "label", label);
            if (choice instanceof CardView card) {
                item.put("card", choiceCard(card, viewer));
                // Link only occurrences already on this seat's table. A revealed
                // library/opponent-hand face must never gain a persistent handle.
                if (isTableChoice(card, viewer)) item.put("cardId", activity.visualId(card));
            }
            items.add(item);
        }
        next.prompt = map("id", next.id, "kind", kind, "message", title, "choices", items, "min", min, "max", max, "ordered", ordered);
        return next;
    }

    static boolean isTableChoice(CardView card, PlayerView viewer) {
        return !card.isFaceDown() && card.canBeShownTo(viewer)
                && (card.getZone() == ZoneType.Battlefield || card.getZone() == ZoneType.Command
                    || card.getZone() == ZoneType.Hand && viewer.equals(card.getController()));
    }

    private List<?> reorderCards(Object[] args) {
        var cards = new ArrayList<CardView>();
        ((Iterable<CardView>)args[1]).forEach(cards::add);
        var movable = new HashSet<CardView>();
        ((Iterable<CardView>)args[2]).forEach(movable::add);
        if (cards.isEmpty()) return cards;
        var next = choicePrompt("choice", (String)args[0], cards, cards.size(), cards.size(), true, null);
        next.validateOrder = order -> {
            var fixed = cards.stream().filter(card -> !movable.contains(card)).toList();
            var arranged = order.stream().map(cards::get).toList();
            if (!arranged.stream().filter(card -> !movable.contains(card)).toList().equals(fixed)) throw new IllegalArgumentException("Keep the hidden or fixed cards in their original order");
            if ((boolean)args[5]) return;
            int firstFixed = fixed.isEmpty() ? cards.size() : arranged.indexOf(fixed.get(0));
            int lastFixed = fixed.isEmpty() ? -1 : arranged.indexOf(fixed.get(fixed.size() - 1));
            for (CardView card : movable) {
                int index = arranged.indexOf(card);
                if (index < 0 || (boolean)args[3] && index < firstFixed || (boolean)args[4] && index > lastFixed) continue;
                long before = cards.subList(0, cards.indexOf(card)).stream().filter(fixed::contains).count();
                long after = arranged.subList(0, index).stream().filter(fixed::contains).count();
                if (before != after) throw new IllegalArgumentException("Move selected cards only to the allowed top or bottom of the library");
            }
        };
        var result = new ArrayList<CardView>();
        for (var index : await(next).getAsJsonArray("choices")) result.add(cards.get(index.getAsInt()));
        return result;
    }

    private String label(Object item) {
        if (item instanceof CardView card) return card.canBeShownTo(viewer) && !card.isFaceDown() ? card.getCurrentState().getName() : "Face-down or hidden card";
        if (item instanceof PlayerView player) return player.equals(viewer) ? "You" : player.getName();
        if (item instanceof PaperCard card) return card.getName();
        return String.valueOf(item);
    }

    private JsonObject await(Pending next) {
        game.getAction().withState(() -> { synchronized (gate) {
            if (closed) throw new CancellationException("Match closed");
            pending = next;
            publish(next);
        } });
        try { return game.getAction().awaitState(next.response); }
        catch (InterruptedException e) { Thread.currentThread().interrupt(); throw new CancellationException("Match interrupted"); }
        catch (ExecutionException e) { throw new CancellationException(e.getCause().getMessage()); }
    }

    private int number(String title, int min, int max) {
        Pending next = new Pending("number", null);
        next.min = min; next.max = max;
        next.prompt = map("id", next.id, "kind", "number", "message", title, "min", min, "max", max);
        return await(next).get("value").getAsInt();
    }

    private int announcedNumber(String title, SpellAbilityView view, String symbol, int min, int max) {
        SpellAbility ability = human.getDecisionAbility(view);
        Pending next = new Pending("number", null);
        next.min = min; next.max = max;
        next.announcedAbility = ability; next.announcedSymbol = symbol;
        next.prompt = map("id", next.id, "kind", "number", "message", title, "min", min, "max", max);
        if (ability != null && ability.getHostCard() != null) {
            CardView source = ability.getHostCard().getView();
            String sourceId = numericSourceId(source);
            if (!sourceId.isEmpty()) {
                next.prompt.put("sourceCardId", sourceId);
                next.prompt.put("sourceCard", counterPaymentCard(source));
                next.prompt.put("sourceZone", source.getZone().name());
                next.prompt.put("sourceControllerId", source.getController().getId());
            }
        }
        return await(next).get("value").getAsInt();
    }

    private String numericSourceId(CardView source) {
        return source != null && source.getZone() != null && !source.isFaceDown() && source.canBeShownTo(viewer)
                ? combatIds.id(source) : "";
    }

    private String inputText(Object[] args) {
        if (args[4] != null) return String.valueOf(first(choose(args[1] + "\n" + args[0], 1, 1, (List<?>)args[4], false, null)));
        Pending next = new Pending("text", null);
        next.numeric = (boolean)args[5];
        next.prompt = map("id", next.id, "kind", "text", "message", args[0], "initial", args[3], "numeric", args[5]);
        return await(next).get("value").getAsString();
    }

    private Map<CardView, Integer> combatDamage(Object[] a) {
        var blockers = (List<CardView>)a[1];
        var choices = new ArrayList<Object>(blockers);
        var attacker = ((CardView)a[0]).getCurrentState();
        boolean canHitDefender = a[3] != null && (attacker.hasTrample() || attacker.hasDivideDamage() && (boolean)a[4]);
        if (canHitDefender) choices.add(a[3]);
        Pending next = allocation("Assign " + a[2] + " combat damage from " + label(a[0]), choices, (int)a[2], false);
        next.defenderIndex = !canHitDefender || attacker.hasDivideDamage() && (boolean)a[4] ? -1 : choices.size() - 1;
        boolean deathtouch = attacker.hasDeathtouch();
        next.lethal = blockers.stream().map(card -> Math.max(0, deathtouch ? Math.min(1, card.getLethalDamage()) : card.getLethalDamage())).toList();
        next.maySkip = (boolean)a[5];
        next.prompt.put("maySkip", next.maySkip);
        describeAllocation(next, choices, (CardView)a[0], "combat", "combat damage");
        next.prompt.put("overrideOrder", a[4]);
        var response = await(next);
        if (string(response, "action").equals("skip")) return null;
        var amounts = response.getAsJsonArray("values");
        var result = new LinkedHashMap<CardView, Integer>();
        for (int i = 0; i < amounts.size(); i++) if (amounts.get(i).getAsInt() > 0) result.put(i < blockers.size() ? blockers.get(i) : null, amounts.get(i).getAsInt());
        return result;
    }

    private Map<Object, Integer> genericAmount(Object[] a) {
        var choices = new ArrayList<>(((Map<?, ?>)a[1]).keySet());
        String amountLabel = forge.util.Localizer.getInstance().getMessageorUseDefault(String.valueOf(a[4]), String.valueOf(a[4]));
        Pending next = allocation("Assign " + a[2] + " " + amountLabel, choices, (int)a[2], (boolean)a[3]);
        next.limits = choices.stream().map(choice -> (Integer)((Map<?, ?>)a[1]).get(choice)).toList();
        next.prompt.put("limits", next.limits);
        describeAllocation(next, choices, (CardView)a[0], "generic", amountLabel);
        var amounts = await(next).getAsJsonArray("values");
        var result = new LinkedHashMap<Object, Integer>();
        for (int i = 0; i < amounts.size(); i++) result.put(choices.get(i), amounts.get(i).getAsInt());
        return result;
    }

    private Pending allocation(String title, List<?> choices, int amount, boolean atLeastOne) {
        Pending next = new Pending("allocate", null);
        next.size = choices.size(); next.amount = amount; next.atLeastOne = atLeastOne;
        var items = new ArrayList<Object>();
        for (int i = 0; i < choices.size(); i++) items.add(map("index", i, "label", label(choices.get(i))));
        next.prompt = map("id", next.id, "kind", "allocate", "message", title, "choices", items, "amount", amount, "atLeastOne", atLeastOne);
        return next;
    }

    /** Allocation indices remain the engine's response ordering. Occurrence links
     * identify public cards without granting an input action or revealing a face. */
    private void describeAllocation(Pending next, List<?> choices, CardView source, String kind, String amountLabel) {
        next.prompt.put("allocationKind", kind);
        next.prompt.put("amountLabel", amountLabel);
        if (source != null) {
            next.prompt.put("sourceCard", allocationCard(source));
            if (isAllocationOccurrence(source, viewer)) {
                next.prompt.put("sourceCardId", combatIds.id(source));
                if (source.getController() != null) next.prompt.put("sourceControllerId", source.getController().getId());
            }
        }
        var items = new ArrayList<Object>();
        for (int i = 0; i < choices.size(); i++) {
            Object choice = choices.get(i);
            var item = map("index", i, "id", next.id + ":allocation:" + i, "label", label(choice),
                    "minimum", next.atLeastOne ? 1 : 0,
                    "maximum", i < next.limits.size() ? Math.min(next.amount, next.limits.get(i)) : next.amount,
                    "isDefender", i == next.defenderIndex);
            if (i < next.lethal.size()) item.put("lethal", next.lethal.get(i));
            if (choice instanceof CardView card) {
                item.put("card", allocationCard(card));
                if (isAllocationOccurrence(card, viewer)) {
                    item.put("cardId", combatIds.id(card));
                    if (card.getController() != null) item.put("controllerId", card.getController().getId());
                    if (card.getOwner() != null) item.put("ownerId", card.getOwner().getId());
                }
            } else if (choice instanceof PlayerView player) item.put("playerId", player.getId());
            items.add(item);
        }
        next.prompt.put("choices", items);
    }

    private Map<String, Object> allocationCard(CardView card) {
        var details = allocationCardDetails(card, viewer);
        if (isAllocationOccurrence(card, viewer)) {
            details.put("visualId", activity.visualId(card));
            details.put("combatId", combatIds.id(card));
        }
        return details;
    }

    static boolean isAllocationOccurrence(CardView card, PlayerView viewer) {
        if (!card.canBeShownTo(viewer)) return false;
        ZoneType zone = card.getZone();
        if (card.isFaceDown()) return zone == ZoneType.Battlefield;
        return zone == ZoneType.Hand ? viewer.equals(card.getController())
                : zone == ZoneType.Battlefield || zone == ZoneType.Stack || zone == ZoneType.Graveyard
                || zone == ZoneType.Exile || zone == ZoneType.Command;
    }

    /** Face-down permanents have public current characteristics, but their
     * original identity, rules text, mana cost and alternate faces stay hidden. */
    static Map<String, Object> allocationCardDetails(CardView card, PlayerView viewer) {
        var details = choiceCard(card, viewer);
        details.put("key", "");
        boolean visible = card.canBeShownTo(viewer);
        boolean publicFace = visible && (!card.isFaceDown() || card.getZone() == ZoneType.Battlefield);
        if (!publicFace) return details;
        var face = card.getCurrentState();
        details.put("type", face.getType().toString());
        details.put("power", face.getPower());
        details.put("toughness", face.getToughness());
        details.put("damage", card.getDamage());
        details.put("tapped", card.isTapped());
        details.put("phasedOut", card.isPhasedOut());
        var counters = new LinkedHashMap<String, Integer>();
        if (card.getCounters() != null) for (var entry : card.getCounters().entrySet()) counters.put(entry.getElement().getName(), entry.getCount());
        details.put("counters", counters);
        details.put("combatKeywords", publicKeywords(face));
        details.put("states", PublicStates.of(card.getPublicStates(viewer)));
        return details;
    }

    private static void validateDialog(Pending next, JsonObject request) {
        switch (next.kind) {
            case "choice" -> {
                var choices = request.getAsJsonArray("choices");
                if (choices == null || choices.size() < next.min || choices.size() > next.max) throw new IllegalArgumentException("Choose the required number of items");
                Set<Integer> selected = new LinkedHashSet<>();
                for (var item : choices) {
                    int index = exactInt(item.getAsString());
                    if (index < 0 || index >= next.size || !selected.add(index)) throw new IllegalArgumentException("Invalid or duplicate choice");
                }
                if (next.validateOrder != null) next.validateOrder.accept(new ArrayList<>(selected));
            }
            case "number" -> { int value = exactInt(string(request, "value")); if (value < next.min || value > next.max) throw new IllegalArgumentException("Number is out of range"); }
            case "text" -> {
                String value = string(request, "value");
                if (value.length() > 500) throw new IllegalArgumentException("Input is too long");
                if (next.numeric) exactInt(value);
            }
            case "allocate" -> {
                if (next.maySkip && string(request, "action").equals("skip")) return;
                var values = request.getAsJsonArray("values");
                if (values == null || values.size() != next.size) throw new IllegalArgumentException("Assign every amount");
                long total = 0;
                for (var value : values) { int amount = exactInt(value.getAsString()); if (amount < (next.atLeastOne ? 1 : 0) || amount > next.amount) throw new IllegalArgumentException("Invalid amount"); total += amount; }
                if (total != next.amount) throw new IllegalArgumentException("The assigned amounts must total " + next.amount);
                for (int i = 0; i < next.limits.size(); i++) if (values.get(i).getAsInt() > next.limits.get(i)) throw new IllegalArgumentException("An amount exceeds its target's limit");
                if (next.defenderIndex >= 0 && values.get(next.defenderIndex).getAsInt() > 0) {
                    for (int i = 0; i < next.lethal.size(); i++) if (values.get(i).getAsInt() < next.lethal.get(i)) throw new IllegalArgumentException("Assign lethal damage to blockers before the defender");
                }
            }
            case "reveal" -> { }
            default -> throw new IllegalArgumentException("Unknown prompt");
        }
    }

    private static int exactInt(String value) { if (!value.matches("-?\\d{1,10}")) throw new IllegalArgumentException("Expected an integer"); return Integer.parseInt(value); }
    private static String string(JsonObject json, String key) { return json.has(key) ? json.get(key).getAsString() : ""; }
    private static LinkedHashMap<String, Object> map(Object... fields) {
        var result = new LinkedHashMap<String, Object>();
        for (int i = 0; i < fields.length; i += 2) result.put((String)fields[i], fields[i + 1]);
        return result;
    }
}
