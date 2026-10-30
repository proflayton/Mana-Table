package forge.api;

import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.gamemodes.match.input.InputPayMana;

import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
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
            promptFacts = copyFacts(promptFacts);
            automatedFacts = copyFacts(automatedFacts);
            poolManaColors = List.copyOf(poolManaColors);
        }

        boolean matches(String decision, long board, long version) {
            return Objects.equals(decisionId, decision) && boardRevision == board && sourceVersion == version;
        }
    }

    private static Map<String, Object> copyFacts(Map<String, Object> facts) {
        var copy = new LinkedHashMap<String, Object>();
        facts.forEach((key, value) -> copy.put(key, copyValue(value, 0)));
        return Collections.unmodifiableMap(copy);
    }

    /** Only the structured values emitted by this payment projection belong in
     * its retained wire tree. Unknown objects must not retain a live rules owner. */
    private static Object copyValue(Object value, int depth) {
        if (depth > 32) throw new IllegalArgumentException("Current payment facts exceed their value-tree bound");
        if (value == null || value instanceof String || value instanceof Boolean || value instanceof Integer
                || value instanceof Long || value instanceof Float || value instanceof Double || value instanceof PublicStates.State)
            return value;
        if (value instanceof Map<?, ?> values) {
            var copy = new LinkedHashMap<String, Object>();
            for (var entry : values.entrySet()) {
                if (!(entry.getKey() instanceof String key)) throw new IllegalArgumentException("Current payment facts require string keys");
                copy.put(key, copyValue(entry.getValue(), depth + 1));
            }
            return Collections.unmodifiableMap(copy);
        }
        if (value instanceof List<?> values) {
            var copy = new ArrayList<Object>(values.size());
            for (var item : values) copy.add(copyValue(item, depth + 1));
            return Collections.unmodifiableList(copy);
        }
        throw new IllegalArgumentException("Unsupported current payment fact value");
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
