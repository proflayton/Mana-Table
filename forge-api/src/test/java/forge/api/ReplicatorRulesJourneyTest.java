package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static forge.api.CardDataRules.*;
import static org.testng.Assert.*;

/** Actual casts, target restrictions, payment and token-copy resolution. */
public class ReplicatorRulesJourneyTest {
    @Test(timeOut = 90000)
    public void entersCreatesFoodAndOnlyControlledTokensCanBeCopiedTapped() throws Exception {
        // The inert seats must survive a normal first-turn draw/cleanup while
        // this journey waits for the actor's next own sorcery-speed window.
        try (var game = new RulesScenario("replicator-food-copy", Map.of(
                0, List.of("Wastes", "Wastes", "Spellbook"), 1, List.of("Wastes", "Wastes"),
                2, List.of("Spellbook"), 3, List.of("Spellbook")),
                "Replicator", "Rhys the Redeemed")) {
            var main = main(game, 0, 1);
            int actor = main.state().get("viewerId").getAsInt();
            int opponent = game.state(1).get("viewerId").getAsInt();
            assertTrue(food(main.state(), actor).isEmpty());
            main = fund(game, main, 2);
            invoke(game, main, cards(viewer(main.state()), "Hand", "Replicator").get(0));
            var entered = settle(game, 0);
            assertEquals(food(entered.state(), actor).size(), 1, "The actual Replicator ETB creates exactly one Food");
            var source = cards(viewer(entered.state()), "Battlefield", "Replicator").get(0);
            String sourceId = text(source, "visualId"), originalId = text(food(entered.state(), actor).get(0), "visualId");
            assertFalse(food(entered.state(), actor).get(0).get("tapped").getAsBoolean());
            save(game, "01-real-etb.json", entered.state());

            // Produce the foreign token through another real cast, rather than
            // injecting a token or editing a running battlefield for a negative.
            var other = fund(game, main(game, 1, 1), 2);
            invoke(game, other, cards(viewer(other.state()), "Hand", "Replicator").get(0));
            var otherEntered = settle(game, 1);
            assertEquals(food(otherEntered.state(), opponent).size(), 1);
            String foreignId = text(food(otherEntered.state(), opponent).get(0), "visualId");

            main = fund(game, main(game, 0, 2), 2);
            source = occurrence(main.state(), sourceId);
            assertFalse(source.get("tapped").getAsBoolean());
            assertEquals(viewer(main.state()).getAsJsonObject("mana").get("C").getAsInt(), 2);
            invoke(game, main, source);
            var targets = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var original = occurrence(targets.state(), originalId);
            var foreign = occurrence(targets.state(), foreignId);
            var nontoken = cards(viewer(targets.state()), "Battlefield", "Spellbook").get(0);
            assertTrue(original.get("selectable").getAsBoolean());
            assertFalse(foreign.get("selectable").getAsBoolean(), "The opponent-controlled token is not a legal target");
            assertFalse(nontoken.get("selectable").getAsBoolean(), "An owned nontoken is not a legal target");
            for (var rejected : List.of(foreign, nontoken)) {
                assertFalse(text(rejected, "key").isEmpty(), "Use only an actually published current card key for the negative");
                var before = footprint(targets.state(), actor, sourceId);
                game.rejected(targets, Map.of("action", "card", "key", text(rejected, "key")), "That card cannot be selected for this choice");
                assertEquals(footprint(game.state(0), actor, sourceId), before, "A rejected target cannot pay/tap/change the board");
            }
            save(game, "02-current-targets.json", targets.state());
            assertTrue(game.answer(targets, Map.of("action", "card", "key", text(original, "key"))));
            var copied = settle(game, 0);
            var ownFood = food(copied.state(), actor);
            assertEquals(ownFood.size(), 2); assertEquals(food(copied.state(), opponent).size(), 1);
            var copy = ownFood.stream().filter(card -> !text(card, "visualId").equals(originalId)).findFirst().orElseThrow();
            var retained = occurrence(copied.state(), originalId);
            assertNotEquals(text(copy, "visualId"), originalId);
            assertEquals(text(copy, "gamePieceType"), "token");
            assertTrue(text(copy, "type").contains("Artifact") && text(copy, "type").contains("Food"));
            assertEquals(text(copy, "type"), text(retained, "type"));
            assertEquals(text(copy, "name"), text(retained, "name"));
            assertEquals(text(copy, "text"), text(retained, "text"));
            assertTrue(copy.get("tapped").getAsBoolean(), "The first settled copy publication is already tapped");
            assertFalse(retained.get("tapped").getAsBoolean(), "The original Food was not tapped to manufacture this result");
            assertTrue(occurrence(copied.state(), sourceId).get("tapped").getAsBoolean(), "Replicator paid its actual tap cost");
            assertEquals(viewer(copied.state()).getAsJsonObject("mana").get("C").getAsInt(), 0, "The actual activation spent two mana");
            assertEquals(text(copied.state(), "phaseKey"), "MAIN1");
            assertEquals(copied.state().get("turn"), main.state().get("turn"), "No later untap phase supplies the tapped-copy witness");
            assertTrue(game.table.game.getAction().readState(() -> game.table.game.getCardsIn(ZoneType.Battlefield).stream()
                    .filter(card -> card.isToken() && card.getType().hasSubtype("Food") && card.getController().getId() == actor)
                    .allMatch(card -> card.getOwner().getId() == actor)), "Both real controlled Food tokens retain the actor as owner");
            save(game, "03-resolved-copy.json", copied.state());
            var spectator = HeadlessPlatform.query(game.table, () -> new Gson().toJsonTree(SpectatorObservation.project(game.table,
                    ((Number)game.session(0).state().get("boardRevision")).longValue(), null)).getAsJsonObject());
            assertEquals(food(spectator, actor).size(), 2); assertEquals(food(spectator, opponent).size(), 1);
            assertTrue(occurrence(spectator, text(copy, "visualId")).get("tapped").getAsBoolean());
            save(game, "04-public-copy.json", spectator);
        }
    }

