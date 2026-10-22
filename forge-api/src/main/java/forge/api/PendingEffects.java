package forge.api;

import com.google.common.collect.Multiset;
import forge.game.ability.AbilityKey;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.card.CounterType;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.WrappedAbility;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.function.Function;

/** Allowlisted pending facts, copied from the owned ability rather than its
 * formatted description or the current total on a counter recipient. */
final class PendingEffects {
    private record Amount(Integer value, String basis) { }
    private static final Amount DYNAMIC = new Amount(null, "dynamic");
    private static final Set<String> SIMPLE_DAMAGE_PARAMETERS = Set.of("AB", "SP", "DB", "Cost",
            "ValidTgts", "TgtPrompt", "NumDmg", "TargetMin", "TargetMax", "SpellDescription", "StackDescription",
            "Planeswalker", "Ultimate", "DividedAsYouChoose", "DivideEvenly", "AILogic");

    static Map<String, Object> project(SpellAbility ability, PlayerView viewer, MatchIdentities identities,
                                       Function<CardView, Map<String, Object>> publicCard) {
        if (ability == null) return null;
        SpellAbility effect = unwrap(ability);
        if (effect.getApi() != ApiType.DealDamage || effect.getHostCard() == null) return null;
        CardView source = CardView.get(effect.getHostCard());
        if (source == null || !source.canBeShownTo(viewer) || source.isFaceDown()) return null;
        SpellAbility root = unwrap(effect.getRootAbility());
        if (!simpleDamage(effect, root)) return null;
        Amount amount = amount(effect, root, effect.getParam("NumDmg"), new HashSet<>());
        var result = new LinkedHashMap<String, Object>();
        result.put("id", identities.pendingEffect(root));
        result.put("kind", "damage");
        result.put("amount", amount.value());
        result.put("amountBasis", amount.basis());
        // This is the pre-replacement/pre-prevention scalar. A divided amount
        // is a total pool, never an assertion that each target takes that much.
        result.put("distribution", effect.isDividedAsYouChoose() || effect.hasParam("DivideEvenly")
                || effect.hasParam("DividerOnResolution") ? "divided"
                : effect.usesTargeting() && effect.getMaxTargets() == 1 ? "single" : "each");
        Map<String, Object> counters = counterChange(root, publicCard);
        if (counters != null) result.put("counterChange", counters);
        return result;
    }

    private static SpellAbility unwrap(SpellAbility ability) {
        while (ability instanceof WrappedAbility wrapped) ability = wrapped.getWrappedAbility();
        return ability;
    }

    /** The numeric face replaces the complete pending description. Opt in only
     * when it can describe the whole chosen effect, preserving legacy text for
     * follow-ups, conditions, alternate sources and unmodeled script behavior. */
    private static boolean simpleDamage(SpellAbility effect, SpellAbility root) {
        if (effect != root || effect.getSubAbility() != null || !effect.getAdditionalAbilities().isEmpty()
                || !effect.getAdditionalAbilityLists().isEmpty() || effect.isReplacementAbility()
                || !effect.usesTargeting() || !effect.getMapParams().keySet().stream().allMatch(SIMPLE_DAMAGE_PARAMETERS::contains))
            return false;
        if (effect.hasParam("DivideEvenly") && !effect.getParam("DivideEvenly").equals("RoundedDown")) return false;
        return root.getTrigger() == null || root.getTrigger().getMapParams().keySet().stream()
                .noneMatch(key -> key.startsWith("Condition") || key.startsWith("CheckSVar") || key.equals("InterveningIf"));
    }

    /** Resolve only constants and known engine bindings. Arbitrary script
     * evaluation can depend on private cards, unchosen targets or unpaid X. */
    private static Amount amount(SpellAbility effect, SpellAbility root, String expression, Set<String> seen) {
        if (expression == null || expression.isBlank() || seen.size() >= 8) return DYNAMIC;
        expression = expression.trim();
        if (!seen.add(expression)) return DYNAMIC;
        if (expression.matches("[+-]?\\d+")) {
            try { return new Amount(Integer.valueOf(expression), "base"); }
            catch (NumberFormatException overflow) { return DYNAMIC; }
        }
        if (expression.equals("TriggerCount$Amount")) {
            return root.getTriggeringObject(AbilityKey.Amount) instanceof Integer captured
                    ? new Amount(captured, "trigger") : DYNAMIC;
        }
        if (expression.equals("Count$xPaid")) {
            Integer paid = root.getXManaCostPaid();
            return paid == null ? DYNAMIC : new Amount(paid, "base");
        }
        if (!expression.matches("[A-Za-z][A-Za-z0-9_]*")) return DYNAMIC;
        String binding = effect.getSVar(expression);
        if (binding == null || binding.isBlank()) binding = root.getSVar(expression);
        if ((binding == null || binding.isBlank()) && effect.getHostCard() != null)
            binding = effect.getHostCard().getSVar(expression);
        return amount(effect, root, binding, seen);
    }

    private static Map<String, Object> counterChange(SpellAbility root,
                                                    Function<CardView, Map<String, Object>> publicCard) {
        if (!(root.getTriggeringObject(AbilityKey.CounterMap) instanceof Multiset<?> additions)) return null;
        var counters = new ArrayList<Map<String, Object>>();
        for (var entry : additions.entrySet()) {
            if (entry.getElement() instanceof CounterType type && entry.getCount() > 0)
                counters.add(Map.of("type", type.toString(), "label", type.getName(), "added", entry.getCount()));
        }
        if (counters.isEmpty()) return null;
        var result = new LinkedHashMap<String, Object>();
        Object recipient = root.getTriggeringObject(AbilityKey.Object);
        if (recipient instanceof Card card) {
            Map<String, Object> visible = publicCard.apply(CardView.get(card));
            if (visible == null) return null;
            result.put("card", visible);
        } else if (recipient instanceof Player player) result.put("playerId", player.getId());
        else return null;
        if (root.getTriggeringObject(AbilityKey.Source) instanceof Player actor) result.put("actorPlayerId", actor.getId());
        result.put("counters", counters);
        return result;
    }
}
