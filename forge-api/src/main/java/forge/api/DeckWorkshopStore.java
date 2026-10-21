package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.deck.Deck;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.UUID;

/** Durable workshop data is separate from legacy deck files and from transient edit revisions. */
final class DeckWorkshopStore {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    record Role(String cardId, List<String> roles, boolean pinned, String origin) { }
    record ConsideringEdit(String cardId, int quantity, List<String> categories) { }
    record Considering(String cardId, CardCatalog.CardInfo card, int quantity, List<String> categories) { }
    record Version(String id, String label, long created, int mainCount, int commanderCount,
                   String name, String format, List<DeckEditor.Edit> cards) { }
    record Source(String url, String remoteId, String name, String updatedAt, long lastChecked, String status) { }
    record Insights(Map<String, Integer> roleCounts, int[] curve, Map<String, Integer> coloredSources, String note) { }
    record View(String deckId, long revision, long metadataRevision, String versionId, String plan, String question,
                List<Role> roles, List<Considering> considering, List<Version> versions, Source source, Insights insights) { }
    record Change(String key, String card, String section, int baseline, int local, int remote, boolean conflict) { }
    record Preview(String token, String url, String name, String format, List<ArchidektSource.Row> cards,
                   List<String> problems, List<String> warnings, List<Change> changes, boolean hasChanges) { }
    record Applied(DeckEditor editor, String format, String deckId) { }
    record AnalysisContext(long metadataRevision, String plan, List<String> protectedCards, JsonObject analysis) { }
    private record Pending(Preview preview, ArchidektSource.Remote remote, String deckId, long revision,
                           long metadataRevision, String localSignature, long created) { }
    private record PoolDelta(String cardId, int quantity, List<String> beforeCategories, List<String> afterCategories) { }
    private record Transition(String beforeToken, String afterToken, String beforeFormat, String afterFormat, List<PoolDelta> deltas) { }
    private static final class Document {
        int schema = 1;
        long metadataRevision;
        String plan = "";
        String question = "";
        List<Role> roles = new ArrayList<>();
        List<ConsideringEdit> considering = new ArrayList<>();
        List<Version> versions = new ArrayList<>();
        Source source;
        ArchidektSource.Remote baseline;
        com.google.gson.JsonElement analysis;
    }
    private final Path directory;
    private final Path decks;
    private final CardCatalog catalog;
    private final Map<String, Pending> previews = new LinkedHashMap<>();
    private final Map<String, List<Transition>> history = new LinkedHashMap<>();

    DeckWorkshopStore(Path decks, CardCatalog catalog) throws IOException {
        this.decks = decks;
        directory = decks.resolve("workshop");
        this.catalog = catalog;
        Files.createDirectories(directory);
        recover();
    }

    synchronized String ensureVersion(String deckId, DeckEditor.Snapshot snapshot, String format) throws IOException {
        Document document = read(deckId);
        String id = fingerprint(snapshot, format);
        if (document.versions.stream().noneMatch(version -> version.id().equals(id))) {
            document.versions.add(new Version(id, "v" + (document.versions.size() + 1), System.currentTimeMillis(),
                    count(snapshot, "Main"), count(snapshot, "Commander"), snapshot.name(), format,
                    snapshot.entries().stream().map(entry -> new DeckEditor.Edit(entry.section(), entry.card().id(), entry.quantity())).toList()));
            write(deckId, document);
        }
        return id;
    }

    synchronized View view(String deckId, DeckEditor.Snapshot snapshot, String format) throws IOException {
        String id = ensureVersion(deckId, snapshot, format);
        Document document = read(deckId);
        var roles = effectiveRoles(document, snapshot);
        var considering = document.considering.stream().map(row -> new Considering(row.cardId(), CardCatalog.describe(catalog.resolve(row.cardId())),
                row.quantity(), row.categories())).toList();
        return new View(deckId, snapshot.revision(), document.metadataRevision, id, document.plan, document.question,
                roles, considering, List.copyOf(document.versions), document.source, insights(snapshot, roles));
    }

    synchronized View metadata(String deckId, DeckEditor.Snapshot snapshot, String format, JsonObject patch) throws IOException {
        ensureVersion(deckId, snapshot, format);
        var document = read(deckId);
        if (patch.has("metadataRevision") && !patch.get("metadataRevision").isJsonNull()
                && patch.get("metadataRevision").getAsLong() != document.metadataRevision)
            throw new IllegalArgumentException("Workshop notes changed. Refresh before saving.");
        String before = JSON.toJson(document);
        if (patch.has("plan")) document.plan = checkedText(patch.get("plan").getAsString(), 2000, "Deck plan");
        if (patch.has("question")) document.question = checkedText(patch.get("question").getAsString(), 500, "Test question");
        if (patch.has("roles")) {
            var rows = JSON.fromJson(patch.get("roles"), Role[].class);
            if (rows.length > 2000) throw new IllegalArgumentException("Too many card roles");
            var unique = new LinkedHashMap<String, Role>();
            for (var row : rows) {
                catalog.resolve(row.cardId());
                if ("estimated".equals(row.origin()) && !row.pinned()) continue;
                unique.put(row.cardId(), new Role(row.cardId(), checkedTags(row.roles()), row.pinned(),
                        "Archidekt".equals(row.origin()) ? "Archidekt" : "local"));
            }
            document.roles = new ArrayList<>(unique.values());
        }
        if (patch.has("considering")) {
            var rows = JSON.fromJson(patch.get("considering"), ConsideringEdit[].class);
            if (rows.length > 2000) throw new IllegalArgumentException("Too many Considering cards");
            var unique = new LinkedHashMap<String, ConsideringEdit>();
            for (var row : rows) {
                catalog.resolve(row.cardId());
                if (row.quantity() < 0 || row.quantity() > 1000) throw new IllegalArgumentException("Considering quantity must be 0..1000");
                if (row.quantity() > 0) unique.put(row.cardId(), new ConsideringEdit(row.cardId(), row.quantity(), checkedTags(row.categories())));
            }
            document.considering = new ArrayList<>(unique.values());
        }
        if (!before.equals(JSON.toJson(document))) { document.metadataRevision++; write(deckId, document); }
        return view(deckId, snapshot, format);
    }

