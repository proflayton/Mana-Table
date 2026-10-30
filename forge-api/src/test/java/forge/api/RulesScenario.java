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
import java.util.function.LongSupplier;

import static org.testng.Assert.*;

/** Real Forge games, exercised only through the production decision API.
 * Fixture permanents enter during game setup, using Forge's registered-player API;
 * no test modifies a running game or supplies its own rules/cleanup implementation. */
final class RulesScenario implements AutoCloseable {
    private static final Gson JSON = new GsonBuilder().setPrettyPrinting().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    final MatchTable table;
    final Path evidence;
    private final int seatCount;
    private final boolean automated;
    private final List<Object> trace = new ArrayList<>();
    private final Map<Integer, String> answered = new HashMap<>();
    enum TraceMode { FULL, SCALE }
    private TraceMode traceMode = TraceMode.FULL;
    private Input firstInput, previousInput, currentInput;
    private Object firstFailure;

    RulesScenario(String name, Map<Integer, List<String>> battlefield, TraceMode mode) throws Exception {
        this(name, battlefield); traceMode = mode;
    }

    RulesScenario(String name, Map<Integer, List<String>> battlefield) throws Exception {
        this(name, battlefield, "Forest", "Rhys the Redeemed");
    }

    static RulesScenario automated(String name, Map<Integer, List<String>> battlefield, String mainCard, String commander) throws Exception {
        return automated(name, battlefield, mainCard, commander, false);
    }
    static RulesScenario automated(String name, Map<Integer, List<String>> battlefield, String mainCard, String commander,
                                   boolean enableEtbCounters) throws Exception {
        return new RulesScenario(name, battlefield, mainCard, List.of(commander), enableEtbCounters, 4, DecisionTempo.Settings.off(),
                () -> System.nanoTime() / 1_000_000, Map.of(), true);
    }

    RulesScenario(String name, int seatCount) throws Exception {
        this(name, Map.of(), "Forest", List.of("Rhys the Redeemed"), false, seatCount);
    }
    RulesScenario(String name, DecisionTempo.Settings settings, LongSupplier time) throws Exception {
        this(name, Map.of(), settings, time);
    }
    RulesScenario(String name, Map<Integer, List<String>> battlefield, DecisionTempo.Settings settings, LongSupplier time) throws Exception {
        this(name, battlefield, "Forest", List.of("Rhys the Redeemed"), false, 4, settings, time);
    }

    RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, String commander) throws Exception {
        this(name, battlefield, mainCard, commander, false);
    }

    RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, String commander,
                  Map<Integer, List<String>> mainDeckOverrides) throws Exception {
        this(name, battlefield, mainCard, List.of(commander), false, 4, DecisionTempo.Settings.off(),
                () -> System.nanoTime() / 1_000_000, mainDeckOverrides);
    }

    RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, String commander,
                  boolean enableEtbCounters) throws Exception {
        this(name, battlefield, mainCard, List.of(commander), enableEtbCounters);
    }

    RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, List<String> commanders) throws Exception {
        this(name, battlefield, mainCard, commanders, false);
    }

    private RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, List<String> commanders,
                          boolean enableEtbCounters) throws Exception {
        this(name, battlefield, mainCard, commanders, enableEtbCounters, 4);
    }

    private RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, List<String> commanders,
                          boolean enableEtbCounters, int seatCount) throws Exception {
        this(name, battlefield, mainCard, commanders, enableEtbCounters, seatCount, DecisionTempo.Settings.off(), () -> System.nanoTime() / 1_000_000);
    }
    private RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, List<String> commanders,
                          boolean enableEtbCounters, int seatCount, DecisionTempo.Settings settings, LongSupplier time) throws Exception {
        this(name, battlefield, mainCard, commanders, enableEtbCounters, seatCount, settings, time, Map.of());
    }

    private RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, List<String> commanders,
                          boolean enableEtbCounters, int seatCount, DecisionTempo.Settings settings, LongSupplier time,
                          Map<Integer, List<String>> mainDeckOverrides) throws Exception {
        this(name, battlefield, mainCard, commanders, enableEtbCounters, seatCount, settings, time, mainDeckOverrides, false);
    }

    private RulesScenario(String name, Map<Integer, List<String>> battlefield, String mainCard, List<String> commanders,
                          boolean enableEtbCounters, int seatCount, DecisionTempo.Settings settings, LongSupplier time,
                          Map<Integer, List<String>> mainDeckOverrides, boolean automated) throws Exception {
        this.seatCount = seatCount;
        this.automated = automated;
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        evidence = ApiTestWorkspace.evidence(name);
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < seatCount; seat++) {
            var deck = new Deck("Rules seat " + seat);
            if (mainDeckOverrides.containsKey(seat)) for (var card : mainDeckOverrides.get(seat))
                deck.getMain().add(StaticData.instance().getCommonCards().getCard(card));
            else deck.getMain().add(StaticData.instance().getCommonCards().getCard(mainCard), 100 - commanders.size());
            for (String commander : commanders)
                deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard(commander));
            var player = automated ? MatchTable.automated(deck, "Commander", "Seat " + seat) : MatchTable.human(deck, "Commander", "Seat " + seat);
            player.setEnableETBCountersEffect(enableEtbCounters);
            List<IPaperCard> extras = new ArrayList<>();
            for (String card : battlefield.getOrDefault(seat, List.of())) {
                var printing = StaticData.instance().getCommonCards().getCard(card);
                assertNotNull(printing, "Missing fixture card " + card);
                extras.add(printing);
            }
            player.addExtraCardsOnBattlefield(extras);
            players.add(player);
        }
        table = new MatchTable("Commander", players, RESOURCES, evidence, settings, time, automated);
        table.start();
    }

    JsonObject state(int seat) {
        var state = JSON.toJsonTree(session(seat).state()).getAsJsonObject();
        assertNotEquals(text(state, "status"), "error", state.toString());
        assertNotEquals(text(state, "status"), "finished", "Scenario ended early: " + state);
        return state;
    }

    MatchSession session(int seat) {
        return automated ? table.automatedSeat(table.game.getRegisteredPlayers().get(seat).getId()) : table.seat(seat);
    }
    private List<Map<String, Object>> views() {
        return automated ? table.automatedSeats().stream().map(MatchSession::state).toList() : table.testViews();
    }

    record Input(int seat, JsonObject state, JsonObject prompt) { }

    // A predicate gets every new decision. Return true to pause before answering it.
    Input until(Predicate<Input> stop) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(45).toNanos();
        while (System.nanoTime() < deadline) {
            for (int seat = 0; seat < seatCount; seat++) {
                var state = state(seat);
                if (!state.has("prompt") || state.get("prompt").isJsonNull()) continue;
                var prompt = state.getAsJsonObject("prompt");
                if (text(prompt, "id").equals(answered.get(seat))) continue;
                var input = new Input(seat, state, prompt);
                remember(input);
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
        throw new AssertionError("Scenario timed out: " + views());
    }

    boolean answer(Input input, Map<String, ?> answer) {
        JsonObject request = request(input, answer);
        try {
            session(input.seat()).action(request);
            answered.put(input.seat(), text(input.prompt(), "id"));
            if (traceMode == TraceMode.FULL) trace.add(Map.of("seat", input.seat(), "state", input.state(), "answer", answer));
            else {
                remember(input);
                if (trace.size() >= 512) throw new AssertionError("Scale fixture exceeded its512-receipt evidence bound");
                trace.add(compactReceipt(input, answer));
            }
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
                () -> session(input.seat()).action(request(input, answer)));
        assertEquals(error.getMessage(), message);
        assertEquals(text(state(input.seat()).getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
    }

    static JsonObject request(Input input, Map<String, ?> answer) {
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

    /** A concession receipt admits a departure; its rules cleanup completes later. */
    static JsonObject awaitConcession(MatchTable table, MatchSession session) throws InterruptedException {
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        JsonObject latest;
        do {
            latest = JSON.toJsonTree(session.state()).getAsJsonObject();
            int playerId = latest.get("viewerId").getAsInt();
            assertNotEquals(text(latest, "status"), "error", latest.toString());
            if (!latest.getAsJsonArray("players").isEmpty() && viewer(latest).get("eliminated").getAsBoolean()
                    && !text(latest, "status").equals("resolving")
                    && table.game.getAction().readState(() -> table.game.getPlayers().stream()
                    .noneMatch(player -> player.getId() == playerId))) return latest;
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        throw new AssertionError("The admitted concession did not complete its actual departure: " + latest);
    }

    private void remember(Input input) {
        if (traceMode != TraceMode.SCALE || currentInput == input) return;
        if (firstInput == null) firstInput = input;
        previousInput = currentInput; currentInput = input;
    }
    void failed(Throwable failure) {
        if (traceMode != TraceMode.SCALE || firstFailure != null) return;
        String message = String.valueOf(failure);
        firstFailure = Map.of("type", failure.getClass().getName(), "message", message.substring(0, Math.min(message.length(), 2048)),
                "receiptCount", trace.size());
    }
    private Object compactReceipt(Input input, Map<String, ?> answer) {
        var receipt = new LinkedHashMap<String, Object>();
        receipt.put("step", trace.size()); receipt.put("seat", input.seat()); receipt.put("decisionId", text(input.prompt(), "id"));
        receipt.put("inputType", text(input.prompt(), "inputType")); receipt.put("revision", input.state().get("revision"));
        receipt.put("phaseKey", text(input.state(), "phaseKey")); receipt.put("answer", answer); receipt.put("accepted", true);
        if (input.state().has("combat") && !input.state().get("combat").isJsonNull()) {
            var combat = input.state().getAsJsonObject("combat");
            receipt.put("declarationId", text(combat, "declarationId"));
            receipt.put("attackersBefore", combat.getAsJsonArray("attackers").size());
            receipt.put("assignedPairsBefore", combat.getAsJsonArray("attackers").asList().stream()
                    .mapToInt(value -> value.getAsJsonObject().getAsJsonArray("blockerIds").size()).sum());
            String attackerId = combatId(input.state(), Objects.toString(answer.get("attackerKey"), ""));
            String blockerId = combatId(input.state(), Objects.toString(answer.get("blockerKey"), ""));
            if (!attackerId.isEmpty()) {
                receipt.put("attackerId", attackerId);
                if (!blockerId.isEmpty()) {
                    receipt.put("blockerId", blockerId);
                    receipt.put("currentlyOffered", combat.getAsJsonArray("attackers").asList().stream().map(value -> value.getAsJsonObject())
                            .anyMatch(value -> text(value, "cardId").equals(attackerId)
                                    && value.getAsJsonArray("eligibleBlockerIds").asList().stream().anyMatch(id -> id.getAsString().equals(blockerId))));
                } else if (combat.has("attackOptions")) receipt.put("currentAttackOffer", combat.getAsJsonArray("attackOptions").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "cardId").equals(attackerId)).findFirst().orElse(null));
            }
        }
        return receipt;
    }
    private static String combatId(JsonObject state, String key) {
        if (key.isEmpty()) return "";
        for (var player : state.getAsJsonArray("players")) for (var zone : player.getAsJsonObject().getAsJsonArray("zones"))
            for (var value : zone.getAsJsonObject().getAsJsonArray("cards")) {
                var card = value.getAsJsonObject(); if (text(card, "key").equals(key)) return text(card, "combatId");
            }
        return "";
    }

    @Override public void close() throws Exception {
        try {
            if (traceMode == TraceMode.FULL) Files.writeString(evidence.resolve("trace.json"), JSON.toJson(Map.of("actions", trace, "lastViews", views())));
            else {
                var evidenceFrame = new LinkedHashMap<String, Object>();
                evidenceFrame.put("traceMode", "scale"); evidenceFrame.put("actions", trace); evidenceFrame.put("firstDecision", firstInput);
                evidenceFrame.put("previousDecision", previousInput); evidenceFrame.put("currentDecision", currentInput);
                evidenceFrame.put("firstFailure", firstFailure); evidenceFrame.put("lastViews", views());
                Files.writeString(evidence.resolve("trace.json"), JSON.toJson(evidenceFrame));
            }
        } finally { table.close(); }
    }
}
