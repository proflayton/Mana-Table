package forge.api;

import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.deck.DeckSection;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.ConcurrentModificationException;
import java.util.Deque;
import java.util.List;
import java.util.Objects;
import java.util.UUID;

/** In-memory editor; each batch is atomic, revision checked, and undoable. */
public final class DeckEditor {
    public record Edit(String section, String cardId, int quantity) { }
    public record Entry(String section, CardCatalog.CardInfo card, int quantity) { }
    public record Snapshot(String name, long revision, List<Entry> entries, boolean canUndo, boolean canRedo) {
        public Snapshot { entries = List.copyOf(entries); }
    }
    /** Structural rules and available bundled eligibility; never an online legality claim. */
    public record Issue(String code, String message, String section, List<String> cardIds) {
        public Issue { cardIds = List.copyOf(cardIds); }
    }
    public record Validation(String format, boolean valid, String problem, List<Issue> issues,
                             boolean structureValid, String eligibility) {
        public Validation { issues = List.copyOf(issues); }
        public Validation(String format, boolean valid, String problem) { this(format, valid, problem, List.of(), valid, "unknown"); }
    }

    private final CardCatalog catalog;
    private record Frame(Deck deck, String token) { }
    private final Deque<Frame> undo = new ArrayDeque<>();
    private final Deque<Frame> redo = new ArrayDeque<>();
    private Deck deck;
    private long revision;
    private String historyToken = UUID.randomUUID().toString();

    public DeckEditor(CardCatalog catalog, Deck initial) {
        this.catalog = Objects.requireNonNull(catalog);
        deck = new Deck(Objects.requireNonNull(initial));
    }

    public synchronized Snapshot snapshot() {
        List<Entry> entries = new ArrayList<>();
        for (var section : deck) {
            for (var card : section.getValue()) {
                entries.add(new Entry(section.getKey().name(), CardCatalog.describe(card.getKey()), card.getValue()));
            }
        }
        entries.sort(Comparator.comparing(Entry::section).thenComparing(e -> e.card().name())
                .thenComparing(e -> e.card().id()));
        return new Snapshot(deck.getName(), revision, entries, !undo.isEmpty(), !redo.isEmpty());
    }

    public synchronized Snapshot apply(long expectedRevision, List<Edit> edits) {
        checkRevision(expectedRevision);
        Objects.requireNonNull(edits);
        if (edits.isEmpty()) { return snapshot(); }
        Deck updated = new Deck(deck);
        for (Edit edit : edits) {
            if (edit.quantity() < 0 || edit.quantity() > 1000) {
                throw new IllegalArgumentException("Quantity must be 0..1000");
            }
            DeckSection section = DeckSection.valueOf(edit.section());
            var card = catalog.resolve(edit.cardId());
            if (edit.quantity() > 0 && !section.validate(card)) {
                throw new IllegalArgumentException("Card does not belong in " + section);
            }
            var pool = updated.getOrCreate(section);
            pool.remove(card, pool.count(card));
            pool.add(card, edit.quantity());
        }
        remember(undo, deck, historyToken);
        deck = updated;
        historyToken = UUID.randomUUID().toString();
        redo.clear();
        revision++;
        return snapshot();
    }

    /** Replace only this section's copies; the command zone and partners stay intact. */
    public synchronized Snapshot replacePrinting(long expectedRevision, String sectionName, String cardId, String printingId) {
        checkRevision(expectedRevision);
        var section = DeckSection.valueOf(sectionName);
        var previous = catalog.resolve(cardId);
        var replacement = catalog.resolve(printingId);
        if (!previous.getName().equals(replacement.getName())
                || !Objects.equals(previous.getFunctionalVariant(), replacement.getFunctionalVariant())) {
            throw new IllegalArgumentException("Choose another printing of the same card");
        }
        var pool = deck.get(section);
        int quantity = pool == null ? 0 : pool.count(previous);
        if (quantity == 0) throw new IllegalArgumentException("That printing is no longer in this deck section");
        if (cardId.equals(printingId)) return snapshot();
        return apply(expectedRevision, List.of(new Edit(sectionName, cardId, 0),
                new Edit(sectionName, printingId, quantity + pool.count(replacement))));
    }

    public synchronized Snapshot undo(long expectedRevision) {
        checkRevision(expectedRevision);
        if (!undo.isEmpty()) {
            remember(redo, deck, historyToken);
            var frame = undo.pop();
            deck = frame.deck(); historyToken = frame.token();
            revision++;
        }
        return snapshot();
    }

    public synchronized Snapshot rename(long expectedRevision, String name) {
        checkRevision(expectedRevision);
        if (name == null || name.isBlank() || name.length() > 100) {
            throw new IllegalArgumentException("Deck name must be 1..100 characters");
        }
        remember(undo, deck, historyToken);
        deck = new Deck(deck, name.strip());
        historyToken = UUID.randomUUID().toString();
        redo.clear();
        revision++;
        return snapshot();
    }

    public synchronized Snapshot redo(long expectedRevision) {
        checkRevision(expectedRevision);
        if (!redo.isEmpty()) {
            remember(undo, deck, historyToken);
            var frame = redo.pop();
            deck = frame.deck(); historyToken = frame.token();
            revision++;
        }
        return snapshot();
    }

    public synchronized Validation validate(DeckFormat format) {
        String problem = Objects.requireNonNull(format).getDeckConformanceProblem(deck);
        return DeckLegality.validate(catalog, deck, format, problem);
    }

    /** A detached copy for Forge's match launcher or existing DeckSerializer. */
    public synchronized Deck toDeck() { return new Deck(deck); }

    /** Opaque in-memory boundaries let host metadata follow the same undo step without guessing from contents. */
    synchronized String historyToken() { return historyToken; }
    synchronized String undoToken() { return undo.isEmpty() ? historyToken : undo.peek().token(); }
    synchronized String redoToken() { return redo.isEmpty() ? historyToken : redo.peek().token(); }
    synchronized Deck historyDeck(boolean backwards) {
        var frames = backwards ? undo : redo;
        return new Deck(frames.isEmpty() ? deck : frames.peek().deck());
    }
    synchronized void checkpoint(long expectedRevision) {
        checkRevision(expectedRevision);
        remember(undo, deck, historyToken); redo.clear();
        historyToken = UUID.randomUUID().toString(); revision++;
    }

    private void checkRevision(long expected) {
        if (expected != revision) {
            throw new ConcurrentModificationException("Expected revision " + expected + "; current is " + revision);
        }
    }

    private static void remember(Deque<Frame> history, Deck previous, String token) {
        history.push(new Frame(previous, token));
        if (history.size() > 100) { history.removeLast(); }
    }
}
