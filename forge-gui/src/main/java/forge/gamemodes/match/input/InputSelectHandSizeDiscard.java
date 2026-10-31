package forge.gamemodes.match.input;

import forge.game.card.Card;
import forge.game.card.CardCollection;
import forge.game.player.Player;
import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;

import java.util.Collections;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Set;

/** The rules-owned, exact whole-hand choice made by maximum-hand-size cleanup. */
public final class InputSelectHandSizeDiscard extends InputSelectCardsFromList {
    private static final long serialVersionUID = 1L;

    /** Rules objects stay inside the engine; frontends receive copied faces and indices. */
    public record Offer(int playerId, int handSize, int maximumHandSize, int discardCount, List<Card> cards) {
        public Offer { cards = List.copyOf(cards); }
    }

    private final Player player;
    private final Offer offer;

    public InputSelectHandSizeDiscard(PlayerControllerHuman controller, int discardCount) {
        super(controller, discardCount, discardCount, new CardCollection(controller.getPlayer().getCardsIn(ZoneType.Hand)));
        player = controller.getPlayer();
        offer = new Offer(player.getId(), getValidChoices().size(), player.getMaxHandSize(), discardCount,
                List.copyOf(getValidChoices()));
        setCancelAllowed(false);
    }

    @Override
    protected boolean allowAwaitNextInput() { return true; }

    /** A partly selected legacy input does not grant a new complete-set offer. */
    public Offer getHandReductionOffer() { return isOfferCurrent(offer) ? offer : null; }

    public boolean isOfferCurrent(Offer observed) {
        if (observed != offer || !isSelectionActive() || !getSelected().isEmpty()
                || player != getController().getPlayer() || player.hasLost() || player.isUnlimitedHandSize()
                || player.getMaxHandSize() != offer.maximumHandSize()
                || offer.discardCount() <= 0 || offer.handSize() - offer.maximumHandSize() != offer.discardCount()
                || getController().getInputQueue().getInput() != this || getController().getInputProxy().getInput() != this)
            return false;
        var hand = player.getCardsIn(ZoneType.Hand);
        if (hand.size() != offer.handSize()) return false;
        Set<Card> current = Collections.newSetFromMap(new IdentityHashMap<>());
        for (Card card : hand) current.add(card);
        return current.size() == offer.cards().size() && offer.cards().stream()
                .allMatch(card -> current.contains(card) && card.getZone() == player.getZone(ZoneType.Hand));
    }

    /** Validate the entire batch before changing highlights, selection or the input latch. */
    public boolean acceptsSelection(Offer observed, List<Card> cards) {
        if (!isOfferCurrent(observed) || cards == null || cards.size() != offer.discardCount()) return false;
        Set<Card> unique = Collections.newSetFromMap(new IdentityHashMap<>());
        for (Card card : cards) if (card == null || !unique.add(card)
                || offer.cards().stream().noneMatch(candidate -> candidate == card)) return false;
        return true;
    }

    /** One owned input transition; this never replays individual card clicks. */
    public boolean submitSelection(Offer observed, List<Card> cards) {
        if (cards == null) return false;
        var copied = new ArrayList<>(cards);
        if (!acceptsSelection(observed, copied)) return false;
        selected.addAll(copied);
        selectButtonOK();
        return true;
    }
}
