package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.game.ExecutionTracker;
import forge.game.card.CardView;
import forge.game.event.*;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;

import java.util.*;

/** Copies an allowlist of events at emission time; never exports raw engine logs or views. */
public final class MatchActivity {
    static final int MAXIMUM_FINITE_ENTRIES = 4096;
    public record Entry(long id, int turn, String phaseKey, String kind, Integer playerId,
                        String message, String cardId, String cardName, String detail, String outcome, int count,
                        PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield, PublicImpact impact, String stackId,
                        String executionId, String executionStep) {
        public Entry(long id, int turn, String phaseKey, String kind, Integer playerId, String message,
                     String cardId, String cardName, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield, PublicImpact impact, String stackId) {
            this(id, turn, phaseKey, kind, playerId, message, cardId, cardName, detail, outcome, count,
                    change, cause, interruptsQuietYield, impact, stackId, null, null);
        }
        public Entry(long id, int turn, String phaseKey, String kind, Integer playerId, String message,
                     String cardId, String cardName, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield, PublicImpact impact) {
            this(id, turn, phaseKey, kind, playerId, message, cardId, cardName, detail, outcome, count, change, cause, interruptsQuietYield, impact, null);
        }
        public Entry(long id, int turn, String phaseKey, String kind, Integer playerId, String message,
                     String cardId, String cardName, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield) {
            this(id, turn, phaseKey, kind, playerId, message, cardId, cardName, detail, outcome, count, change, cause, interruptsQuietYield, null);
        }
        public Entry(long id, int turn, String phaseKey, String kind, Integer playerId, String message,
                     String cardId, String cardName, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause) {
            this(id, turn, phaseKey, kind, playerId, message, cardId, cardName, detail, outcome, count, change, cause, true);
        }
        public Entry(long id, int turn, String phaseKey, String kind, Integer playerId, String message,
                     String cardId, String cardName, String detail, String outcome, int count) {
            this(id, turn, phaseKey, kind, playerId, message, cardId, cardName, detail, outcome, count, null, null);
        }
    }
    public record PublicCardChange(String kind, String property, int before, int after) { }
    public record PublicEffectCause(String cardId, String name, String ability, Integer controllerId, String kind, String id) { }
    /** Actual event-time results, distinct from planned damage and from one another.
     * Card references are public opaque occurrences, never retained faces or actions. */
    public record PublicImpact(String kind, long amount, String cardId, String cardName, Integer playerId,
                               String sourceCardId, String sourceName, Integer before, Integer after,
                               String damageType, Boolean combat, Boolean infect,
                               Integer sourceControllerId, Integer recipientControllerId, String fromZone, String toZone) { }
    public record Frame(long revision, int turn, String phaseKey, String phase, Integer activePlayerId, List<Entry> entries) { }
    private final PlayerView viewer;
    private final MatchIdentities identities;
    private final java.util.function.Predicate<CardView> currentView;
    private final java.util.function.Function<CardView, CardView> currentOccurrence;
    private boolean ownsIdentities;
    private final Deque<Entry> entries = new ArrayDeque<>();
    private final Map<Long, Entry> finiteEntries = new LinkedHashMap<>();
    private String finiteGrantId = "";
    private Set<String> finiteRoots = Set.of();
    private boolean finiteOverflow;
    private java.util.function.BiPredicate<String, Set<String>> finiteDescendant = (id, roots) -> false;
    // CardView equality is physical ID only. A delayed trigger may hold a
    // different historical view with that same ID and no current zone.
    private record VisibleOccurrence(CardView view, String id) { }
    private final Map<CardView, VisibleOccurrence> visibleIds = new HashMap<>();
    private final Map<Integer, Map<String, PublicPlayerStates.State>> playerStates = new HashMap<>();
    private long sequence;
    private long quietSequence;
    private int turn;
    private String phase = "PREGAME";
    private String phaseName = "Pregame";
    private Integer activePlayerId;
    private long revision;
    private String latestCounterCause;
    private int entriesAfterLatestCounterCause;
    private int ownTurns, cardsDrawn, landsPlayed, spellsCast, mulligans, commanderCasts;
    private ExecutionTracker.Reference eventExecution;
    private java.util.function.BiFunction<ExecutionTracker.Reference, GameEvent, ExecutionTracker.Reference> executionFilter = (ignored, event) -> null;

