package forge.api;

import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.gamemodes.match.input.InputPayMana;

import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.function.Function;

/** The owner of this current payment, separate from an earlier cast or numeric decision. */
final class PaymentDecisionProjection {
    /** One audience's copied payment facts at an input-owner publication. The
     * scope is a board cohort, not the metadata-only transport revision. */
    record Capture(String decisionId, long boardRevision, long sourceVersion,
                   String executionId, String executionStep, Map<String, Object> promptFacts,
                   Map<String, Object> automatedFacts, List<String> poolManaColors) {
        Capture {
            promptFacts = PublishedDecisionFacts.freeze(promptFacts);
            automatedFacts = PublishedDecisionFacts.freeze(automatedFacts);
            poolManaColors = List.copyOf(poolManaColors);
        }

        boolean matches(String decision, long board, long version) {
            return Objects.equals(decisionId, decision) && boardRevision == board && sourceVersion == version;
        }
    }

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
