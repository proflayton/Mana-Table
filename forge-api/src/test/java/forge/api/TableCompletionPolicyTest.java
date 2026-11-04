package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.api.sync.SyncTransport;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.GameEndReason;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;
import java.util.stream.IntStream;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A lobby's humans losing is independent of the actual rules contest finishing. */
public class TableCompletionPolicyTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test(timeOut = 45000)
    public void theActualSoloCompositionStillStopsAfterItsHumanConcedesWithoutInventingABotVictory() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var deck = new Deck("Solo completion");
        deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        var solo = MatchSession.soloDecks(deck, "Commander", List.of(new MatchSession.OpponentDeck(deck, "One"),
                new MatchSession.OpponentDeck(deck, "Two")), RESOURCES, ApiTestWorkspace.evidence("solo-completion-policy"), true);
        try {
            var request = new JsonObject(); request.addProperty("sessionId", text(JSON.toJsonTree(solo.state()).getAsJsonObject(), "id"));
            solo.concede(request);
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            JsonObject state;
            do {
                state = JSON.toJsonTree(solo.state()).getAsJsonObject();
                if (text(state, "status").equals("finished")) break;
                Thread.sleep(3);
            } while (System.nanoTime() < deadline);
            assertEquals(text(state, "status"), "finished");
            assertEquals(text(state, "result"), "Defeat");
            assertTrue(state.get("outcome").isJsonNull());
            assertEquals(state.getAsJsonArray("players").asList().stream().map(row -> row.getAsJsonObject())
                    .filter(player -> !player.get("eliminated").getAsBoolean()).count(), 2L);
        } finally { solo.closeTable(); }
    }

    @Test(timeOut = 90000)
    public void twoRealDrainSpellsLetTheBotsContinueAfterBothHumanLossesAndProduceOnlyTheActualOutcome() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var evidence = ApiTestWorkspace.evidence("mixed-rules-completion");
        var players = IntStream.range(0, 4).mapToObj(index -> {
            var deck = new Deck("Completion seat " + index);
            deck.getMain().add(StaticData.instance().getCommonCards().getCard(index == 2 ? "Syphon Soul" : "Swamp"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Gonti, Lord of Luxury"));
            var player = index < 2 ? MatchTable.human(deck, "Commander", "Human " + index)
                    : MatchTable.automated(deck, "Commander", "Policy " + index);
            player.setStartingLife(index < 2 ? 2 : index == 3 ? 3 : 40);
            if (index == 2) player.addExtraCardsOnBattlefield(java.util.Collections.nCopies(6,
                    StaticData.instance().getCommonCards().getCard("Swamp")));
            return player;
        }).toList();
        try (var table = new MatchTable("Commander", players, RESOURCES, evidence);
             var server = new SyncTransport.Server((seat, method, arguments) -> switch (method) {
                 case "observe" -> table.seat(seat).state();
                 case "reply" -> table.seat(seat).action(arguments);
                 case "sourceActions" -> table.seat(seat).sourceActions(arguments);
                 default -> throw new IllegalArgumentException("Unknown seat command");
             }, () -> 1);
             var host = new SeatConnection(server.local(0));
             var guest = new SeatConnection(new SyncTransport.Remote("127.0.0.1:" + server.listen()))) {
            int caster = table.game.getRegisteredPlayers().get(2).getId();
            var endpoints = List.<MatchEndpoint>of(host, guest, table.automatedSeat(caster),
                    table.automatedSeat(table.game.getRegisteredPlayers().get(3).getId()));
            var driver = new Driver(endpoints, caster); table.start();
            var main = driver.until(states -> priority(states.get(2)));
            driver.cast(main.get(2));
            var humansLost = driver.until(states -> viewer(states.get(0)).get("eliminated").getAsBoolean()
                    && viewer(states.get(1)).get("eliminated").getAsBoolean());
            Files.writeString(evidence.resolve("after-first-real-drain.json"), JSON.toJson(humansLost));
            assertFalse(table.game.isGameOver(), "Both human seats lost to a real spell, but two actual bots still own the rules contest");
            assertEquals(table.game.getPlayers().size(), 2);
            for (var state : humansLost) {
                assertEquals(text(state, "status"), "playing");
                assertTrue(state.get("outcome").isJsonNull());
                assertTrue(state.get("result").isJsonNull());
            }
            int firstTurn = humansLost.get(2).get("turn").getAsInt();
            var next = driver.until(states -> text(states.get(2), "status").equals("finished")
                    || priority(states.get(2)) && states.get(2).get("turn").getAsInt() > firstTurn);
            Files.writeString(evidence.resolve("after-real-phase-boundary.json"), JSON.toJson(next));
            assertFalse(table.game.isGameOver(), "The actual next-phase event must preserve the two surviving bots' rules contest");
            assertFalse(text(next.get(2).getAsJsonObject("prompt"), "id").isBlank(), "The surviving bot has an actual current decision");
            driver.cast(next.get(2));
            var finished = driver.until(states -> states.stream().allMatch(state -> text(state, "status").equals("finished")));
            var outcome = table.game.getAction().readState(() -> MatchOutcomeProjection.of(table, null));
            assertNotNull(outcome, "A second real drain, rather than a host cutoff, produces the completion");
            assertEquals(outcome.winningPlayerIds(), List.of(caster));
            assertNotEquals(outcome.ruleReason(), GameEndReason.AllHumansLost.name());
            for (var state : finished) assertEquals(state.get("outcome"), JSON.toJsonTree(outcome));
            assertEquals(text(finished.get(0), "result"), "Defeat");
            assertEquals(text(finished.get(1), "result"), "Defeat");
            assertEquals(text(finished.get(2), "result"), "Victory");
            Files.writeString(evidence.resolve("actual-rules-completion.json"), JSON.toJson(finished));
        }
    }

    private static boolean priority(JsonObject state) {
        return text(state, "phaseKey").equals("MAIN1") && state.get("viewerId").equals(state.get("activePlayerId"))
                && state.get("prompt").isJsonObject() && text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority");
    }

    private static final class Driver {
        private final List<MatchEndpoint> endpoints;
        private final int caster;
        private final Map<Integer, String> answered = new HashMap<>();
        private List<JsonObject> last = List.of();
        Driver(List<MatchEndpoint> endpoints, int caster) { this.endpoints = endpoints; this.caster = caster; }

        List<JsonObject> until(Predicate<List<JsonObject>> stop) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(20);
            while (System.nanoTime() < deadline) {
                last = endpoints.stream().map(endpoint -> JSON.toJsonTree(endpoint.state()).getAsJsonObject()).toList();
                if (stop.test(last)) return last;
                for (int index = 0; index < endpoints.size(); index++) {
                    var state = last.get(index); assertNotEquals(text(state, "status"), "error", state.toString());
                    if (!state.get("prompt").isJsonObject()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    String stamp = text(prompt, "id") + ":" + state.get("revision").getAsLong();
                    if (stamp.equals(answered.get(index))) continue;
                    Map<String, ?> answer;
                    if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                        answer = Map.of("action", "player", "playerId", caster);
                    else if (prompt.has("choices") && !prompt.getAsJsonArray("choices").isEmpty())
                        answer = Map.of("choices", prompt.getAsJsonArray("choices").asList().stream()
                                .limit(prompt.has("min") ? prompt.get("min").getAsInt() : 1)
                                .map(value -> value.getAsJsonObject().get("index").getAsInt()).toList());
                    else if (text(prompt, "inputType").startsWith("InputPayMana")) {
                        if (prompt.get("okEnabled").getAsBoolean()) answer = Map.of("action", "ok");
                        else if (prompt.has("usablePoolManaColors") && !prompt.getAsJsonArray("usablePoolManaColors").isEmpty())
                            answer = Map.of("action", "spendPoolMana", "manaColor", prompt.getAsJsonArray("usablePoolManaColors").get(0).getAsString());
                        else {
                            var swamp = zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream()
                                    .map(value -> value.getAsJsonObject()).filter(card -> !card.get("tapped").getAsBoolean()).findFirst();
                            if (swamp.isEmpty()) continue;
                            use(index, state, swamp.get(), "activate"); answered.put(index, stamp); continue;
                        }
                    } else if (prompt.has("okEnabled") && prompt.get("okEnabled").getAsBoolean())
                        answer = Map.of("action", text(prompt, "inputType").equals("InputPassPriority") ? "passPriority" : "ok");
                    else continue;
                    endpoints.get(index).action(request(new Input(index, state, prompt), answer));
                    answered.put(index, stamp);
                }
                Thread.sleep(3);
            }
            throw new AssertionError("Completion fixture did not reach its actual rules boundary: " + last);
        }

        void cast(JsonObject state) {
            var source = zone(viewer(state), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Syphon Soul")).findFirst().orElseThrow();
            use(2, state, source, "cast");
        }

        private void use(int index, JsonObject state, JsonObject source, String kind) {
            var prompt = state.getAsJsonObject("prompt");
            var catalog = JSON.toJsonTree(endpoints.get(index).sourceActions(request(new Input(index, state, prompt),
                    Map.of("key", text(source, "key"))))).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "kind").equals(kind)).findFirst().orElseThrow();
            endpoints.get(index).action(request(new Input(index, state, prompt), Map.of("action", "useSourceAction",
                    "key", text(source, "key"), "actionId", text(action, "id"))));
        }
    }
}
