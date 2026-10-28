package forge.game.event;

import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.zone.Zone;
import forge.game.zone.ZoneView;
import forge.util.TextUtil;

public record GameEventCardChangeZone(CardView card, ZoneView from, ZoneView to, Origin origin,
                                     GameEventCause cause, Integer recipientControllerId) implements GameEvent {

    /** Rules provenance for this exact movement, never inherited by nested effects. */
    public enum Origin { OTHER, TURN_BASED_DRAW }

    public GameEventCardChangeZone {
        if (origin == null) origin = Origin.OTHER;
    }

    public GameEventCardChangeZone(CardView card, ZoneView from, ZoneView to, Origin origin) {
        this(card, from, to, origin, null, null);
    }

    public GameEventCardChangeZone(CardView card, ZoneView from, ZoneView to) {
        this(card, from, to, Origin.OTHER);
    }

    public GameEventCardChangeZone(Card card, Zone zoneFrom, Zone zoneTo) {
        this(card, zoneFrom, zoneTo, Origin.OTHER);
    }

    public GameEventCardChangeZone(Card card, Zone zoneFrom, Zone zoneTo, Origin origin) {
        this(CardView.get(card),
             zoneFrom == null ? null : zoneFrom.getView(),
             zoneTo == null ? null : zoneTo.getView(), origin);
    }

    public GameEventCardChangeZone(Card card, Zone zoneFrom, Zone zoneTo, Origin origin,
                                   GameEventCause cause, Integer recipientControllerId) {
        this(CardView.get(card), zoneFrom == null ? null : zoneFrom.getView(),
                zoneTo == null ? null : zoneTo.getView(), origin, cause, recipientControllerId);
    }

    @Override
    public <T> T visit(IGameEventVisitor<T> visitor) {
        return visitor.visit(this);
    }

    /* (non-Javadoc)
     * @see java.lang.Object#toString()
     */
    @Override
    public String toString() {
        final String fromStr = from != null ? "" + from.zoneType() : "null";
        final String toStr = to != null ? "" + to.zoneType() : "null";
        return TextUtil.concatWithSpace("" + card, ":", TextUtil.enclosedBracket(fromStr), "->", TextUtil.enclosedBracket(toStr));
    }
}