    private static List<JsonObject> food(JsonObject state, int playerId) {
        return cards(player(state, playerId), "Battlefield", null).stream()
                .filter(card -> text(card, "gamePieceType").equals("token") && text(card, "type").contains("Food")).toList();
    }
    private static Object footprint(JsonObject state, int actor, String source) {
        return List.of(state.get("boardRevision"), player(state, actor).get("mana").deepCopy(), occurrence(state, source).get("tapped"));
    }
}

/** Shared current-input helpers for the bounded card-data rules journeys. */
final class CardDataRules {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
    private CardDataRules() { }
    static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    static Input main(RulesScenario game, int seat, int minimumTurn) throws Exception {
        return game.until(input -> input.seat() == seat && priority(input) && text(input.state(), "phaseKey").equals("MAIN1")
                && input.state().get("viewerId").equals(input.state().get("activePlayerId"))
                && input.state().get("turn").getAsInt() >= minimumTurn && input.state().getAsJsonArray("stack").isEmpty());
    }
    static List<JsonObject> cards(JsonObject player, String zone, String name) {
        return zone(player, zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> name == null || text(card, "name").equals(name)).toList();
    }
    static JsonObject occurrence(JsonObject state, String id) {
        return state.getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                .flatMap(player -> cards(player, "Battlefield", null).stream()).filter(card -> text(card, "visualId").equals(id)).findFirst().orElseThrow();
    }
    static void invoke(RulesScenario game, Input input, JsonObject source) {
        var actions = JSON.toJsonTree(game.session(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        assertEquals(actions.getAsJsonArray("actions").size(), 1, "The controlled source has one real current action");
        var action = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"))));
    }
    static Input fund(RulesScenario game, Input input, int amount) throws Exception {
        int seat = input.seat();
        for (int paid = 0; paid < amount; paid++) {
            var land = cards(viewer(input.state()), "Battlefield", "Wastes").stream().filter(card -> !card.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            invoke(game, input, land);
            input = main(game, seat, input.state().get("turn").getAsInt());
        }
        assertEquals(viewer(input.state()).getAsJsonObject("mana").get("C").getAsInt(), amount);
        return input;
    }
    static Input settle(RulesScenario game, int seat) throws Exception {
        for (int count = 0; count < 8; count++) {
            var input = game.until(current -> current.seat() == seat && (text(current.prompt(), "inputType").startsWith("InputPayMana")
                    || text(current.prompt(), "inputType").equals("InputSelectTargets")
                    || priority(current) && current.state().getAsJsonArray("stack").isEmpty()));
            if (priority(input)) return input;
            assertTrue(input.prompt().get("okEnabled").getAsBoolean(), "Only a funded bill or completed real target choice may continue");
            assertTrue(game.answer(input, Map.of("action", "ok")));
        }
        throw new AssertionError("The actual card-data action did not settle within eight continuations");
    }
    static void save(RulesScenario game, String file, Object value) throws Exception {
        Files.writeString(game.evidence.resolve(file), JSON.toJson(value));
    }
}