    synchronized AnalysisContext analysisContext(String deckId) throws IOException {
        var document = read(deckId);
        var protectedCards = new java.util.TreeSet<String>(String.CASE_INSENSITIVE_ORDER);
        for (var role : document.roles) if (role.pinned()) protectedCards.add(CardCatalog.describe(catalog.resolve(role.cardId())).name());
        return new AnalysisContext(document.metadataRevision, document.plan, List.copyOf(protectedCards),
                document.analysis == null || document.analysis.isJsonNull() ? null : document.analysis.getAsJsonObject().deepCopy());
    }

    /** Analysis goals, favorites and a considered card are one metadata transaction, outside playable contents. */
    synchronized void analysisWrite(String deckId, DeckEditor.Snapshot snapshot, long metadataRevision, JsonObject analysis, String plan,
                                    List<String> protectedCards, String consideredCardId) throws IOException {
        var document = read(deckId);
        if (document.metadataRevision != metadataRevision) throw new IllegalArgumentException("Deck preferences changed. Refresh the analysis before saving.");
        String before = JSON.toJson(document);
        document.analysis = analysis.deepCopy();
        if (plan != null) document.plan = checkedText(plan, 2000, "Deck plan");
        if (protectedCards != null) {
            var names = new java.util.TreeSet<String>(String.CASE_INSENSITIVE_ORDER);
            for (String name : protectedCards) names.add(catalog.named(name) == null ? name : catalog.named(name).name());
            var roles = new LinkedHashMap<String, Role>();
            var effective = new LinkedHashMap<String, Role>(); effectiveRoles(document, snapshot).forEach(role -> effective.put(role.cardId(), role));
            for (var role : document.roles) {
                String name = CardCatalog.describe(catalog.resolve(role.cardId())).name();
                boolean pinned = names.contains(name);
                boolean changed = pinned != role.pinned();
                roles.put(role.cardId(), new Role(role.cardId(), changed ? effective.get(role.cardId()).roles() : role.roles(), pinned, changed ? "local" : role.origin()));
            }
            for (String name : names) {
                var cards = new LinkedHashMap<String, CardCatalog.CardInfo>();
                snapshot.entries().stream().map(DeckEditor.Entry::card).filter(card -> card.name().equalsIgnoreCase(name)).forEach(card -> cards.put(card.id(), card));
                for (var row : document.considering) {
                    var card = CardCatalog.describe(catalog.resolve(row.cardId()));
                    if (card.name().equalsIgnoreCase(name)) cards.put(card.id(), card);
                }
                if (cards.isEmpty() && catalog.named(name) != null) cards.put(catalog.named(name).id(), catalog.named(name));
                for (var card : cards.values()) if (!roles.containsKey(card.id())) roles.put(card.id(), new Role(card.id(), DeckInsights.roles(card), true, "local"));
            }
            document.roles = new ArrayList<>(roles.values());
        }
        if (consideredCardId != null) {
            catalog.resolve(consideredCardId);
            if (document.considering.stream().noneMatch(row -> row.cardId().equals(consideredCardId)))
                document.considering.add(new ConsideringEdit(consideredCardId, 1, List.of()));
        }
        if (!before.equals(JSON.toJson(document))) { document.metadataRevision++; write(deckId, document); }
    }

    synchronized DeckVersionView inspect(String deckId, String versionId, DeckEditor.Snapshot current, String format) throws IOException {
        var version = read(deckId).versions.stream().filter(v -> v.id().equals(versionId)).findFirst()
                .orElseThrow(() -> new IllegalArgumentException("That saved deck version is unavailable"));
        var entries = version.cards().stream().map(e -> new DeckEditor.Entry(e.section(),
                CardCatalog.describe(catalog.resolve(e.cardId())), e.quantity())).toList();
        return DeckVersionView.compare(version.id(), version.name(), version.format(), entries, current, format);
    }

