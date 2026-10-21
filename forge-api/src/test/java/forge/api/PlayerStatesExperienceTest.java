package forge.api;

import com.google.common.collect.HashMultiset;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.card.CardStateName;
import forge.card.CardType;
import forge.card.GamePieceType;
import forge.deck.Deck;
import forge.game.card.CardView;
import forge.game.card.CounterEnumType;
import forge.game.card.CounterType;
import forge.game.player.RegisteredPlayer;
import forge.trackable.TrackableProperty;
import forge.trackable.TrackableCollection;
import forge.game.zone.ZoneType;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class PlayerStatesExperienceTest {
    private static final String VNWXT = "Vnwxt, Verbose Host";
    private static final com.google.gson.Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @BeforeClass public void resources() { if (StaticData.instance() == null) EngineResources.load(RESOURCES); }

    @Test public void publicMechanicsUseOneImmutableSchemaAndTransitionsDoNotReplay() throws Exception {
        var registered = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 4; seat++) registered.add(MatchTable.human(new Deck(), "Constructed", "Seat " + seat));
        // Projection fixtures are configured before starting a game.
        try (var table = new MatchTable("Constructed", registered, RESOURCES, Files.createTempDirectory("player-states"))) {
            HeadlessPlatform.activate(table);
            var owner = table.game.getPlayers().get(0);
            owner.setSpeed(2);
            table.game.setMonarch(owner);
            table.game.setHasInitiative(owner);
            owner.setBlessing(true, null);
            var counters = HashMultiset.<CounterType>create();
            counters.add(CounterEnumType.POISON, 3); counters.add(CounterEnumType.ENERGY, 2);
            counters.add(CounterEnumType.EXPERIENCE, 1); counters.add(CounterEnumType.RAD, 1); counters.add(CounterEnumType.TICKET, 1);
            owner.getView().set(TrackableProperty.Counters, counters);
            var concealed = new CardView(31000, null);
            concealed.set(TrackableProperty.Owner, owner.getView()); concealed.set(TrackableProperty.Controller, owner.getView());
            concealed.set(TrackableProperty.Zone, ZoneType.Battlefield); concealed.set(TrackableProperty.Facedown, true);
            concealed.set(TrackableProperty.GamePieceType, GamePieceType.EFFECT);
            var face = concealed.new CardStateView(31000, CardStateName.FaceDown, null);
            face.set(TrackableProperty.Name, "Concealed effect identity"); face.set(TrackableProperty.Type, CardType.parse("Creature", false));
            concealed.set(TrackableProperty.CurrentState, face);
            owner.getView().set(TrackableProperty.Battlefield, new TrackableCollection<>(concealed));
            table.publish();
            var saved = JSON.toJsonTree(table.seat(1).state()).getAsJsonObject();
            var initial = player(saved, owner.getId()).getAsJsonArray("states").deepCopy();
            assertEquals(initial.size(), 9);
            for (int seat = 0; seat < 4; seat++) {
                var state = JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject();
                assertEquals(player(state, owner.getId()).getAsJsonArray("states"), initial);
                var concealedFace = zone(player(state, owner.getId()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
                assertEquals(text(concealedFace, "gamePieceType"), "card", "A hidden game piece never exposes its secret category");
                assertEquals(MatchSession.choiceCard(concealed, table.game.getPlayers().get(seat).getView()).get("gamePieceType"), "card");
                assertFalse(state.toString().contains("Concealed effect identity"));
                var speed = mechanic(state, owner.getId(), "speed");
                assertEquals(text(speed, "kind"), "progress"); assertEquals(speed.get("value").getAsInt(), 2); assertEquals(speed.get("maximum").getAsInt(), 4);
                for (String id : List.of("monarch", "initiative", "citys-blessing")) {
                    var flag = mechanic(state, owner.getId(), id);
                    assertEquals(text(flag, "kind"), "status"); assertTrue(flag.get("value").isJsonNull()); assertTrue(flag.get("maximum").isJsonNull());
                }
                for (var value : initial) assertFalse(text(value.getAsJsonObject(), "detail").isBlank());
                assertTrue(state.getAsJsonArray("activity").asList().stream().noneMatch(value -> text(value.getAsJsonObject(), "kind").equals("playerState")), "The initial board is a baseline");
            }
            owner.setSpeed(4);
            table.game.setMonarch(table.game.getPlayers().get(1));
            table.game.setHasInitiative(null);
            owner.setBlessing(false, null);
            owner.getView().set(TrackableProperty.Counters, HashMultiset.<CounterType>create());
            table.publish();
            for (int seat = 0; seat < 4; seat++) {
                var state = JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject();
                assertEquals(player(state, owner.getId()).getAsJsonArray("states").size(), 1);
                assertEquals(mechanic(state, owner.getId(), "speed").get("value").getAsInt(), 4);
                var changes = state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject()).filter(value -> text(value, "kind").equals("playerState")).toList();
                assertEquals(changes.size(), 10, "One max-speed change, eight removals and the new monarch are public");
                assertTrue(changes.stream().anyMatch(value -> text(value, "message").contains("reached the maximum: 4")));
                assertTrue(changes.stream().anyMatch(value -> text(value, "message").contains("status ended")));
                assertTrue(changes.stream().allMatch(value -> value.get("cardId").isJsonNull() && value.get("cardName").isJsonNull()));
                var snapshot = table.seat(seat).state();
                assertEquals(table.seat(seat).state(), snapshot, "Network reads do not replay state transitions");
            }
            assertEquals(player(saved, owner.getId()).getAsJsonArray("states"), initial, "Published historical records are immutable");
            var stable = JSON.toJsonTree(table.seat(0).state()).getAsJsonObject().getAsJsonArray("activity").deepCopy();
            table.publish();
            assertEquals(JSON.toJsonTree(table.seat(0).state()).getAsJsonObject().getAsJsonArray("activity"), stable, "Repeated publication does not replay unchanged states");
        }
    }

    @Test(timeOut = 90000)
    public void vnwxtStartsSpeedWithoutCastingASorceryAndSpeedPersistsToMax() throws Exception {
        var field = new LinkedHashMap<Integer, List<String>>();
        field.put(0, List.of("Reliquary Tower", "Island", "Island", "Prodigal Pyromancer", "Prodigal Pyromancer", "Thousand-Year Elixir",
                "Temple Bell", "Temple Bell", "Seal of Removal", "Seal of Removal", "Seal of Fire"));
        for (int seat = 1; seat < 4; seat++) field.put(seat, List.of("Reliquary Tower"));
        try (var game = new RulesScenario("player-speed-vnwxt", field, "Island", VNWXT)) {
            var evidence = new LinkedHashMap<String, Object>();
            var main = priority(game);
            int owner = main.state().get("viewerId").getAsInt();
            int opponent = game.state(1).get("viewerId").getAsInt();
            assertSpeed(game, owner, 0);
            main = cast(game, main, "Command");
            assertSpeed(game, owner, 1);
            evidence.put("started", main.state());
            assertEquals(events(main.state(), "cast", owner), 1L, "Only Vnwxt is cast; start your engines is a state-based action");
            for (int seat = 0; seat < 4; seat++) {
                var view = game.state(seat);
                var helper = zone(player(view, owner), "Command").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "gamePieceType").equals("effect") && text(value, "name").contains("Engines")).findFirst().orElseThrow();
                assertEquals(text(helper, "type"), "Effect", "A generated speed marker is not the unrelated printed sorcery with the same name");
                assertFalse(helper.get("selectable").getAsBoolean(), "A generated speed marker has no cast action");
            }
            main = damage(game, main, "Prodigal Pyromancer", opponent, true);
            assertSpeed(game, owner, 2);
            main = damage(game, main, "Prodigal Pyromancer", opponent, false);
            assertSpeed(game, owner, 2);
            main = bounce(game, main);
            assertSpeed(game, owner, 2);
            evidence.put("sourceRemoved", main.state());
            int firstTurn = main.state().get("turn").getAsInt();
            assertTrue(game.answer(main, Map.of("action", "ok")));
            var foreign = game.until(input -> input.seat() == 0 && input.state().get("activePlayerId").getAsInt() == opponent
                    && text(input.state(), "phaseKey").equals("MAIN1") && text(input.prompt(), "inputType").equals("InputPassPriority"));
            foreign = damage(game, foreign, "Seal of Fire", opponent, false);
            assertSpeed(game, owner, 2);
            assertTrue(game.answer(foreign, Map.of("action", "ok")));
            main = nextOwnMain(game, firstTurn);
            main = damage(game, main, "Prodigal Pyromancer", opponent, true);
            assertSpeed(game, owner, 3);
            main = damage(game, main, "Prodigal Pyromancer", opponent, false);
            assertSpeed(game, owner, 3);
            int secondTurn = main.state().get("turn").getAsInt();
            assertTrue(game.answer(main, Map.of("action", "ok")));
            main = nextOwnMain(game, secondTurn);
            main = damage(game, main, "Prodigal Pyromancer", opponent, true);
            assertSpeed(game, owner, 4);
            evidence.put("maximumWithoutSource", main.state());
            main = cast(game, main, "Hand");
            assertSpeed(game, owner, 4);
            int before = count(viewer(main.state()), "Hand"), library = count(viewer(main.state()), "Library");
            main = bell(game, main);
            assertEquals(count(viewer(main.state()), "Hand"), before + 2, "Vnwxt's real max-speed replacement doubles the draw");
            assertEquals(count(viewer(main.state()), "Library"), library - 2);
            evidence.put("maximumDraw", main.state());
            main = bounce(game, main);
            assertSpeed(game, owner, 4);
            before = count(viewer(main.state()), "Hand"); library = count(viewer(main.state()), "Library");
            main = bell(game, main);
            assertEquals(count(viewer(main.state()), "Hand"), before + 1, "The card-specific draw replacement ends when Vnwxt leaves");
            assertEquals(count(viewer(main.state()), "Library"), library - 1);
            evidence.put("after", main.state());
            evidence.put("viewsBySeat", List.of(game.state(0), game.state(1), game.state(2), game.state(3)));
            Files.writeString(game.evidence.resolve("player-states-observations.json"), JSON.toJson(evidence));
            for (int seat = 0; seat < 4; seat++) {
                var view = game.state(seat);
                var transitions = view.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "kind").equals("playerState") && value.get("playerId").getAsInt() == owner).toList();
                assertTrue(transitions.stream().anyMatch(value -> text(value, "message").contains("Speed started at 1")));
                assertTrue(transitions.stream().anyMatch(value -> text(value, "message").contains("Speed increased from 1 to 2")));
                assertTrue(transitions.stream().anyMatch(value -> text(value, "message").contains("Speed reached the maximum: 4")));
                assertTrue(transitions.stream().allMatch(value -> value.get("cardId").isJsonNull() && value.get("cardName").isJsonNull()));
                if (seat != 0) assertTrue(zone(player(view, owner), "Hand").getAsJsonArray("cards").isEmpty());
            }
        }
    }

    private static RulesScenario.Input cast(RulesScenario game, RulesScenario.Input main, String from) throws Exception {
        assertTrue(game.answer(main, Map.of("action", "card", "key", text(card(main.state(), from, VNWXT, false), "key"))));
        var payment = game.until(input -> text(input.prompt(), "inputType").startsWith("InputPayMana"));
        assertTrue(game.answer(payment, Map.of("action", "ok")));
        awaitPayment(game);
        var pending = pending(game);
        assertTrue(pending.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject()).anyMatch(value -> text(value, "name").equals(VNWXT)));
        assertTrue(game.answer(pending, Map.of("action", "ok")));
        return priority(game);
    }

    private static RulesScenario.Input damage(RulesScenario game, RulesScenario.Input main, String source, int target, boolean speedTrigger) throws Exception {
        assertTrue(game.answer(main, Map.of("action", "card", "key", text(card(main.state(), "Battlefield", source, source.equals("Prodigal Pyromancer")), "key"))));
        var targeting = game.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
        assertTrue(game.answer(targeting, Map.of("action", "player", "playerId", target)));
        var pending = pending(game);
        assertTrue(game.answer(pending, Map.of("action", "ok")));
        if (speedTrigger) {
            var trigger = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject()).anyMatch(value -> text(value, "text").contains("speed increases")));
            assertTrue(trigger.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject()).allMatch(value -> value.get("ability").getAsBoolean()),
                    "Speed advancement is a triggered ability, not a sorcery cast");
            assertTrue(game.answer(trigger, Map.of("action", "ok")));
        }
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority") && input.state().getAsJsonArray("stack").isEmpty());
    }

    private static RulesScenario.Input bounce(RulesScenario game, RulesScenario.Input main) throws Exception {
        assertTrue(game.answer(main, Map.of("action", "card", "key", text(card(main.state(), "Battlefield", "Seal of Removal", false), "key"))));
        var targeting = game.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
        assertTrue(game.answer(targeting, Map.of("action", "card", "key", text(card(targeting.state(), "Battlefield", VNWXT, false), "key"))));
        var pending = pending(game);
        String prior = text(pending.prompt(), "id");
        assertTrue(game.answer(pending, Map.of("action", "ok")));
        var replacement = game.until(input -> text(input.prompt(), "inputType").equals("InputConfirm") && text(input.prompt(), "message").contains("command zone"));
        assertTrue(game.answer(replacement, Map.of("action", "cancel")));
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        do {
            var state = game.state(0); var value = state.get("prompt");
            if (value != null && !value.isJsonNull()) {
                var prompt = value.getAsJsonObject();
                if (text(prompt, "inputType").equals("InputPassPriority") && !text(prompt, "id").equals(prior)
                        && state.getAsJsonArray("stack").isEmpty() && zone(viewer(state), "Hand").getAsJsonArray("cards").asList().stream()
                        .anyMatch(entry -> text(entry.getAsJsonObject(), "name").equals(VNWXT))) return new RulesScenario.Input(0, state, prompt);
            }
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        throw new AssertionError("The actual bounce must complete before inspecting persistent speed");
    }

    private static RulesScenario.Input bell(RulesScenario game, RulesScenario.Input main) throws Exception {
        assertTrue(game.answer(main, Map.of("action", "card", "key", text(card(main.state(), "Battlefield", "Temple Bell", true), "key"))));
        var pending = pending(game);
        assertTrue(game.answer(pending, Map.of("action", "ok")));
        return priority(game);
    }

    private static RulesScenario.Input priority(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt()
                && text(input.prompt(), "inputType").equals("InputPassPriority") && input.state().getAsJsonArray("stack").isEmpty());
    }

    private static RulesScenario.Input pending(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority") && !input.state().getAsJsonArray("stack").isEmpty());
    }

    private static RulesScenario.Input nextOwnMain(RulesScenario game, int turn) throws Exception {
        return game.until(input -> input.seat() == 0 && input.state().get("turn").getAsInt() > turn && text(input.state(), "phaseKey").equals("MAIN1")
                && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt()
                && text(input.prompt(), "inputType").equals("InputPassPriority") && input.state().getAsJsonArray("stack").isEmpty());
    }

    private static void awaitPayment(RulesScenario game) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        do {
            var state = game.state(0); var value = state.get("prompt");
            if (!state.getAsJsonArray("stack").isEmpty() || value != null && !value.isJsonNull() && !text(value.getAsJsonObject(), "inputType").startsWith("InputPayMana")) return;
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        fail("One Auto payment must complete before the next semantic decision");
    }

    private static void assertSpeed(RulesScenario game, int owner, int value) {
        for (int seat = 0; seat < 4; seat++) {
            var states = player(game.state(seat), owner).getAsJsonArray("states");
            if (value == 0) assertTrue(states.asList().stream().noneMatch(entry -> text(entry.getAsJsonObject(), "id").equals("speed")));
            else assertEquals(mechanic(game.state(seat), owner, "speed").get("value").getAsInt(), value);
        }
    }

    private static JsonObject mechanic(JsonObject state, int player, String id) {
        return RulesScenario.player(state, player).getAsJsonArray("states").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "id").equals(id)).findFirst().orElseThrow();
    }

    private static JsonObject card(JsonObject state, String name, String label, boolean untapped) {
        return zone(viewer(state), name).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(label) && (!untapped || !value.get("tapped").getAsBoolean())).findFirst().orElseThrow();
    }

    private static long events(JsonObject state, String kind, int player) {
        return state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals(kind) && !value.get("playerId").isJsonNull() && value.get("playerId").getAsInt() == player).count();
    }
}
