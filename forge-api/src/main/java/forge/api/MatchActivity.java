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
                        String message, String cardId, String cardName, String detail) { }
    public record Frame(long revision, int turn, String phaseKey, String phase, Integer activePlayerId, List<Entry> entries) { }
    private final PlayerView viewer;
    private final MatchIdentities identities;
    private boolean ownsIdentities;
    private final Deque<Entry> entries = new ArrayDeque<>();
    private final Map<CardView, String> visibleIds = new HashMap<>();
    private long sequence;
    private int turn;
    private String phase = "PREGAME";
    private String phaseName = "Pregame";
    private Integer activePlayerId;
    private long revision;

    MatchActivity(PlayerView viewer) { this(viewer, new MatchIdentities()); ownsIdentities = true; }
    MatchActivity(PlayerView viewer, MatchIdentities identities) { this.viewer = Objects.requireNonNull(viewer); this.identities = identities; }

    public synchronized List<Entry> snapshot() { return List.copyOf(entries); }
    public synchronized Frame frame() { return new Frame(revision, turn, phase, phaseName, activePlayerId, snapshot()); }

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
    private String playerName(PlayerView player) { return player == null ? "A player" : player.equals(viewer) ? "You" : player.getName(); }
    private String possessive(PlayerView player) { return player != null && player.equals(viewer) ? "Your" : playerName(player) + "'s"; }

    private void add(String kind, PlayerView player, String message, CardView card) {
        add(kind, player, message, card, "");
    }
    private void add(String kind, PlayerView player, String message, CardView card, String detail) {
        revision++;
        entries.addLast(new Entry(++sequence, turn, phase, kind, player == null ? null : player.getId(),
                message, visualId(card), visible(card) ? card.getCurrentState().getName() : null, detail));
        while (entries.size() > 120) entries.removeFirst();
    }

    @Subscribe
    public synchronized void onGameEvent(GameEvent event) {
        if (ownsIdentities) {
            if (event instanceof GameEventCardChangeZone moved) identities.moved(moved);
            if (event instanceof GameEventShuffle shuffled) identities.shuffled(shuffled);
        }
        if (event instanceof GameEventTurnBegan e) {
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
            add("land", e.player(), playerName(e.player()) + " played " + cardName(e.land()) + ".", e.land());
        } else if (event instanceof GameEventSpellAbilityCast e) {
            var player = e.si().getActivatingPlayer();
            var card = e.sa().getHostCard();
            String action = e.sa().isSpell() ? " cast " : e.si().isTrigger() ? " triggered an ability of " : " activated an ability of ";
            add(e.sa().isSpell() ? "cast" : "ability", player, playerName(player) + action + cardName(card) + ".", card);
        } else if (event instanceof GameEventSpellResolved e) {
            var card = e.spell().getHostCard();
            String detail = visible(card) ? Objects.requireNonNullElse(e.stackDescription(), "") : "";
            if (visible(card) && e.spell().isSpell()) detail += "\n" + card.getCurrentState().getOracleText();
            add("resolved", card == null ? null : card.getController(), cardName(card) + (e.spell().isSpell() ? "" : "'s ability")
                    + (e.hasFizzled() ? " did not resolve." : " resolved."), card,
                    detail);
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
                add("draw", player, playerName(player) + " moved a card from library to hand.", null);
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
        } else if (event instanceof GameEventCardCounters e && visible(e.card()) && e.oldValue() != e.newValue()) {
            add("counters", e.card().getController(), cardName(e.card()) + ": " + e.type().getName() + " counters " + e.oldValue() + " → " + e.newValue() + ".", e.card());
        } else if (event instanceof GameEventShuffle e) {
            // A shuffle breaks identity correlation even when the new top is visible.
            visibleIds.keySet().removeIf(card -> card.getZone() == ZoneType.Library && e.player().equals(card.getController()));
        } else if (event instanceof GameEventMulligan e) {
            add("mulligan", e.player(), playerName(e.player()) + " took a mulligan.", null);
        }
    }
}