    synchronized void replacePrinting(String deckId, DeckEditor editor, String format, String section, String cardId, String printingId) throws IOException {
        var candidate = new DeckEditor(catalog, editor.toDeck());
        candidate.replacePrinting(0, section, cardId, printingId);
        if (cardId.equals(printingId)) return;
        var document = read(deckId);
        // Keep the old annotation for Undo and other sections using that printing.
        var source = document.roles.stream().filter(row -> row.cardId().equals(cardId)).findFirst();
        if (source.isPresent()) {
            var previous = source.get();
            var target = document.roles.stream().filter(row -> row.cardId().equals(printingId)).findFirst();
            var categories = new LinkedHashSet<String>();
            target.ifPresent(role -> categories.addAll(role.roles()));
            categories.addAll(previous.roles());
            var copied = new Role(printingId, checkedTags(List.copyOf(categories)),
                    previous.pinned() || target.map(Role::pinned).orElse(false),
                    previous.origin().equals("local") || target.map(role -> role.origin().equals("local")).orElse(false) ? "local" : previous.origin());
            if (target.isEmpty() || !target.get().equals(copied)) {
                document.roles.removeIf(role -> role.cardId().equals(printingId));
                document.roles.add(copied);
                document.metadataRevision++;
            }
        }
        commit(deckId, candidate.snapshot(), format, document);
        editor.replacePrinting(editor.snapshot().revision(), section, cardId, printingId);
    }

    synchronized Applied restore(String deckId, DeckEditor editor, String format, String versionId) throws IOException {
        var document = read(deckId);
        var version = document.versions.stream().filter(item -> item.id().equals(versionId)).findFirst()
                .orElseThrow(() -> new IllegalArgumentException("That saved deck version is unavailable"));
        var candidate = new DeckEditor(catalog, editor.toDeck());
        replace(candidate, version.cards());
        commit(deckId, candidate.snapshot(), version.format(), document);
        String beforeToken = editor.historyToken();
        replace(editor, version.cards());
        if (beforeToken.equals(editor.historyToken()) && !format.equals(version.format())) editor.checkpoint(editor.snapshot().revision());
        remember(deckId, beforeToken, editor.historyToken(), format, version.format(), document.considering, document.considering);
        return new Applied(editor, version.format(), deckId);
    }

    synchronized Applied history(String deckId, DeckEditor editor, String format, String action, long revision) throws IOException {
        if (editor.snapshot().revision() != revision) throw new IllegalArgumentException("The deck changed. Refresh before undoing.");
        boolean undo = action.equals("undo");
        if (!undo && !action.equals("redo")) throw new IllegalArgumentException("Choose undo or redo");
        String current = editor.historyToken(), target = undo ? editor.undoToken() : editor.redoToken();
        var document = read(deckId);
        String restoredFormat = format;
        for (var transition : history.getOrDefault(deckId, List.of())) {
            if (!(undo ? transition.afterToken().equals(current) && transition.beforeToken().equals(target)
                    : transition.beforeToken().equals(current) && transition.afterToken().equals(target))) continue;
            var considering = new LinkedHashMap<String, ConsideringEdit>(); document.considering.forEach(row -> considering.put(row.cardId(), row));
            for (var delta : transition.deltas()) {
                var row = considering.get(delta.cardId());
                int next = (row == null ? 0 : row.quantity()) + (undo ? -delta.quantity() : delta.quantity());
                if (next < 0 || next > 1000) throw new IllegalArgumentException("Considering changed for a card in that move. Adjust its quantity before undoing or redoing.");
                if (next == 0) considering.remove(delta.cardId());
                else considering.put(delta.cardId(), new ConsideringEdit(delta.cardId(), next, row == null
                        ? (undo ? delta.beforeCategories() : delta.afterCategories()) : row.categories()));
            }
            document.considering = new ArrayList<>(considering.values());
            if (!transition.deltas().isEmpty()) document.metadataRevision++;
            restoredFormat = undo ? transition.beforeFormat() : transition.afterFormat();
            break;
        }
        var candidate = new DeckEditor(catalog, editor.historyDeck(undo));
        addVersion(document, candidate.snapshot(), restoredFormat);
        commit(deckId, candidate.snapshot(), restoredFormat, document);
        if (undo) editor.undo(revision); else editor.redo(revision);
        return new Applied(editor, restoredFormat, deckId);
    }

    synchronized Applied format(String deckId, DeckEditor editor, String beforeFormat, String afterFormat) throws IOException {
        if (beforeFormat.equals(afterFormat)) return new Applied(editor, afterFormat, deckId);
        var document = read(deckId);
        addVersion(document, editor.snapshot(), afterFormat);
        commit(deckId, editor.snapshot(), afterFormat, document);
        String beforeToken = editor.historyToken();
        editor.checkpoint(editor.snapshot().revision());
        remember(deckId, beforeToken, editor.historyToken(), beforeFormat, afterFormat, document.considering, document.considering);
        return new Applied(editor, afterFormat, deckId);
    }

    synchronized void commanderFormatTransition(String deckId, String beforeToken, DeckEditor editor, String beforeFormat) {
        if (beforeToken.equals(editor.historyToken()) && !beforeFormat.equals("Commander")) editor.checkpoint(editor.snapshot().revision());
        remember(deckId, beforeToken, editor.historyToken(), beforeFormat, "Commander", List.of(), List.of());
    }

    synchronized void duplicate(String originalId, String copyId, DeckEditor.Snapshot snapshot, String format) throws IOException {
        var document = read(originalId);
        document.versions = new ArrayList<>();
        document.metadataRevision = 0;
        if (document.analysis != null && !document.analysis.isJsonNull()) {
            document.analysis.getAsJsonObject().add("history", new com.google.gson.JsonArray());
            document.analysis.getAsJsonObject().addProperty("revision", 0);
        }
        addVersion(document, snapshot, format);
        commit(copyId, snapshot, format, document);
    }

