package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.card.CounterEnumType;
import forge.game.event.GameEventCardCounters;
import forge.game.event.GameEventCause;
import forge.game.event.GameEventSpellResolved;
import forge.game.event.GameEventTurnBegan;
import forge.game.player.RegisteredPlayer;
import forge.game.zone.ZoneType;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Counter causes come from the actual resolving effect, not neighboring events. */
public class PublicCounterCausesTest {
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static final com.google.gson.Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final String TOPH = "Toph, the First Metalbender";
    private static final String RETREAT = "Felidar Retreat";

    @BeforeClass public void resources() { if (StaticData.instance() == null) EngineResources.load(RESOURCES); }

    @Test(timeOut = 90000)
    public void earthbendThenRepeatedLandfallPublishExactSourcesTargetsAndIndependentTriggerGroups() throws Exception {
        try (var game = new RulesScenario("toph-felidar-counter-causes",
                Map.of(0, List.of(TOPH, RETREAT, "Forest", "Grizzly Bears")))) {
            var first = main(game, 0);
            int owner = first.state().get("viewerId").getAsInt();
            int firstTurn = first.state().get("turn").getAsInt();
            assertTrue(game.answer(first, Map.of("action", "ok")));
            var second = main(game, firstTurn);
            var earthbend = counters(second.state(), TOPH);
            assertEquals(earthbend.size(), 1);
            assertCounter(earthbend.get(0), 0, 2, TOPH);
            assertTrue(text(earthbend.get(0).getAsJsonObject("cause"), "ability").toLowerCase(Locale.ROOT).contains("earthbend"));
            String land = text(card(second.state(), owner, "Forest"), "visualId");
            String tophId = text(card(second.state(), owner, TOPH), "visualId");
            String retreatId = text(card(second.state(), owner, RETREAT), "visualId");
            String bears = text(card(second.state(), owner, "Grizzly Bears"), "visualId");
            assertEquals(text(earthbend.get(0), "cardId"), land);
            assertEquals(text(earthbend.get(0).getAsJsonObject("cause"), "cardId"), tophId);

            var groups = new ArrayList<String>();
            for (int trigger = 0; trigger < 2; trigger++) {
                assertTrue(game.answer(second, Map.of("action", "card", "key",
                        text(zone(viewer(second.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject(), "key"))));
                var mode = game.until(i -> text(i.prompt(), "kind").equals("choice"));
                var counterMode = mode.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "label").contains("+1/+1")).findFirst().orElseThrow();
                assertTrue(game.answer(mode, Map.of("choices", List.of(counterMode.get("index").getAsInt()))));
                int expectedEvents = (trigger + 1) * 3;
                var resolved = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputPassPriority")
                        && i.state().getAsJsonArray("stack").isEmpty() && counters(i.state(), RETREAT).size() == expectedEvents);
                List<JsonObject> events = counters(resolved.state(), RETREAT);
                var current = events.subList(trigger * 3, expectedEvents);
                String causeId = text(current.get(0).getAsJsonObject("cause"), "id");
                assertFalse(causeId.isEmpty()); assertFalse(groups.contains(causeId), "Separate landfall triggers need separate groups");
                groups.add(causeId);
                assertEquals(current.stream().map(value -> text(value, "cardId")).collect(java.util.stream.Collectors.toSet()),
                        Set.of(land, tophId, bears), "Every actual creature, including the earthbent land, is explained");
                for (var event : current) {
                    var cause = event.getAsJsonObject("cause");
                    assertEquals(text(cause, "id"), causeId); assertEquals(text(cause, "cardId"), retreatId);
                    assertEquals(text(cause, "kind"), "trigger"); assertEquals(cause.get("controllerId").getAsInt(), owner);
                    assertTrue(text(cause, "ability").contains("+1/+1"));
                    assertFalse(cause.toString().contains("DB$")); assertFalse(cause.toString().contains("ValidCards$"));
                    int before = event.getAsJsonObject("change").get("before").getAsInt();
                    assertCounter(event, before, before + 1, RETREAT);
                    assertEquals(before, text(event, "cardId").equals(land) ? trigger == 0 ? 2 : 5 : trigger);
                    var recipient = zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                            .map(value -> value.getAsJsonObject()).filter(value -> text(value, "visualId").equals(text(event, "cardId"))).findFirst().orElseThrow();
                    assertEquals(recipient.getAsJsonObject("counters").get("+1/+1").getAsInt(), before + 1);
                }
                for (int seat = 0; seat < 4; seat++) {
                    var all = counters(game.state(seat), RETREAT);
                    assertEquals(all.size(), expectedEvents);
                    assertEquals(all.subList(trigger * 3, expectedEvents), current,
                            "The same public event/cause identities reach every private seat view");
                    var animated = card(game.state(seat), owner, "Forest");
                    assertTrue(text(animated, "type").contains("Creature"));
                    assertTrue(animated.getAsJsonArray("states").asList().stream().map(value -> value.getAsJsonObject())
                            .anyMatch(value -> text(value, "key").equals("vigilance")));
                }
                if (trigger == 1) break;
                int previousTurn = resolved.state().get("turn").getAsInt();
                assertTrue(game.answer(resolved, Map.of("action", "ok")));
                second = main(game, previousTurn);
            }
            assertNotEquals(groups.get(0), groups.get(1));
        }
    }

    @Test public void disclosureUsesEachViewerAndPublicFaceDownTargetWithoutBorrowingAnotherSpellsCause() throws Exception {
        try (var table = table("counter-cause-disclosure")) {
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(RETREAT), owner);
            var target = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Invisible Stalker"), owner);
            owner.getZone(ZoneType.Hand).add(source, null, null);
            owner.getZone(ZoneType.Battlefield).add(target, null, null); target.turnFaceDown(true);
            table.publish();
            var activities = table.game.getPlayers().stream().map(player -> new MatchActivity(player.getView(), table.identities)).toList();
            var cause = new GameEventCause(source.getView(), "Put a +1/+1 counter on each creature you control.", owner.getView(), "trigger", "one-exact-instance");
            for (int seat = 0; seat < 4; seat++) {
                var activity = activities.get(seat);
                activity.onGameEvent(new GameEventCardCounters(target.getView(), CounterEnumType.P1P1, 0, 1, cause));
                var entry = activity.snapshot().get(0);
                var state = JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject();
                assertEquals(entry.cardId(), text(card(state, owner.getId(), "Face-down card"), "combatId"));
                assertEquals(entry.cardName(), "Face-down card"); assertEquals(entry.change().before(), 0); assertEquals(entry.change().after(), 1);
                assertFalse(JSON.toJson(entry).contains("Invisible Stalker"));
                if (seat == 0) assertEquals(entry.cause().name(), RETREAT);
                else { assertNull(entry.cause()); assertFalse(JSON.toJson(entry).contains(RETREAT)); }
            }
            // A visible unrelated spell immediately before the counter mutation is
            // deliberately insufficient evidence of causality.
            var shock = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Shock"), owner);
            table.game.getStackZone().add(shock, null, null);
            for (var activity : activities) {
                activity.onGameEvent(new GameEventSpellResolved(shock.getSpellAbilities().get(0), false));
                activity.onGameEvent(new GameEventCardCounters(target, CounterEnumType.P1P1, 1, 0));
                var entry = activity.snapshot().get(2);
                assertNull(entry.cause()); assertEquals(entry.change().before(), 1); assertEquals(entry.change().after(), 0);
                assertFalse(entry.message().contains("Shock"));
            }
            // Even the owner cannot disclose a concealed source's printed rules.
            owner.getZone(ZoneType.Hand).remove(source); owner.getZone(ZoneType.Battlefield).add(source, null, null);
            source.turnFaceDown(true);
            for (var activity : activities) {
                activity.onGameEvent(new GameEventCardCounters(target.getView(), CounterEnumType.P1P1, 0, 1, cause));
                assertNull(activity.snapshot().get(3).cause());
            }
        }
    }

    @Test public void legacyWireAndConstructorsRemainValidAndRetainedEntriesContainCopiedValues() {
        var entry = JSON.fromJson("{\"id\":1,\"turn\":1,\"phaseKey\":\"MAIN1\",\"kind\":\"counters\",\"message\":\"Counters changed.\",\"count\":0}", MatchActivity.Entry.class);
        assertNull(entry.change()); assertNull(entry.cause());
        var old = new MatchActivity.Entry(2, 1, "MAIN1", "counters", null, "Counters changed.", null, null, "", "", 0);
        assertNull(old.change()); assertNull(old.cause());
    }

    @Test public void largeCounterEffectsSurviveResolutionAndExpireOnlyAsWholeGroups() throws Exception {
        try (var table = table("large-counter-cause-retention")) {
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(RETREAT), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var activity = new MatchActivity(owner.getView());
            var targets = new ArrayList<Card>();
            for (int index = 0; index < 500; index++) {
                var target = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), owner);
                owner.getZone(ZoneType.Battlefield).add(target, null, null); targets.add(target);
            }
            var first = new GameEventCause(source.getView(), "Put a +1/+1 counter on each creature you control.", owner.getView(), "trigger", "first-trigger");
            for (var target : targets) activity.onGameEvent(new GameEventCardCounters(target.getView(), CounterEnumType.P1P1, 0, 1, first));
            var retained = activity.snapshot();
            assertEquals(retained.size(), 500);
            assertEquals(retained.stream().map(MatchActivity.Entry::cardId).distinct().count(), 500L);
            assertEquals(retained.stream().map(entry -> entry.cause().id()).distinct().count(), 1L);
            var spell = source.getSpellAbilities().get(0);
            activity.onGameEvent(new GameEventSpellResolved(spell, false));
            activity.onGameEvent(new GameEventTurnBegan(owner.getView(), 2));
            assertEquals(activity.snapshot().size(), 502, "Resolution and later ordinary publication must retain every recipient");

            var second = new GameEventCause(source.getView(), first.ability(), owner.getView(), "trigger", "second-trigger");
            for (var target : targets) activity.onGameEvent(new GameEventCardCounters(target.getView(), CounterEnumType.P1P1, 1, 2, second));
            var current = activity.snapshot().stream().filter(entry -> entry.kind().equals("counters")).toList();
            assertEquals(current.size(), 500);
            assertTrue(current.stream().allMatch(entry -> entry.change().before() == 1 && entry.change().after() == 2));
            assertEquals(current.stream().map(MatchActivity.Entry::cardId).distinct().count(), 500L);
            assertEquals(current.stream().map(entry -> entry.cause().id()).distinct().count(), 1L);
            assertNotEquals(current.get(0).cause().id(), retained.get(0).cause().id());
            assertEquals(retained.size(), 500, "Published prior history remains an immutable value copy");
            for (int index = 0; index < 120; index++) activity.onGameEvent(new GameEventTurnBegan(owner.getView(), 3 + index));
            assertEquals(activity.snapshot().size(), 120);
            assertTrue(activity.snapshot().stream().noneMatch(entry -> entry.kind().equals("counters")), "The old effect expires atomically");
        }
    }

    @Test(timeOut = 15000) public void laterConcealmentRevokesTargetHandlesWithoutRewritingHistoricalFacts() throws Exception {
        try (var table = table("counter-cause-occurrence-revocation")) {
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(RETREAT), owner);
            var target = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Invisible Stalker"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            owner.getZone(ZoneType.Battlefield).add(target, null, null); target.turnFaceDown(true);
            var activity = new MatchActivity(table.game.getPlayers().get(1).getView(), table.identities);
            var cause = new GameEventCause(source.getView(), "Put a +1/+1 counter on each creature you control.", owner.getView(), "trigger", "before-concealment");
            activity.onGameEvent(new GameEventCardCounters(target.getView(), CounterEnumType.P1P1, 0, 1, cause));
            var historical = activity.snapshot().get(0);
            var leaving = target;
            var moved = CompletableFuture.supplyAsync(() -> table.game.getAction().moveToHand(leaving, null));
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            while (!moved.isDone() && System.nanoTime() < deadline) {
                for (int seat = 0; seat < 4; seat++) {
                    var state = JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject();
                    if (!state.has("prompt") || state.get("prompt").isJsonNull()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    assertEquals(text(prompt, "kind"), "reveal", "A face-down departure requires the real rules reveal acknowledgement");
                    var answer = new JsonObject(); answer.addProperty("sessionId", text(state, "id"));
                    answer.addProperty("promptId", text(prompt, "id")); answer.addProperty("action", "ok");
                    table.seat(seat).action(answer);
                }
                Thread.sleep(5);
            }
            target = moved.get(1, TimeUnit.SECONDS); table.publish();
            var concealed = JSON.toJsonTree(table.seat(1).state()).getAsJsonObject();
            assertTrue(zone(player(concealed, owner.getId()), "Hand").getAsJsonArray("cards").isEmpty());
            activity.onGameEvent(new GameEventCardCounters(target, CounterEnumType.P1P1, 1, 0));
            assertEquals(activity.snapshot().size(), 1, "A hidden target's counter events are not disclosed");
            target = table.game.getAction().moveToPlay(target, owner, null, forge.game.ability.AbilityKey.newMap());
            target.turnFaceDown(true); table.publish();
            var returned = JSON.toJsonTree(table.seat(1).state()).getAsJsonObject();
            String freshId = text(card(returned, owner.getId(), "Face-down card"), "combatId");
            assertNotEquals(freshId, historical.cardId(), "A new public occurrence cannot reuse the concealed target handle");
            activity.onGameEvent(new GameEventCardCounters(target.getView(), CounterEnumType.P1P1, 0, 1, cause));
            assertEquals(activity.snapshot().get(1).cardId(), freshId);
            assertEquals(activity.snapshot().get(0), historical);
        }
    }

    private static RulesScenario.Input main(RulesScenario game, int afterTurn) throws Exception {
        while (true) {
            var input = game.until(i -> i.seat() == 0 && i.state().get("turn").getAsInt() > afterTurn
                    && i.state().get("activePlayerId").equals(i.state().get("viewerId"))
                    && text(i.state(), "phaseKey").equals("MAIN1") && text(i.prompt(), "inputType").equals("InputPassPriority")
                    && i.state().getAsJsonArray("stack").isEmpty()
                    || text(i.prompt(), "inputType").equals("InputSelectTargets")
                    || text(i.prompt(), "inputType").equals("InputSelectHandSizeDiscard") && text(i.state(), "phaseKey").equals("CLEANUP"));
            if (text(input.prompt(), "inputType").equals("InputPassPriority")) return input;
            if (text(input.prompt(), "inputType").equals("InputSelectTargets")) {
                assertEquals(input.seat(), 0);
                var forest = card(input.state(), input.state().get("viewerId").getAsInt(), "Forest");
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(forest, "key"))));
            } else {
                var discard = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> value.get("selectable").getAsBoolean() && !value.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(discard, "key"))));
            }
        }
    }
    private static JsonObject card(JsonObject state, int owner, String name) {
        return zone(player(state, owner), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static List<JsonObject> counters(JsonObject state, String source) {
        return state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals("counters") && value.has("cause") && !value.get("cause").isJsonNull()
                        && text(value.getAsJsonObject("cause"), "name").equals(source)).toList();
    }
    private static void assertCounter(JsonObject entry, int before, int after, String source) {
        var change = entry.getAsJsonObject("change");
        assertEquals(text(change, "kind"), "counters"); assertEquals(text(change, "property"), "+1/+1");
        assertEquals(change.get("before").getAsInt(), before); assertEquals(change.get("after").getAsInt(), after);
        assertEquals(text(entry.getAsJsonObject("cause"), "name"), source);
        assertTrue(text(entry, "message").contains(source));
    }
    private static MatchTable table(String name) throws Exception {
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 4; seat++) players.add(MatchTable.human(new Deck(), "Constructed", "Seat " + seat));
        var table = new MatchTable("Constructed", players, RESOURCES, Files.createTempDirectory(name));
        HeadlessPlatform.activate(table); return table;
    }
}
