package forge.api;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Read-only saved list and playable-quantity comparison, never a restore operation. */
public record DeckVersionView(String versionId, String name, String format, String currentFormat,
                              List<DeckEditor.Entry> entries, List<Difference> differences) {
    public record Difference(String section, String card, int saved, int current) { }
    public DeckVersionView { entries = List.copyOf(entries); differences = List.copyOf(differences); }
    static DeckVersionView compare(String id, String name, String format, List<DeckEditor.Entry> saved,
                                   DeckEditor.Snapshot current, String currentFormat) {
        var before = quantities(saved); var after = quantities(current.entries());
        var keys = new java.util.LinkedHashSet<>(before.keySet()); keys.addAll(after.keySet());
        var differences = keys.stream().filter(k -> !before.getOrDefault(k, 0).equals(after.getOrDefault(k, 0)))
                .map(k -> new Difference(k.substring(0, k.indexOf('\n')), k.substring(k.indexOf('\n') + 1),
                        before.getOrDefault(k, 0), after.getOrDefault(k, 0))).toList();
        return new DeckVersionView(id, name, format, currentFormat, saved, differences);
    }
    private static Map<String, Integer> quantities(List<DeckEditor.Entry> entries) {
        var values = new LinkedHashMap<String, Integer>();
        for (var e : entries) values.merge(e.section() + "\n" + e.card().name(), e.quantity(), Integer::sum);
        return values;
    }
}