    void executionFilter(java.util.function.BiFunction<ExecutionTracker.Reference, GameEvent, ExecutionTracker.Reference> filter) {
        executionFilter = filter;
    }
    void finiteAncestry(java.util.function.BiPredicate<String, Set<String>> ancestry) { finiteDescendant = ancestry; }
    synchronized void finiteGrant(String id, Collection<String> roots) {
        if (finiteGrantId.equals(id)) return;
        finiteGrantId = id; finiteRoots = Set.copyOf(roots); finiteEntries.clear(); finiteOverflow = false;
        for (var entry : entries) retainFinite(entry);
        revision++;
    }
    synchronized boolean finiteOverflow() { return finiteOverflow; }
    private void retainFinite(Entry entry) {
        if (entry.executionId() == null || !finiteDescendant.test(entry.executionId(), finiteRoots) || finiteEntries.containsKey(entry.id())) return;
        if (finiteEntries.size() >= MAXIMUM_FINITE_ENTRIES) finiteOverflow = true;
        else finiteEntries.put(entry.id(), entry);
    }

    MatchActivity(PlayerView viewer) { this(viewer, new MatchIdentities()); ownsIdentities = true; }
    MatchActivity(PlayerView viewer, MatchIdentities identities) { this(viewer, identities, Objects::nonNull); }
    MatchActivity(PlayerView viewer, MatchIdentities identities, java.util.function.Predicate<CardView> currentView) {
        this(viewer, identities, currentView, card -> currentView.test(card) ? card : null);
    }
    MatchActivity(PlayerView viewer, MatchIdentities identities, java.util.function.Predicate<CardView> currentView,
            java.util.function.Function<CardView, CardView> currentOccurrence) {
        this.viewer = Objects.requireNonNull(viewer); this.identities = identities;
        this.currentView = Objects.requireNonNull(currentView);
        this.currentOccurrence = Objects.requireNonNull(currentOccurrence);
    }

    public synchronized List<Entry> snapshot() {
        if (finiteEntries.isEmpty()) return List.copyOf(entries);
        var merged = new TreeMap<Long, Entry>(finiteEntries);
        for (var entry : entries) merged.put(entry.id(), entry);
        return List.copyOf(merged.values());
    }

    /** The rules stream may advance after an activity copy releases this lock.
     * Retire references against the exact immutable frame being published, not
     * another live admission lookup. Keep the historical result unchanged. */
    static List<Entry> retainedExecutions(List<Entry> copied, Set<String> retained) {
        return copied.stream().map(entry -> entry.executionId() == null || retained.contains(entry.executionId())
                ? entry : withoutExecution(entry)).toList();
    }

    private static Entry withoutExecution(Entry entry) {
        return new Entry(entry.id(), entry.turn(), entry.phaseKey(), entry.kind(), entry.playerId(), entry.message(),
                entry.cardId(), entry.cardName(), entry.detail(), entry.outcome(), entry.count(), entry.change(),
                entry.cause(), entry.interruptsQuietYield(), entry.impact(), entry.stackId(), null, null);
    }
    public synchronized Frame frame() { return new Frame(revision, turn, phase, phaseName, activePlayerId, snapshot()); }

    record Publication(Frame activity, MatchExecutions.Frame executions) { }

    /** A terminal rules transition follows its synchronous committed events.
     * Retry if the immutable execution frame changed while copying activity;
     * otherwise completion can never overtake its draw/damage/movement facts. */
    static Publication capture(java.util.function.Supplier<Frame> activity,
                               java.util.function.Supplier<MatchExecutions.Frame> executions) {
        for (;;) {
            var before = executions.get();
            var copied = activity.get();
            var after = executions.get();
            if (before == after) {
                var retained = after.occurrences().stream().map(MatchExecutions.Occurrence::id)
                        .collect(java.util.stream.Collectors.toSet());
                var admitted = new Frame(copied.revision(), copied.turn(), copied.phaseKey(), copied.phase(),
                        copied.activePlayerId(), retainedExecutions(copied.entries(), retained));
                return new Publication(admitted, after);
            }
        }
    }

