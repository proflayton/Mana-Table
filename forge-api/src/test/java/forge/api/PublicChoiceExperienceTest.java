package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;
import java.nio.file.Files;
import java.util.*;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual waiting rules inputs, public occurrences and authenticated seat
 * endpoints. Cosmetic consideration never stages or confirms a rules choice. */
public class PublicChoiceExperienceTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000) public void otherSeatsSeeAttackAndBlockAimsBeforeAnyAssignmentChanges() throws Exception {
        try (var game = new RulesScenario("public-choice-combat", Map.of(0, List.of("Memnite", "Reliquary Tower"),
                1, List.of("Memnite", "Reliquary Tower"), 2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")))) {
            var attack = game.until(input -> input.seat() == 0 && type(input, "InputAttack"));
            var attacker = card(attack.state(), 0, "Memnite");
            int defender = game.state(1).get("viewerId").getAsInt();
            var source = reference("card", text(attacker, "visualId")); var target = reference("player", Integer.toString(defender));
            var captured = HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0); var observer = game.state(1);
                assertChoice(before, "attack"); assertEquals(before.get("publicChoice"), observer.get("publicChoice"));
                assertTrue(before.getAsJsonObject("combat").getAsJsonArray("attackers").isEmpty());
                assertEquals(choices(game.session(0).presence(update(before, 1, source, target))).size(), 1);
                var received = game.session(1).presence(clear(observer, 1));
                assertEquals(choices(received).size(), 1);
                assertEquals(choices(received).get(0).getAsJsonObject("preview"), JSON.toJsonTree(Map.of("source", source, "target", target)));
                assertEquals(game.state(0), before, "Consideration cannot change a command, timer, history, board or prompt");
                assertEquals(game.state(1), observer);
                return Map.of("actor", before, "observer", observer, "received", received);
            });
            Files.writeString(game.evidence.resolve("attack-aim.json"), JSON.toJson(captured));
            assertTrue(game.answer(attack, Map.of("action", "attack", "attackerKey", text(attacker, "key"), "defenderPlayerId", defender)));
            var staged = game.until(input -> input.seat() == 0 && type(input, "InputAttack"));
            assertEquals(game.state(1).getAsJsonObject("combat").getAsJsonArray("attackers").size(), 1,
                    "An ordinary assignment was already shared before final confirmation");
            assertTrue(choices(game.session(0).presence(update(attack.state(), 2, source, target))).isEmpty(), "The earlier aim cannot survive an actual assignment publication");
            assertTrue(game.answer(staged, Map.of("action", "ok")));
            var block = game.until(input -> input.seat() == 1 && type(input, "InputBlock"));
            var blocker = card(block.state(), 1, "Memnite");
            var blockSource = reference("card", text(blocker, "visualId"));
            var blockTarget = reference("card", text(card(block.state(), 0, "Memnite"), "visualId"));
            var blockCaptured = HeadlessPlatform.query(game.table, () -> {
                var before = game.state(1); assertChoice(before, "block");
                assertEquals(choices(game.session(1).presence(update(before, 2, blockSource, blockTarget))).size(), 1);
                var received = game.session(0).presence(clear(game.state(0), 3));
                assertEquals(choices(received).size(), 1);
                assertEquals(game.state(1), before);
                assertTrue(before.getAsJsonObject("combat").getAsJsonArray("attackers").get(0).getAsJsonObject().getAsJsonArray("blockerIds").isEmpty());
                return Map.of("actor", before, "received", received);
            });
            Files.writeString(game.evidence.resolve("block-aim.json"), JSON.toJson(blockCaptured));
        }
    }

    @Test(timeOut = 90000) public void aPublicAbilityCanAimAtPublicCardsOrPlayersWithoutGivingAwayPrivateFacesOrCommands() throws Exception {
        try (var game = new RulesScenario("public-choice-target", Map.of(0, List.of("Concordant Crossroads", "Prodigal Pyromancer"),
                1, List.of("Grizzly Bears")))) {
            var input = main(game);
            start(game, input, card(input.state(), 0, "Prodigal Pyromancer"), "1 damage");
            var targeting = game.until(value -> type(value, "InputSelectTargets"));
            var selected = card(targeting.state(), 1, "Grizzly Bears");
            var source = reference("card", text(card(targeting.state(), 0, "Prodigal Pyromancer"), "visualId"));
            var target = reference("card", text(selected, "visualId"));
            var captured = HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0); var observer = game.state(1);
                assertChoice(before, "target"); assertEquals(before.getAsJsonObject("publicChoice").get("source"), JSON.toJsonTree(source));
                assertEquals(before.get("publicChoice"), observer.get("publicChoice"));
                assertTrue(before.get("publicInspection") == null || before.get("publicInspection").isJsonNull(), "The old priority-only grant stays retired");
                var frame = game.session(0).presence(update(before, 1, source, target)); assertEquals(choices(frame).size(), 1);
                var received = game.session(1).presence(clear(observer, 1)); assertEquals(choices(received).size(), 1);
                assertEquals(game.state(0), before); assertEquals(game.state(1), observer);
                var privateCard = zone(viewer(before), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
                assertTrue(choices(game.session(0).presence(update(before, 2, source, reference("card", text(privateCard, "visualId"))))).isEmpty());
                var forged = update(before, 3, target, source);
                assertTrue(choices(game.session(0).presence(forged)).isEmpty(), "A target cannot impersonate the actual public ability source");
                var request = new JsonObject(); request.addProperty("sessionId", game.table.id);
                var busy = JSON.toJsonTree(game.session(3).concede(request)).getAsJsonObject();
                assertEquals(text(busy, "status"), "resolving");
                assertTrue(busy.get("publicChoice") == null || busy.get("publicChoice").isJsonNull(), "Concession admission clears the audience's preview grant immediately");
                assertEquals(game.state(0).get("publicChoice"), before.get("publicChoice"), "The still-acting peer keeps its own grant");
                return Map.of("actor", before, "observer", observer, "received", received, "busy", busy);
            });
            Files.writeString(game.evidence.resolve("target-aim.json"), JSON.toJson(captured));
            awaitConcession(game.table, game.session(3));
            var departed = game.state(3);
            assertTrue(departed.get("publicChoice") == null || departed.get("publicChoice").isJsonNull());
            HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0); game.table.publish(); var after = game.state(0);
                assertNotEquals(before.get("publicChoice"), after.get("publicChoice"));
                assertTrue(choices(game.session(0).presence(update(before, 4, source, target))).isEmpty());
                return null;
            });
        }
    }

    @Test(timeOut = 90000) public void aResolvingOrConcedingAudienceCannotReceiveAnotherPlayersCurrentAim() throws Exception {
        var clock = new java.util.concurrent.atomic.AtomicLong(1000);
        try (var game = new RulesScenario("public-choice-audience", Map.of(0, List.of("Concordant Crossroads", "Prodigal Pyromancer"),
                1, List.of("Grizzly Bears")), DecisionTempo.Settings.off(), clock::get)) {
            var input = main(game); start(game, input, card(input.state(), 0, "Prodigal Pyromancer"), "1 damage");
            game.until(value -> type(value, "InputSelectTargets"));
            var captured = HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0); assertChoice(before, "target");
                var source = reference("card", text(card(before, 0, "Prodigal Pyromancer"), "visualId"));
                var target = reference("player", Integer.toString(game.state(1).get("viewerId").getAsInt()));
                assertEquals(choices(game.session(0).presence(update(before, 1, source, target))).size(), 1);
                // Exercise the actual audience publication helper, without
                // changing rules state or authoring a synthetic wire frame.
                var markBusy = Arrays.stream(MatchSession.class.getDeclaredMethods()).filter(method -> method.getName().equals("markBusy")).findFirst().orElseThrow();
                markBusy.setAccessible(true); markBusy.invoke(game.session(3), new Object[]{null});
                var busy = game.state(3); assertEquals(text(busy, "status"), "resolving");
                assertTrue(busy.get("publicChoice") == null || busy.get("publicChoice").isJsonNull());
                var withheld = game.session(3).presence(clear(busy, 1));
                assertTrue(choices(withheld).isEmpty(), "A resolving audience cannot receive another player's current aim through the presence response");
                var playing = game.session(1).presence(clear(game.state(1), 1));
                assertEquals(choices(playing).size(), 1, "Withholding one audience must not erase the actor's valid cue for playing peers");
                var afterBusy = game.state(0);
                assertEquals(afterBusy.get("revision").getAsLong(), before.get("revision").getAsLong() + 1);
                assertEquals(afterBusy.getAsJsonObject("undo").get("barrierVersion").getAsLong(), before.getAsJsonObject("undo").get("barrierVersion").getAsLong() + 1,
                        "The intentional admission retires Undo; a presence read does not perform that transition");
                var expected = before.deepCopy(); var observed = afterBusy.deepCopy();
                for (var state : List.of(expected, observed)) { state.remove("revision"); state.getAsJsonObject("undo").remove("barrierVersion"); }
                assertEquals(observed, expected, "Every other board, choice, source, target, timer and history field stays exact");
                game.table.publish(); clock.addAndGet(250);
                var fresh = game.state(0); assertChoice(fresh, "target");
                assertEquals(choices(game.session(0).presence(update(fresh, 2, source, target))).size(), 1);
                var request = new JsonObject(); request.addProperty("sessionId", game.table.id);
                var conceded = JSON.toJsonTree(game.session(3).concede(request)).getAsJsonObject();
                assertTrue(choices(game.session(3).presence(clear(conceded, 2))).isEmpty(), "Concession admission cannot borrow a surviving actor's grant");
                assertEquals(choices(game.session(1).presence(clear(game.state(1), 2))).size(), 1);
                return Map.of("actor", before, "busy", busy, "withheld", withheld, "playing", playing, "conceded", conceded);
            });
            Files.writeString(game.evidence.resolve("audience-presence.json"), JSON.toJson(captured));
            awaitConcession(game.table, game.session(3));
            assertTrue(choices(game.session(3).presence(clear(game.state(3), 3))).isEmpty(), "The departed audience stays outside current presence projection");
        }
    }

    @Test(timeOut = 90000) public void aSpellStillInAPlayersHandCannotPublishItsSourceOrTentativePublicTarget() throws Exception {
        try (var game = new RulesScenario("public-choice-private-spell", Map.of(0, List.of("Forest", "Grizzly Bears"),
                1, List.of("Grizzly Bears")), "Battlegrowth", "Rhys the Redeemed")) {
            var input = main(game); start(game, input, card(input.state(), 0, "Forest"), "Add {G}");
            input = game.until(value -> value.seat() == 0 && priority(value) && viewer(value.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            var spell = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            start(game, input, spell, "Put a +1/+1 counter");
            var targeting = game.until(value -> type(value, "InputSelectTargets"));
            for (int seat = 0; seat < 4; seat++) {
                var state = game.state(seat);
                assertTrue(state.get("publicChoice") == null || state.get("publicChoice").isJsonNull(), "Own reading permission cannot reveal an unannounced private spell to seat " + seat);
            }
            assertTrue(card(targeting.state(), 1, "Grizzly Bears").get("selectable").getAsBoolean());
        }
    }

    private static void assertChoice(JsonObject state, String kind) {
        var context = state.get("publicChoice");
        assertNotNull(context, "The actual waiting " + kind + " input must publish a public-choice context");
        assertFalse(context.isJsonNull(), "The actual waiting " + kind + " input must publish a public-choice context");
        assertEquals(text(context.getAsJsonObject(), "kind"), kind);
    }
    private static Input main(RulesScenario game) throws Exception { return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1")); }
    private static boolean priority(Input input) { return type(input, "InputPassPriority"); }
    private static boolean type(Input input, String type) { return text(input.prompt(), "inputType").equals(type); }
    private static JsonObject card(JsonObject state, int seat, String name) {
        return zone(state.getAsJsonArray("players").get(seat).getAsJsonObject(), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static void start(RulesScenario game, Input input, JsonObject source, String contains) {
        var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "text").contains(contains)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"), "actionId", text(action, "id"))));
    }
    private static Map<String, String> reference(String kind, String id) { return Map.of("kind", kind, "id", id); }
    private static JsonObject clear(JsonObject state, long sequence) {
        var update = new JsonObject(); update.addProperty("gameId", text(state, "id"));
        update.addProperty("boardRevision", state.get("boardRevision").getAsLong()); update.addProperty("sequence", sequence); return update;
    }
    private static JsonObject update(JsonObject state, long sequence, Map<String, String> source, Map<String, String> target) {
        var update = clear(state, sequence); update.addProperty("choiceContextId", text(state.getAsJsonObject("publicChoice"), "id"));
        update.add("choice", JSON.toJsonTree(Map.of("source", source, "target", target))); return update;
    }
    private static List<JsonObject> choices(Map<String, Object> frame) {
        return JSON.toJsonTree(frame).getAsJsonObject().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
}