    synchronized Applied move(String deckId, DeckEditor editor, String format, long metadataRevision,
                              String cardId, String to, String cutCardId) throws IOException {
        var document = read(deckId);
        if (document.metadataRevision != metadataRevision) throw new IllegalArgumentException("Considering changed. Refresh before moving cards.");
        if (!List.of("Main", "Considering").contains(to)) throw new IllegalArgumentException("Choose Main or Considering");
        catalog.resolve(cardId);
        var before = editor.snapshot();
        String beforeToken = editor.historyToken();
        var beforeConsidering = List.copyOf(document.considering);
        var cards = new ArrayList<>(before.entries().stream().map(row -> new DeckEditor.Edit(row.section(), row.card().id(), row.quantity())).toList());
        var considering = new LinkedHashMap<String, ConsideringEdit>();
        document.considering.forEach(row -> considering.put(row.cardId(), row));
        int main = before.entries().stream().filter(row -> row.section().equals("Main") && row.card().id().equals(cardId)).mapToInt(DeckEditor.Entry::quantity).sum();
        int quantity;
        if (to.equals("Considering")) {
            if (main == 0) throw new IllegalArgumentException("Choose a card in the main deck");
            quantity = main;
            var old = considering.get(cardId);
            if (main + (old == null ? 0 : old.quantity()) > 1000) throw new IllegalArgumentException("Considering quantity must be 0..1000");
            considering.put(cardId, new ConsideringEdit(cardId, main + (old == null ? 0 : old.quantity()), old == null ? List.of() : old.categories()));
            cards.removeIf(row -> row.section().equals("Main") && row.cardId().equals(cardId));
        } else {
            var source = considering.get(cardId);
            if (source == null) throw new IllegalArgumentException("Choose a card in Considering");
            quantity = cutCardId == null || cutCardId.isBlank() ? source.quantity() : 1;
            if (source.quantity() == quantity) considering.remove(cardId);
            else considering.put(cardId, new ConsideringEdit(cardId, source.quantity() - quantity, source.categories()));
            cards.removeIf(row -> row.section().equals("Main") && row.cardId().equals(cardId));
            cards.add(new DeckEditor.Edit("Main", cardId, main + quantity));
            if (cutCardId != null && !cutCardId.isBlank()) {
                if (cutCardId.equals(cardId)) throw new IllegalArgumentException("Choose a different card to cut");
                catalog.resolve(cutCardId);
                int cutCount = before.entries().stream().filter(row -> row.section().equals("Main") && row.card().id().equals(cutCardId)).mapToInt(DeckEditor.Entry::quantity).sum();
                if (cutCount == 0) throw new IllegalArgumentException("The selected cut is no longer in the main deck");
                cards.removeIf(row -> row.section().equals("Main") && row.cardId().equals(cutCardId));
                if (cutCount > 1) cards.add(new DeckEditor.Edit("Main", cutCardId, cutCount - 1));
                var old = considering.get(cutCardId);
                if (old != null && old.quantity() == 1000) throw new IllegalArgumentException("Considering quantity must be 0..1000");
                considering.put(cutCardId, new ConsideringEdit(cutCardId, 1 + (old == null ? 0 : old.quantity()), old == null ? List.of() : old.categories()));
            }
        }
        var candidate = new DeckEditor(catalog, editor.toDeck());
        replace(candidate, cards);
        document.considering = new ArrayList<>(considering.values());
        document.metadataRevision++;
        addVersion(document, candidate.snapshot(), format);
        commit(deckId, candidate.snapshot(), format, document);
        replace(editor, cards);
        remember(deckId, beforeToken, editor.historyToken(), format, format, beforeConsidering, document.considering);
        return new Applied(editor, format, deckId);
    }

    Preview importPreview(String url) throws Exception { return stage(ArchidektSource.fetch(url, catalog), null, null, null); }

    Preview refresh(String deckId, DeckEditor.Snapshot snapshot, String format) throws Exception {
        var document = read(deckId);
        if (document.source == null) throw new IllegalArgumentException("This deck is not linked to Archidekt");
        try { return stage(ArchidektSource.fetch(document.source.url(), catalog), deckId, snapshot, format); }
        catch (Exception error) {
            synchronized (this) {
                document = read(deckId);
                var old = document.source;
                document.source = new Source(old.url(), old.remoteId(), old.name(), old.updatedAt(), System.currentTimeMillis(), "Check failed; saved deck available offline");
                write(deckId, document);
            }
            throw error;
        }
    }

