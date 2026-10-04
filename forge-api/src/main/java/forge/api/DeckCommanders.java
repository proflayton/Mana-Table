package forge.api;

import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.deck.DeckSection;
import forge.item.PaperCard;

import java.util.*;

/** Persistent commander selection, shared by every client and match setup. */
public final class DeckCommanders {
    public record Choice(String id, List<String> partners) { }

    private static Map<String, PaperCard> candidates(Deck deck) {
        var cards = new TreeMap<String, PaperCard>();
        for (var section : List.of(DeckSection.Main, DeckSection.Sideboard, DeckSection.Commander)) {
            var pool = deck.get(section);
            if (pool == null) continue;
            for (var entry : pool) if (DeckFormat.Commander.isLegalCommander(entry.getKey().getRules()))
                cards.put(CardCatalog.id(entry.getKey()), entry.getKey());
        }
        return cards;
    }

    public static List<Choice> choices(Deck deck) {
        var cards = candidates(deck);
        return cards.entrySet().stream().map(a -> new Choice(a.getKey(), cards.entrySet().stream()
                .filter(b -> !a.getValue().getName().equals(b.getValue().getName())
                        && a.getValue().getRules().canBePartnerCommanders(b.getValue().getRules()))
                .map(Map.Entry::getKey).toList())).toList();
    }

    public static void set(DeckEditor editor, long revision, List<String> ids) {
        Objects.requireNonNull(ids);
        if (ids.size() > 2 || new HashSet<>(ids).size() != ids.size())
            throw new IllegalArgumentException("Choose one commander or two compatible partners");
        var source = editor.toDeck();
        var candidates = candidates(source);
        var chosen = new ArrayList<PaperCard>();
        for (String id : ids) {
            var card = candidates.get(id);
            if (card == null) throw new IllegalArgumentException("Choose a commander from this deck");
            chosen.add(card);
        }
        if (chosen.size() == 2 && (chosen.get(0).getName().equals(chosen.get(1).getName())
                || !chosen.get(0).getRules().canBePartnerCommanders(chosen.get(1).getRules())))
            throw new IllegalArgumentException("These cards cannot be partner commanders");

        var updated = new Deck(source);
        var command = updated.getOrCreate(DeckSection.Commander);
        // Returning the old leaders first also preserves duplicates/printings in
        // incomplete drafts. Only one occurrence of each new leader is moved.
        for (var entry : command) updated.getMain().add(entry.getKey(), entry.getValue());
        command.clear();
        for (var card : chosen) {
            var from = updated.getMain().count(card) > 0 ? updated.getMain() : updated.get(DeckSection.Sideboard);
            from.remove(card, 1);
            command.add(card, 1);
        }
        var edits = new ArrayList<DeckEditor.Edit>();
        for (var section : List.of(DeckSection.Main, DeckSection.Sideboard, DeckSection.Commander)) {
            var before = source.get(section);
            var after = updated.get(section);
            var cards = new HashSet<PaperCard>();
            if (before != null) for (var entry : before) cards.add(entry.getKey());
            if (after != null) for (var entry : after) cards.add(entry.getKey());
            for (var card : cards) {
                int oldCount = before == null ? 0 : before.count(card), count = after == null ? 0 : after.count(card);
                if (oldCount != count) edits.add(new DeckEditor.Edit(section.name(), CardCatalog.id(card), count));
            }
        }
        editor.apply(revision, edits);
    }
}
