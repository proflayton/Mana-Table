package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import forge.StaticData;
import org.testng.annotations.Test;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Map;

import static org.testng.Assert.*;

/** Actual resource catalog and deterministic public import, not synthetic cards. */
public class BundledCardDataImportTest {
    @Test(timeOut = 90000)
    public void frozenPublicDeckRetainsEveryRequestedPrintingAndFinish() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        var catalog = CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values());
        JsonObject source;
        try (var stream = getClass().getResourceAsStream("/forge/api/card-data/hermit-28555340.v1.json")) {
            assertNotNull(stream);
            source = JsonParser.parseString(new String(stream.readAllBytes(), StandardCharsets.UTF_8)).getAsJsonObject();
        }
        var evidence = ApiTestWorkspace.evidence("bundled-public-card-data");
        var json = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
        var remote = ArchidektSource.parse("28555340", source, catalog);
        var missing = new ArrayList<String>();
        var mapped = new ArrayList<Object>();
        for (var value : source.getAsJsonArray("cards")) {
            var row = value.getAsJsonObject(); var card = row.getAsJsonObject("card");
            String name = card.getAsJsonObject("oracleCard").get("name").getAsString();
            String set = card.getAsJsonObject("edition").get("editioncode").getAsString();
            String collector = card.get("collectorNumber").getAsString();
            boolean foil = row.get("modifier").getAsString().toLowerCase(Locale.ROOT).contains("foil");
            var exact = catalog.printings(name).stream().filter(item -> item.name().equals(name)
                    && item.edition().equalsIgnoreCase(set) && item.collectorNumber().equals(collector) && item.foil() == foil).findFirst();
            if (exact.isEmpty()) { missing.add(name + " / " + set + " / " + collector + " / foil=" + foil); continue; }
            var printing = exact.get();
            var imported = remote.cards().stream().filter(item -> item.cardId().equals(printing.id())).findFirst().orElseThrow();
            assertEquals(imported.quantity(), row.get("quantity").getAsInt());
            var categories = row.getAsJsonArray("categories").asList().stream().map(item -> item.getAsString()).toList();
            assertEquals(imported.categories(), categories);
            assertEquals(imported.section(), categories.contains("Commander") ? "Commander" : "Main");
            assertEquals(catalog.resolve(printing.id()).getCollectorNumber(), collector);
            assertEquals(catalog.resolve(printing.id()).isFoil(), foil);
            mapped.add(Map.of("requested", row, "actual", printing));
        }
        // Retain every measured mapping and original failure before the hard
        // assertions, including the baseline's actually unmapped Replicator.
        Files.writeString(evidence.resolve("catalog-import-audit.json"), json.toJson(Map.of(
                "source", source.getAsJsonObject("_manaSource"), "mapped", mapped, "missing", missing,
                "problems", remote.problems(), "warnings", remote.warnings())));
        assertNotNull(catalog.named("Replicator"), "The actual shipped-resource catalog must contain Replicator");
        assertTrue(missing.isEmpty(), missing.toString());
        assertTrue(remote.problems().isEmpty(), remote.problems().toString());
        assertTrue(remote.warnings().isEmpty(), remote.warnings().toString());
        assertEquals(source.getAsJsonArray("cards").size(), 79);
        assertEquals(remote.cards().size(), 79);
        assertEquals(remote.cards().stream().mapToInt(ArchidektSource.Row::quantity).sum(), 100);
        assertEquals(remote.name(), "Homer"); assertEquals(remote.format(), "Commander");
        assertEquals(remote.updatedAt(), "2026-11-20T05:36:13.591026Z");
        assertEquals(remote.original(), source, "Minimal source printing/finish/notes/categories stay in the baseline");

        var store = new DeckWorkshopStore(ApiTestWorkspace.profile("bundled-public-import"), catalog);
        var preview = store.stage(remote, null, null, null);
        assertTrue(preview.problems().isEmpty()); assertTrue(preview.warnings().isEmpty());
        var applied = store.apply(preview.token(), null, null, "Commander", "remote");
        var snapshot = applied.editor().snapshot();
        assertEquals(snapshot.entries().stream().filter(item -> item.section().equals("Main")).mapToInt(DeckEditor.Entry::quantity).sum(), 99);
        assertEquals(snapshot.entries().stream().filter(item -> item.section().equals("Commander")).mapToInt(DeckEditor.Entry::quantity).sum(), 1);
        var commander = snapshot.entries().stream().filter(item -> item.section().equals("Commander")).findFirst().orElseThrow().card();
        assertEquals(commander.name(), "Homer, the Hermit"); assertEquals(commander.edition(), "MBC"); assertEquals(commander.collectorNumber(), "40");
        assertTrue(snapshot.entries().stream().anyMatch(item -> item.card().name().equals("Replicator")
                && item.card().edition().equals("TRK") && item.card().collectorNumber().equals("267") && !item.card().foil()));
        assertTrue(snapshot.entries().stream().anyMatch(item -> item.card().name().equals("Zellix, Sanity Flayer")
                && item.card().edition().equals("CLB") && item.card().collectorNumber().equals("652") && item.card().foil()));
        Files.writeString(evidence.resolve("applied-snapshot.json"), json.toJson(snapshot));

        var unavailable = source.deepCopy();
        unavailable.getAsJsonArray("cards").asList().stream().map(item -> item.getAsJsonObject())
                .filter(item -> item.getAsJsonObject("card").getAsJsonObject("oracleCard").get("name").getAsString().equals("Replicator"))
                .findFirst().orElseThrow().getAsJsonObject("card").addProperty("collectorNumber", "unavailable-regression-printing");
        var fallback = ArchidektSource.parse("28555340", unavailable, catalog);
        assertTrue(fallback.problems().isEmpty());
        assertEquals(fallback.warnings(), List.of("Printing substituted for Replicator; original printing, finish, notes and categories remain in the source baseline."));
        assertEquals(fallback.original(), unavailable);
        var unknown = source.deepCopy();
        unknown.getAsJsonArray("cards").get(0).getAsJsonObject().getAsJsonObject("card").getAsJsonObject("oracleCard")
                .addProperty("name", "ManaTable regression nonexistent card 28555340");
        var rejected = store.stage(ArchidektSource.parse("28555340", unknown, catalog), null, null, null);
        assertTrue(rejected.problems().stream().anyMatch(item -> item.startsWith("Cannot map ManaTable regression nonexistent card")));
        assertThrows(IllegalArgumentException.class, () -> store.apply(rejected.token(), null, null, "Commander", "remote"));
    }
}
