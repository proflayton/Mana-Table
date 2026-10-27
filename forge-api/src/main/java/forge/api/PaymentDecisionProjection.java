package forge.api;

import forge.game.card.CardView;
import forge.gamemodes.match.input.InputPayMana;

import java.util.Map;
import java.util.function.Function;

/** The owner of this current payment, separate from an earlier cast or numeric decision. */
final class PaymentDecisionProjection {
    static Map<String, Object> describe(InputPayMana input, Function<CardView, String> identity) {
        if (!input.isPaymentActive()) return null;
        var ability = input.getPaidForAbility();
        if (ability == null || ability.getHostCard() == null) return null;
        String sourceId = identity.apply(ability.getHostCard().getView());
        if (sourceId == null || sourceId.isEmpty()) return null;
        String purpose = ability.isTrigger() ? "TriggeredAbilityCost"
                : input.isEffectPayment() ? "EffectCost"
                : ability.isSpell() ? "SpellCost"
                : ability.isActivatedAbility() ? "ActivatedAbilityCost" : "Unknown";
        return Map.of("purpose", purpose, "payerId", input.getPayer().getId(), "sourceCardId", sourceId,
                "mandatory", input.isPaymentMandatory());
    }

    private PaymentDecisionProjection() { }
}