    /** Monotonic public events, including ones that resolved between snapshots. */
    synchronized long sequence() { return sequence; }

    /** Turn-based hidden draws stay in history without ending an explicit yield. */
    synchronized long quietSequence() { return quietSequence; }

    /** Exact event totals for this viewer, independent of the bounded feed. */
    public synchronized Map<String, Integer> observations() {
        return Map.of("ownTurns", ownTurns, "cardsDrawn", cardsDrawn, "landsPlayed", landsPlayed,
                "spellsCast", spellsCast, "mulligans", mulligans, "commanderCasts", commanderCasts);
    }

    synchronized String visualId(CardView card) {
        // A public historical portrait remains readable, but is neither the
        // current occurrence nor evidence that the current one was concealed.
        if (!currentView.test(card)) return null;
        if (!visible(card)) {
            var prior = card == null ? null : visibleIds.get(card);
            if (prior != null && prior.view() == card) {
                visibleIds.remove(card);
                identities.forget(card, prior.id());
            }
            return null;
        }
        String id = identities.id(card);
        visibleIds.put(card, new VisibleOccurrence(card, id));
        return id;
    }

    private String eventVisualId(CardView card) {
        String current = visualId(card);
        if (current != null || !visible(card)) return current;
        // A real zone-change event carries the departing old CardView. History
        // can copy its already observed anchor, but cannot create or install a
        // live occurrence from that historical view.
        var known = visibleIds.get(card);
        return known != null && known.view() == card ? known.id() : null;
    }

    synchronized void refreshVisibility() {
        visibleIds.entrySet().removeIf(entry -> {
            var known = entry.getValue();
            var current = currentOccurrence.apply(known.view());
            if (current != null && visible(current)) {
                // A public zone move replaces the engine view, not the known
                // physical card. Only the authoritative replacement can keep
                // that display anchor; a historical portrait cannot do so.
                entry.setValue(new VisibleOccurrence(current, known.id()));
                return false;
            }
            // A different audience may already have observed a newer private
            // occurrence. Revoking our old view cannot erase that generation.
            identities.forget(known.view(), known.id());
            return true;
        });
    }

    private boolean visible(CardView card) {
        return card != null && card.getZone() != null
                && (card.getZone() != ZoneType.Hand || viewer.equals(card.getController()))
                && !card.isFaceDown() && card.canBeShownTo(viewer);
    }

    private String cardName(CardView card) { return visible(card) ? card.getCurrentState().getName() : "a face-down or hidden card"; }
    private boolean publicFaceDown(CardView card) {
        return card != null && card.isFaceDown() && card.getZone() == ZoneType.Battlefield && card.canBeShownTo(viewer);
    }
    private PublicEffectCause publicCause(GameEventCause cause) {
        if (cause == null || !visible(cause.card())) return null;
        return new PublicEffectCause(eventVisualId(cause.card()), cause.card().getCurrentState().getName(), cause.ability(),
                cause.controller() == null ? null : cause.controller().getId(), cause.kind(), cause.instanceId());
    }
    private boolean publicOccurrence(CardView card) {
        return card != null && card.getZone() != null && switch (card.getZone()) {
            case Battlefield, Stack, Graveyard, Exile, Command -> true;
            default -> false;
        };
    }
    private PublicImpact damageImpact(CardView card, PlayerView player, CardView source, int amount,
                                      String damageType, Boolean combat, Boolean infect,
                                      Integer sourceControllerId, Integer recipientControllerId) {
        boolean anonymous = publicFaceDown(card);
        if (card != null && !(anonymous || publicOccurrence(card) && visible(card))) return null;
        boolean disclosedSource = publicOccurrence(source) && visible(source);
        return new PublicImpact("damage", amount,
                card == null ? null : anonymous ? identities.concealed(card) : eventVisualId(card),
                card == null ? null : anonymous ? "Face-down card" : card.getCurrentState().getName(),
                player == null ? null : player.getId(),
                disclosedSource ? eventVisualId(source) : null,
                disclosedSource ? source.getCurrentState().getName() : null,
                null, null, damageType, combat, infect,
                disclosedSource ? sourceControllerId : null, recipientControllerId, null, null);
    }

