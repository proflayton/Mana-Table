package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.player.RegisteredPlayer;
import forge.item.IPaperCard;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.*;
import java.util.function.Predicate;

import static org.testng.Assert.*;

/** Real four-seat Forge games, exercised only through the production decision API.
 * Fixture permanents enter during game setup, using Forge's registered-player API;
 * no test modifies a running game or supplies its own rules/cleanup implementation. */
final class RulesScenario implements AutoCloseable {
    private static final Gson JSON = new GsonBuilder().setPrettyPrinting().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    final MatchTable table;
    final Path evidence;
    private final List<Object> trace = new ArrayList<>();
    private final Map<Integer, String> answered = new HashMap<>();

    RulesScenario(String name, Map<Integer, List<String>> battlefield) throws Exception {
        this(name, battlefield, "Forest", "Rhys the Redeemed");
    }

    RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, String commander) throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        evidence = Path.of("target/rules-regressions", name);
        Files.createDirectories(evidence);
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 4; seat++) {
            var deck = new Deck("Rules seat " + seat);
            deck.getMain().add(StaticData.instance().getCommonCards().getCard(mainCard), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard(commander));
            var player = MatchTable.human(deck, "Commander", "Seat " + seat);
            List<IPaperCard> extras = new ArrayList<>();
            for (String card : battlefield.getOrDefault(seat, List.of())) {
                var printing = StaticData.instance().getCommonCards().getCard(card);
                assertNotNull(printing, "Missing fixture card " + card);
                extras.add(printing);
            }
            player.addExtraCardsOnBattlefield(extras);
            players.add(player);
        }
        table = new MatchTable("Commander", players, RESOURCES, evidence);
        table.start();
    }

    JsonObject state(int seat) {
        var state = JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject();
        assertNotEquals(text(state, "status"), "error", state.toString());
        assertNotEquals(text(state, "status"), "finished", "Scenario ended early: " + state);
        return state;
    }

    record Input(int seat, JsonObject state, JsonObject prompt) { }

    // A predicate gets every new decision. Return true to pause before answering it.
    Input until(Predicate<Input> stop) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(45).toNanos();
        while (System.nanoTime() < deadline) {
            for (int seat = 0; seat < 4; seat++) {
                var state = state(seat);
                if (!state.has("prompt") || state.get("prompt").isJsonNull()) continue;
                var prompt = state.getAsJsonObject("prompt");
                if (text(prompt, "id").equals(answered.get(seat))) continue;
                var input = new Input(seat, state, prompt);
                if (stop.test(input)) return input;
                String type = text(prompt, "inputType");
                if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty()) {
                    // Choose seat zero to start regardless of the random coin toss.
                    answer(input, Map.of("action", "player", "playerId", table.game.getPlayers().get(0).getId()));
                } else if (type.equals("InputPassPriority") || type.equals("InputConfirm") || type.contains("Mulligan")) {
                    if (!prompt.get("okEnabled").getAsBoolean()) continue;
                    answer(input, Map.of("action", type.equals("InputPassPriority") && prompt.get("canAutoPass").getAsBoolean() ? "passIfNoResponse" : "ok"));
                } else if ((type.equals("InputAttack") || type.equals("InputBlock")) && prompt.get("okEnabled").getAsBoolean()) {
                    answer(input, Map.of("action", "ok"));
                } else throw new AssertionError("Unhandled decision (test must explicitly choose) in " + text(state, "phaseKey") + ": " + prompt);
            }
            Thread.sleep(5);
        }
        throw new AssertionError("Scenario timed out: " + table.testViews());
    }

    boolean answer(Input input, Map<String, ?> answer) {
        JsonObject request = request(input, answer);
        try {
            table.seat(input.seat()).action(request);
            answered.put(input.seat(), text(input.prompt(), "id"));
            trace.add(Map.of("seat", input.seat(), "state", input.state(), "answer", answer));
            return true;
        } catch (IllegalArgumentException error) {
            // Publication may supersede a prompt before it is answered. Never replay
            // an answer against a new prompt; let the test inspect the fresh input.
            if (!"That choice has changed. Use the current prompt.".equals(error.getMessage())) throw error;
            return false;
        }
    }

    void rejected(Input input, String action, String message) {
        rejected(input, Map.of("action", action), message);
    }

    void rejected(Input input, Map<String, ?> answer, String message) {
        var error = expectThrows(IllegalArgumentException.class,
                () -> table.seat(input.seat()).action(request(input, answer)));
        assertEquals(error.getMessage(), message);
        assertEquals(text(state(input.seat()).getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
    }

    private JsonObject request(Input input, Map<String, ?> answer) {
        var request = JSON.toJsonTree(answer).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id"));
        request.addProperty("promptId", text(input.prompt(), "id"));
        return request;
    }

    static JsonObject player(JsonObject state, int id) {
        for (var entry : state.getAsJsonArray("players")) if (entry.getAsJsonObject().get("id").getAsInt() == id) return entry.getAsJsonObject();
        throw new AssertionError("Unknown player " + id);
    }
    static JsonObject viewer(JsonObject state) { return player(state, state.get("viewerId").getAsInt()); }
    static JsonObject zone(JsonObject player, String name) {
        for (var entry : player.getAsJsonArray("zones")) if (text(entry.getAsJsonObject(), "name").equals(name)) return entry.getAsJsonObject();
        throw new AssertionError("Unknown zone " + name);
    }
    static int count(JsonObject player, String zone) { return zone(player, zone).get("count").getAsInt(); }
    static String text(JsonObject object, String key) { var value = object.get(key); return value == null || value.isJsonNull() ? "" : value.getAsString(); }

    @Override public void close() throws Exception {
        try {
            Files.writeString(evidence.resolve("trace.json"), JSON.toJson(Map.of("actions", trace, "lastViews", table.testViews())));
        } finally { table.close(); }
    }
}
