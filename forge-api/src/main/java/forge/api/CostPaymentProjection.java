package forge.api;

import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.zone.CostPaymentStack;

import java.util.Map;
import java.util.function.Function;

/** Current nonmana cost's parent bill and read-only resources, never action offers. */
final class CostPaymentProjection {
    static Map<String, Object> describe(CostPaymentStack.Entry current, Player viewer,
                                        Function<CardView, String> identity) {
        var payment = current.payment();
        var ability = payment.getAbility();
        if (viewer == null || identity == null || payment.getPayer() == null
                || ability == null || ability.getHostCard() == null || ability.getHostCard().isFaceDown()) return null;
        String sourceId = identity.apply(ability.getHostCard().getView());
        if (sourceId == null || sourceId.isEmpty()) return null;
        var remaining = payment.getUnpaidCostParts();
        int currentIndex = -1;
        for (int index = 0; index < remaining.size(); index++)
            if (remaining.get(index) == current.cost()) { currentIndex = index; break; }
        if (currentIndex < 0) return null;
        var funding = payment.getPayer() == viewer ? NumericDecisionProjection.funding(ability, viewer, identity)
                : Map.of("knowledge", "Unknown", "pool", Map.of(), "sources", java.util.List.of());
        return Map.of("sourceCardId", sourceId, "payerId", payment.getPayer().getId(),
                "remainingCosts", remaining.stream().map(ActionSemantics::cost).toList(), "currentCostIndex", currentIndex,
                "manaCostKnowledge", "Unknown", "funding", funding);
    }

    private CostPaymentProjection() { }
}
