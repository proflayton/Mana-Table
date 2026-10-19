package forge.api;

import com.google.gson.JsonParser;
import forge.StaticData;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;

import static org.testng.Assert.*;

public class DeckPlaytestsTest {
    @BeforeClass public void resources() {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
    }

    private static DeckEditor.Entry row(String section, String name, int count) {
        return new DeckEditor.Entry(section, CardCatalog.describe(StaticData.instance().getCommonCards().getCard(name)), count);
    }

    private static List<DeckEditor.Entry> commander() {
        return List.of(row("Main", "Forest", 99), row("Commander", "Rhys the Redeemed", 1));
    }

    @Test public void exactDeckAndFactsSurviveRestartWhileAnUnfinishedGameRemainsHonest() throws Exception {
        Path directory = Files.createTempDirectory("mana-tests-history");
        var store = new DeckPlaytests(directory);
        var original = new ArrayList<>(commander());
        var test = store.begin("deck-one", "version-old", "Original name", "Commander", original, "game", "Can I hit four lands?", List.of("green", "red", "green"));
        original.clear();
        store.checkpoint(test.id(), new MatchSession.PlaytestFrame("real-game", "running", null,
                Map.of("ownTurns", 4, "cardsDrawn", 5, "landsPlayed", 3)));
        store.note("deck-one", test.id(), "I chose to hold the fourth land.");
        var reopened = new DeckPlaytests(directory);
        var restored = reopened.get("deck-one", test.id());
        assertEquals(restored.status(), "interrupted");
        assertNull(restored.result(), "An interrupted test must not manufacture a win or loss");
        assertNotNull(restored.ended());
        assertEquals(restored.versionId(), "version-old");
        assertEquals(restored.observations().get("landsPlayed"), Integer.valueOf(3));
        assertEquals(restored.note(), "I chose to hold the fourth land.");
        assertEquals(restored.opponents(), List.of("green", "red", "green"));
        var saved = JsonParser.parseString(Files.readString(directory.resolve("workshop-tests/" + test.id() + ".json"))).getAsJsonObject();
        assertEquals(saved.getAsJsonArray("entries").size(), 2);
        assertEquals(saved.getAsJsonArray("entries").get(0).getAsJsonObject().get("quantity").getAsInt(), 99);
        assertEquals(saved.get("deckName").getAsString(), "Original name");
        assertEquals(saved.get("gameId").getAsString(), "real-game");
        assertThrows(IllegalArgumentException.class, () -> reopened.note("other-deck", test.id(), "Wrong deck"));
    }

    @Test public void completedResultsAreImmutableAndNewQuestionsCanTestTheSameVersion() throws Exception {
        Path directory = Files.createTempDirectory("mana-tests-finish");
        var store = new DeckPlaytests(directory);
        var test = store.begin("deck", "version", "Deck", "Commander", commander(), "game", "Opening speed", List.of("green"));
        store.checkpoint(test.id(), new MatchSession.PlaytestFrame("match", "completed", "Victory", Map.of("ownTurns", 7)));
        store.checkpoint(test.id(), new MatchSession.PlaytestFrame("match", "running", null, Map.of("ownTurns", 90)));
        store.finish(test.id(), "interrupted", null);
        var next = store.begin("deck", "version", "Renamed locally", "Commander", commander(), "game", "Against a different opponent", List.of("red"));
        assertEquals(next.versionId(), test.versionId());
        assertNotEquals(next.id(), test.id());
        var reopened = new DeckPlaytests(directory);
        var completed = reopened.get("deck", test.id());
        assertEquals(completed.status(), "completed");
        assertEquals(completed.result(), "Victory");
        assertEquals(completed.observations(), Map.of("ownTurns", 7));
        assertEquals(reopened.list("deck").size(), 2);
        assertTrue(reopened.list("unknown-deck").isEmpty());
    }

    @Test public void handSamplerExcludesCommandersPreservesCardConservationAndFreezesSavedSamples() throws Exception {
        Path directory = Files.createTempDirectory("mana-tests-hands");
        var store = new DeckPlaytests(directory);
        var hand = store.newHand("deck", "version", "Deck", "Commander", commander(), "How many lands?");
        assertEquals(hand.hand().size(), 7);
        assertEquals(hand.libraryCount(), 92);
        assertTrue(hand.hand().stream().allMatch(c -> c.name().equals("Forest")));
        var drawn = store.practice(hand.testId(), "draw", null);
        assertEquals(drawn.hand().size(), 8);
        assertEquals(drawn.libraryCount(), 91);
        var bottomed = store.practice(hand.testId(), "bottom", 2);
        assertEquals(bottomed.hand().size() + bottomed.libraryCount(), 99);
        var redrawn = store.practice(hand.testId(), "mulligan", null);
        assertEquals(redrawn.hand().size(), 7, "Draw-only redraws are explicitly not London mulligan enforcement");
        assertEquals(redrawn.libraryCount(), 92);
        assertEquals(redrawn.draws(), 0);
        assertEquals(redrawn.redraws(), 1);
        var saved = store.practice(hand.testId(), "keep", null);
        assertEquals(saved.status(), "completed");
        assertThrows(IllegalStateException.class, () -> store.practice(hand.testId(), "draw", null));
        var next = store.newHand("deck", "new-version", "Deck", "Commander", commander(), "Another question");
        assertNotEquals(next.testId(), hand.testId());
        var reopened = new DeckPlaytests(directory);
        assertEquals(reopened.get("deck", hand.testId()).versionId(), "version");
        assertEquals(reopened.get("deck", hand.testId()).status(), "completed");
        assertEquals(reopened.get("deck", hand.testId()).observations().get("handLands"), Integer.valueOf(7));
        assertEquals(reopened.get("deck", hand.testId()).hand().size(), 7);
        assertTrue(reopened.get("deck", hand.testId()).hand().stream().allMatch(card -> card.name().equals("Forest")));
        assertEquals(reopened.note("deck", hand.testId(), "All lands").hand(), reopened.get("deck", hand.testId()).hand());
        assertEquals(reopened.get("deck", next.testId()).status(), "interrupted");
    }

    @Test public void emptySamplesAndFailedWritesCannotCreateSuccessfulHistory() throws Exception {
        Path directory = Files.createTempDirectory("mana-tests-write-failure");
        var store = new DeckPlaytests(directory);
        assertThrows(IllegalStateException.class, () -> store.newHand("deck", "v", "Deck", "Commander", List.of(row("Commander", "Rhys the Redeemed", 1)), ""));
        assertTrue(store.list("deck").isEmpty());
        var test = store.begin("deck", "v", "Deck", "Commander", commander(), "game", "", List.of("green"));
        Files.createDirectory(directory.resolve("workshop-tests/" + test.id() + ".tmp"));
        assertThrows(IllegalStateException.class, () -> store.note("deck", test.id(), "This write failed"));
        assertEquals(store.get("deck", test.id()).note(), "");
        var saved = JsonParser.parseString(Files.readString(directory.resolve("workshop-tests/" + test.id() + ".json"))).getAsJsonObject();
        assertEquals(saved.getAsJsonObject("test").get("note").getAsString(), "");
        store.checkpoint(test.id(), new MatchSession.PlaytestFrame("match", "running", null, Map.of("landsPlayed", 1)));
        var error = expectThrows(IllegalStateException.class, () -> store.list("deck"));
        assertTrue(error.getMessage().contains("could not be saved"));
    }
}
