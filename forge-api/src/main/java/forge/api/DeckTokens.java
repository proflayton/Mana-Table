package forge.api;

import forge.StaticData;
import forge.card.CardSplitType;
import forge.deck.Deck;
import forge.item.PaperToken;
import forge.token.TokenDb;
import java.util.*;

/** Deck cosmetics use exact token definitions, never a same-name rules substitute. */
final class DeckTokens {
    record Token(String id, CardCatalog.CardInfo card, String printingId, String defaultPrintingId,
                 boolean automatic, boolean unavailable, int printingCount, List<String> sources) { }
    record View(String deckId, long revision, List<Token> tokens) { }

    static Map<String, List<String>> definitions(Deck deck) {
        var result = new TreeMap<String, List<String>>();
        if (StaticData.instance() == null) return result;
        var database = StaticData.instance().getAllTokens();
        for (var section : deck) for (var entry : section.getValue()) {
            var pending = new ArrayDeque<>(entry.getKey().getRules().getTokens());
            var visited = new HashSet<String>();
            while (!pending.isEmpty()) {
                String script = pending.removeFirst().trim();
                if (!visited.add(script) || !database.containsRule(script)) continue;
                result.computeIfAbsent(script, ignored -> new ArrayList<>()).add(entry.getKey().getName());
                pending.addAll(database.getRules().get(script).getTokens());
            }
        }
        result.replaceAll((key, sources) -> sources.stream().distinct().sorted().toList());
        return result;
    }

    static View view(String deckId, DeckEditor editor) {
        Deck deck = editor.toDeck();
        if (StaticData.instance() == null) return new View(deckId, editor.snapshot().revision(), List.of());
        var database = StaticData.instance().getAllTokens();
        var tokens = new ArrayList<Token>();
        for (var definition : definitions(deck).entrySet()) {
            String script = definition.getKey();
            var printings = database.getPrintings(script);
            var latest = printings.isEmpty() ? database.getToken(script) : printings.get(0);
            if (latest == null) continue;
            String preference = deck.getTokenPrintings().get(script);
            var selected = preference == null ? null : database.getPrinting(script, preference);
            boolean unavailable = preference != null && selected == null;
            if (selected == null) selected = latest;
            tokens.add(new Token(script, describe(script, selected), printings.isEmpty() ? "" : TokenDb.printingId(script, selected),
                    printings.isEmpty() ? "" : TokenDb.printingId(script, latest), preference == null, unavailable, printings.size(), definition.getValue()));
        }
        tokens.sort(Comparator.comparing(token -> token.card().name()));
        return new View(deckId, editor.snapshot().revision(), List.copyOf(tokens));
    }

    static CardCatalog.Page printings(Deck deck, String script, int offset, int limit) {
        requireDefinition(deck, script);
        if (offset < 0 || limit < 1 || limit > 30) throw new IllegalArgumentException("Invalid token printing page");
        var choices = StaticData.instance().getAllTokens().getPrintings(script);
        return new CardCatalog.Page(choices.size(), offset, choices.stream().skip(offset).limit(limit)
                .map(token -> describe(script, token)).toList(), choices.size());
    }

    static void requireDefinition(Deck deck, String script) {
        if (!definitions(deck).containsKey(script)) throw new IllegalArgumentException("That token is no longer created by this deck");
    }

    static void validateChoice(Deck deck, String script, String printingId) {
        requireDefinition(deck, script);
        if (printingId != null && StaticData.instance().getAllTokens().getPrinting(script, printingId) == null)
            throw new IllegalArgumentException("Choose a printing of this exact token");
    }

    // Older saves have no choices; absent/removed printings use the current default.
    static Map<String, String> readPreferences(com.google.gson.JsonElement value) {
        if (value == null || value.isJsonNull()) return Map.of();
        if (!value.isJsonObject() || value.getAsJsonObject().size() > 1000) throw new IllegalArgumentException("Invalid token artwork choices");
        var result = new LinkedHashMap<String, String>();
        for (var entry : value.getAsJsonObject().entrySet()) {
            if (entry.getKey().length() > 200 || !entry.getValue().isJsonPrimitive()
                    || !entry.getValue().getAsJsonPrimitive().isString() || entry.getValue().getAsString().length() > 500)
                throw new IllegalArgumentException("Invalid token artwork choice");
            String id = entry.getValue().getAsString();
            if (!id.startsWith(entry.getKey() + "|")) throw new IllegalArgumentException("A token printing belongs to another token");
            result.put(entry.getKey(), id);
        }
        return Map.copyOf(result);
    }

    static CardCatalog.CardInfo describe(String script, PaperToken token) {
        var rules = token.getRules();
        var face = rules.getMainPart();
        var edition = StaticData.instance().getCardEdition(token.getEdition());
        return new CardCatalog.CardInfo(TokenDb.printingId(script, token), token.getName(), token.getEdition(), token.getArtIndex(), false,
                rules.getManaCost().toString(), 0, rules.getType().toString(), Objects.requireNonNullElse(rules.getOracleText(), "").replace("\\n", "\n"),
                rules.getColor().getColor(), rules.getColorIdentity().getColor(), "Token", token.getCollectorNumber(), "Tokens",
                face.getPower(), face.getToughness(), token.getName(), "front",
                CardSplitType.DUAL_FACED_CARDS.contains(rules.getSplitType()) && rules.getOtherPart() != null
                        ? new CardCatalog.FaceInfo(rules.getOtherPart().getName(), "", rules.getOtherPart().getType().toString(),
                            rules.getOtherPart().getOracleText(), rules.getOtherPart().getPower(), rules.getOtherPart().getToughness(), token.getName(), "back") : null,
                edition.getTokensCode());
    }
}
