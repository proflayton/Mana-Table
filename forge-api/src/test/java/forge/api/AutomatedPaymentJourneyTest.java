package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.*;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A real rules continuation pays from existing floating mana with no Forge Auto planner. */
public class AutomatedPaymentJourneyTest {
    private static final Gson JSON = new Gson();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void exactPoolPaymentCastsARealSpellAndRejectsAnUnavailableColor() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Forest"), "Commander", "Human");
        var bot = MatchTable.automated(deck("Policy", "Sol Ring"), "Commander", "Policy");
        bot.addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Sol Ring"),
                StaticData.instance().getCommonCards().getCard("Forest")));
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES,
                Files.createTempDirectory("automated-floating-payment"))) {
            int policyId = table.game.getPlayers().get(1).getId();
            var driver = new Driver(table, policyId);
            table.start();
            var priority = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId
                    && text(input.state, "phaseKey").equals("MAIN1"));
            var ring = card(priority.state, "Battlefield", "Sol Ring");
            driver.use(priority, ring, "activate");
            priority = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && viewer(input.state).getAsJsonObject("mana").get("C").getAsInt() == 2);
            driver.use(priority, card(priority.state, "Hand", "Sol Ring"), "cast");
            var payment = driver.until(input -> input.playerId == policyId
                    && text(input.prompt, "inputType").startsWith("InputPayMana"));
            assertFalse(payment.prompt.get("okEnabled").getAsBoolean(), "A policy cannot invoke Forge's Auto payment");
            var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            assertEquals(observation.getAsJsonObject("domain").getAsJsonArray("poolManaColors").toString(), "[\"C\"]");
            var before = table.automatedSeat(policyId).state();
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(policyId).action(
                    request(payment, Map.of("action", "spendPoolMana", "manaColor", "G"))));
            assertEquals(table.automatedSeat(policyId).state(), before, "Rejected pool payment cannot spend resources");
            driver.answer(payment, Map.of("action", "spendPoolMana", "manaColor", "C"));
            driver.until(input -> input.playerId == policyId && isPriority(input)
                    && !input.state.getAsJsonArray("stack").isEmpty());
            var resolved = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.getAsJsonArray("stack").isEmpty()
                    && zone(viewer(input.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .filter(value -> text(value.getAsJsonObject(), "name").equals("Sol Ring")).count() == 2);
            assertFalse(card(resolved.state, "Battlefield", "Forest").get("tapped").getAsBoolean(),
                    "Existing floating mana pays the spell without selecting a fresh mana source");
            assertEquals(viewer(resolved.state).getAsJsonObject("mana").get("C").getAsInt(), 1);
            assertTrue(zone(player(JSON.toJsonTree(table.seat(0).state()).getAsJsonObject(), policyId), "Hand")
                    .getAsJsonArray("cards").isEmpty(), "The human never receives the policy's hidden hand");
        }
    }

    private static Deck deck(String name, String main) {
        var deck = new Deck(name);
        deck.getMain().add(StaticData.instance().getCommonCards().getCard(main), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        return deck;
    }

    private record Input(int playerId, MatchSession session, JsonObject state, JsonObject prompt) { }
    private static boolean isPriority(Input input) { return text(input.prompt, "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zoneName, String name) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject request(Input input, Map<String, ?> answer) {
        var request = JSON.toJsonTree(answer).getAsJsonObject();
        request.addProperty("sessionId", text(input.state, "id")); request.addProperty("promptId", text(input.prompt, "id"));
        return request;
    }

    private static final class Driver {
        private final MatchTable table;
        private final int policyId;
        private final List<MatchSession> seats;
        private final Set<String> answered = new HashSet<>();
        Driver(MatchTable table, int policyId) {
            this.table = table; this.policyId = policyId;
            seats = List.of(table.seat(0), table.automatedSeat(policyId));
        }
        Input until(Predicate<Input> stop) throws Exception {
            long deadline = System.nanoTime() + Duration.ofSeconds(20).toNanos();
            while (System.nanoTime() < deadline) {
                for (var session : seats) {
                    var state = JSON.toJsonTree(session.state()).getAsJsonObject();
                    assertNotEquals(text(state, "status"), "error", state.toString());
                    if (!state.has("prompt") || state.get("prompt").isJsonNull()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    if (answered.contains(text(prompt, "id"))) continue;
                    var input = new Input(state.get("viewerId").getAsInt(), session, state, prompt);
                    if (stop.test(input)) return input;
                    String kind = text(prompt, "inputType");
                    if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                        answer(input, Map.of("action", "player", "playerId", policyId));
                    else if ((kind.equals("InputPassPriority") || kind.equals("InputConfirmMulligan") || kind.equals("InputConfirm")
                            || kind.equals("InputAttack") || kind.equals("InputBlock")) && prompt.get("okEnabled").getAsBoolean())
                        answer(input, Map.of("action", kind.equals("InputPassPriority") ? "passPriority" : "ok"));
                    else throw new AssertionError("Unexpected fixture decision: " + prompt);
                }
                Thread.sleep(3);
            }
            throw new AssertionError("Timed out waiting for automated payment: " + seats.stream().map(MatchSession::state).toList());
        }
        void answer(Input input, Map<String, ?> answer) {
            input.session.action(request(input, answer)); answered.add(text(input.prompt, "id"));
        }
        void use(Input input, JsonObject source, String kind) {
            var query = request(input, Map.of("key", text(source, "key")));
            var catalog = JSON.toJsonTree(input.session.sourceActions(query)).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "kind").equals(kind)).findFirst().orElseThrow(() ->
                            new AssertionError("Missing " + kind + " for " + source + "; catalog=" + catalog + "; state=" + input.state));
            answer(input, Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id")));
        }
    }
}