    /** Staging is also used by deterministic offline tests; no source fetch can mutate deck contents. */
    synchronized Preview stage(ArchidektSource.Remote remote, String deckId, DeckEditor.Snapshot snapshot, String format) throws IOException {
        Document document = deckId == null ? new Document() : read(deckId);
        var baseline = document.baseline == null ? Map.<String, Counted>of() : remoteCounts(document.baseline);
        var local = snapshot == null ? Map.<String, Counted>of() : localCounts(snapshot, document);
        var fetched = remoteCounts(remote);
        var keys = new LinkedHashSet<String>(); keys.addAll(baseline.keySet()); keys.addAll(local.keySet()); keys.addAll(fetched.keySet());
        var changes = new ArrayList<Change>();
        for (String key : keys) {
            int b = quantity(baseline, key), l = quantity(local, key), r = quantity(fetched, key);
            if (b == l && b == r) continue;
            var card = fetched.containsKey(key) ? fetched.get(key) : local.containsKey(key) ? local.get(key) : baseline.get(key);
            boolean conflict = l != b && r != b && l != r;
            changes.add(new Change(key, card.card().name(), card.section(), b, l, r, conflict));
        }
        // A move of one logical card between sections must not duplicate it during a merge.
        var names = keys.stream().map(key -> key.substring(key.indexOf('\n') + 1)).distinct().toList();
        for (String name : names) {
            var related = changes.stream().filter(change -> change.key().endsWith("\n" + name)).toList();
            boolean localChanged = related.stream().anyMatch(change -> change.local() != change.baseline());
            boolean remoteChanged = related.stream().anyMatch(change -> change.remote() != change.baseline());
            boolean disagree = related.stream().anyMatch(change -> change.local() != change.remote());
            if (localChanged && remoteChanged && disagree && related.size() > 1) {
                for (int i = 0; i < changes.size(); i++) {
                    var change = changes.get(i);
                    if (change.key().endsWith("\n" + name)) changes.set(i, new Change(change.key(), change.card(), change.section(), change.baseline(), change.local(), change.remote(), true));
                }
            }
        }
        boolean hasChanges = document.baseline == null || !remoteSignature(document.baseline).equals(remoteSignature(remote));
        String token = UUID.randomUUID().toString();
        var result = new Preview(token, remote.url(), remote.name(), remote.format(), remote.cards(), remote.problems(), remote.warnings(), List.copyOf(changes), hasChanges);
        previews.entrySet().removeIf(item -> System.currentTimeMillis() - item.getValue().created() > 900_000);
        while (previews.size() >= 16) previews.remove(previews.keySet().iterator().next());
        previews.put(token, new Pending(result, remote, deckId, snapshot == null ? -1 : snapshot.revision(), document.metadataRevision,
                snapshot == null ? "" : localSignature(snapshot, format), System.currentTimeMillis()));
        return result;
    }

    synchronized Applied apply(String token, String deckId, DeckEditor editor, String format, String choice) throws IOException {
        var pending = previews.get(token);
        if (pending == null || System.currentTimeMillis() - pending.created() > 900_000) throw new IllegalArgumentException("Source preview expired. Check the source again.");
        if (!List.of("remote", "local", "merge", "copy").contains(choice)) throw new IllegalArgumentException("Choose keep local, use source, merge, or copy");
        if (!pending.preview().problems().isEmpty()) throw new IllegalArgumentException("Resolve every unmapped source card before importing: " + String.join("; ", pending.preview().problems()));
        boolean fresh = pending.deckId() == null;
        if (!fresh) {
            if (!Objects.equals(deckId, pending.deckId()) || editor == null || editor.snapshot().revision() != pending.revision()
                    || read(deckId).metadataRevision != pending.metadataRevision() || !pending.localSignature().equals(localSignature(editor.snapshot(), format)))
                throw new IllegalArgumentException("The local deck changed after this preview. Check the source again.");
        } else if (deckId != null) throw new IllegalArgumentException("This preview creates a new linked deck");
        if (choice.equals("merge") && pending.preview().changes().stream().anyMatch(Change::conflict)) throw new IllegalArgumentException("Both copies changed the same card. Choose keep local, use source, or import a copy.");
        boolean copy = choice.equals("copy") || fresh;
        String targetId = copy ? UUID.randomUUID().toString() : deckId;
        Document document = copy ? new Document() : read(deckId);
        var beforeConsidering = List.copyOf(document.considering);
        String beforeToken = copy ? null : editor.historyToken();
        DeckEditor target = copy ? new DeckEditor(catalog, new Deck(checkedText(pending.remote().name(), 100, "Deck name"))) : new DeckEditor(catalog, editor.toDeck());
        String targetFormat = copy || choice.equals("remote") ? pending.remote().format() : format;
        if (!choice.equals("local") || copy) {
            List<ArchidektSource.Row> desired = pending.remote().cards();
            if (!copy && choice.equals("merge")) {
                var baseline = document.baseline == null ? Map.<String, Counted>of() : remoteCounts(document.baseline);
                var remote = remoteCounts(pending.remote());
                var localRows = localRows(editor.snapshot(), document);
                var local = localCounts(editor.snapshot(), document);
                var keys = new LinkedHashSet<String>(); keys.addAll(remote.keySet()); keys.addAll(local.keySet());
                var merged = new ArrayList<ArchidektSource.Row>();
                for (String key : keys) {
                    // Unchanged source rows retain every local printing; source changes retain every source printing.
                    var rows = quantity(remote, key) == quantity(baseline, key) ? localRows : pending.remote().cards();
                    rows.stream().filter(row -> logicalKey(row.section(), row.card()).equals(key)).forEach(merged::add);
                }
                desired = merged;
            }
            var cards = desired.stream().filter(card -> !card.section().equals("Considering"))
                    .map(card -> new DeckEditor.Edit(card.section(), card.card().id(), card.quantity())).toList();
            replace(target, cards);
            document.considering = new ArrayList<>(desired.stream().filter(card -> card.section().equals("Considering"))
                    .map(card -> new ConsideringEdit(card.card().id(), card.quantity(), card.categories())).toList());
            var preserved = new LinkedHashMap<String, Role>();
            for (var role : document.roles) if (!"Archidekt".equals(role.origin())) preserved.put(role.cardId(), role);
            for (var row : pending.remote().cards()) if (!preserved.containsKey(row.cardId())) preserved.put(row.cardId(),
                    new Role(row.cardId(), row.categories(), false, "Archidekt"));
            document.roles = new ArrayList<>(preserved.values());
        }
        var remote = pending.remote();
        document.source = new Source(remote.url(), remote.remoteId(), remote.name(), remote.updatedAt(), System.currentTimeMillis(), "Public source checked; local changes stay in Mana Table");
        document.baseline = remote;
        document.metadataRevision++;
        addVersion(document, target.snapshot(), targetFormat);
        commit(targetId, target.snapshot(), targetFormat, document);
        if (!copy) {
            replace(editor, target.snapshot().entries().stream().map(row -> new DeckEditor.Edit(row.section(), row.card().id(), row.quantity())).toList());
            if (beforeToken.equals(editor.historyToken()) && !format.equals(targetFormat)) editor.checkpoint(editor.snapshot().revision());
            remember(deckId, beforeToken, editor.historyToken(), format, targetFormat, beforeConsidering, document.considering);
            target = editor;
        } else target = new DeckEditor(catalog, target.toDeck());
        previews.remove(token);
        return new Applied(target, targetFormat, targetId);
    }

