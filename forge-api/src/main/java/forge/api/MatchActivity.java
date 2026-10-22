package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.game.card.CardView;
import forge.game.event.*;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;

import java.util.*;

/** Copies an allowlist of events at emission time; never exports raw engine logs or views. */
public final class MatchActivity {
    public record Entry(long id, int turn, String phaseKey, String kind, Integer playerId,
                        String message, String cardId, String cardName, String detail, String outcome, int count,
                        PublicCardChange change, PublicEffectCause cause, boolean interruptsQuietYield) {
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
    public record Frame(long revision, int turn, String phaseKey, String phase, Integer activePlayerId, List<Entry> entries) { }
    private final PlayerView viewer;
    private final MatchIdentities identities;
    private boolean ownsIdentities;
    private final Deque<Entry> entries = new ArrayDeque<>();
    private final Map<CardView, String> visibleIds = new HashMap<>();
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

    MatchActivity(PlayerView viewer) { this(viewer, new MatchIdentities()); ownsIdentities = true; }
    MatchActivity(PlayerView viewer, MatchIdentities identities) { this.viewer = Objects.requireNonNull(viewer); this.identities = identities; }

    public synchronized List<Entry> snapshot() { return List.copyOf(entries); }
    public synchronized Frame frame() { return new Frame(revision, turn, phase, phaseName, activePlayerId, snapshot()); }

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
        if (!visible(card)) { if (card != null && visibleIds.remove(card) != null) identities.forget(card); return null; }
        String id = identities.id(card);
        visibleIds.put(card, id);
        return id;
    }

    synchronized void refreshVisibility() { visibleIds.keySet().removeIf(card -> { if (visible(card)) return false; identities.forget(card); return true; }); }

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
        return new PublicEffectCause(visualId(cause.card()), cause.card().getCurrentState().getName(), cause.ability(),
                cause.controller() == null ? null : cause.controller().getId(), cause.kind(), identities.effect(cause.instanceId()));
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
        revision++;
        if (interruptsQuietYield) quietSequence++;
        boolean anonymousTarget = change != null && publicFaceDown(card);
        entries.addLast(new Entry(++sequence, turn, phase, kind, player == null ? null : player.getId(),
                message, anonymousTarget ? identities.concealed(card) : visualId(card),
                anonymousTarget ? "Face-down card" : visible(card) ? card.getCurrentState().getName() : null,
                detail, outcome, count, change, cause, interruptsQuietYield));
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

    @Subscribe
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
            add("turn", e.turnOwner(), possessive(e.turnOwner()) + " turn began.", null);
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
            add(e.sa().isSpell() ? "cast" : "ability", player, playerName(player) + action + cardName(card) + ".", card);
        } else if (event instanceof GameEventSpellResolved e) {
            var card = e.spell().getHostCard();
            String detail = visible(card) ? Objects.requireNonNullElse(e.stackDescription(), "") : "";
            if (visible(card) && e.spell().isSpell()) detail += "\n" + card.getCurrentState().getOracleText();
            add("resolved", card == null ? null : card.getController(), cardName(card) + (e.spell().isSpell() ? "" : "'s ability")
                    + (e.hasFizzled() ? " did not resolve." : " resolved."), card,
                    detail, e.hasFizzled() ? "notResolved" : "resolved", 0);
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
                // Ordinary land plays have their own event.
                if (from != ZoneType.Hand || !e.card().getCurrentState().getType().isLand() || !visible(e.card()))
                    add("arrived", e.card().getController(), cardName(e.card()) + (visible(e.card()) && e.card().isToken() && !cardName(e.card()).toLowerCase(Locale.ROOT).endsWith("token") ? " token" : "")
                            + " entered the battlefield under " + (viewer.equals(e.card().getController()) ? "your" : playerName(e.card().getController()) + "'s") + " control.", e.card());
            } else if (to == ZoneType.Graveyard || to == ZoneType.Exile || from == ZoneType.Battlefield) {
                // The view may still be frozen in its previous zone during this event.
                var publicCard = hiddenDestination ? null : e.card();
                add("moved", player, (visible(publicCard) ? cardName(publicCard) : "A card") + " moved "
                        + (from == null ? "" : "from " + from.name().toLowerCase(Locale.ROOT) + " ")
                        + "to " + to.name().toLowerCase(Locale.ROOT) + ".", publicCard);
            }
        } else if (event instanceof GameEventPlayerLivesChanged e && turn > 0 && e.oldLives() != e.newLives()) {
            int change = e.newLives() - e.oldLives();
            add("life", e.player(), playerName(e.player()) + (change < 0 ? " lost " : " gained ") + Math.abs(change)
                    + " life (" + e.newLives() + " remaining).", null);
        } else if (event instanceof GameEventPlayerDamaged e) {
            add("damage", e.target(), cardName(e.source()) + " dealt " + e.amount() + (e.combat() ? " combat" : "")
                    + " damage to " + playerName(e.target()) + (e.infect() ? " as poison counters." : "."), e.source());
        } else if (event instanceof GameEventCardDamaged e) {
            add("damage", e.card().getController(), cardName(e.source()) + " dealt " + e.amount() + " damage to " + cardName(e.card()) + ".", e.card());
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
            visibleIds.keySet().removeIf(card -> card.getZone() == ZoneType.Library && e.player().equals(card.getController()));
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
