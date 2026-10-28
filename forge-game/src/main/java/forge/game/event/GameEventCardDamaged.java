package forge.game.event;

import forge.game.card.CardView;

public record GameEventCardDamaged(CardView card, CardView source, int amount, DamageType type,
                                   Integer sourceControllerId, Integer recipientControllerId,
                                   GameEventCause cause, Boolean combat) implements GameEvent {
    /** Legacy callers have no event-time attribution; do not infer it later from mutable views. */
    public GameEventCardDamaged(CardView card, CardView source, int amount, DamageType type) {
        this(card, source, amount, type, null, null, null, null);
    }
    public GameEventCardDamaged(CardView card, CardView source, int amount, DamageType type,
                               Integer sourceControllerId, Integer recipientControllerId) {
        this(card, source, amount, type, sourceControllerId, recipientControllerId, null, null);
    }

    public enum DamageType {
        Normal, 
        M1M1Counters, 
        Deathtouch, 
        LoyaltyLoss,
        DefenseLoss
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
        return "" + source + " dealt " + amount + " " + type + " damage to " + card;
    }
}