    private record Counted(CardCatalog.CardInfo card, String section, int quantity, List<String> categories) { }
    private void remember(String deckId, String beforeToken, String afterToken, String beforeFormat, String afterFormat,
                          List<ConsideringEdit> before, List<ConsideringEdit> after) {
        if (beforeToken.equals(afterToken)) return;
        var old = new LinkedHashMap<String, ConsideringEdit>(); before.forEach(row -> old.put(row.cardId(), row));
        var next = new LinkedHashMap<String, ConsideringEdit>(); after.forEach(row -> next.put(row.cardId(), row));
        var keys = new LinkedHashSet<String>(); keys.addAll(old.keySet()); keys.addAll(next.keySet());
        var deltas = new ArrayList<PoolDelta>();
        for (String key : keys) {
            var a = old.get(key); var b = next.get(key);
            int quantity = (b == null ? 0 : b.quantity()) - (a == null ? 0 : a.quantity());
            if (quantity != 0) deltas.add(new PoolDelta(key, quantity, a == null ? List.of() : a.categories(), b == null ? List.of() : b.categories()));
        }
        history.computeIfAbsent(deckId, ignored -> new ArrayList<>()).add(new Transition(beforeToken, afterToken, beforeFormat, afterFormat, List.copyOf(deltas)));
    }
    private static Map<String, Counted> remoteCounts(ArchidektSource.Remote remote) {
        var result = new LinkedHashMap<String, Counted>();
        for (var row : remote.cards()) add(result, row.card(), row.section(), row.quantity(), row.categories());
        return result;
    }
    private Map<String, Counted> localCounts(DeckEditor.Snapshot snapshot, Document document) {
        var result = new LinkedHashMap<String, Counted>();
        for (var row : snapshot.entries()) add(result, row.card(), row.section(), row.quantity(), List.of());
        for (var row : document.considering) add(result, CardCatalog.describe(catalog.resolve(row.cardId())), "Considering", row.quantity(), row.categories());
        return result;
    }
    private List<ArchidektSource.Row> localRows(DeckEditor.Snapshot snapshot, Document document) {
        var result = new ArrayList<ArchidektSource.Row>();
        for (var row : snapshot.entries()) result.add(new ArchidektSource.Row(row.card().id(), row.card(), row.quantity(), row.section(), List.of(), true));
        for (var row : document.considering) result.add(new ArchidektSource.Row(row.cardId(), CardCatalog.describe(catalog.resolve(row.cardId())), row.quantity(), "Considering", row.categories(), false));
        return result;
    }
    private static void add(Map<String, Counted> result, CardCatalog.CardInfo card, String section, int quantity, List<String> categories) {
        String key = logicalKey(section, card);
        var old = result.get(key);
        var tags = new LinkedHashSet<>(categories); if (old != null) tags.addAll(old.categories());
        result.put(key, new Counted(card, section, quantity + (old == null ? 0 : old.quantity()), List.copyOf(tags)));
    }
    private static String logicalKey(String section, CardCatalog.CardInfo card) { return section + "\n" + card.name().toLowerCase(Locale.ROOT); }
    private static int quantity(Map<String, Counted> rows, String key) { return rows.containsKey(key) ? rows.get(key).quantity() : 0; }

    private void replace(DeckEditor editor, List<DeckEditor.Edit> cards) {
        var before = editor.snapshot();
        var desired = new LinkedHashMap<String, DeckEditor.Edit>();
        for (var row : cards) desired.put(row.section() + "\n" + row.cardId(), row);
        var edits = new ArrayList<DeckEditor.Edit>();
        for (var row : before.entries()) if (!desired.containsKey(row.section() + "\n" + row.card().id())) edits.add(new DeckEditor.Edit(row.section(), row.card().id(), 0));
        for (var row : desired.values()) {
            int count = before.entries().stream().filter(old -> old.section().equals(row.section()) && old.card().id().equals(row.cardId())).mapToInt(DeckEditor.Entry::quantity).sum();
            if (count != row.quantity()) edits.add(row);
        }
        // Validate against a detached deck first: a failed source apply never partially edits the active deck.
        var candidate = new DeckEditor(catalog, editor.toDeck());
        candidate.apply(0, edits);
        if (!edits.isEmpty()) editor.apply(before.revision(), edits);
    }

