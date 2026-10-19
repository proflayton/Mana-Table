package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/** Durable observations tied to an immutable, playable deck version, never a mutable editor. */
public final class DeckPlaytests {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
    private static final Set<String> TERMINAL = Set.of("completed", "conceded", "interrupted", "error");
    public record TestRecord(String id, String deckId, String versionId, String kind, String question,
                             long started, Long ended, String status, String result, List<String> opponents,
                             Map<String, Integer> observations, String note, List<CardCatalog.CardInfo> hand) {
        public TestRecord {
            opponents = List.copyOf(opponents); observations = Map.copyOf(observations);
            hand = hand == null ? List.of() : List.copyOf(hand);
        }
        public TestRecord(String id, String deckId, String versionId, String kind, String question,
                          long started, Long ended, String status, String result, List<String> opponents,
                          Map<String, Integer> observations, String note) {
            this(id, deckId, versionId, kind, question, started, ended, status, result, opponents, observations, note, List.of());
        }
    }
    public record HandPractice(String testId, String versionId, List<CardCatalog.CardInfo> hand,
                               int libraryCount, int draws, int redraws, String status) {
        public HandPractice { hand = List.copyOf(hand); }
    }
    private record Stored(int schema, TestRecord test, String deckName, String format,
                          List<DeckEditor.Entry> entries, String gameId,
                          List<CardCatalog.CardInfo> library, List<CardCatalog.CardInfo> hand) {
        Stored { entries = List.copyOf(entries); library = List.copyOf(library); hand = List.copyOf(hand); }
    }

    private final Path directory;
    private final Map<String, Stored> records = new LinkedHashMap<>();
    private final Map<String, String> activeHands = new HashMap<>();
    private String activeGame;
    private String persistenceError;

    public DeckPlaytests(Path deckDirectory) {
        directory = deckDirectory.resolve("workshop-tests");
        try {
            Files.createDirectories(directory);
            try (var paths = Files.list(directory)) {
                for (Path path : paths.filter(p -> p.getFileName().toString().endsWith(".json")).toList()) {
                    var saved = JSON.fromJson(Files.readString(path), Stored.class);
                    if (saved == null || saved.schema() != 1 || saved.test() == null
                            || !path.getFileName().toString().equals(saved.test().id() + ".json"))
                        throw new IOException("Unsupported playtest record: " + path.getFileName());
                    records.put(saved.test().id(), saved);
                }
            }
            // An engine restart cannot resume a game. Preserve partial facts and
            // distinguish them from completed tests instead of inventing results.
            for (var saved : List.copyOf(records.values())) if (saved.test().status().equals("running"))
                replace(saved, "interrupted", null, saved.test().observations(), saved.library(), saved.hand());
        } catch (IOException | RuntimeException error) { persistenceError = "Playtest history could not be loaded: " + error.getMessage(); }
    }

    public synchronized TestRecord begin(String deckId, String versionId, String deckName, String format,
                                          List<DeckEditor.Entry> entries, String kind, String question, List<String> opponents) {
        checkStorage();
        if (!Set.of("game", "hand").contains(kind)) throw new IllegalArgumentException("Unknown test kind");
        if (deckId == null || deckId.isBlank() || versionId == null || versionId.isBlank())
            throw new IllegalArgumentException("A saved deck version is required");
        if (question != null && question.length() > 1000) throw new IllegalArgumentException("Test question must be at most 1000 characters");
        if (kind.equals("game")) interruptActive();
        var test = new TestRecord(UUID.randomUUID().toString(), deckId, versionId, kind,
                Objects.requireNonNullElse(question, ""), System.currentTimeMillis(), null, "running", null,
                opponents, Map.of(), "");
        var saved = new Stored(1, test, deckName, format, entries, null, List.of(), List.of());
        persist(saved);
        if (kind.equals("game")) activeGame = test.id();
        return test;
    }

    public void attach(String testId, MatchSession session) {
        synchronized (this) {
            var saved = require(testId);
            if (!saved.test().kind().equals("game") || !saved.test().status().equals("running"))
                throw new IllegalStateException("This game test is no longer active");
        }
        // Never hold the store lock while taking the game's gate: input-boundary
        // callbacks take the locks in the reverse direction.
        session.observePlaytest(frame -> checkpoint(testId, frame));
    }

