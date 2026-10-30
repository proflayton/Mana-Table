package forge.api;

import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.gamemodes.match.input.InputPayMana;

import java.util.Map;
import java.util.function.Function;

/** The owner of this current payment, separate from an earlier cast or numeric decision. */
final class PaymentDecisionProjection {
    static Map<String, Object> describe(InputPayMana input, Function<CardView, String> identity) {
        if (!input.isPaymentActive()) return null;
        return describe(input.getPaidForAbility(), input.getPayer(), input.isPaymentMandatory(), input.isEffectPayment(), identity);
    }

    static Map<String, Object> describe(SpellAbility ability, Player payer, boolean mandatory, boolean effect,
                                        Function<CardView, String> identity) {
        if (ability == null || ability.getHostCard() == null || payer == null) return null;
        String sourceId = identity.apply(ability.getHostCard().getView());
        if (sourceId == null || sourceId.isEmpty()) return null;
        String purpose = ability.isTrigger() ? "TriggeredAbilityCost"
                : effect ? "EffectCost"
                : ability.isSpell() ? "SpellCost"
                : ability.isActivatedAbility() ? "ActivatedAbilityCost" : "Unknown";
        return Map.of("purpose", purpose, "payerId", payer.getId(), "sourceCardId", sourceId, "mandatory", mandatory);
    }

    private PaymentDecisionProjection() { }
}
