package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;
import org.testng.annotations.DataProvider;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** One audience cannot revoke the current occurrences published for another audience. */
public class ActionOccurrenceCoherenceTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] actors() { return new Object[][] {{0}, {1}, {2}, {3}}; }

    @Test(dataProvider = "actors", timeOut = 90000)
    public void aCommanderPaymentProjectsOneCurrentTransientStackSourceAfterAllAudienceRevocations(int actor) throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var deck = new Deck("Commander payment occurrence");
        deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        var players = java.util.stream.IntStream.range(0, 4)
                .mapToObj(index -> MatchTable.automated(deck, "Commander", "Policy " + index)).toList();
        players.get(actor).addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Plains")));
        Path evidence = ApiTestWorkspace.evidence("commander-payment-occurrence-" + actor);
        try (var table = new MatchTable("Commander", players, resources, evidence, true)) {
            table.start(); var answered = new HashMap<Integer, String>();
            var main = until(table, answered, actor, input -> input.seat() == actor && priority(input)
                    && input.state().get("activePlayerId").getAsInt() == actor
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            use(table, answered, main, "Command", "Rhys the Redeemed", false);
            var payment = until(table, answered, actor, input -> input.seat() == actor
                    && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            assertTrue(payment.state().getAsJsonArray("stack").isEmpty(), "Paying does not yet put the spell on the actual stack");
            for (int publication = 0; publication < 3; publication++) {
                var retained = HeadlessPlatform.query(table, () -> {
                    // Compare pure reads at one observation cut; a queued input
                    // publication may legitimately advance revisions between cuts.
                    var batch = table.automatedSeats().stream().map(MatchSession::automatedObservation).toList();
                    var owner = batch.stream().map(row -> JSON.toJsonTree(row).getAsJsonObject())
                            .filter(row -> row.getAsJsonObject("state").get("viewerId").getAsInt() == actor).findFirst().orElseThrow();
                    var state = owner.getAsJsonObject("state"); var prompt = state.getAsJsonObject("prompt");
                    var portrait = prompt.getAsJsonObject("sourceCard");
                    assertEquals(text(portrait, "name"), "Rhys the Redeemed");
                    assertEquals(text(prompt, "sourceZone"), "Stack");
                    assertFalse(text(portrait, "visualId").isEmpty(), "The current actual payment source must stay identifiable");
                    assertEquals(text(portrait, "combatId"), text(portrait, "visualId"));
                    assertEquals(text(owner.getAsJsonObject("semantics").getAsJsonObject("payment"), "sourceCardId"),
                            text(portrait, "visualId"), "Current payment provenance and its entitled readonly portrait must be the same occurrence");
                    assertEquals(text(portrait, "key"), ""); assertFalse(portrait.get("selectable").getAsBoolean());
                    assertFalse(portrait.get("highlighted").getAsBoolean());
                    assertEquals(JSON.toJsonTree(table.automatedSeats().stream().map(MatchSession::automatedObservation).toList()), JSON.toJsonTree(batch),
                            "A current payment read must not change any audience's occurrence");
                    return JSON.toJson(batch);
                });
                Files.writeString(evidence.resolve("private-payment-observations-" + publication + ".json"), retained);
                table.publish();
            }
            payment = until(table, answered, actor, input -> input.seat() == actor
                    && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            use(table, answered, payment, "Battlefield", "Plains", false);
            var resolved = until(table, answered, actor, input -> input.seat() == actor && priority(input)
                    && input.state().getAsJsonArray("stack").isEmpty()
                    && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(card -> text(card.getAsJsonObject(), "name").equals("Rhys the Redeemed")));
            assertEquals(text(card(resolved.state(), "Battlefield", "Rhys the Redeemed"), "power"), "1");
        }
    }

    @Test(timeOut = 120000)
    public void aHistoricalWarpTriggerCannotRevokeTheCurrentPermanentOrItsTargetCandidates() throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var actorDeck = new Deck("Warp occurrence");
        actorDeck.getMain().add(StaticData.instance().getCommonCards().getCard("Haliya, Guided by Light"));
        actorDeck.getMain().add(StaticData.instance().getCommonCards().getCard("Mishra's Bauble"), 2);
        actorDeck.getMain().add(StaticData.instance().getCommonCards().getCard("Swords to Plowshares"), 96);
        actorDeck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        var otherDeck = new Deck("Observer");
        otherDeck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
        otherDeck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        var players = java.util.stream.IntStream.range(0, 4).mapToObj(index -> MatchTable.automated(
                index == 0 ? actorDeck : otherDeck, "Commander", "Policy " + index)).toList();
        // After the genuine first draw all 99 cards are present, independent
        // of opening shuffle. The test stops before cleanup/discard or deckout.
        players.get(0).setStartingHand(98);
        players.get(0).addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Plains")));
        Path evidence = ApiTestWorkspace.evidence("warp-occurrence-coherence");
        try (var table = new MatchTable("Commander", players, resources, evidence, true)) {
            table.start(); var answered = new HashMap<Integer, String>();
            var main = until(table, answered, 0, input -> input.seat() == 0 && priority(input)
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            assertEquals(count(viewer(main.state()), "Hand"), 99);
            use(table, answered, main, "Hand", "Haliya, Guided by Light", true);
            var payment = until(table, answered, 0, input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            use(table, answered, payment, "Battlefield", "Plains", false);
            for (int artifact = 0; artifact < 2; artifact++) {
                var settled = until(table, answered, 0, input -> input.seat() == 0 && priority(input)
                        && input.state().getAsJsonArray("stack").isEmpty());
                use(table, answered, settled, "Hand", "Mishra's Bauble", false);
            }
            var gained = until(table, answered, 0, input -> input.seat() == 0 && priority(input)
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(viewer(gained.state()).get("life").getAsInt(), 43);
            var end = until(table, answered, 0, input -> input.seat() == 0 && priority(input)
                    && text(input.state(), "phaseKey").equals("END_OF_TURN") && input.state().getAsJsonArray("stack").size() == 2);
            record WarpObservation(String json, String sourceId) { }
            var retained = HeadlessPlatform.query(table, () -> {
                var batch = table.automatedSeats().stream().map(MatchSession::automatedObservation).toList();
                String currentId = null;
                for (var observation : batch) {
                    var row = JSON.toJsonTree(observation).getAsJsonObject();
                    assertCurrentOccurrences(row);
                    var state = row.getAsJsonObject("state");
                    var haliya = card(state, "Battlefield", "Haliya, Guided by Light");
                    if (currentId == null) currentId = text(haliya, "visualId");
                    assertEquals(text(haliya, "visualId"), currentId, "All audiences share one live public occurrence");
                    assertEquals(state.getAsJsonArray("stack").size(), 2);
                    for (var item : state.getAsJsonArray("stack")) {
                        var portrait = item.getAsJsonObject().getAsJsonObject("card");
                        assertNotNull(portrait.get("visualId")); assertFalse(portrait.get("visualId").isJsonNull());
                        assertNotNull(portrait.get("combatId")); assertFalse(portrait.get("combatId").isJsonNull());
                    }
                    assertTrue(state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject().getAsJsonObject("card"))
                            .anyMatch(portrait -> text(portrait, "visualId").isEmpty()), "The Warp trigger's historical portrait grants no live identity");
                }
                var spectator = JSON.toJsonTree(table.spectatorState()).getAsJsonObject();
                assertEquals(text(card(spectator, "Battlefield", "Haliya, Guided by Light"), "visualId"), currentId);
                var stable = JSON.toJsonTree(batch);
                assertEquals(JSON.toJsonTree(table.automatedSeats().stream().map(MatchSession::automatedObservation).toList()), stable,
                        "A historical portrait cannot rotate identities during repeated current reads");
                return new WarpObservation(JSON.toJson(batch), currentId);
            });
            Files.writeString(evidence.resolve("private-observations.json"), retained.json());
            String currentId = retained.sourceId();
            table.publish();
            for (var seat : table.automatedSeats()) {
                var row = JSON.toJsonTree(seat.automatedObservation()).getAsJsonObject(); assertCurrentOccurrences(row);
                assertEquals(text(card(row.getAsJsonObject("state"), "Battlefield", "Haliya, Guided by Light"), "visualId"), currentId);
            }
            assertEquals(text(end.prompt(), "inputType"), "InputPassPriority");
        }
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        for (var player : state.getAsJsonArray("players"))
            for (var value : zone(player.getAsJsonObject(), zone).getAsJsonArray("cards"))
                if (text(value.getAsJsonObject(), "name").equals(name)) return value.getAsJsonObject();
        throw new AssertionError("Missing fixture card " + name);
    }
    private static void use(MatchTable table, Map<Integer, String> answered, Input input, String zone, String name, boolean warp) {
        var source = zone(viewer(input.state()), zone).getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
        var session = table.automatedSeat(input.seat());
        var catalog = JSON.toJsonTree(session.sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var actions = catalog.getAsJsonArray("actions");
        int index = 0;
        if (warp) {
            var actor = table.game.getRegisteredPlayers().get(input.seat());
            var controller = (forge.player.PlayerControllerHuman)actor.getController();
            var actual = actor.getCardsIn(ZoneType.Hand).stream().filter(card -> card.getName().equals(name)).findFirst().orElseThrow();
            var abilities = controller.getInputQueue().getInput().getSourceActions(actual);
            while (index < abilities.size() && !abilities.get(index).isWarp()) index++;
            assertTrue(index < abilities.size(), "The real source offers its Warp alternative");
        }
        session.action(request(input, Map.of("action", "useSourceAction", "key", text(source, "key"),
                "actionId", text(actions.get(index).getAsJsonObject(), "id"))));
        answered.put(input.seat(), text(input.prompt(), "id"));
    }

    @Test(dataProvider = "actors", timeOut = 90000)
    public void cancellingARealCastKeepsSnapshotCatalogAndGraphOnTheSameCurrentOccurrence(int actor) throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var deck = new Deck();
        deck.getMain().add(StaticData.instance().getCommonCards().getCard("Crackling Drake"), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        var players = java.util.stream.IntStream.range(0, 4)
                .mapToObj(index -> MatchTable.automated(deck, "Commander", "Policy " + index)).toList();
        players.get(actor).addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Island")));
        try (var table = new MatchTable("Commander", players, resources, Files.createTempDirectory("action-occurrence-coherence"), true)) {
            table.start();
            var answered = new HashMap<Integer, String>();
            var main = until(table, answered, actor, input -> input.seat() == actor
                    && text(input.prompt(), "inputType").equals("InputPassPriority") && text(input.state(), "phaseKey").equals("MAIN1"));
            var source = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(table.automatedSeat(actor).sourceActions(request(main, Map.of("key", text(source, "key"))))).getAsJsonObject();
            var cast = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).findFirst().orElseThrow();
            table.automatedSeat(actor).action(request(main, Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(cast, "id"))));
            answered.put(actor, text(main.prompt(), "id"));
            var payment = until(table, answered, actor, input -> input.seat() == actor && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            var island = zone(viewer(payment.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var mana = JSON.toJsonTree(table.automatedSeat(actor).sourceActions(request(payment, Map.of("key", text(island, "key"))))).getAsJsonObject();
            table.automatedSeat(actor).action(request(payment, Map.of("action", "useSourceAction", "key", text(island, "key"),
                    "actionId", text(mana.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
            answered.put(actor, text(payment.prompt(), "id"));
            payment = until(table, answered, actor, input -> input.seat() == actor && text(input.prompt(), "inputType").startsWith("InputPayMana")
                    && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject().get("tapped").getAsBoolean());
            assertTrue(payment.prompt().get("cancelEnabled").getAsBoolean());
            table.automatedSeat(actor).action(request(payment, Map.of("action", "cancel")));
            answered.put(actor, text(payment.prompt(), "id"));
            until(table, answered, actor, input -> input.seat() == actor && text(input.prompt(), "inputType").equals("InputPassPriority"));
            HeadlessPlatform.query(table, () -> null);

            var observation = JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()).getAsJsonObject();
            assertCurrentOccurrences(observation);
            for (var seat : table.automatedSeats()) seat.automatedObservation();
            table.publish();
            HeadlessPlatform.query(table, () -> {
                assertCurrentOccurrences(JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()).getAsJsonObject());
                var republished = JSON.toJsonTree(table.automatedSeat(actor).automatedObservation());
                assertEquals(JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()), republished,
                        "Repeated current observations cannot rotate another audience's occurrence identities");
                var state = republished.getAsJsonObject().getAsJsonObject("state");
                var current = new Input(actor, state, state.getAsJsonObject("prompt"));
                var returned = zone(viewer(state), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
                assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(actor).action(request(current,
                        Map.of("action", "useSourceAction", "key", text(returned, "key"), "actionId", text(cast, "id")))));
                assertEquals(JSON.toJsonTree(table.automatedSeat(actor).automatedObservation()), republished,
                        "The old cast handle cannot be rebound to the returned card's new current key");
                return null;
            });
        }
    }

    private static void assertCurrentOccurrences(JsonObject observation) {
        var state = observation.getAsJsonObject("state");
        var cards = new HashMap<String, JsonObject>();
        for (var player : state.getAsJsonArray("players"))
            for (var zone : player.getAsJsonObject().getAsJsonArray("zones"))
                for (var card : zone.getAsJsonObject().getAsJsonArray("cards")) {
                    var occurrence = card.getAsJsonObject(); cards.put(text(occurrence, "key"), occurrence);
                }
        var currentIds = cards.values().stream().map(card -> text(card, "combatId")).toList();
        var domain = observation.getAsJsonObject("domain");
        if (!text(domain, "coverage").equals("Complete")) return;
        for (var source : domain.getAsJsonArray("sources")) {
            var offer = source.getAsJsonObject();
            var current = cards.get(text(offer, "sourceCardKey"));
            assertNotNull(current);
            assertEquals(text(offer, "sourceCardId"), text(current, "combatId"),
                    "The exact current keyed source and its offered action must share one current occurrence");
        }
        for (var action : domain.getAsJsonArray("actions"))
            for (var operation : action.getAsJsonObject().getAsJsonObject("operationGraph").getAsJsonArray("operations")) {
                for (String binding : List.of("sources", "recipients"))
                    for (var entity : operation.getAsJsonObject().getAsJsonObject(binding).getAsJsonArray("entities")) {
                        var value = entity.getAsJsonObject();
                        if (!text(value, "cardId").isEmpty()) assertTrue(currentIds.contains(text(value, "cardId")),
                                "Graph entities cannot reference an occurrence absent from the same authorized snapshot: " + value);
                    }
                var targets = operation.getAsJsonObject().get("targets");
                if (targets != null && targets.isJsonObject()) for (var entity : targets.getAsJsonObject().getAsJsonArray("candidates")) {
                    var value = entity.getAsJsonObject();
                    if (!text(value, "cardId").isEmpty()) assertTrue(currentIds.contains(text(value, "cardId")),
                            "Target candidates must use the same currently entitled occurrence: " + value);
                }
            }
    }

    static Input until(MatchTable table, Map<Integer, String> answered, int actor, Predicate<Input> stop) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(45);
        while (System.nanoTime() < deadline) {
            for (var seat : table.automatedSeats()) {
                var observation = JSON.toJsonTree(seat.automatedObservation()).getAsJsonObject();
                assertCurrentOccurrences(observation);
                var state = observation.getAsJsonObject("state");
                assertNotEquals(text(state, "status"), "error");
                if (!state.has("prompt") || !state.get("prompt").isJsonObject()) continue;
                var prompt = state.getAsJsonObject("prompt");
                int owner = state.get("viewerId").getAsInt();
                if (text(prompt, "id").equals(answered.get(owner))) continue;
                var input = new Input(owner, state, prompt);
                if (stop.test(input)) return input;
                Map<String, ?> answer;
                if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                    answer = Map.of("action", "player", "playerId", actor);
                else if (prompt.has("ordered") && prompt.get("ordered").getAsBoolean())
                    answer = Map.of("choices", prompt.getAsJsonArray("choices").asList().stream()
                            .map(value -> value.getAsJsonObject().get("index").getAsInt()).toList());
                else if ((text(prompt, "inputType").equals("InputPassPriority") || text(prompt, "inputType").contains("Mulligan"))
                        && prompt.get("okEnabled").getAsBoolean()) answer = Map.of("action", "ok");
                else throw new AssertionError("Unexpected setup decision: " + prompt);
                try { seat.action(request(input, answer)); answered.put(owner, text(prompt, "id")); }
                catch (IllegalArgumentException changed) {
                    if (!changed.getMessage().equals("That choice has changed. Use the current prompt.")) throw changed;
                }
            }
            Thread.sleep(5);
        }
        throw new AssertionError("The real cast/rollback did not reach its next decision");
    }
}
