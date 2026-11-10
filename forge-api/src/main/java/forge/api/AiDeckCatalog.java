package forge.api;

import forge.deck.Deck;
import forge.deck.DeckFormat;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.Random;
import java.util.Set;
import java.util.TreeMap;

/** Host-owned committed deck choices. Catalog IDs describe a saved revision, not permission to change a seat. */
final class AiDeckCatalog {
    private final CardCatalog catalog;
    private final DeckDocumentSession documents;
    private final Map<String, Optional<Deck>> validated = new HashMap<>();
    private record Composition(String content, Set<String> commanders) { }
    private record Candidate(String id, Composition composition) { }
    private final Map<String, Optional<Composition>> compositions = new HashMap<>();

    AiDeckCatalog(CardCatalog catalog, DeckDocumentSession documents) {
        this.catalog = catalog; this.documents = documents;
    }

    synchronized List<Map<String, String>> choices(String format) {
        var rows = baseChoices(format);
        var retained = new HashSet<String>();
        var result = new ArrayList<Map<String, String>>();
        for (var row : rows) {
            retained.add(format + "\n" + row.get("id"));
            var facts = facts(row.get("id"), format);
            if (facts.isEmpty()) { result.add(row); continue; }
            var described = new HashMap<>(row);
            described.put("compositionId", facts.get().content());
            result.add(Map.copyOf(described));
        }
        compositions.keySet().removeIf(key -> key.startsWith(format + "\n") && !retained.contains(key));
        return List.copyOf(result);
    }

    private List<Map<String, String>> baseChoices(String format) {
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

    /** Host-private composition diversity, not a power rating or permission to change a seat. */
    synchronized List<String> selectPod(Deck reference, String format, int count, List<Deck> reserved, long seed) {
        return selectPod(reference, format, count, reserved, seed, choices(format));
    }

    /** The preparation owner can reuse the exact catalog it is publishing. */
    synchronized List<String> selectPod(Deck reference, String format, int count, List<Deck> reserved, long seed,
                                        List<Map<String, String>> choices) {
        int maximum = format.equals("Commander") ? 5 : format.equals("Constructed") ? 1 : 0;
        if (count < 0 || count > maximum) throw new IllegalArgumentException("Choose a valid number of AI opponents");
        if (count == 0) return List.of();
        var groups = new TreeMap<String, List<Candidate>>();
        for (var choice : choices) {
            String id = choice.get("id");
            var facts = facts(id, format);
            facts.ifPresent(value -> groups.computeIfAbsent(value.content(), ignored -> new ArrayList<>()).add(new Candidate(id, value)));
        }
        if (groups.isEmpty()) throw new IllegalStateException("No compatible legal AI decks are available.");
        // Saved copies and printing/name changes must not buy extra random tickets.
        // Prefer an existing bundled handle for an identical list; explicit saved
        // handles are still accepted unchanged by the ordinary assignment route.
        var pool = new ArrayList<Candidate>();
        for (var aliases : groups.values()) {
            aliases.sort(Comparator.comparing((Candidate candidate) -> candidate.id().startsWith("deck:"))
                    .thenComparing(Candidate::id));
            pool.add(aliases.get(0));
        }
        var referenceFacts = reference == null ? null : composition(reference, format);
        if (referenceFacts != null && pool.stream().anyMatch(candidate -> !candidate.composition().content().equals(referenceFacts.content())))
            pool.removeIf(candidate -> candidate.composition().content().equals(referenceFacts.content()));
        // Canonical content ordering precedes the shuffle, independently of library recency.
        Collections.shuffle(pool, new Random(seed));
        var contentUses = new HashMap<String, Integer>();
        var commanderUses = new HashMap<String, Integer>();
        if (referenceFacts != null) reserve(referenceFacts, contentUses, commanderUses);
        for (var deck : reserved) reserve(composition(deck, format), contentUses, commanderUses);
        var selected = new ArrayList<String>();
        for (int seat = 0; seat < count; seat++) {
            Candidate best = null; int fewestCopies = Integer.MAX_VALUE, fewestCommanders = Integer.MAX_VALUE;
            for (var candidate : pool) {
                var facts = candidate.composition();
                int copies = contentUses.getOrDefault(facts.content(), 0);
                int commanders = facts.commanders().stream().mapToInt(name -> commanderUses.getOrDefault(name, 0)).sum();
                if (copies < fewestCopies || copies == fewestCopies && commanders < fewestCommanders) {
                    best = candidate; fewestCopies = copies; fewestCommanders = commanders;
                }
            }
            selected.add(best.id()); reserve(best.composition(), contentUses, commanderUses);
        }
        return List.copyOf(selected);
    }

    private Composition composition(Deck deck, String format) {
        return new Composition(DeckWorkshopStore.fingerprint(new DeckEditor(catalog, deck).snapshot(), format),
                Set.copyOf(deck.getCommanders().stream().map(card -> card.getName()).sorted().toList()));
    }
    private Optional<Composition> facts(String id, String format) {
        return compositions.computeIfAbsent(format + "\n" + id, ignored -> {
            try {
                var committed = validated.get(id);
                return Optional.of(composition(committed != null && committed.isPresent() ? committed.get() : load(id, format), format));
            }
            catch (Exception invalid) { return Optional.empty(); }
        });
    }
    private static void reserve(Composition facts, Map<String, Integer> contentUses, Map<String, Integer> commanderUses) {
        contentUses.merge(facts.content(), 1, Integer::sum);
        facts.commanders().forEach(name -> commanderUses.merge(name, 1, Integer::sum));
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
