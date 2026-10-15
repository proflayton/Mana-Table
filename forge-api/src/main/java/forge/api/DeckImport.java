package forge.api;

import forge.StaticData;
import forge.card.CardDb;
import forge.deck.Deck;
import forge.deck.DeckRecognizer;
import forge.deck.DeckSection;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;
import java.util.regex.Pattern;

/** Paste/import preview using Forge's existing multi-format deck recognizer. */
public final class DeckImport {
    private static final Pattern NATIVE_LINE = Pattern.compile("^(\\d+)\\s+(.+\\|.+)$");
    private static final Pattern EXPORTED_PRINTING = Pattern.compile(
            "^(?<card>.+?)\\s+\\((?<set>[A-Za-z0-9]{2,12})\\)"
                    + "(?:\\s+[0-9A-Za-z★☆-]+)?(?:\\s+(?<foil>\\*[FE]\\*|\\(F\\)))?$",
            Pattern.CASE_INSENSITIVE);
    private static final Pattern QUANTITY_ROW = Pattern.compile(
            "^(?:(?<section>MB|SB|CM)\\s*:\\s*)?(?<quantity>\\d+)\\s*[xX]?\\s+(?<card>.+)$",
            Pattern.CASE_INSENSITIVE);
    private static final Pattern TRAILING_TAG = Pattern.compile("\\s+\\[([^\\[\\]]+)\\]$");
    private static final Pattern TRAILING_OVERRIDE = Pattern.compile("\\s+\\^[^\\^\\r\\n]*\\^$");
    private static final Pattern EXCLUDED_TAG = Pattern.compile("\\{noDeck\\}", Pattern.CASE_INSENSITIVE);
    private static final Pattern UNSUPPORTED_BOARD = Pattern.compile("^(?:Maybeboard|Considering|Companion)(?:/.*)?$", Pattern.CASE_INSENSITIVE);
    private DeckImport() { }

    private static String problemMessage(Problem problem) {
        String reason = switch (problem.kind()) {
            case "UNSUPPORTED_BOARD" -> "unsupported Companion/Maybeboard or excluded board; export Main, Sideboard and Commander only";
            case "INVALID_QUANTITY" -> "quantity must be 1..1000";
            case "INVALID_SECTION", "CONFLICTING_SECTIONS" -> "invalid or conflicting deck section";
            case "INVALID_FORGE_ROW" -> "invalid Forge printing row";
            default -> "unrecognized card, printing or deck text";
        };
        return "Line " + problem.line() + ": " + reason + ": " + problem.text();
    }

    private record ImportLine(String text, Integer quantity, DeckSection section) { }

    private static ImportLine importLine(String line) {
        var row = QUANTITY_ROW.matcher(line);
        if (!row.matches()) return new ImportLine(line, null, null);
        int quantity;
        try { quantity = Integer.parseInt(row.group("quantity")); }
        catch (NumberFormatException error) { throw new IllegalArgumentException("INVALID_QUANTITY"); }
        if (quantity < 1 || quantity > 1000) throw new IllegalArgumentException("INVALID_QUANTITY");
        DeckSection section = row.group("section") == null ? null : switch (row.group("section").toUpperCase(java.util.Locale.ROOT)) {
            case "MB" -> DeckSection.Main;
            case "SB" -> DeckSection.Sideboard;
            default -> DeckSection.Commander;
        };
        String card = row.group("card");
        card = TRAILING_OVERRIDE.matcher(card).replaceFirst("");
        var tag = TRAILING_TAG.matcher(card);
        while (tag.find()) {
            // Export categories annotate this row; only actual deck-section names
            // (including Archidekt's Commander{top}) change where its card belongs.
            if (EXCLUDED_TAG.matcher(tag.group(1)).find()) throw new IllegalArgumentException("UNSUPPORTED_BOARD");
            boolean sectionTag = false;
            for (String category : tag.group(1).split(",")) {
                String name = category.replaceAll("\\{[^{}]*\\}", "").strip();
                if (UNSUPPORTED_BOARD.matcher(name).matches()) throw new IllegalArgumentException("UNSUPPORTED_BOARD");
                DeckSection tagged = DeckSection.smartValueOf(name);
                if (tagged != null) {
                    if (section != null && section != tagged) throw new IllegalArgumentException("CONFLICTING_SECTIONS");
                    section = tagged;
                    sectionTag = true;
                }
            }
            // Reserved section tags take precedence over edition-name aliases.
            // Otherwise preserve a known printing request even when it is invalid:
            // removing it would silently replace a wrong-set card by another set.
            if (!sectionTag && StaticData.instance().getEditions().get(tag.group(1)) != null) break;
            card = card.substring(0, tag.start()).stripTrailing();
            tag = TRAILING_TAG.matcher(card);
        }
        // The recognizer accepts two-digit quantities. Resolve one printing, then
        // apply the independently validated original amount without truncation.
        return new ImportLine("1 " + card, quantity, section);
    }

