package forge.api;

import forge.game.GameFormat;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;

/** Uses the shipped rules snapshot, without booting the GUI's FModel singleton. */
final class DeckAnalysisFormats implements DeckAnalysis.Eligibility {
    private final CardCatalog catalog;
    private Map<String, GameFormat> formats = Map.of();
    DeckAnalysisFormats(CardCatalog catalog) { this.catalog = catalog; }
    void load(Path resources) {
        Path path = resources.resolve("formats");
        if (Files.isDirectory(path)) formats = new GameFormat.Reader(path.toFile(), null, false).readAll();
    }
    @Override public String check(CardCatalog.CardInfo card, String format) {
        var rules = formats.get(format);
        if (rules == null) return "unknown";
        if (!rules.getFilterRules().test(catalog.resolve(card.id()))) return "ineligible";
        return rules.getRestrictedCards().contains(card.name()) ? "restricted" : "bundled-rules";
    }
}