    private PublicImpact zoneImpact(GameEventCardChangeZone event, CardView disclosedCard, PublicEffectCause cause) {
        ZoneType from = event.from() == null ? null : event.from().zoneType();
        ZoneType to = event.to() == null ? null : event.to().zoneType();
        if (from != ZoneType.Battlefield && to != ZoneType.Battlefield || from == to || !impactZone(from) || !impactZone(to)) return null;
        boolean anonymous = publicFaceDown(disclosedCard);
        boolean disclosed = visible(disclosedCard);
        return new PublicImpact("zone", 1, anonymous ? identities.concealed(disclosedCard) : disclosed ? eventVisualId(disclosedCard) : null,
                anonymous ? "Face-down card" : disclosed ? cardName(disclosedCard) : null, null,
                cause == null ? null : cause.cardId(), cause == null ? null : cause.name(), null, null, null, null, null,
                null, event.recipientControllerId(), from.name(), to.name());
    }

    private static boolean impactZone(ZoneType zone) {
        return zone != null && switch (zone) {
            case Battlefield, Stack, Graveyard, Exile, Command, Hand, Library -> true;
            default -> false;
        };
    }

    /** Admission for a source-less rules occurrence follows an actual public
     * mutation, never an unproductive or private-only state-based check. */
    synchronized boolean publicCommitted(GameEvent event) {
        if (event instanceof GameEventCardChangeZone moved) {
            ZoneType from = moved.from() == null ? null : moved.from().zoneType();
            ZoneType to = moved.to() == null ? null : moved.to().zoneType();
            return turn > 0 && from != to && to != null && (from == ZoneType.Battlefield || to == ZoneType.Battlefield);
        }
        if (event instanceof GameEventPlayerDamaged damaged) return damaged.amount() > 0;
        if (event instanceof GameEventPlayerLivesChanged life) return turn > 0 && life.oldLives() != life.newLives();
        if (event instanceof GameEventCardDamaged damaged)
            return damaged.amount() > 0 && (publicFaceDown(damaged.card()) || publicOccurrence(damaged.card()) && visible(damaged.card()));
        if (event instanceof GameEventCardCounters counters)
            return counters.oldValue() != counters.newValue() && (visible(counters.card()) || publicFaceDown(counters.card()));
        return false;
    }
    private String playerName(PlayerView player) { return player == null ? "A player" : player.equals(viewer) ? "You" : player.getName(); }
    private String possessive(PlayerView player) { return player != null && player.equals(viewer) ? "Your" : playerName(player) + "'s"; }

    /** Observe safe controller-boundary snapshots, not network polls or inferred rules causes. */
    synchronized void observePlayerStates(PlayerView player, List<PublicPlayerStates.State> states) {
        var current = new LinkedHashMap<String, PublicPlayerStates.State>();
        for (var state : states) current.put(state.id(), state);
        var previous = playerStates.put(player.getId(), Collections.unmodifiableMap(current));
        if (previous == null) return; // The initial board is a baseline, not a replay of its history.
        var ids = new LinkedHashSet<>(previous.keySet());
        ids.addAll(current.keySet());
        for (String id : ids) {
            var before = previous.get(id);
            var after = current.get(id);
            if (Objects.equals(before, after)) continue;
            // Metadata changes do not represent a new mechanic state.
            if (before != null && after != null && Objects.equals(before.value(), after.value())
                    && Objects.equals(before.maximum(), after.maximum()) && before.kind().equals(after.kind())) continue;
            var state = after == null ? before : after;
            String label = possessive(player) + " " + state.label();
            String message;
            if (state.kind().equals("status")) {
                message = label + (after == null ? " status ended." : " status became active.");
            } else {
                int oldValue = before == null || before.value() == null ? 0 : before.value();
                int newValue = after == null || after.value() == null ? 0 : after.value();
                if (after == null) message = label + " decreased from " + oldValue + " to 0.";
                else if (state.kind().equals("progress") && state.maximum() != null && state.maximum() > 0 && newValue >= state.maximum()) message = label + " reached the maximum: " + newValue + ".";
                else if (before == null && state.kind().equals("progress")) message = label + " started at " + newValue + ".";
                else message = label + (newValue >= oldValue ? " increased from " : " decreased from ") + oldValue + " to " + newValue + ".";
            }
            add("playerState", player, message, null, state.detail());
        }
    }