    public record Problem(int line, String kind, String text) { }
    public record Preview(List<DeckEditor.Entry> entries, List<Problem> problems, List<Problem> warnings) {
        public Preview {
            entries = List.copyOf(entries);
            problems = List.copyOf(problems);
            warnings = List.copyOf(warnings);
        }

        /** Unknown or unsupported lines must be resolved before creating a deck. */
        public DeckEditor open(String name, CardCatalog catalog) {
            if (!problems.isEmpty()) {
                String details = problems.stream().limit(3)
                        .map(DeckImport::problemMessage)
                        .collect(java.util.stream.Collectors.joining("\n"));
                throw new IllegalArgumentException("Cannot import " + problems.size() + " unresolved line(s):\n" + details
                        + (problems.size() > 3 ? "\n...and " + (problems.size() - 3) + " more." : ""));
            }
            Deck deck = new Deck(name);
            for (DeckEditor.Entry entry : entries) {
                if (entry.quantity() < 1 || entry.quantity() > 1000) {
                    throw new IllegalArgumentException("Quantity must be 1..1000");
                }
                var section = DeckSection.valueOf(entry.section());
                var card = catalog.resolve(entry.card().id());
                if (!section.validate(card)) { throw new IllegalArgumentException("Card does not belong in " + section); }
                var pool = deck.getOrCreate(section);
                int total = Math.addExact(pool.count(card), entry.quantity());
                if (total > 1000) { throw new IllegalArgumentException("Quantity exceeds 1000"); }
                pool.add(card, entry.quantity());
            }
            return new DeckEditor(catalog, deck);
        }
    }

