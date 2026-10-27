package forge.api;

import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;

import java.util.Map;
import java.util.function.Function;

/** Closed read-only mana facts for an already authorized source action. */
final class ManaCostPreviewProjection {
    static Map<String, Object> describe(SpellAbility ability, Player viewer, Function<CardView, String> identity) {
        if (!ActionSemantics.hasManaCost(ability)) return null;
        return Map.of("costs", ability.getPayCosts().getCostParts().stream().map(ActionSemantics::cost).toList(),
                "manaCostKnowledge", "Unknown", "funding", NumericDecisionProjection.funding(ability, viewer, identity));
    }

    private ManaCostPreviewProjection() { }
}