    synchronized void checkpoint(String testId, MatchSession.PlaytestFrame frame) {
        var saved = records.get(testId);
        if (saved == null || TERMINAL.contains(saved.test().status())) return;
        var status = frame.status();
        if (!status.equals("running") && !TERMINAL.contains(status)) return;
        if (saved.test().observations().equals(frame.observations()) && saved.test().status().equals(status)
                && Objects.equals(saved.gameId(), frame.gameId())) return;
        var withGame = new Stored(1, saved.test(), saved.deckName(), saved.format(), saved.entries(), frame.gameId(), saved.library(), saved.hand());
        try { replace(withGame, status, frame.result(), frame.observations(), saved.library(), saved.hand()); }
        catch (RuntimeException failure) {
            // Disk trouble must be reported by history/note RPCs, not abort a
            // rules decision or turn an otherwise valid game into an engine error.
            persistenceError = "Playtest observations could not be saved: " + failure.getMessage();
        }
    }

    public synchronized TestRecord finish(String testId, String status, String result) {
        if (!TERMINAL.contains(status)) throw new IllegalArgumentException("Choose a completed test status");
        var saved = require(testId);
        if (TERMINAL.contains(saved.test().status())) return exported(saved);
        return exported(replace(saved, status, result, saved.test().observations(), saved.library(), saved.hand()));
    }

    public synchronized void interruptActive() {
        if (activeGame != null) {
            var saved = records.get(activeGame);
            if (saved != null && saved.test().status().equals("running")) finish(activeGame, "interrupted", null);
            activeGame = null;
        }
    }

    public synchronized List<TestRecord> list(String deckId) {
        checkStorage();
        return records.values().stream().map(DeckPlaytests::exported).filter(t -> t.deckId().equals(deckId))
                .sorted(Comparator.comparingLong(TestRecord::started).reversed().thenComparing(TestRecord::id)).toList();
    }

    public synchronized TestRecord get(String deckId, String testId) { return exported(requireDeck(deckId, testId)); }

    public synchronized TestRecord note(String deckId, String testId, String note) {
        var saved = requireDeck(deckId, testId);
        if (note == null || note.length() > 10000) throw new IllegalArgumentException("Test note must be at most 10000 characters");
        var t = saved.test();
        var updated = new TestRecord(t.id(), t.deckId(), t.versionId(), t.kind(), t.question(), t.started(), t.ended(),
                t.status(), t.result(), t.opponents(), t.observations(), note);
        var next = new Stored(1, updated, saved.deckName(), saved.format(), saved.entries(), saved.gameId(), saved.library(), saved.hand());
        persist(next);
        return exported(next);
    }

    public synchronized HandPractice newHand(String deckId, String versionId, String deckName, String format,
                                               List<DeckEditor.Entry> entries, String question) {
        String prior = activeHands.get(deckId);
        if (prior != null) finish(prior, "completed", null);
        var library = new ArrayList<CardCatalog.CardInfo>();
        for (var entry : entries) if (entry.section().equals("Main")) {
            if (entry.quantity() < 0 || entry.quantity() > 1000 || library.size() + entry.quantity() > 10000)
                throw new IllegalArgumentException("The sample library is too large");
            for (int i = 0; i < entry.quantity(); i++) library.add(entry.card());
        }
        if (library.isEmpty()) throw new IllegalStateException("Add cards to the main deck before drawing a hand");
        Collections.shuffle(library);
        var hand = take(library, Math.min(7, library.size()));
        var test = begin(deckId, versionId, deckName, format, entries, "hand", question, List.of());
        var saved = require(test.id());
        saved = replace(saved, "running", null, handObservations(hand, 0, 0), library, hand);
        activeHands.put(deckId, test.id());
        return handView(saved);
    }

    public synchronized HandPractice activeHand(String deckId) {
        String id = activeHands.get(deckId);
        return id == null ? null : handView(require(id));
    }

