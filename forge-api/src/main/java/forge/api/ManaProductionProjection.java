package forge.api;

import forge.game.ability.ApiType;
import forge.game.card.CounterEnumType;
import forge.game.cost.CostPartMana;
import forge.game.cost.CostTap;
import forge.game.replacement.ReplacementType;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.TriggerType;
import forge.game.zone.ZoneType;

import java.util.*;

/** Exact nominal bundles; unresolved choices/restrictions never become certain funding. */
final class ManaProductionProjection {
    private static final int MAX_ALTERNATIVES = 32;
    private static final Set<String> COLORS = Set.of("W", "U", "B", "R", "G", "C");

    static Map<String, Object> describe(SpellAbility ability) {
        if (!ability.isManaAbility()) return unknown();
        var alternatives = new ArrayList<Map<String, Integer>>();
        alternatives.add(new TreeMap<>());
        boolean produced = false;
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (SpellAbility part = ability; part != null; part = part.getSubAbility()) {
            if (!seen.add(part) || !part.getAdditionalAbilities().isEmpty() || !part.getAdditionalAbilityLists().isEmpty()
                    || part.getMapParams().keySet().stream().anyMatch(key -> key.startsWith("Condition") || key.startsWith("Unless")
                        || Set.of("Optional", "Chooser").contains(key))) return unknown();
            if (part.getApi() == ApiType.Cleanup) continue;
            if (part.getApi() != ApiType.Mana || part.getManaPart() == null) return unknown();
            var mana = part.getManaPart();
            // Restricted spending is evaluated against the exact current bill by
            // InputPayMana. A color list alone cannot certify generic spending.
            if (!mana.getManaRestrictions().isEmpty() || !mana.getExtraManaRestriction().isEmpty()) return unknown();
            Integer amount = amount(part);
            Integer each = integer(part.getParamOrDefault("Each", "1"));
            if (amount == null || each == null || amount < 0 || each < 0) return unknown();
            String raw = mana.getOrigProduced();
            var choices = new ArrayList<Map<String, Integer>>();
            if (raw.equals("Any")) {
                for (String color : List.of("W", "U", "B", "R", "G")) choices.add(bundle(color, amount));
            } else if (raw.startsWith("Combo ") && !raw.contains("Different")) {
                String colors = raw.substring(6);
                List<String> palette = colors.equals("Any") ? List.of("W", "U", "B", "R", "G") : Arrays.asList(colors.split(" "));
                if (palette.isEmpty() || !palette.stream().allMatch(COLORS::contains)
                        || !combinations(palette, 0, amount, each, new TreeMap<>(), choices)) return unknown();
            } else {
                var bundle = new TreeMap<String, Integer>();
                for (String token : raw.split(" ")) {
                    if (COLORS.contains(token)) bundle.merge(token, amount, Integer::sum);
                    else {
                        Integer neutral = integer(token);
                        if (neutral == null || neutral < 0 || (long)neutral * amount > Integer.MAX_VALUE) return unknown();
                        bundle.merge("C", neutral * amount, Integer::sum);
                    }
                }
                choices.add(bundle);
            }
            var combined = new ArrayList<Map<String, Integer>>();
            for (var prior : alternatives) for (var choice : choices) {
                if (combined.size() == MAX_ALTERNATIVES) return unknown();
                var sum = new TreeMap<>(prior);
                for (var count : choice.entrySet()) {
                    if ((long)sum.getOrDefault(count.getKey(), 0) + count.getValue() > Integer.MAX_VALUE) return unknown();
                    sum.merge(count.getKey(), count.getValue(), Integer::sum);
                }
                sum.values().removeIf(count -> count == 0);
                if (!combined.contains(sum)) combined.add(sum);
            }
            alternatives = combined; produced = true;
        }
        if (!produced) return unknown();
        return Map.of("knowledge", "Known", "alternatives", alternatives.stream()
                .map(colors -> Map.of("colors", Collections.unmodifiableMap(colors))).toList());
    }

    private static boolean combinations(List<String> palette, int index, int remaining, int each,
                                        Map<String, Integer> current, List<Map<String, Integer>> output) {
        if (output.size() == MAX_ALTERNATIVES) return false;
        String color = palette.get(index);
        if (index == palette.size() - 1) {
            if ((long)remaining * each > Integer.MAX_VALUE) return false;
            current.put(color, remaining * each); output.add(new TreeMap<>(current)); current.remove(color); return true;
        }
        // A finite structural projection never expands an unbounded amount.
        // More than 32 distinct distributions cannot fit the advertised domain.
        if (remaining > MAX_ALTERNATIVES) return false;
        for (int count = 0; count <= remaining; count++) {
            if ((long)count * each > Integer.MAX_VALUE) return false;
            current.put(color, count * each);
            if (!combinations(palette, index + 1, remaining - count, each, current, output)) return false;
        }
        current.remove(color); return true;
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
        if (!counter.matches("[A-Z][A-Z0-9_]*") || !currentCounterStateIsStable(ability)) return null;
        long count;
        if (counter.equals("ALL")) count = ability.getHostCard().getCounters().entrySet().stream().mapToLong(entry -> entry.getCount()).sum();
        else {
            try { count = ability.getHostCard().getCounters(CounterEnumType.valueOf(counter)); }
            catch (IllegalArgumentException unsupported) { return null; }
        }
        return count >= 0 && count <= 1024 ? (int) count : null;
    }

    private static boolean currentCounterStateIsStable(SpellAbility ability) {
        var source = ability.getHostCard();
        var actor = ability.getActivatingPlayer();
        if (actor == null || source == null || source.isFaceDown() || !source.isInZone(ZoneType.Battlefield)
                || source.getController() != actor || ability.getRootAbility() != ability || ability.getSubAbility() != null
                || !ability.getAdditionalAbilities().isEmpty() || !ability.getAdditionalAbilityLists().isEmpty()
                || ability.hasParam("Defined") && !ability.getParam("Defined").equals("You") || ability.getPayCosts() == null
                || ability.getPayCosts().getCostParts().stream().filter(CostTap.class::isInstance).count() != 1
                || ability.getPayCosts().getCostParts().stream().anyMatch(cost -> !(cost instanceof CostTap)
                    && !(cost instanceof CostPartMana mana && mana.getMana().isZero()))) return false;
        // This is a current public source-state fact, not a hypothetical payment
        // or replacement simulation. Unknown interactions stay unexpanded; never
        // inspect the original face or abilities of a concealed occurrence.
        for (ZoneType zone : List.of(ZoneType.Battlefield, ZoneType.Command))
            for (var card : source.getGame().getCardsIn(zone)) {
                if (card.isFaceDown() || !card.getView().canBeShownTo(actor.getView())) return false;
                if (card.getReplacementEffects().stream().anyMatch(replacement -> replacement.zonesCheck(card.getZone())
                        && (replacement.getMode() == ReplacementType.ProduceMana || replacement.getMode() == ReplacementType.Tap))) return false;
                if (card.getTriggers().stream().anyMatch(trigger -> trigger.zonesCheck(card.getZone())
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
