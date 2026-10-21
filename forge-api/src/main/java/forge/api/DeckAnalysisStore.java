package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.List;
import java.util.TreeSet;
import java.util.UUID;

/** Analysis shares workshop goals and metadata transactions; legacy sidecars remain readable. */
final class DeckAnalysisStore {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    record Entry(String id, String fingerprint, String recommendationId, String decision, String note, long created) { }
    record View(long metadataRevision, DeckAnalysis.Report report, List<Entry> history, List<Entry> decisions) { }
    private static final class Document {
        int schema = 1;
        long revision;
        DeckAnalysis.Profile profile = DeckAnalysis.Profile.defaults();
        List<Entry> history = new ArrayList<>();
    }
    private final Path directory;
    private final CardCatalog catalog;
    private final DeckWorkshopStore workshop;
    private record Loaded(Document document, DeckWorkshopStore.AnalysisContext workshop) { }
    DeckAnalysisStore(Path decks, CardCatalog catalog) { this(decks, catalog, null); }
    DeckAnalysisStore(Path decks, CardCatalog catalog, DeckWorkshopStore workshop) {
        directory = decks.resolve("analysis"); this.catalog = catalog; this.workshop = workshop;
    }

    synchronized View view(String id, DeckEditor.Snapshot deck, String format, DeckAnalysis.Eligibility eligibility) throws IOException {
        return view(id, deck, format, load(id), eligibility);
    }
    private View view(String id, DeckEditor.Snapshot deck, String format, Loaded loaded, DeckAnalysis.Eligibility eligibility) {
        String fingerprint = fingerprint(deck, format);
        var latest = new java.util.LinkedHashMap<String, Entry>();
        for (var item : loaded.document().history)
            if (item.fingerprint().equals(fingerprint) && !item.recommendationId().isEmpty()) latest.put(item.recommendationId(), item);
        return new View(revision(loaded), DeckAnalysis.analyze(catalog, deck, format, id, fingerprint, effective(loaded, format), eligibility),
                List.copyOf(loaded.document().history), List.copyOf(latest.values()));
    }
    synchronized View profile(String id, DeckEditor.Snapshot deck, String format, long revision,
                              JsonObject profile, DeckAnalysis.Eligibility eligibility) throws IOException {
        return profile(id, deck, format, revision, fingerprint(deck, format), profile, eligibility);
    }
    synchronized View profile(String id, DeckEditor.Snapshot deck, String format, long revision, String fingerprint,
                              JsonObject profile, DeckAnalysis.Eligibility eligibility) throws IOException {
        var loaded = load(id); requireRevision(loaded, revision); requireFingerprint(deck, format, fingerprint);
        var candidate = JSON.fromJson(profile, DeckAnalysis.Profile.class);
        if (candidate == null) throw new IllegalArgumentException("Missing analysis profile");
        // Validate the full report before committing a changed profile.
        DeckAnalysis.analyze(catalog, deck, format, id, fingerprint(deck, format), candidate, eligibility);
        var saved = loaded.workshop() == null ? candidate : storedProfile(candidate);
        var document = loaded.document();
        if (!saved.equals(document.profile)) { document.profile = saved; document.revision++; }
        if (loaded.workshop() != null) workshop.analysisWrite(id, deck, revision, JSON.toJsonTree(document).getAsJsonObject(), candidate.plan(), candidate.protectedCards(), null);
        else write(id, document);
        return view(id, deck, format, eligibility);
    }
    synchronized View review(String id, DeckEditor.Snapshot deck, String format, long revision, String fingerprint,
                             String recommendationId, String decision, String note, DeckAnalysis.Eligibility eligibility) throws IOException {
        var loaded = load(id); requireRevision(loaded, revision); requireFingerprint(deck, format, fingerprint);
        if (!List.of("consider", "keep", "dismiss", "reopen", "tested", "observation").contains(decision)) throw new IllegalArgumentException("Unknown review decision");
        if (note == null || note.length() > 2000) throw new IllegalArgumentException("Review note must be at most 2000 characters");
        DeckAnalysis.Recommendation recommendation = null;
        if (decision.equals("observation")) {
            if (!recommendationId.isEmpty() || note.isBlank()) throw new IllegalArgumentException("A playtest observation needs a note and no recommendation ID");
        } else recommendation = view(id, deck, format, loaded, eligibility).report().recommendations().stream().filter(item -> item.id().equals(recommendationId)).findFirst()
                .orElseThrow(() -> new IllegalArgumentException("That recommendation is no longer available. Refresh the analysis."));
        var document = loaded.document();
        var migrated = loaded.workshop() != null && loaded.workshop().analysis() == null ? effective(loaded, format) : null;
        if (migrated != null) document.profile = storedProfile(migrated);
        document.history.add(new Entry(UUID.randomUUID().toString(), fingerprint, recommendationId, decision, note.strip(), System.currentTimeMillis()));
        // Bound local metadata; retained events keep their deck fingerprints and timestamps.
        if (document.history.size() > 2000) document.history = new ArrayList<>(document.history.subList(document.history.size() - 2000, document.history.size()));
        document.revision++;
        if (loaded.workshop() != null) workshop.analysisWrite(id, deck, revision, JSON.toJsonTree(document).getAsJsonObject(), migrated == null ? null : migrated.plan(), migrated == null ? null : migrated.protectedCards(),
                decision.equals("consider") ? recommendation.add().id() : null);
        else write(id, document);
        return view(id, deck, format, eligibility);
    }
    private static void requireRevision(Loaded loaded, long revision) {
        if (revision(loaded) != revision) throw new IllegalArgumentException("Deck preferences changed. Refresh before saving the analysis.");
    }
    private static void requireFingerprint(DeckEditor.Snapshot deck, String format, String fingerprint) {
        if (!fingerprint(deck, format).equals(fingerprint)) throw new IllegalArgumentException("The deck changed. Refresh its analysis before saving.");
    }
    private static long revision(Loaded loaded) { return loaded.workshop() == null ? loaded.document().revision : loaded.workshop().metadataRevision(); }
    private DeckAnalysis.Profile storedProfile(DeckAnalysis.Profile profile) {
        return new DeckAnalysis.Profile(profile.rulesFormat(), profile.strategy(), "", profile.backup(), profile.pace(), profile.targetTurn(),
                profile.protectedCards().stream().filter(name -> catalog.named(name) == null).toList(), profile.pool());
    }
    private DeckAnalysis.Profile effective(Loaded loaded, String format) {
        var profile = loaded.document().profile;
        String rules = profile.rulesFormat();
        if ((List.of("Commander", "Limited").contains(format) || List.of("Commander", "Limited").contains(rules)) && !rules.equals(format)) rules = "";
        if (loaded.workshop() == null) return new DeckAnalysis.Profile(rules, profile.strategy(), profile.plan(), profile.backup(), profile.pace(), profile.targetTurn(), profile.protectedCards(), profile.pool());
        var protectedNames = new TreeSet<String>(String.CASE_INSENSITIVE_ORDER);
        boolean legacyAuthority = loaded.workshop().analysis() == null && loaded.workshop().metadataRevision() > 0;
        profile.protectedCards().stream().filter(name -> !legacyAuthority || catalog.named(name) == null).forEach(protectedNames::add);
        protectedNames.addAll(loaded.workshop().protectedCards());
        String plan = loaded.workshop().metadataRevision() == 0 && loaded.workshop().plan().isEmpty() ? profile.plan() : loaded.workshop().plan();
        return new DeckAnalysis.Profile(rules, profile.strategy(), plan, profile.backup(), profile.pace(), profile.targetTurn(), List.copyOf(protectedNames), profile.pool());
    }
    private Path file(String id) {
        if (id == null || !UUID.fromString(id).toString().equals(id)) throw new IllegalArgumentException("Invalid deck ID");
        return directory.resolve(id + ".json");
    }
    private Loaded load(String id) throws IOException {
        var context = workshop == null ? null : workshop.analysisContext(id);
        if (context != null && context.analysis() != null) return new Loaded(parse(context.analysis()), context);
        Path path = file(id);
        if (!Files.exists(path)) return new Loaded(new Document(), context);
        if (Files.size(path) > 10_000_000) throw new IOException("Analysis notes are too large to read");
        try {
            return new Loaded(parse(JsonParser.parseString(Files.readString(path, StandardCharsets.UTF_8)).getAsJsonObject()), context);
        } catch (RuntimeException error) { throw new IOException("Could not read analysis notes; the saved file was preserved", error); }
    }
    private static Document parse(JsonObject json) throws IOException {
        try {
            if (!json.has("schema") || !json.has("profile") || !json.has("history")) throw new IOException("Incomplete analysis notes; the saved file was preserved");
            var document = JSON.fromJson(json, Document.class);
            if (document.schema != 1 || document.profile == null || document.history == null || document.revision < 0)
                throw new IOException("Unsupported or incomplete analysis notes; the saved file was preserved");
            return document;
        } catch (RuntimeException error) { throw new IOException("Could not read analysis notes; the saved file was preserved", error); }
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
        return DeckWorkshopStore.fingerprint(deck, format);
    }
}