    public static Preview preview(String text) {
        Objects.requireNonNull(text);
        if (text.length() > 1_000_000) { throw new IllegalArgumentException("Deck text is too large"); }
        var recognizer = new DeckRecognizer();
        List<DeckEditor.Entry> entries = new ArrayList<>();
        List<Problem> problems = new ArrayList<>();
        List<Problem> warnings = new ArrayList<>();
        DeckSection section = DeckSection.Main;
        String[] lines = text.split("\\R", -1);
        for (int index = 0; index < lines.length; index++) {
            String line = lines[index].replace('\u00a0', ' ').replaceFirst("^\\uFEFF", "").strip();
            String heading = line.replaceFirst("^//\\s*", "").replaceFirst("^\\[([^\\]]+)\\]$", "$1");
            if (UNSUPPORTED_BOARD.matcher(heading).matches()) {
                problems.add(new Problem(index + 1, "UNSUPPORTED_BOARD", lines[index]));
                continue;
            }
            var nativeLine = NATIVE_LINE.matcher(line);
            if (nativeLine.matches()) {
                try {
                    int quantity = Integer.parseInt(nativeLine.group(1));
                    if (quantity < 1 || quantity > 1000) { throw new IllegalArgumentException("INVALID_QUANTITY"); }
                    var request = CardDb.CardRequest.fromString(nativeLine.group(2));
                    if (request.flags != null && !request.flags.isEmpty()) { throw new IllegalArgumentException("UNSUPPORTED_FLAGS"); }
                    var data = StaticData.instance();
                    var edition = data.getCardEdition(request.edition);
                    forge.item.PaperCard card = null;
                    for (var database : data.getAvailableDatabases().values()) {
                        card = database.getCardFromSet(request.cardName, edition, request.artIndex, request.collectorNumber, request.isFoil);
                        if (card != null) { break; }
                    }
                    if (card == null) { throw new IllegalArgumentException("UNKNOWN_PRINTING"); }
                    if (!section.validate(card)) { throw new IllegalArgumentException("INVALID_SECTION"); }
                    entries.add(new DeckEditor.Entry(section.name(), CardCatalog.describe(card), quantity));
                } catch (RuntimeException error) {
                    problems.add(new Problem(index + 1, "INVALID_FORGE_ROW", lines[index]));
                }
                continue;
            }
            ImportLine row;
            try { row = importLine(line); }
            catch (IllegalArgumentException error) {
                problems.add(new Problem(index + 1, error.getMessage(), lines[index]));
                continue;
            }
            line = row.text();
            DeckSection target = row.section() == null ? section : row.section();
            var token = recognizer.recognizeLine(line, target);
            if (token == null) { continue; }
            // Moxfield/Arena lists may name a printing from an edition Forge
            // does not bundle (e.g. SUM). Retry only that exact card request,
            // preserving quantities, sections and foil markers. Native Forge
            // rows and errors for known editions retain their strict behavior.
            var printing = EXPORTED_PRINTING.matcher(line);
            if (token.getType() == DeckRecognizer.TokenType.UNKNOWN_CARD && printing.matches()
                    && StaticData.instance().getEditions().get(printing.group("set")) == null) {
                String foil = printing.group("foil");
                var alternative = recognizer.recognizeLine(printing.group("card") + (foil == null ? "" : " " + foil), target);
                if (alternative != null && alternative.getType() == DeckRecognizer.TokenType.LEGAL_CARD) {
                    token = alternative;
                    warnings.add(new Problem(index + 1, "PRINTING_SUBSTITUTED", token.getCard().getName()
                            + ": " + printing.group("set") + " is not in the library; using "
                            + token.getCard().getEdition() + " #" + token.getCard().getCollectorNumber() + "."));
                }
            }
            if (row.quantity() != null && token.getType() != DeckRecognizer.TokenType.LEGAL_CARD) {
                // A quantified row is a card request, even if its text resembles a
                // category/header. It cannot disappear as ignored formatting.
                problems.add(new Problem(index + 1, token.getType().name(), lines[index]));
                continue;
            }
            switch (token.getType()) {
                case DECK_SECTION_NAME -> section = DeckSection.valueOf(token.getText());
                case LEGAL_CARD -> {
                    int quantity = row.quantity() == null ? token.getQuantity() : row.quantity();
                    if (quantity < 1 || quantity > 1000) {
                        problems.add(new Problem(index + 1, "INVALID_QUANTITY", lines[index]));
                    } else if (row.section() != null && !row.section().validate(token.getCard())) {
                        problems.add(new Problem(index + 1, "INVALID_SECTION", lines[index]));
                    } else {
                        entries.add(new DeckEditor.Entry(token.getTokenSection().name(),
                                CardCatalog.describe(token.getCard()), quantity));
                    }
                }
                case COMMENT, DECK_NAME, CARD_TYPE, CARD_RARITY, CARD_CMC, MANA_COLOUR -> { }
                default -> problems.add(new Problem(index + 1, token.getType().name(), lines[index]));
            }
        }
        return new Preview(entries, problems, warnings);
    }
}
