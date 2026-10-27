package forge.api;

import forge.game.ability.ApiType;
import forge.game.card.CounterEnumType;
import forge.game.cost.CostPartMana;
import forge.game.cost.CostTap;
import forge.game.replacement.ReplacementType;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.AbilityManaPart;
import forge.game.trigger.TriggerType;
import forge.game.zone.ZoneType;

import java.util.*;

/** Exact nominal bundles; unresolved choices/restrictions never become certain funding. */
final class ManaProductionProjection {
    private static final int MAX_ALTERNATIVES = 32;
    private static final Set<String> COLORS = Set.of("W", "U", "B", "R", "G", "C");

    /** One execution witness for an aggregate bundle, never an action capability. */
    private record Alternative(Map<String, Integer> colors, Map<AbilityManaPart, Map<String, Integer>> choices) { }

    static Map<String, Object> describe(SpellAbility ability) {
        var alternatives = alternatives(ability);
        return alternatives == null ? unknown() : Map.of("knowledge", "Known", "alternatives", alternatives.stream()
                .map(value -> Map.of("colors", value.colors())).toList());
    }

    /** Null is unsupported/mismatched; an empty map is a valid fixed-output witness. */
    static Map<AbilityManaPart, String> outputChoices(SpellAbility ability, Map<String, Integer> desired) {
        var alternatives = alternatives(ability);
        if (alternatives == null) return null;
        var selected = alternatives.stream().filter(value -> value.colors().equals(desired)).findFirst().orElse(null);
        if (selected == null) return null;
        var result = new IdentityHashMap<AbilityManaPart, String>();
        for (var part : selected.choices().entrySet()) {
            if (part.getValue().values().stream().mapToLong(Integer::longValue).sum() > 1024) return null;
            var expression = new ArrayList<String>();
            for (var color : part.getValue().entrySet()) expression.addAll(Collections.nCopies(color.getValue(), color.getKey()));
            result.put(part.getKey(), String.join(" ", expression));
        }
        return Collections.unmodifiableMap(result);
    }

