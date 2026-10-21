package forge.game.keyword;

import forge.game.PublicState;

import java.nio.charset.StandardCharsets;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;

/** Copies effective keyword display fields without exporting original script syntax. */
public final class PublicKeywordStates {
    private record Presentation(String key, String label, String detail) { }
    private PublicKeywordStates() { }

    public static List<PublicState> snapshot(Iterable<? extends KeywordView> keywords) {
        Map<Presentation, Integer> counts = new LinkedHashMap<>();
        for (var keyword : keywords) {
            // Undefined engine implementation tags have no published human title.
            // Future named keywords participate without another transport allowlist.
            String title = keyword.title();
            if (keyword.keyword() == null || title == null || title.isBlank()) continue;
            String key = keyword.keyword().name().toLowerCase(Locale.ROOT).replace('_', ' ');
            if (keyword.keyword() == Keyword.UNDEFINED) key = "custom";
            var presentation = new Presentation(key, title, keyword.reminderText() == null ? "" : keyword.reminderText());
            counts.merge(presentation, 1, Integer::sum);
        }
        return counts.entrySet().stream().sorted(Map.Entry.comparingByKey(
                Comparator.comparing(Presentation::key).thenComparing(Presentation::label).thenComparing(Presentation::detail)))
                .map(entry -> {
                    var shown = entry.getKey();
                    String identity = UUID.nameUUIDFromBytes((shown.label() + "\u001f" + shown.detail()).getBytes(StandardCharsets.UTF_8)).toString();
                    return new PublicState("keyword:" + shown.key() + ":" + identity, shown.label(), PublicState.Kind.ABILITY,
                            entry.getValue(), null, shown.detail(), shown.key());
                }).toList();
    }
}
