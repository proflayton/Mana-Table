package forge.game.event;

import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.card.CounterType;
import forge.game.spellability.SpellAbility;

public record GameEventCardCounters(CardView card, CounterType type, int oldValue, int newValue, GameEventCause cause) implements GameEvent {
    public GameEventCardCounters(CardView card, CounterType type, int oldValue, int newValue) {
        this(card, type, oldValue, newValue, null);
    }
    public GameEventCardCounters(Card card, CounterType type, int oldValue, int newValue) {
        this(CardView.get(card), type, oldValue, newValue);
    }
    public GameEventCardCounters(Card card, CounterType type, int oldValue, int newValue, SpellAbility cause) {
        this(CardView.get(card), type, oldValue, newValue, GameEventCause.from(cause));
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
        return "" + card + " " + type + " counters: " + oldValue + " -> " + newValue;
    }
}