    private void add(String kind, PlayerView player, String message, CardView card) {
        add(kind, player, message, card, "");
    }
    private void add(String kind, PlayerView player, String message, CardView card, String detail) {
        add(kind, player, message, card, detail, "", 0);
    }
    private void add(String kind, PlayerView player, String message, CardView card, String detail, String outcome, int count) {
        add(kind, player, message, card, detail, outcome, count, null, null);
    }
    private void add(String kind, PlayerView player, String message, CardView card, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause) {
        add(kind, player, message, card, detail, outcome, count, change, cause, true);
    }
    private void add(String kind, PlayerView player, String message, CardView card, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield) {
        add(kind, player, message, card, detail, outcome, count, change, cause, interruptsQuietYield, null);
    }
    private void add(String kind, PlayerView player, String message, CardView card, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield, PublicImpact impact) {
        add(kind, player, message, card, detail, outcome, count, change, cause, interruptsQuietYield, impact, null);
    }

    private void add(String kind, PlayerView player, String message, CardView card, String detail, String outcome, int count,
                     PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield, PublicImpact impact, String stackId) {
        revision++;
        if (interruptsQuietYield) quietSequence++;
        boolean anonymousTarget = change != null && publicFaceDown(card);
        var execution = eventExecution;
        var added = new Entry(++sequence, turn, phase, kind, player == null ? null : player.getId(),
                message, anonymousTarget ? identities.concealed(card) : eventVisualId(card),
                anonymousTarget ? "Face-down card" : visible(card) ? card.getCurrentState().getName() : null,
                detail, outcome, count, change, cause, interruptsQuietYield, impact, stackId,
                execution == null ? null : execution.id(), execution == null ? null : execution.step());
        entries.addLast(added);
        retainFinite(added);
        if (cause != null && change != null && change.kind().equals("counters")) {
            latestCounterCause = cause.id();
            entriesAfterLatestCounterCause = 0;
        } else if (entriesAfterLatestCounterCause < 120) entriesAfterLatestCounterCause++;
        // Never publish an arbitrary tail as if it were a whole multi-target
        // effect. Keep the latest exact counter group through the following 119
        // entries, or until another counter effect replaces it; evict groups
        // atomically. This retains at most 120 entries plus that latest effect.
        while (entries.size() > 120) {
            var oldestCause = entries.peekFirst().cause();
            if (oldestCause == null) { entries.removeFirst(); continue; }
            String oldestId = oldestCause.id();
            if (oldestId.equals(latestCounterCause) && entriesAfterLatestCounterCause < 120) break;
            entries.removeIf(entry -> entry.cause() != null && entry.cause().id().equals(oldestId));
        }
    }

    @Subscribe public void committed(ExecutionTracker.Committed committed) {
        // Execution admission can inspect current disclosure. Keep its lock
        // outside the activity lock, matching ordinary transition admission.
        var admitted = executionFilter.apply(committed.execution(), committed.event());
        synchronized (this) {
            var prior = eventExecution;
            eventExecution = admitted;
            try { onGameEvent(committed.event()); }
            finally { eventExecution = prior; }
        }
    }

