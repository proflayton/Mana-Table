package forge.api;

import com.google.gson.JsonElement;
import forge.game.spellability.AbilityManaPart;
import forge.game.spellability.SpellAbility;

import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

/** A bounded output selection for an independently authorized current mana ability. */
record ManaOutputChoice(Map<String, Integer> colors) {
    private static final Set<String> COLORS = Set.of("W", "U", "B", "R", "G", "C");

    static ManaOutputChoice parse(JsonElement element) {
        if (element == null || element.isJsonNull()) return null;
        if (!element.isJsonObject() || element.getAsJsonObject().size() != 1
                || !element.getAsJsonObject().has("colors") || !element.getAsJsonObject().get("colors").isJsonObject())
            throw invalid();
        var supplied = element.getAsJsonObject().getAsJsonObject("colors");
        if (supplied.size() == 0 || supplied.size() > COLORS.size()) throw invalid();
        var colors = new TreeMap<String, Integer>();
        long total = 0;
        for (var entry : supplied.entrySet()) {
            if (!COLORS.contains(entry.getKey()) || !entry.getValue().isJsonPrimitive()
                    || !entry.getValue().getAsJsonPrimitive().isNumber()) throw invalid();
            final int count;
            try { count = entry.getValue().getAsBigDecimal().intValueExact(); }
            catch (ArithmeticException | NumberFormatException failure) { throw invalid(); }
            if (count <= 0 || (total += count) > 1024) throw invalid();
            colors.put(entry.getKey(), count);
        }
        return new ManaOutputChoice(Map.copyOf(colors));
    }

    Map<AbilityManaPart, String> choicesFor(SpellAbility ability) {
        var choices = ManaProductionProjection.outputChoices(ability, colors);
        if (choices == null) throw invalid();
        return choices;
    }

    private static IllegalArgumentException invalid() {
        return new IllegalArgumentException("Choose an exact current mana output from this ability's available production alternatives");
    }
}