    private static List<Role> effectiveRoles(Document document, DeckEditor.Snapshot snapshot) {
        var roles = new LinkedHashMap<String, Role>();
        for (var row : snapshot.entries()) roles.put(row.card().id(), new Role(row.card().id(), DeckInsights.roles(row.card()), false, "estimated"));
        for (var role : document.roles) {
            if ("Archidekt".equals(role.origin())) {
                var tags = new LinkedHashSet<String>();
                if (roles.containsKey(role.cardId())) tags.addAll(roles.get(role.cardId()).roles());
                role.roles().stream().filter(tag -> !List.of("Commander", "Main", "Sideboard", "Maybeboard", "Considering", "Land").stream().anyMatch(tag::equalsIgnoreCase)).forEach(tags::add);
                roles.put(role.cardId(), new Role(role.cardId(), uniqueTags(tags), role.pinned(), "Archidekt"));
            } else roles.put(role.cardId(), new Role(role.cardId(), uniqueTags(role.roles()), role.pinned(), role.origin()));
        }
        return List.copyOf(roles.values());
    }
    private static Insights insights(DeckEditor.Snapshot snapshot, List<Role> roles) {
        var counts = new java.util.TreeMap<String, Integer>(String.CASE_INSENSITIVE_ORDER); DeckInsights.ROLES.forEach(role -> counts.put(role, 0));
        var lookup = new LinkedHashMap<String, Role>(); roles.forEach(role -> lookup.put(role.cardId(), role));
        int[] curve = new int[7];
        var sources = new LinkedHashMap<String, Integer>(); List.of("W", "U", "B", "R", "G", "C").forEach(color -> sources.put(color, 0));
        for (var row : snapshot.entries()) {
            if (!row.section().equals("Main")) continue;
            var role = lookup.get(row.card().id());
            if (role != null) for (String name : role.roles()) counts.merge(name, row.quantity(), Integer::sum);
            boolean land = row.card().type().contains("Land");
            if (!land) curve[Math.min(6, Math.max(0, row.card().manaValue()))] += row.quantity();
            // Conservative printed-symbol count. Fetches, conditional choices and MDFC backs need player interpretation.
            if (land) {
                String text = row.card().oracleText();
                var produced = new StringBuilder();
                var clauses = java.util.regex.Pattern.compile("(?i)\\badd ([^.\\n]+)").matcher(text);
                while (clauses.find()) produced.append(clauses.group(1));
                for (String color : sources.keySet()) if (produced.indexOf("{" + color + "}") >= 0 || (color.equals("W") && row.card().type().contains("Plains"))
                        || (color.equals("U") && row.card().type().contains("Island")) || (color.equals("B") && row.card().type().contains("Swamp"))
                        || (color.equals("R") && row.card().type().contains("Mountain")) || (color.equals("G") && row.card().type().contains("Forest")))
                    sources.merge(color, row.quantity(), Integer::sum);
            }
        }
        return new Insights(counts, curve, sources, "Roles can overlap and are editable. Curve excludes lands. Colored-source estimates count printed land symbols/basic types; they do not model fetches, conditional mana, MDFC alternatives, or the chance to cast a spell. Draft issues do not hide these tools.");
    }