    public synchronized void onGameEvent(GameEvent event) {
        if (ownsIdentities) {
            if (event instanceof GameEventCardChangeZone moved) identities.moved(moved);
            if (event instanceof GameEventShuffle shuffled) identities.shuffled(shuffled);
        }
        if (event instanceof GameEventTurnBegan e) {
            if (viewer.equals(e.turnOwner())) ownTurns++;
            turn = e.turnNumber();
            phase = "UNTAP";
            phaseName = "Untap";
            activePlayerId = e.turnOwner().getId();
            // The turn scope itself revokes the old token. Its history marker
            // is not a player action which should pause a fresh continuation.
            add("turn", e.turnOwner(), possessive(e.turnOwner()) + " turn began.", null,
                    "", "", 0, null, null, false);
        } else if (event instanceof GameEventTurnPhase e) {
            phase = e.phase().name();
            phaseName = e.phase().nameForUi;
            activePlayerId = e.playerTurn().getId();
            revision++;
        } else if (event instanceof GameEventLandPlayed e) {
            if (viewer.equals(e.player())) landsPlayed++;
            add("land", e.player(), playerName(e.player()) + " played " + cardName(e.land()) + ".", e.land());
        } else if (event instanceof GameEventSpellAbilityCast e) {
            var player = e.si().getActivatingPlayer();
            var card = e.sa().getHostCard();
            if (viewer.equals(player) && e.sa().isSpell() && !e.copied()) {
                spellsCast++;
                if (card != null && card.isCommander()) commanderCasts++;
            }
            String action = e.sa().isSpell() ? " cast " : e.si().isTrigger() ? " triggered an ability of " : " activated an ability of ";
            add(e.sa().isSpell() ? "cast" : "ability", player, playerName(player) + action + cardName(card) + ".", card,
                    "", "", 0, null, null, true, null, String.valueOf(e.si().getId()));
        } else if (event instanceof GameEventSpellResolved e) {
            var card = e.spell().getHostCard();
            String detail = visible(card) ? Objects.requireNonNullElse(e.stackDescription(), "") : "";
            if (visible(card) && e.spell().isSpell()) detail += "\n" + card.getCurrentState().getOracleText();
            add("resolved", card == null ? null : card.getController(), cardName(card) + (e.spell().isSpell() ? "" : "'s ability")
                    + (e.hasFizzled() ? " did not resolve." : " resolved."), card,
                    detail, e.hasFizzled() ? "notResolved" : "resolved", 0, null, null, true, null,
                    e.stackInstanceId() == null ? null : String.valueOf(e.stackInstanceId()));
        } else if (event instanceof GameEventCardChangeZone e) {
            ZoneType from = e.from() == null ? null : e.from().zoneType();
            ZoneType to = e.to() == null ? null : e.to().zoneType();
            boolean hiddenDestination = to == ZoneType.Library || to == ZoneType.Hand && !viewer.equals(e.to().player());
            // Forget correlation handles whenever a card enters a hidden zone, even between polls.
            if (to == ZoneType.Library || to == ZoneType.Hand || e.card().isFaceDown()) {
                visibleIds.remove(e.card());
            }
            if (turn == 0 || from == to || to == null) return;
            var player = e.to().player();
            if (from == ZoneType.Library && to == ZoneType.Hand) {
                add("moved", player, playerName(player) + " moved a card from library to hand.", null,
                        "", "", 0, null, null,
                        e.origin() != GameEventCardChangeZone.Origin.TURN_BASED_DRAW || viewer.equals(player));
            } else if (to == ZoneType.Battlefield) {
                var cause = publicCause(e.cause());
                // Ordinary land plays have their own event.
                if (from != ZoneType.Hand || !e.card().getCurrentState().getType().isLand() || !visible(e.card()))
                    add("arrived", e.card().getController(), cardName(e.card()) + (visible(e.card()) && e.card().isToken() && !cardName(e.card()).toLowerCase(Locale.ROOT).endsWith("token") ? " token" : "")
                            + " entered the battlefield under " + (viewer.equals(e.card().getController()) ? "your" : playerName(e.card().getController()) + "'s") + " control.", e.card(),
                            "", "", 0, null, cause, true, zoneImpact(e, e.card(), cause));
            } else if (to == ZoneType.Graveyard || to == ZoneType.Exile || from == ZoneType.Battlefield) {
                // The view may still be frozen in its previous zone during this event.
                var publicCard = hiddenDestination ? null : e.card();
                var cause = publicCause(e.cause());
                add("moved", player, (visible(publicCard) ? cardName(publicCard) : "A card") + " moved "
                        + (from == null ? "" : "from " + from.name().toLowerCase(Locale.ROOT) + " ")
                        + "to " + to.name().toLowerCase(Locale.ROOT) + ".", publicCard,
                        "", "", 0, null, cause, true, zoneImpact(e, publicCard, cause));
            }
        } else if (event instanceof GameEventPlayerLivesChanged e && turn > 0 && e.oldLives() != e.newLives()) {
            long change = (long)e.newLives() - e.oldLives();
            add("life", e.player(), playerName(e.player()) + (change < 0 ? " lost " : " gained ") + Math.abs(change)
                    + " life (" + e.newLives() + " remaining).", null, "", "", 0, null, null, true,
                    new PublicImpact("life", change, null, null, e.player().getId(), null, null,
                            e.oldLives(), e.newLives(), null, null, null, null, e.player().getId(), null, null));
        } else if (event instanceof GameEventPlayerDamaged e) {
            var cause = publicCause(e.cause());
            add("damage", e.target(), cardName(e.source()) + " dealt " + e.amount() + (e.combat() ? " combat" : "")
                    + " damage to " + playerName(e.target()) + (e.infect() ? " as poison counters." : "."), e.source(),
                    "", "", 0, null, cause, true,
                    damageImpact(null, e.target(), e.source(), e.amount(), null, e.combat(), e.infect(),
                            e.sourceControllerId(), e.target().getId()));
        } else if (event instanceof GameEventCardDamaged e) {
            String damageType = switch (e.type()) {
                case Normal -> "normal";
                case M1M1Counters -> "m1m1Counters";
                case Deathtouch -> "deathtouch";
                case LoyaltyLoss -> "loyaltyLoss";
                case DefenseLoss -> "defenseLoss";
            };
            var cause = publicCause(e.cause());
            add("damage", e.card().getController(), cardName(e.source()) + " dealt " + e.amount() + " damage to " + cardName(e.card()) + ".", e.card(),
                    "", "", 0, null, cause, true,
                    damageImpact(e.card(), null, e.source(), e.amount(), damageType, e.combat(), null,
                            e.sourceControllerId(), e.recipientControllerId()));
        } else if (event instanceof GameEventAttackersDeclared e && !e.attackersMap().isEmpty()) {
            var names = e.attackersMap().values().stream().map(this::cardName).toList();
            add("combat", e.player(), playerName(e.player()) + " attacked with " + String.join(", ", names) + ".", null);
        } else if (event instanceof GameEventBlockersDeclared e) {
            var blocks = new ArrayList<String>();
            for (var map : e.blockers().values()) for (var pair : map.entries()) {
                if (!pair.getKey().equals(pair.getValue())) blocks.add(cardName(pair.getValue()) + " blocked " + cardName(pair.getKey()));
            }
            add("combat", e.defendingPlayer(), blocks.isEmpty() ? playerName(e.defendingPlayer()) + " declared no blockers." : String.join("; ", blocks) + ".", null);
        } else if (event instanceof GameEventCardCounters e && (visible(e.card()) || publicFaceDown(e.card())) && e.oldValue() != e.newValue()) {
            var cause = publicCause(e.cause());
            String target = publicFaceDown(e.card()) ? "Face-down card" : cardName(e.card());
            String message = target + ": " + e.type().getName() + " counters " + e.oldValue() + " → " + e.newValue()
                    + (cause == null ? "." : " from " + cause.name() + ".");
            add("counters", e.card().getController(), message, e.card(), "", "", 0,
                    new PublicCardChange("counters", e.type().getName(), e.oldValue(), e.newValue()), cause);
        } else if (event instanceof GameEventShuffle e) {
            // A shuffle breaks identity correlation even when the new top is visible.
            visibleIds.values().removeIf(occurrence -> occurrence.view().getZone() == ZoneType.Library
                    && e.player().equals(occurrence.view().getController()));
            if (turn > 0) add("shuffle", e.player(), playerName(e.player()) + " shuffled their library.", null, "", "", 1);
        } else if (event instanceof GameEventMulligan e) {
            if (viewer.equals(e.player())) mulligans++;
            add("mulligan", e.player(), playerName(e.player()) + " took a mulligan.", null);
        } else if (event instanceof GameEventCardDrawn e) {
            if (viewer.equals(e.player())) cardsDrawn++;
            add("draw", e.player(), playerName(e.player()) + " drew a card.", null, "", "", 1, null, null,
                    e.origin() != GameEventCardChangeZone.Origin.TURN_BASED_DRAW || viewer.equals(e.player()));
        }
    }
}
