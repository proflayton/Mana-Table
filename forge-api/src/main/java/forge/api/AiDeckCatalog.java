package forge.api;

import forge.deck.Deck;
import forge.deck.DeckFormat;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;

/** Host-owned committed deck choices. Catalog IDs describe a saved revision, not permission to change a seat. */
final class AiDeckCatalog {
    private final CardCatalog catalog;
    private final DeckDocumentSession documents;
    private final Map<String, Optional<Deck>> validated = new HashMap<>();

    AiDeckCatalog(CardCatalog catalog, DeckDocumentSession documents) {
        this.catalog = catalog; this.documents = documents;
    }

    synchronized List<Map<String, String>> choices(String format) {
        var presets = MatchSession.opponents(format);
        if (documents == null) return presets;
        var result = new ArrayList<Map<String, String>>();
        var retained = new java.util.HashSet<String>();
        try {
            for (var saved : documents.savedDeckReferences()) {
                if (!format.equals(saved.format())) continue;
                String id = "deck:" + saved.id() + ":" + saved.token();
                retained.add(id);
                var deck = validated.computeIfAbsent(id, ignored -> {
                    try { return Optional.of(load(id, format)); }
                    catch (Exception invalid) { return Optional.empty(); }
                });
                if (deck.isEmpty()) continue;
                String commanders = String.join(" + ", deck.get().getCommanders().stream().map(card -> card.getName()).toList());
                result.add(Map.of("id", id, "name", saved.name(), "description", "Saved deck"
                        + (commanders.isEmpty() ? "" : " · " + commanders) + " · " + format));
            }
        } catch (Exception error) { throw new IllegalStateException("Could not read saved AI decks. Refresh Your decks.", error); }
        validated.keySet().retainAll(retained);
        result.addAll(presets);
        return List.copyOf(result);
    }

    Deck load(String id, String format) throws Exception {
        Deck source;
        if (!id.startsWith("deck:")) source = MatchSession.aiDeck(id, format);
        else {
            if (documents == null) throw new IllegalArgumentException("This host has no saved AI deck library.");
            String[] parts = id.substring(5).split(":", -1);
            if (parts.length < 1 || parts.length > 2 || parts.length == 2 && !parts[1].matches("[a-f0-9]{64}"))
                throw new IllegalArgumentException("That saved AI deck changed. Choose it again.");
            var saved = documents.loadLobbyDeck(parts[0], parts.length == 2 ? parts[1] : null);
            if (!format.equals(saved.format())) throw new IllegalArgumentException("AI decks must use this table's format");
            source = saved.deck();
        }
        var prepared = MatchSetup.prepare(source, format, "");
        var rules = format.equals("Commander") ? DeckFormat.Commander : DeckFormat.Constructed;
        String problem = DeckLegality.validate(catalog, prepared.deck(), rules, prepared.preview().problem()).problem();
        if (problem != null) throw new IllegalArgumentException("This AI deck is not legal for " + format + ": " + problem);
        return prepared.deck();
    }
}
