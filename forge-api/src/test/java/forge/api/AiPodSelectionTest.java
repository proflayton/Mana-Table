package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;

import static forge.api.RulesScenario.text;
import static org.testng.Assert.*;

/** Committed catalog defaults, independent of saved-list recency and gameplay strategy. */
public class AiPodSelectionTest {
    private static final Gson JSON = new Gson();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private CardCatalog cards;

    @BeforeClass public void resources() {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        cards = CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values());
    }
    private record Library(Path path, DeckDocumentSession documents, AiDeckCatalog catalog) { }
    private Library library(String name) throws Exception {
        var path = ApiTestWorkspace.profile("ai-pod-" + name);
        var documents = new DeckDocumentSession(cards, path, new DeckPlaytests(path));
        return new Library(path, documents, new AiDeckCatalog(cards, documents));
    }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private String content(Deck deck, String format) { return DeckWorkshopStore.fingerprint(new DeckEditor(cards, deck).snapshot(), format); }
    private static List<String> ids(List<Map<String, String>> rows) { return rows.stream().map(row -> row.get("id")).toList(); }
    private static JsonObject seat(JsonObject lobby, int index) { return lobby.getAsJsonArray("slots").get(index).getAsJsonObject(); }
    private static String opponent(JsonObject lobby, int index) { return text(seat(lobby, index), "opponentId"); }
    private static JsonObject assignment(JsonObject lobby, int index, String id) {
        return object(Map.of("tableId", text(lobby, "tableId"), "seat", index, "type", "AI", "opponentId", id));
    }
    private static String savedChoice(Library library, String name) {
        return library.catalog().choices("Commander").stream().filter(row -> row.get("name").equals(name)).findFirst().orElseThrow().get("id");
    }

    @Test public void seededContentSelectionIgnoresCatalogOrderRecencyAndPresetCopies() throws Exception {
        var library = library("aliases"); var catalog = library.catalog();
        var reference = catalog.load("preset:explorers-of-the-deep", "Commander");
        var originalChoices = catalog.choices("Commander");
        var original = catalog.selectPod(reference, "Commander", 5, List.of(), 81234L);
        assertEquals(original.size(), 5);
        assertEquals(original.stream().map(id -> {
            try { return content(catalog.load(id, "Commander"), "Commander"); }
            catch (Exception failure) { throw new AssertionError(failure); }
        }).distinct().count(), 5L);
        assertFalse(original.contains("preset:explorers-of-the-deep"), "The exact human list is avoided when alternatives exist");
        library.documents().importPreset("blood-rites");
        var copy = object(library.documents().importPreset("blood-rites"));
        String copyId = "deck:" + text(copy, "id") + ":" + text(copy, "libraryToken");
        var copiedChoices = catalog.choices("Commander");
        assertEquals(copiedChoices.get(0).get("id"), copyId, "The picker keeps its existing recent-first order");
        assertEquals(copiedChoices.stream().filter(row -> row.get("id").equals(copyId)).findFirst().orElseThrow().get("compositionId"),
                copiedChoices.stream().filter(row -> row.get("id").equals("preset:blood-rites")).findFirst().orElseThrow().get("compositionId"));
        assertEquals(catalog.selectPod(reference, "Commander", 5, List.of(), 81234L), original,
                "Extra saved aliases must not alter seeded content tickets or replace the bundled representative");
        var reversed = new ArrayList<>(copiedChoices); Collections.reverse(reversed);
        assertEquals(catalog.selectPod(reference, "Commander", 5, List.of(), 81234L, reversed), original);
        assertEquals(ids(originalChoices), ids(catalog.choices("Commander")).stream().filter(id -> !id.startsWith("deck:")).toList());
        assertTrue(copiedChoices.stream().allMatch(row -> row.get("compositionId").matches("[a-f0-9]{64}")));
    }

    @Test public void diversityUsesContentAndCommandersWithABoundedSmallPoolFallback() throws Exception {
        var library = library("small-pool"); var catalog = library.catalog();
        library.documents().importDeck("Same label", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed", "Commander");
        String first = savedChoice(library, "Same label");
        library.documents().importDeck("Different Rhys list", "Deck\n98 Forest\n1 Plains\nCommander\n1 Rhys the Redeemed", "Commander");
        String second = savedChoice(library, "Different Rhys list");
        library.documents().importDeck("Same label", "Deck\n99 Plains\nCommander\n1 Isamaru, Hound of Konda", "Commander");
        String third = catalog.choices("Commander").stream().filter(row -> row.get("description").contains("Isamaru")).findFirst().orElseThrow().get("id");
        var small = catalog.choices("Commander").stream().filter(row -> List.of(first, second, third).contains(row.get("id"))).toList();
        var reference = catalog.load(first, "Commander");
        var selected = catalog.selectPod(reference, "Commander", 5, List.of(), 19, small);
        assertEquals(selected.get(0), third, "A different commander is preferred; a duplicate display name does not exclude its list");
        assertFalse(selected.contains(first));
        assertEquals(selected.stream().distinct().count(), 2L);
        long copies = selected.stream().filter(third::equals).count();
        assertTrue(copies == 2 || copies == 3, "Exhaustion cycles evenly through eligible content rather than repeatedly taking index zero");
        assertEquals(catalog.selectPod(reference, "Commander", 5, List.of(), 19, small), selected);
        var onlyMirror = small.stream().filter(row -> row.get("id").equals(first)).toList();
        assertEquals(catalog.selectPod(reference, "Commander", 3, List.of(), 19, onlyMirror), List.of(first, first, first));
        assertThrows(IllegalArgumentException.class, () -> catalog.selectPod(null, "Commander", 6, List.of(), 1));
    }

    @Test public void reservedSnapshotsAndFormatScopedFactsRemainDistinct() throws Exception {
        var catalog = library("reserved").catalog();
        var choices = catalog.choices("Commander");
        String greenCommander = choices.stream().filter(row -> row.get("id").equals("green")).findFirst().orElseThrow().get("compositionId");
        String greenConstructed = catalog.choices("Constructed").stream().filter(row -> row.get("id").equals("green")).findFirst().orElseThrow().get("compositionId");
        assertNotEquals(greenCommander, greenConstructed);
        assertEquals(catalog.choices("Commander").stream().filter(row -> row.get("id").equals("green")).findFirst().orElseThrow().get("compositionId"), greenCommander);
        var reference = catalog.load(choices.get(0).get("id"), "Commander");
        var reserved = List.of(catalog.load(choices.get(1).get("id"), "Commander"), catalog.load(choices.get(2).get("id"), "Commander"));
        var unavailable = List.of(content(reference, "Commander"), content(reserved.get(0), "Commander"), content(reserved.get(1), "Commander"));
        for (var id : catalog.selectPod(reference, "Commander", 3, reserved, 409))
            assertFalse(unavailable.contains(content(catalog.load(id, "Commander"), "Commander")));
        assertEquals(catalog.selectPod(catalog.load("green", "Constructed"), "Constructed", 1, List.of(), 17), List.of("red"));
    }

    @Test(timeOut = 90000)
    public void actualPreparationStaysPinnedThroughReadsAndFailedStartButSuccessfulStartConsumesIt() throws Exception {
        var library = library("preparation"); var seeds = new AtomicInteger();
        try (var engine = new DesktopEngine(cards, library.path(),
                (players, resources, profile) -> new MatchTable("Commander", players, resources, profile, true),
                SyncLobby.PregameSetup.NONE, seeds::incrementAndGet)) {
            var resources = DesktopEngine.class.getDeclaredField("resources"); resources.setAccessible(true); resources.set(engine, RESOURCES);
            engine.dispatch("automatedEnable", new JsonObject());
            var imported = object(engine.dispatch("import", object(Map.of("name", "Reference Rhys", "format", "Commander",
                    "text", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed"))));
            String deckId = text(imported, "id");
            var request = object(Map.of("deckId", deckId));
            var prepared = object(engine.dispatch("matchSetup", request));
            assertEquals(prepared.getAsJsonArray("defaultOpponentIds").size(), 5); assertEquals(seeds.get(), 1);
            for (int read = 0; read < 3; read++) {
                engine.dispatch("list", new JsonObject()); engine.dispatch("matchOpponents", new JsonObject());
                assertEquals(object(engine.dispatch("matchSetup", request)).get("defaultOpponentIds"), prepared.get("defaultOpponentIds"));
                assertEquals(seeds.get(), 1);
            }
            var failed = object(Map.of("deckId", deckId, "revision", prepared.get("revision").getAsLong(), "opponents", List.of("unknown-opponent")));
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("matchStart", failed));
            assertEquals(object(engine.dispatch("matchSetup", request)).get("defaultOpponentIds"), prepared.get("defaultOpponentIds"));
            assertEquals(seeds.get(), 1, "Failed admission cannot consume or reroll the prepared defaults");
            String explicitMirror = "deck:" + deckId + ":" + text(imported, "libraryToken");
            var started = object(engine.dispatch("matchStart", object(Map.of("deckId", deckId,
                    "revision", prepared.get("revision").getAsLong(), "opponents", List.of(explicitMirror, explicitMirror)))));
            long deadline = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(10);
            while (started.getAsJsonArray("players").size() < 3 && System.nanoTime() < deadline) {
                assertNotEquals(text(started, "status"), "error");
                Thread.sleep(3); started = object(engine.dispatch("matchState", new JsonObject()));
            }
            assertEquals(started.getAsJsonArray("players").size(), 3, "Exact explicit mirrors and duplicate choices still start unchanged");
            assertEquals(seeds.get(), 1);
            assertTrue(started.getAsJsonArray("players").asList().stream().skip(1)
                    .allMatch(value -> text(value.getAsJsonObject(), "name").contains("Reference Rhys")));
            var next = object(engine.dispatch("matchSetup", request));
            assertEquals(seeds.get(), 2, "A successful match consumes the preparation; the next preparation receives one new seed");
            assertEquals(object(engine.dispatch("matchSetup", request)).get("defaultOpponentIds"), next.get("defaultOpponentIds"));
            assertEquals(seeds.get(), 2);
        }
    }

    @Test(timeOut = 45000)
    public void hostedBlankAssignmentsReserveBoundDecksAndExplicitMirrorsStayLegal() throws Exception {
        var library = library("hosted"); var catalog = library.catalog();
        var human = catalog.load("preset:explorers-of-the-deep", "Commander");
        var host = new SyncLobby(RESOURCES, library.path(), false, catalog);
        try {
            var lobby = object(host.host("Commander", 4, false, "local")); host.selectDeck(human, "Commander");
            lobby = object(host.setSeat(assignment(lobby, 2, "")));
            String first = opponent(lobby, 2);
            lobby = object(host.setSeat(assignment(lobby, 3, "")));
            String second = opponent(lobby, 3);
            assertNotEquals(content(catalog.load(first, "Commander"), "Commander"), content(human, "Commander"));
            assertNotEquals(content(catalog.load(second, "Commander"), "Commander"), content(human, "Commander"));
            assertNotEquals(content(catalog.load(first, "Commander"), "Commander"), content(catalog.load(second, "Commander"), "Commander"));
            var frozen = seat(lobby, 2).deepCopy();
            for (int poll = 0; poll < 3; poll++) assertEquals(seat(object(host.state()), 2), frozen);
            var explicit = object(host.setSeat(assignment(lobby, 3, "preset:explorers-of-the-deep")));
            assertEquals(opponent(explicit, 3), "preset:explorers-of-the-deep");
            assertEquals(seat(explicit, 2), frozen);
        } finally { host.close(); }
    }

    @Test(timeOut = 45000)
    public void cooperativeNewOppositionIsOneDistinctFrozenPodAndGuestCannotReadItsCatalog() throws Exception {
        var library = library("cooperative"); var catalog = library.catalog();
        var host = new SyncLobby(RESOURCES, library.path(), true, catalog);
        try {
            var lobby = object(host.host("Commander", 4, false, "local"));
            host.selectDeck(catalog.load("preset:explorers-of-the-deep", "Commander"), "Commander");
            var guest = host.localGuest();
            try {
                guest.selectDeck(catalog.load("red", "Commander"), "Commander");
                var specification = new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE, List.of(
                        new MatchSetupSpecification.Seat(0, 7, "party"), new MatchSetupSpecification.Seat(1, 7, "party"),
                        new MatchSetupSpecification.Seat(2, 9, "opposition"), new MatchSetupSpecification.Seat(3, 9, "opposition")));
                lobby = object(host.state());
                lobby = object(host.configureMatch(object(Map.of("tableId", text(lobby, "tableId"),
                        "expectedLobbyRevision", lobby.get("revision").getAsLong(), "specification", specification))));
                assertNotEquals(opponent(lobby, 2), opponent(lobby, 3));
                assertNotEquals(content(catalog.load(opponent(lobby, 2), "Commander"), "Commander"),
                        content(catalog.load(opponent(lobby, 3), "Commander"), "Commander"));
                var first = seat(lobby, 2).deepCopy(); var second = seat(lobby, 3).deepCopy();
                library.documents().importPreset("blood-rites");
                for (int poll = 0; poll < 3; poll++) {
                    var refreshed = object(host.state()); assertEquals(seat(refreshed, 2), first); assertEquals(seat(refreshed, 3), second);
                }
                var remote = object(guest.state());
                assertTrue(remote.getAsJsonArray("aiOpponents").isEmpty());
                assertFalse(remote.toString().contains("compositionId"));
                assertFalse(seat(remote, 2).has("opponentId"));
                var unchanged = object(host.configureMatch(object(Map.of("tableId", text(lobby, "tableId"),
                        "expectedLobbyRevision", object(host.state()).get("revision").getAsLong(), "specification", specification))));
                assertEquals(seat(unchanged, 2), first); assertEquals(seat(unchanged, 3), second);
                // An explicit duplicate remains legal, but the next automatic
                // fill must reserve it and revoke the reviewed encounter consent.
                var duplicated = object(host.setSeat(assignment(unchanged, 3, opponent(unchanged, 2))));
                String consent = text(duplicated, "matchSetupConsent");
                var ready = object(Map.of("tableId", text(duplicated, "tableId"), "matchSetupConsent", consent, "ready", true));
                host.ready(ready); guest.ready(ready);
                var current = object(host.state());
                assertTrue(seat(current, 0).get("ready").getAsBoolean()); assertTrue(seat(current, 1).get("ready").getAsBoolean());
                var same = object(host.setSeat(assignment(current, 2, opponent(current, 2))));
                assertEquals(text(same, "matchSetupConsent"), consent);
                assertTrue(seat(same, 0).get("ready").getAsBoolean()); assertTrue(seat(same, 1).get("ready").getAsBoolean());
                var rerolled = object(host.setSeat(assignment(same, 2, "")));
                assertNotEquals(opponent(rerolled, 2), opponent(same, 2), "The other seat reserves the former content");
                assertNotEquals(text(rerolled, "matchSetupConsent"), consent);
                assertFalse(seat(rerolled, 0).get("ready").getAsBoolean()); assertFalse(seat(rerolled, 1).get("ready").getAsBoolean());
            } finally { guest.close(); }
        } finally { host.close(); }
    }
}