    /** A draw-only sampler. Redraws do not claim to implement London mulligans. */
    public synchronized HandPractice practice(String testId, String action, Integer index) {
        var saved = require(testId);
        if (!saved.test().kind().equals("hand")) throw new IllegalArgumentException("This is not a hand sample");
        if (!saved.test().status().equals("running")) throw new IllegalStateException("This hand is saved. Draw a new hand to continue sampling");
        var hand = new ArrayList<>(saved.hand());
        var library = new ArrayList<>(saved.library());
        int draws = saved.test().observations().getOrDefault("draws", 0);
        int redraws = saved.test().observations().getOrDefault("redraws", 0);
        String status = "running";
        switch (action) {
            case "draw" -> { if (library.isEmpty()) throw new IllegalStateException("The sample library is empty"); hand.add(library.remove(0)); draws++; }
            case "mulligan" -> { library.addAll(hand); Collections.shuffle(library); hand = take(library, Math.min(7, library.size())); draws = 0; redraws++; }
            case "bottom" -> {
                if (index == null || index < 0 || index >= hand.size()) throw new IllegalArgumentException("Choose a card in this sample hand");
                library.add(hand.remove(index.intValue()));
            }
            case "keep" -> status = "completed";
            default -> throw new IllegalArgumentException("Unknown hand sampling action");
        }
        return handView(replace(saved, status, null, handObservations(hand, draws, redraws), library, hand));
    }

    private static Map<String, Integer> handObservations(List<CardCatalog.CardInfo> hand, int draws, int redraws) {
        // Modal cards have choices; do not add both faces as simultaneous lands.
        int lands = (int) hand.stream().filter(c -> c.type().toLowerCase(Locale.ROOT).contains("land")).count();
        return Map.of("handCards", hand.size(), "handLands", lands, "draws", draws, "redraws", redraws);
    }

    private static ArrayList<CardCatalog.CardInfo> take(ArrayList<CardCatalog.CardInfo> library, int count) {
        var hand = new ArrayList<>(library.subList(0, count));
        library.subList(0, count).clear();
        return hand;
    }

    private HandPractice handView(Stored saved) {
        var observations = saved.test().observations();
        return new HandPractice(saved.test().id(), saved.test().versionId(), saved.hand(), saved.library().size(),
                observations.getOrDefault("draws", 0), observations.getOrDefault("redraws", 0), saved.test().status());
    }

    private static TestRecord exported(Stored saved) {
        var t = saved.test();
        return new TestRecord(t.id(), t.deckId(), t.versionId(), t.kind(), t.question(), t.started(), t.ended(),
                t.status(), t.result(), t.opponents(), t.observations(), t.note(),
                t.kind().equals("hand") ? saved.hand() : List.of());
    }

    private Stored replace(Stored saved, String status, String result, Map<String, Integer> observations,
                           List<CardCatalog.CardInfo> library, List<CardCatalog.CardInfo> hand) {
        var t = saved.test();
        var updated = new TestRecord(t.id(), t.deckId(), t.versionId(), t.kind(), t.question(), t.started(),
                TERMINAL.contains(status) ? Objects.requireNonNullElse(t.ended(), System.currentTimeMillis()) : null,
                status, result, t.opponents(), observations, t.note());
        var next = new Stored(1, updated, saved.deckName(), saved.format(), saved.entries(), saved.gameId(), library, hand);
        persist(next);
        return next;
    }

    private Stored requireDeck(String deckId, String testId) {
        var saved = require(testId);
        if (!saved.test().deckId().equals(deckId)) throw new IllegalArgumentException("This test belongs to another deck");
        return saved;
    }

    private Stored require(String testId) {
        checkStorage();
        var saved = records.get(testId);
        if (saved == null) throw new IllegalArgumentException("This playtest was not found");
        return saved;
    }

    private void checkStorage() { if (persistenceError != null) throw new IllegalStateException(persistenceError); }

    private void persist(Stored saved) {
        Path target = directory.resolve(saved.test().id() + ".json");
        Path temporary = directory.resolve(saved.test().id() + ".tmp");
        try {
            Files.writeString(temporary, JSON.toJson(saved), StandardCharsets.UTF_8);
            try { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE); }
            catch (AtomicMoveNotSupportedException ignored) { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING); }
            records.put(saved.test().id(), saved);
        } catch (IOException failure) { throw new IllegalStateException("Playtest could not be saved: " + failure.getMessage(), failure); }
    }
}
