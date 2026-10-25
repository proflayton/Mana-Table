package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import forge.StaticData;
import forge.deck.Deck;
import org.testng.annotations.Test;

import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.testng.Assert.*;

/** The native fixture comes from the production GUI bridge, not guessed bounds. */
public class NumericTextboxProducerTest {
    @Test(timeOut = 30000)
    public void integerTextboxHasNoInventedRangeAndKeepsItsNumberTask() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        var players = List.of(MatchTable.human(new Deck(), "Constructed", "Seat 0"),
                MatchTable.human(new Deck(), "Constructed", "Seat 1"));
        try (var table = new MatchTable("Constructed", players, Path.of("../forge-gui/res"), Files.createTempDirectory("numeric-text-producer"))) {
            HeadlessPlatform.activate(table);
            var session = table.seat(0);
            var answer = CompletableFuture.supplyAsync(() -> session.platformDialog("showInputDialog",
                    new Object[] {"Choose an amount", null, null, "-2", null, true}));
            long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
            JsonObject state = null, prompt = null;
            while (System.nanoTime() < deadline) {
                state = new Gson().toJsonTree(session.state()).getAsJsonObject();
                if (state.has("prompt") && !state.get("prompt").isJsonNull()) {
                    prompt = state.getAsJsonObject("prompt");
                    break;
                }
                if (answer.isDone()) answer.get(1, TimeUnit.SECONDS);
                Thread.sleep(5);
            }
            assertNotNull(prompt, "The real GUI bridge must publish its numeric input");
            assertFalse(prompt.has("min"));
            assertFalse(prompt.has("max"));
            JsonObject expected;
            try (var stream = getClass().getResourceAsStream("numeric-text-prompt.json")) {
                assertNotNull(stream);
                expected = JsonParser.parseReader(new InputStreamReader(stream, StandardCharsets.UTF_8)).getAsJsonObject();
            }
            var actual = prompt.deepCopy();
            actual.addProperty("id", expected.get("id").getAsString());
            assertEquals(actual, expected, "The native boundary fixture must match the current producer exactly");
            session.action(new Gson().toJsonTree(Map.of("sessionId", state.get("id").getAsString(),
                    "promptId", prompt.get("id").getAsString(), "expectedTask", "number", "value", "-2")).getAsJsonObject());
            assertEquals(answer.get(5, TimeUnit.SECONDS), "-2", "Numeric text uses the same scoped number command while preserving the GUI's string response");
        }
    }
}
