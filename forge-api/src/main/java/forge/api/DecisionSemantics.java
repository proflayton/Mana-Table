package forge.api;

import forge.game.Game;
import forge.game.card.CardView;
import forge.game.cost.*;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.SpellAbilityView;
import forge.game.spellability.StackItemView;
import forge.game.zone.CostPaymentStack;
import forge.gamemodes.match.input.*;

import java.util.*;
import java.util.function.Function;

/** Exact operation context for policy decisions, never a classifier of prompt wording. */
final class DecisionSemantics {
    static Map<String, Object> describe(Input input, Game game) {
        return describe(input, game, null, null);
    }

    static Map<String, Object> describe(Input input, Game game, Player viewer, Function<CardView, String> identity) {
        var paying = game.costPaymentStack.peek();
        SpellAbility ability = paying != null ? null : input instanceof InputSelectTargets targets ? targets.getSpellAbility()
                : input instanceof InputSelectManyBase<?> many ? many.getSpellAbility()
                : input instanceof InputConfirm confirm ? confirm.getSpellAbility() : null;
        return describe(input, paying, viewer, identity, ability);
    }

    static Map<String, Object> describeTargets(SpellAbility ability, Game game, Player viewer, Function<CardView, String> identity) {
        return describe(null, game.costPaymentStack.peek(), viewer, identity, ability);
    }

    static Map<String, Object> describeSelection(InputSelectManyBase<?> input, SpellAbility ability, CostPaymentStack.Entry paying,
                                                Player viewer, Function<CardView, String> identity) {
        return describe(input, paying, viewer, identity, ability);
    }

    static Map<String, Object> describeChoice(CostPaymentStack.Entry paying) {
        return describe(null, paying, null, null, null);
    }

    private static Map<String, Object> describe(Input input, CostPaymentStack.Entry paying, Player viewer,
                                               Function<CardView, String> identity, SpellAbility ability) {
        if (input instanceof InputSelectCardsForConvokeOrImprovise)
            return Map.of("intent", "Tap", "selectionIsCost", true);
        if (paying != null) {
            var result = new LinkedHashMap<String, Object>();
            result.put("intent", costIntent(paying.cost()));
            result.put("selectionIsCost", true);
            Integer amount = integer(paying.cost().getAmount());
            result.put("amount", amount);
            result.put("counterType", paying.cost() instanceof CostRemoveCounter remove && remove.counter != null ? remove.counter.toString()
                    : paying.cost() instanceof CostPutCounter put && put.getCounter() != null ? put.getCounter().toString() : "");
            if (input instanceof InputSelectManyBase<?>) {
                var funding = CostPaymentProjection.describe(paying, viewer, identity);
                if (funding != null) result.put("costPayment", funding);
            }
            return result;
        }
        return describeAbility(ability, viewer, identity);
    }

    static Map<String, Object> describeAbility(SpellAbility ability) {
        return describeAbility(ability, null, null);
    }

    static Map<String, Object> describeAbility(SpellAbility ability, Player viewer, Function<CardView, String> identity) {
        var result = new LinkedHashMap<String, Object>();
        if (ability == null) {
            result.put("intent", "Unknown");
            return result;
        }
        result.putAll(ActionOperationProjection.describeDecision(ability, viewer, identity));
        result.put("intent", switch (ActionSemantics.effect(ability)) {
            case "Permanent", "Land", "Mana", "Draw", "LifeGain", "Token" -> "Benefit";
            case "Damage" -> "Damage";
            case "Destroy" -> "Destroy";
            case "Exile" -> "Exile";
            case "Bounce" -> "Bounce";
            case "CounterSpell" -> "Counter";
            case "AddCounters" -> "AddCounters";
            case "RemoveCounters" -> "RemoveCounters";
            case "LifeLoss", "Mill" -> "Harm";
            case "Tap" -> "Tap";
            case "Untap" -> "Untap";
            case "Search" -> "Search";
            case "Discard" -> "Discard";
            default -> "Unknown";
        });
        result.put("amount", ActionSemantics.effectAmount(ability));
        result.put("counterType", ability.getParamOrDefault("CounterType", ""));
        result.put("selectionIsCost", false);
        return result;
    }

    static List<Map<String, Object>> options(List<?> choices, Function<SpellAbilityView, SpellAbility> resolve) {
        return options(choices, resolve, null, null);
    }

    /** Cost ordering retains the provider's actual default part order. This is
     * context for this full ordering callback, never a classification of labels. */
    static Map<String, Object> ordering(List<?> choices, boolean ordered, int min, int max) {
        if (!ordered || choices.isEmpty() || min != choices.size() || max != choices.size()
                || !choices.stream().allMatch(CostPart.class::isInstance)) return Map.of();
        return Map.of("orderingPurpose", "CostPayment", "selectionIsCost", true,
                "defaultOrder", java.util.stream.IntStream.range(0, choices.size()).boxed().toList());
    }

    static List<Map<String, Object>> options(List<?> choices, Function<SpellAbilityView, SpellAbility> resolve,
                                           Player viewer, Function<CardView, String> identity) {
        var options = new ArrayList<Map<String, Object>>();
        for (int index = 0; index < choices.size(); index++) {
            Object choice = choices.get(index);
            if (choice instanceof StackItemView stack) {
                var target = ActionOperationProjection.stackTarget(stack, viewer, identity);
                if (target != null) options.add(Map.of("index", index, "effect", "Unknown", "target", target,
                        "operationGraph", ActionOperationProjection.unknown()));
                continue;
            }
            SpellAbility ability = choice instanceof SpellAbility actual ? actual
                    : choice instanceof SpellAbilityView view ? resolve.apply(view) : null;
            if (ability == null) continue;
            var option = new LinkedHashMap<String, Object>();
            option.put("index", index);
            option.put("effect", ActionSemantics.effect(ability));
            option.put("amount", ActionSemantics.effectAmount(ability));
            option.put("operationGraph", ActionOperationProjection.describe(ability, viewer, identity));
            options.add(option);
        }
        return List.copyOf(options);
    }

    private static String costIntent(CostPart cost) {
        if (cost instanceof CostDiscard) return "Discard";
        if (cost instanceof CostSacrifice) return "Sacrifice";
        if (cost instanceof CostExile || cost instanceof CostExileFromStack) return "Exile";
        if (cost instanceof CostRemoveCounter || cost instanceof CostRemoveAnyCounter) return "RemoveCounters";
        if (cost instanceof CostPutCounter || cost instanceof CostPutCounterYou) return "AddCounters";
        if (cost instanceof CostTap || cost instanceof CostTapType) return "Tap";
        if (cost instanceof CostUntap || cost instanceof CostUntapType) return "Untap";
        return "Unknown";
    }
    private static Integer integer(String text) {
        try { return Integer.valueOf(text); }
        catch (NumberFormatException ignored) { return null; }
    }
    private DecisionSemantics() { }
}
