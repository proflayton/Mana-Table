package forge.api;

import forge.game.ability.AbilityFactory;
import forge.game.ability.AbilityUtils;
import forge.game.ability.ApiType;
import forge.game.spellability.SpellAbility;

import java.util.Map;

/** An uninstalled future recipe from one actual route's SVar scope. Parsing
 * scalar parameters allocates no ability/view, registers no trigger and never
 * evaluates the future rules condition or recipient. */
record ScheduledOperationRecipe(SpellAbility context, String name, boolean trigger,
                                String executeOverride, boolean actorKnown,
                                Map<String, String> parameters) {
    private static final int MAX_RECIPE_TEXT = 32768;

    static ScheduledOperationRecipe read(SpellAbility context, String name, boolean trigger,
                                         String executeOverride, boolean actorKnown, boolean textChanges) {
        Map<String, String> parameters = Map.of();
        try {
            String script = name == null || name.isBlank() ? ""
                    : textChanges ? AbilityUtils.getSVar(context, name) : context.getSVar(name);
            if (script != null && !script.isBlank() && script.length() <= MAX_RECIPE_TEXT)
                parameters = Map.copyOf(AbilityFactory.getMapParams(script));
        } catch (RuntimeException invalid) {
            // A malformed reference remains an explicit unknown scheduled node.
        }
        return new ScheduledOperationRecipe(context, name, trigger, executeOverride, actorKnown, parameters);
    }

    static ScheduledOperationRecipe copied(SpellAbility ability, boolean actorKnown) {
        return new ScheduledOperationRecipe(ability, "", false, "", actorKnown, Map.copyOf(ability.getMapParams()));
    }

    ApiType api() {
        if (trigger) return null;
        var type = AbilityFactory.AbilityRecordType.getRecordType(parameters);
        if (type == null) return null;
        try { return type.getApiTypeOf(parameters); }
        catch (RuntimeException unknown) { return null; }
    }

    boolean conditional() {
        return parameters.keySet().stream().anyMatch(key -> key.startsWith("Condition") || key.startsWith("Unless")
                || key.startsWith("CheckSVar") || key.startsWith("IsPresent")
                || java.util.Set.of("Optional", "OptionalDecider", "UpTo", "Upto", "InterveningIf").contains(key));
    }
}
