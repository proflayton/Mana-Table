package forge.api;

import forge.card.MagicColor;
import forge.card.mana.ManaAtom;
import forge.game.card.CardView;
import forge.game.cost.CostTap;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.staticability.StaticAbilityMode;
import forge.game.zone.ZoneType;

import java.util.*;
import java.util.function.Function;

/** Read-only announcement context. No route identifiers or executable source offers. */
final class NumericDecisionProjection {
    private static final int MAX_SOURCES = 128, MAX_ROUTES = 32;

    static Map<String, Object> describe(SpellAbility announced, String symbol, Player actor,
                                        Function<CardView, String> identity) {
        var source = announced.getHostCard();
        String sourceId = source == null ? "" : identity.apply(source.getView());
        if (sourceId == null || sourceId.isEmpty()) return null;
        var costs = announced.getPayCosts() == null ? List.of()
                : announced.getPayCosts().getCostParts().stream().map(ActionSemantics::cost).toList();
        return Map.of("purpose", "X".equals(symbol) ? "AnnounceX" : "Unknown", "sourceCardId", sourceId,
                "costs", costs, "manaCostKnowledge", "Unknown", "funding", funding(announced, actor, identity));
    }

    private static Map<String, Object> funding(SpellAbility announced, Player actor, Function<CardView, String> identity) {
        // A controlling player may answer another player's announcement. Their
        // own mana is not that invocation's funding; retain an unknown subset.
        if (announced.getActivatingPlayer() != actor)
            return Map.of("knowledge", "Unknown", "pool", Map.of(), "sources", List.of());
        var pool = new TreeMap<String, Integer>();
        boolean sourceRestrictions = announced.hasParam("ManaRestriction")
                || announced.getHostCard().getStaticAbilities().stream()
                    .anyMatch(ability -> ability.checkMode(StaticAbilityMode.ManaRestriction));
        for (var mana : actor.getManaPool()) {
            // Restrictions can depend on the still-unannounced value. Do not
            // certify those units using the old X or an unrelated payment.
            if (mana.isRestricted() || sourceRestrictions) continue;
            if (announced.allowsPayingWithShard(mana.getSourceCard(), mana.getColor()))
                pool.merge(MagicColor.toShortString(mana.getColor()), 1, Integer::sum);
        }
        var sources = new ArrayList<Map<String, Object>>();
        for (var source : actor.getCardsIn(ZoneType.Battlefield)) {
            if (source.isFaceDown() || source.isPhasedOut() || !source.getView().canBeShownTo(actor.getView())) {
                continue;
            }
            String sourceId = identity.apply(source.getView());
            if (sourceId == null || sourceId.isEmpty()) continue;
            var routes = new ArrayList<Map<String, Object>>();
            for (SpellAbility original : source.getManaAbilities()) {
                if (routes.size() == MAX_ROUTES) break;
                // LKI copies do not allocate live view identities or mutate the
                // live ability's activator while inspecting another seat.
                SpellAbility ability = original.copy(source, actor, true);
                if (!ability.canPlay(true)) continue;
                // Full-control settings may permit starting unpaid actions.
                // A funding witness still needs an actually available tap.
                if (ability.getPayCosts() != null && ability.getPayCosts().getCostParts().stream()
                        .filter(CostTap.class::isInstance).anyMatch(cost -> !cost.canPay(ability, actor, false))) continue;
                boolean stable = !sourceRestrictions
                        && ManaProductionProjection.simpleTapSourceIsStable(ability);
                var production = ManaProductionProjection.describe(ability);
                if (stable && "Known".equals(production.get("knowledge"))) {
                    @SuppressWarnings("unchecked")
                    var alternatives = (List<Map<String, Object>>) production.get("alternatives");
                    for (var alternative : alternatives) {
                        @SuppressWarnings("unchecked")
                        var colors = (Map<String, Integer>) alternative.get("colors");
                        for (var color : colors.entrySet())
                            if (color.getValue() > 0 && !announced.allowsPayingWithShard(source, ManaAtom.fromName(color.getKey())))
                                stable = false;
                    }
                }
                var costs = ability.getPayCosts() == null ? List.of()
                        : ability.getPayCosts().getCostParts().stream().map(ActionSemantics::cost).toList();
                routes.add(Map.of("costs", costs, "production", production,
                        "eligibility", stable ? "Known" : "Unknown"));
            }
            if (routes.isEmpty()) continue;
            if (sources.size() == MAX_SOURCES) break;
            sources.add(Map.of("sourceCardId", sourceId, "routes", List.copyOf(routes)));
        }
        // This useful subset omits hand/external-zone producers and alternative
        // activation costs. It can witness funding, never prove its absence.
        return Map.of("knowledge", "Unknown", "pool", Collections.unmodifiableMap(pool),
                "sources", List.copyOf(sources));
    }

    private NumericDecisionProjection() { }
}