    private static List<Alternative> alternatives(SpellAbility ability) {
        if (!ability.isManaAbility()) return null;
        var alternatives = new ArrayList<Alternative>();
        alternatives.add(new Alternative(Map.of(), Map.of()));
        boolean produced = false;
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (SpellAbility part = ability; part != null; part = part.getSubAbility()) {
            if (!seen.add(part) || !part.getAdditionalAbilities().isEmpty() || !part.getAdditionalAbilityLists().isEmpty()
                    || part.getMapParams().keySet().stream().anyMatch(key -> key.startsWith("Condition") || key.startsWith("Unless")
                        || Set.of("Optional", "Chooser").contains(key))) return null;
            if (part.getApi() == ApiType.Cleanup) continue;
            if (part.getApi() != ApiType.Mana || part.getManaPart() == null) return null;
            var mana = part.getManaPart();
            // Restricted spending is evaluated against the exact current bill by
            // InputPayMana. A color list alone cannot certify generic spending.
            if (!mana.getManaRestrictions().isEmpty() || !mana.getExtraManaRestriction().isEmpty()) return null;
            Integer amount = amount(part);
            Integer each = integer(part.getParamOrDefault("Each", "1"));
            if (amount == null || each == null || amount < 0 || each < 0) return null;
            String raw = mana.getOrigProduced();
            var choices = new ArrayList<Alternative>();
            if (raw.equals("Any")) {
                for (String color : List.of("W", "U", "B", "R", "G"))
                    choices.add(new Alternative(canonical(bundle(color, amount)), Map.of(mana, Map.of(color, 1))));
            } else if (raw.startsWith("Combo ") && !raw.contains("Different")) {
                String colors = raw.substring(6);
                boolean commanderIdentity = colors.equals("ColorIdentity");
                if (commanderIdentity) {
                    if (!simpleTapSourceIsStable(part)) return null;
                    // This exact operator reads only the controller's registered
                    // public commander identity. Do not invoke the broader
                    // resolver for chosen, marked or draft-dependent colors.
                    colors = mana.getComboColors(part);
                }
                List<String> palette = colors.equals("Any") ? List.of("W", "U", "B", "R", "G") : Arrays.asList(colors.split(" "));
                if (commanderIdentity && colors.isEmpty()) choices.add(new Alternative(Map.of(), Map.of())); // No commander colors means no mana, including no invented {C}.
                else if (!palette.stream().allMatch(COLORS::contains)
                        || !combinations(palette, 0, amount, each, new TreeMap<>(), new TreeMap<>(), mana, choices)) return null;
            } else {
                var bundle = new TreeMap<String, Integer>();
                for (String token : raw.split(" ")) {
                    if (COLORS.contains(token)) bundle.merge(token, amount, Integer::sum);
                    else {
                        Integer neutral = integer(token);
                        if (neutral == null || neutral < 0 || (long)neutral * amount > Integer.MAX_VALUE) return null;
                        bundle.merge("C", neutral * amount, Integer::sum);
                    }
                }
                choices.add(new Alternative(canonical(bundle), Map.of()));
            }
            var combined = new ArrayList<Alternative>();
            for (var prior : alternatives) for (var choice : choices) {
                var sum = new TreeMap<>(prior.colors());
                for (var count : choice.colors().entrySet()) {
                    if ((long)sum.getOrDefault(count.getKey(), 0) + count.getValue() > Integer.MAX_VALUE) return null;
                    sum.merge(count.getKey(), count.getValue(), Integer::sum);
                }
                var colors = canonical(sum);
                if (combined.stream().noneMatch(value -> value.colors().equals(colors))) {
                    if (combined.size() == MAX_ALTERNATIVES) return null;
                    var expressions = new IdentityHashMap<AbilityManaPart, Map<String, Integer>>(prior.choices());
                    expressions.putAll(choice.choices());
                    combined.add(new Alternative(colors, Collections.unmodifiableMap(expressions)));
                }
            }
            alternatives = combined; produced = true;
        }
        return produced ? List.copyOf(alternatives) : null;
    }

    private static boolean combinations(List<String> palette, int index, int remaining, int each,
                                        Map<String, Integer> current, Map<String, Integer> expression, AbilityManaPart mana, List<Alternative> output) {
        if (output.size() == MAX_ALTERNATIVES) return false;
        String color = palette.get(index);
        if (index == palette.size() - 1) {
            if ((long)remaining * each > Integer.MAX_VALUE) return false;
            current.put(color, remaining * each);
            // One choice token per iteration, not per produced mana: Each may
            // multiply a single selected color into several mana of that color.
            expression.put(color, remaining);
            output.add(new Alternative(canonical(current), Map.of(mana, canonical(expression))));
            expression.remove(color); current.remove(color); return true;
        }
        // A finite structural projection never expands an unbounded amount.
        // More than 32 distinct distributions cannot fit the advertised domain.
        if (remaining > MAX_ALTERNATIVES) return false;
        for (int count = 0; count <= remaining; count++) {
            if ((long)count * each > Integer.MAX_VALUE) return false;
            current.put(color, count * each);
            expression.put(color, count);
            if (!combinations(palette, index + 1, remaining - count, each, current, expression, mana, output)) return false;
            expression.remove(color);
        }
        current.remove(color); return true;
    }
    private static Map<String, Integer> canonical(Map<String, Integer> value) {
        var result = new TreeMap<>(value); result.values().removeIf(count -> count == 0);
        return Collections.unmodifiableMap(result);
    }
    private static Map<String, Integer> bundle(String color, int amount) { return Map.of(color, amount); }
    private static Integer amount(SpellAbility ability) {
        String value = ability.getParamOrDefault("Amount", "1");
        Integer literal = integer(value);
        if (literal != null) return literal;
        String expression = value.contains("$") ? value : ability.getSVar(value);
        String prefix = "Count$CardCounters.";
        if (!expression.startsWith(prefix)) return null;
        String counter = expression.substring(prefix.length());
        if (!counter.matches("[A-Z][A-Z0-9_]*") || !simpleTapSourceIsStable(ability)) return null;
        long count;
        if (counter.equals("ALL")) count = ability.getHostCard().getCounters().entrySet().stream().mapToLong(entry -> entry.getCount()).sum();
        else {
            try { count = ability.getHostCard().getCounters(CounterEnumType.valueOf(counter)); }
            catch (IllegalArgumentException unsupported) { return null; }
        }
        return count >= 0 && count <= 1024 ? (int) count : null;
    }