    static String fingerprint(DeckEditor.Snapshot snapshot, String format) {
        var counts = new java.util.TreeMap<String, Integer>();
        for (var row : snapshot.entries()) counts.merge(row.section() + "\n" + JSON.toJson(rulesIdentity(row.card())), row.quantity(), Integer::sum);
        return hash(format + "\n" + JSON.toJson(counts));
    }
    private record FaceRules(String name, String manaCost, String type, String text, String power, String toughness) { }
    private record RulesIdentity(String name, String manaCost, int manaValue, String type, String text,
                                 String power, String toughness, int colors, int colorIdentity, FaceRules otherFace) { }
    private static RulesIdentity rulesIdentity(CardCatalog.CardInfo card) {
        var face = card.otherFace();
        return new RulesIdentity(card.name(), card.manaCost(), card.manaValue(), card.type(), card.oracleText(), card.power(), card.toughness(),
                card.colors(), card.colorIdentity(), face == null ? null : new FaceRules(face.name(), face.manaCost(), face.type(), face.oracleText(), face.power(), face.toughness()));
    }
    private static String remoteSignature(ArchidektSource.Remote remote) {
        // Do not create a change just because a service timestamp or view counter changed.
        var value = new JsonObject();
        value.addProperty("name", remote.name()); value.addProperty("format", remote.format());
        value.add("cards", remote.original().get("cards")); value.add("categories", remote.original().get("categories"));
        return hash(JSON.toJson(value));
    }
    private static String localSignature(DeckEditor.Snapshot snapshot, String format) { return hash(format + "\n" + snapshot.name() + "\n" + JSON.toJson(snapshot.entries())); }
    private static String hash(String value) {
        try { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8))); }
        catch (java.security.NoSuchAlgorithmException error) { throw new IllegalStateException(error); }
    }
    private static int count(DeckEditor.Snapshot snapshot, String section) { return snapshot.entries().stream().filter(row -> row.section().equals(section)).mapToInt(DeckEditor.Entry::quantity).sum(); }
    private Document read(String id) throws IOException {
        Path path = file(id);
        assertWritable(id);
        if (!Files.exists(path)) return new Document();
        JsonObject json;
        try { json = com.google.gson.JsonParser.parseString(Files.readString(path, StandardCharsets.UTF_8)).getAsJsonObject(); }
        catch (RuntimeException error) { throw new IOException("Workshop data could not be read; saved deck contents are still available", error); }
        if (!json.has("schema") || !json.has("versions") || !json.has("roles") || !json.has("considering"))
            throw new IOException("Unsupported workshop data; saved deck contents are still available");
        var document = JSON.fromJson(json, Document.class);
        if (document == null || document.schema != 1 || document.versions == null || document.roles == null || document.considering == null)
            throw new IOException("Unsupported workshop data; saved deck contents are still available");
        return document;
    }
    synchronized void assertWritable(String id) {
        file(id);
        if (Files.exists(directory.resolve(id + ".journal"))) throw new IllegalStateException(
                "An interrupted deck change is waiting to recover. Restart Mana Table before editing or saving this deck; the saved change is preserved.");
    }
    private void write(String id, Document document) throws IOException {
        Path target = file(id), temporary = directory.resolve(id + ".tmp");
        Files.writeString(temporary, JSON.toJson(document), StandardCharsets.UTF_8);
        try { Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
        catch (AtomicMoveNotSupportedException error) { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING); }
    }

    private void addVersion(Document document, DeckEditor.Snapshot snapshot, String format) {
        String id = fingerprint(snapshot, format);
        if (document.versions.stream().noneMatch(version -> version.id().equals(id))) document.versions.add(new Version(id,
                "v" + (document.versions.size() + 1), System.currentTimeMillis(), count(snapshot, "Main"), count(snapshot, "Commander"),
                snapshot.name(), format, snapshot.entries().stream().map(row -> new DeckEditor.Edit(row.section(), row.card().id(), row.quantity())).toList()));
    }

    /** A durable intent makes the deck and its Considering/source metadata recover as one change after interruption. */
    private void commit(String id, DeckEditor.Snapshot snapshot, String format, Document document) throws IOException {
        file(id);
        var deck = new JsonObject();
        deck.addProperty("version", 1); deck.addProperty("name", snapshot.name()); deck.addProperty("format", format);
        deck.add("cards", JSON.toJsonTree(snapshot.entries().stream().map(row -> new DeckEditor.Edit(row.section(), row.card().id(), row.quantity())).toList()));
        deck.addProperty("updated", System.currentTimeMillis());
        var intent = new JsonObject(); intent.add("deck", deck); intent.add("workshop", JSON.toJsonTree(document));
        Path journal = directory.resolve(id + ".journal");
        atomic(journal, JSON.toJson(intent));
        atomic(decks.resolve(id + ".json"), JSON.toJson(deck));
        write(id, document);
        Files.delete(journal);
    }

    private void recover() throws IOException {
        try (var paths = Files.list(directory)) {
            for (Path journal : paths.filter(path -> path.getFileName().toString().endsWith(".journal")).toList()) {
                String id = journal.getFileName().toString().replace(".journal", "");
                file(id);
                var intent = com.google.gson.JsonParser.parseString(Files.readString(journal, StandardCharsets.UTF_8)).getAsJsonObject();
                var document = JSON.fromJson(intent.get("workshop"), Document.class);
                if (document == null || document.schema != 1 || !intent.has("deck")) throw new IOException("Cannot recover interrupted workshop change for " + id);
                atomic(decks.resolve(id + ".json"), JSON.toJson(intent.get("deck")));
                write(id, document);
                Files.delete(journal);
            }
        }
    }

    private static void atomic(Path target, String text) throws IOException {
        Path temporary = target.resolveSibling(target.getFileName() + ".tmp");
        Files.writeString(temporary, text, StandardCharsets.UTF_8);
        try { Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
        catch (AtomicMoveNotSupportedException error) { Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING); }
    }
    private Path file(String id) {
        if (!UUID.fromString(id).toString().equals(id)) throw new IllegalArgumentException("Invalid deck ID");
        return directory.resolve(id + ".json");
    }
    private static List<String> checkedTags(List<String> values) {
        if (values == null || values.size() > 32) throw new IllegalArgumentException("Use at most 32 roles/categories per card");
        var tags = new ArrayList<String>();
        for (String value : values) { String tag = checkedText(value, 100, "Role/category").strip(); if (!tag.isBlank()) tags.add(tag); }
        return uniqueTags(tags);
    }
    private static List<String> uniqueTags(Iterable<String> values) {
        var tags = new LinkedHashMap<String, String>();
        for (String value : values) {
            String canonical = List.of("Lands", "Ramp", "Draw", "Interaction", "Protection", "Win condition").stream()
                    .filter(known -> known.equalsIgnoreCase(value)).findFirst().orElse(value);
            tags.putIfAbsent(canonical.toLowerCase(Locale.ROOT), canonical);
        }
        return List.copyOf(tags.values());
    }
    private static String checkedText(String value, int max, String name) {
        if (value == null || value.length() > max) throw new IllegalArgumentException(name + " must be at most " + max + " characters");
        return value;
    }
}
