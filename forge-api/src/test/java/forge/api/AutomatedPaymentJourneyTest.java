package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.card.CounterEnumType;
import forge.game.zone.ZoneType;
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

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void announcedDoubleXUsesCurrentTypedFundingThenManualPaymentAndResolvesWithThatInvestment() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Forest"), "Commander", "Human");
        var bot = MatchTable.automated(deck("Policy", "Hangarback Walker"), "Commander", "Policy");
        bot.addExtraCardsOnBattlefield(Collections.nCopies(4, StaticData.instance().getCommonCards().getCard("Forest")));
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES,
                Files.createTempDirectory("automated-x-investment"))) {
            int policyId = table.game.getPlayers().get(1).getId();
            var driver = new Driver(table, policyId);
            table.start();
            var main = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId
                    && text(input.state, "phaseKey").equals("MAIN1"));
            driver.use(main, card(main.state, "Hand", "Hangarback Walker"), "cast");
            var number = driver.until(input -> input.playerId == policyId && text(input.prompt, "kind").equals("number"));
            var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            var numeric = observation.getAsJsonObject("semantics").getAsJsonObject("numeric");
            assertNotNull(numeric, "The real announcement callback must retain its exact current ability, not an untyped integer prompt");
            assertEquals(text(numeric, "purpose"), "AnnounceX");
            assertFalse(text(numeric, "sourceCardId").isEmpty());
            assertEquals(text(numeric, "sourceCardId"), text(number.prompt, "sourceCardId"));
            assertEquals(text(number.prompt.getAsJsonObject("sourceCard"), "name"), "Hangarback Walker");
            var symbols = numeric.getAsJsonArray("costs").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(cost -> text(cost, "kind").equals("Mana"))
                    .flatMap(cost -> cost.getAsJsonArray("manaSymbols").asList().stream()).map(value -> value.getAsString()).toList();
            assertEquals(symbols, List.of("{X}", "{X}"));
            var funding = numeric.getAsJsonObject("funding");
            assertEquals(funding.getAsJsonArray("sources").size(), 4);
            assertEquals(funding.getAsJsonArray("sources").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "sourceCardId")).distinct().count(), 4L,
                    "Four forests are four independent physical resources");
            assertEquals(text(funding, "knowledge"), "Unknown");
            var sourceOccurrences = zone(viewer(number.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "combatId")).toList();
            for (var source : funding.getAsJsonArray("sources")) {
                var route = source.getAsJsonObject().getAsJsonArray("routes").get(0).getAsJsonObject();
                assertTrue(sourceOccurrences.contains(text(source.getAsJsonObject(), "sourceCardId")));
                assertEquals(text(route, "eligibility"), "Known");
                assertEquals(route.getAsJsonObject("production").getAsJsonArray("alternatives").get(0)
                        .getAsJsonObject().getAsJsonObject("colors").get("G").getAsInt(), 1);
            }
            assertEquals(JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()), observation,
                    "Reading funding must not announce X, tap a source, or change the current scope");
            driver.answer(number, Map.of("action", "number", "value", 2));
            var payment = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").startsWith("InputPayMana"));
            assertFalse(payment.prompt.get("okEnabled").getAsBoolean(), "The policy must fund XX itself");
            HeadlessPlatform.query(table, () -> null);
            var beforeStale = table.automatedSeat(policyId).state();
            var stale = expectThrows(IllegalArgumentException.class,
                    () -> table.automatedSeat(policyId).action(request(number, Map.of("action", "number", "value", 0))));
            assertEquals(stale.getMessage(), "That choice has changed. Use the current prompt.");
            assertEquals(table.automatedSeat(policyId).state(), beforeStale, "An expired numeric scope cannot revise the investment");
            var paying = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            assertFalse(paying.getAsJsonObject("semantics").has("numeric"), "The announcement context ends when its decision is consumed");
            assertEquals(paying.getAsJsonObject("semantics").getAsJsonArray("unpaidMana").size(), 4, "XX with X=2 costs four mana");
            for (int resource = 0; resource < 4; resource++) {
                if (resource > 0) payment = driver.until(input -> input.playerId == policyId
                        && text(input.prompt, "inputType").startsWith("InputPayMana"));
                var forest = zone(viewer(payment.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                                && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                driver.use(payment, forest, "activate");
                int expected = resource + 1;
                driver.awaitState(state -> tappedForests(state) == expected);
            }
            var committed = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && !input.state.getAsJsonArray("stack").isEmpty());
            assertEquals(tappedForests(committed.state), 4L);
            var resolved = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.getAsJsonArray("stack").isEmpty()
                    && zone(viewer(input.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Hangarback Walker")));
            var walker = card(resolved.state, "Battlefield", "Hangarback Walker");
            assertEquals(text(walker, "power"), "2"); assertEquals(text(walker, "toughness"), "2");
            assertEquals(walker.getAsJsonObject("counters").get("+1/+1").getAsInt(), 2);
            table.game.getAction().withState(() -> {
                var actual = table.game.getRegisteredPlayers().get(1).getCardsIn(ZoneType.Battlefield).stream()
                        .filter(value -> value.getName().equals("Hangarback Walker")).findFirst().orElseThrow();
                assertEquals(actual.getXManaCostPaid(), 2);
                assertEquals(actual.getCounters(CounterEnumType.P1P1), 2);
                assertEquals(actual.getNetPower(), 2); assertEquals(actual.getNetToughness(), 2);
            });
        }
    }

    private static long tappedForests(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                        && value.get("tapped").getAsBoolean()).count();
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
        void awaitState(Predicate<JsonObject> stop) throws Exception {
            long deadline = System.nanoTime() + Duration.ofSeconds(20).toNanos();
            while (System.nanoTime() < deadline) {
                var state = JSON.toJsonTree(table.automatedSeat(policyId).state()).getAsJsonObject();
                assertNotEquals(text(state, "status"), "error", state.toString());
                if (stop.test(state)) return;
                Thread.sleep(3);
            }
            throw new AssertionError("Timed out awaiting payment publication: " + table.automatedSeat(policyId).state());
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
