package forge.game.event;

import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.PlayerView;

public record GameEventCardTapped(CardView card, boolean tapped, PlayerView controller) implements GameEvent {

    public GameEventCardTapped(CardView card, boolean tapped) {
        this(card, tapped, card == null ? null : card.getController());
    }

    public GameEventCardTapped(Card card, boolean tapped) {
        this(CardView.get(card), tapped, card == null || card.getController() == null ? null : card.getController().getView());
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
        return "" + controller + (tapped ? " tapped " : " untapped ") + card;
    }
}
