package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.UUID;

/** Separate metadata keeps analysis decisions out of playable deck files and undo history. */
final class DeckAnalysisStore {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    record Entry(String id, String fingerprint, String recommendationId, String decision, String note, long created) { }
    record View(long metadataRevision, DeckAnalysis.Report report, List<Entry> history) { }
    private static final class Document {
        int schema = 1;
        long revision;
        DeckAnalysis.Profile profile = DeckAnalysis.Profile.defaults();
        List<Entry> history = new ArrayList<>();
    }
    private final Path directory;
    private final CardCatalog catalog;
    DeckAnalysisStore(Path decks, CardCatalog catalog) { directory = decks.resolve("analysis"); this.catalog = catalog; }

    synchronized View view(String id, DeckEditor.Snapshot deck, String format, DeckAnalysis.Eligibility eligibility) throws IOException {
        var document = read(id);
        return view(id, deck, format, document, eligibility);
    }
    private View view(String id, DeckEditor.Snapshot deck, String format, Document document, DeckAnalysis.Eligibility eligibility) {
        return new View(document.revision, DeckAnalysis.analyze(catalog, deck, format, id, fingerprint(deck, format), document.profile, eligibility), List.copyOf(document.history));
    }
    synchronized View profile(String id, DeckEditor.Snapshot deck, String format, long revision,
                              JsonObject profile, DeckAnalysis.Eligibility eligibility) throws IOException {
        var document = read(id); requireRevision(document, revision);
        var candidate = JSON.fromJson(profile, DeckAnalysis.Profile.class);
        if (candidate == null) throw new IllegalArgumentException("Missing analysis profile");
        // Validate the full report before committing a changed profile.
        DeckAnalysis.analyze(catalog, deck, format, id, fingerprint(deck, format), candidate, eligibility);
        if (!candidate.equals(document.profile)) { document.profile = candidate; document.revision++; write(id, document); }
        return view(id, deck, format, document, eligibility);
    }
    synchronized View review(String id, DeckEditor.Snapshot deck, String format, long revision, String fingerprint,
                             String recommendationId, String decision, String note, DeckAnalysis.Eligibility eligibility) throws IOException {
        var document = read(id); requireRevision(document, revision);
        if (!fingerprint(deck, format).equals(fingerprint)) throw new IllegalArgumentException("The deck changed. Refresh its analysis before recording a decision.");
        if (!List.of("consider", "keep", "dismiss", "tested", "observation").contains(decision)) throw new IllegalArgumentException("Unknown review decision");
        if (note == null || note.length() > 2000) throw new IllegalArgumentException("Review note must be at most 2000 characters");
        if (decision.equals("observation")) {
            if (!recommendationId.isEmpty() || note.isBlank()) throw new IllegalArgumentException("A playtest observation needs a note and no recommendation ID");
        } else if (view(id, deck, format, document, eligibility).report().recommendations().stream().noneMatch(r -> r.id().equals(recommendationId)))
            throw new IllegalArgumentException("That recommendation is no longer available. Refresh the analysis.");
        document.history.add(new Entry(UUID.randomUUID().toString(), fingerprint, recommendationId, decision, note.strip(), System.currentTimeMillis()));
        // Bound local metadata; retained events keep their deck fingerprints and timestamps.
        if (document.history.size() > 2000) document.history = new ArrayList<>(document.history.subList(document.history.size() - 2000, document.history.size()));
        document.revision++; write(id, document);
        return view(id, deck, format, document, eligibility);
    }
    private static void requireRevision(Document document, long revision) {
        if (document.revision != revision) throw new IllegalArgumentException("Analysis notes changed. Refresh before saving.");
    }
    private Path file(String id) {
        if (id == null || !UUID.fromString(id).toString().equals(id)) throw new IllegalArgumentException("Invalid deck ID");
        return directory.resolve(id + ".json");
    }
    private Document read(String id) throws IOException {
        Path path = file(id);
        if (!Files.exists(path)) return new Document();
        if (Files.size(path) > 10_000_000) throw new IOException("Analysis notes are too large to read");
        try {
            var document = JSON.fromJson(Files.readString(path, StandardCharsets.UTF_8), Document.class);
            if (document == null || document.schema != 1 || document.profile == null || document.history == null)
                throw new IOException("Unsupported or incomplete analysis notes; the saved file was preserved");
            return document;
        } catch (com.google.gson.JsonParseException error) { throw new IOException("Could not read analysis notes; the saved file was preserved", error); }
    }
    private void write(String id, Document document) throws IOException {
        Files.createDirectories(directory);
        Path target = file(id), temporary = Files.createTempFile(directory, id + "-", ".tmp");
        try {
            Files.writeString(temporary, JSON.toJson(document), StandardCharsets.UTF_8);
            try { Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
            catch (AtomicMoveNotSupportedException error) { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING); }
        } finally { Files.deleteIfExists(temporary); }
    }
    static String fingerprint(DeckEditor.Snapshot deck, String format) {
        var canonical = deck.entries().stream().map(e -> e.section() + ":" + e.card().id() + ":" + e.quantity()).sorted().toList();
        try { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(JSON.toJson(List.of(format, canonical)).getBytes(StandardCharsets.UTF_8))); }
        catch (java.security.NoSuchAlgorithmException impossible) { throw new IllegalStateException(impossible); }
    }
}