    /** A source-state projection guard, separate from timing and payment legality. */
    static boolean simpleTapSourceIsStable(SpellAbility ability) {
        return simpleTapSourceHasStableInputs(ability) && ambientManaOutcomeIsStable(ability);
    }

    /** The route's own finite inputs; unrelated concealed originals are not inputs. */
    private static boolean simpleTapSourceHasStableInputs(SpellAbility ability) {
        var source = ability.getHostCard();
        var actor = ability.getActivatingPlayer();
        var mana = ability.getManaPart();
        if (actor == null || source == null || source.isFaceDown() || !source.isInZone(ZoneType.Battlefield)
                || source.getController() != actor || ability.getRootAbility() != ability || ability.getSubAbility() != null
                || !ability.getAdditionalAbilities().isEmpty() || !ability.getAdditionalAbilityLists().isEmpty()
                || mana == null || mana.getSourceCard() != source || mana.addsCounters(ability) || mana.addKeywords(ability)
                || !mana.getManaRestrictions().isEmpty() || !mana.getExtraManaRestriction().isEmpty()
                || mana.getTriggersWhenSpent() || mana.isCannotCounterPaidWith() || mana.isCombatMana() || mana.isPersistentMana()
                || ability.hasParam("Defined") && !ability.getParam("Defined").equals("You") || ability.getPayCosts() == null
                || ability.getPayCosts().getCostParts().stream().filter(CostTap.class::isInstance).count() != 1
                || ability.getPayCosts().getCostParts().stream().anyMatch(cost -> !(cost instanceof CostTap)
                    && !(cost instanceof CostPartMana part && part.getMana().isZero()))) return false;
        return true;
    }

    private static boolean ambientManaOutcomeIsStable(SpellAbility ability) {
        var source = ability.getHostCard();
        var actor = ability.getActivatingPlayer();
        // This is a current public source-state fact, not a hypothetical payment
        // or replacement simulation. A face-down permanent's current rules state
        // is public and can include granted traits. Its concealed original is not
        // an input: neither its identity nor its intrinsic traits are inspected.
        for (ZoneType zone : List.of(ZoneType.Battlefield, ZoneType.Command))
            for (var card : source.getGame().getCardsIn(zone)) {
                if (!card.getView().canBeShownTo(actor.getView())) return false;
                var current = card.getCurrentState();
                if (current.getReplacementEffects(false).stream().anyMatch(replacement -> replacement.zonesCheck(card.getZone())
                        && (replacement.getMode() == ReplacementType.ProduceMana || replacement.getMode() == ReplacementType.Tap))) return false;
                if (current.getTriggers().stream().anyMatch(trigger -> trigger.zonesCheck(card.getZone())
                        && Set.of(TriggerType.Taps, TriggerType.TapAll, TriggerType.TapsForMana, TriggerType.ManaAdded).contains(trigger.getMode()))) return false;
            }
        return true;
    }
    private static Integer integer(String text) {
        try { return Integer.valueOf(text); } catch (NumberFormatException ignored) { return null; }
    }
    private static Map<String, Object> unknown() { return Map.of("knowledge", "Unknown", "alternatives", List.of()); }
    private ManaProductionProjection() { }
}
