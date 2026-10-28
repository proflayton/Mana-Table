package forge.api;

import forge.deck.Deck;
import forge.deck.DeckFormat;
import java.util.List;
import java.util.UUID;

/** One owned deck snapshot and its immutable, deliberately public seat identity. */
record LobbyDeckSelection(Deck deck, String problem, PublicView preview) {
    record PublicView(String selectionId, String name, List<CardCatalog.CardInfo> commanders, String problem) {
        PublicView { commanders = List.copyOf(commanders); }
    }

    static LobbyDeckSelection capture(Deck source, String format, CardCatalog catalog) {
        var deck = new Deck(source);
        String problem = new DeckEditor(catalog, deck).validate(DeckFormat.valueOf(format)).problem();
        var commanders = deck.getCommanders().stream().map(CardCatalog::describe).toList();
        // Legality diagnostics may identify private main-deck cards. Only the
        // choosing player receives those diagnostics when attempting Ready.
        var preview = new PublicView(UUID.randomUUID().toString(), deck.getName(), commanders,
                problem == null ? null : "This deck needs changes before it can be readied.");
        return new LobbyDeckSelection(deck, problem, preview);
    }
}
