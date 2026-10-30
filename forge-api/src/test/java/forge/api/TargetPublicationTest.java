package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityApiBased;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.cost.Cost;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.TargetChoices;
import forge.game.spellability.TargetRestrictions;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectTargets;
import forge.gui.interfaces.IGuiGame;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual target inputs publish their meaning once without reopening a cost or a rules choice. */
public class TargetPublicationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(timeOut = 30000)
    public void publishedTargetsStayReadableWithoutReopeningTheAbilityOrProbingItsCost() throws Exception {
        try (var fixture = new Fixture("retained")) {
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.cost.forbidProbes = true;
                fixture.table.publish();
                var raw = fixture.session().automatedObservation();
                var before = JSON.toJsonTree(raw).getAsJsonObject();
                var prompt = before.getAsJsonObject("state").getAsJsonObject("prompt");
                assertEquals(prompt.getAsJsonObject("effect").get("amount").getAsInt(), 3);
                assertEquals(text(prompt.getAsJsonObject("effect"), "amountBasis"), "base");
                assertTrue(before.getAsJsonObject("semantics").get("amount").isJsonNull(),
                        "AI's literal quantity remains unknown even when PendingEffects knows the paid X");
                assertEquals(text(before.getAsJsonObject("semantics"), "intent"), "Damage");
                var facts = (Map<?, ?>)raw.get("semantics");
                var operations = (List<?>)((Map<?, ?>)facts.get("operationGraph")).get("operations");
                assertEquals(operations.size(), 1);
                assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)operations.get(0))::clear);
                assertThrows(UnsupportedOperationException.class, operations::clear);
                var publishedPrompt = (Map<?, ?>)((Map<?, ?>)raw.get("state")).get("prompt");
                assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)publishedPrompt.get("effect"))::clear);
                assertThrows(IllegalArgumentException.class, () -> PublishedDecisionFacts.freeze(Map.of("liveCard", fixture.source)));
                assertThrows(IllegalArgumentException.class, () -> PublishedDecisionFacts.freeze(Map.of("unmodeled", new UnmodeledValue("unknown"))));
                var spectator = JSON.toJsonTree(fixture.table.spectatorState()).getAsJsonObject();
                assertTrue(!spectator.has("prompt") || spectator.get("prompt").isJsonNull());
                assertFalse(spectator.has("semantics"));
                for (var audience : fixture.table.automatedSeats()) if (audience != fixture.session()) {
                    var other = JSON.toJsonTree(audience.automatedObservation()).getAsJsonObject();
                    assertFalse(other.getAsJsonObject("semantics").has("selectedPlayers"));
                    assertEquals(text(other.getAsJsonObject("semantics"), "intent"), "Unknown");
                }

                fixture.ability.forbidFacts = true; fixture.ability.forbidCostReads = true;
                try {
                    for (int read = 0; read < 3; read++) {
                        assertEquals(JSON.toJsonTree(fixture.session().automatedObservation()), before);
                        assertEquals(JSON.toJsonTree(fixture.session().state()).getAsJsonObject().get("prompt"), prompt);
                    }
                } finally { fixture.ability.forbidFacts = false; fixture.ability.forbidCostReads = false; }

                fixture.ability.getMapParams().put("NumDmg", "5");
                assertEquals(JSON.toJsonTree(fixture.session().automatedObservation()), before,
                        "Changing the original ability cannot rewrite facts already published to either consumer");
                fixture.table.publish();
                var refreshed = fixture.observation();
                var next = refreshed.getAsJsonObject("state").getAsJsonObject("prompt");
                assertSame(fixture.controller().getInputQueue().getInput(), fixture.input);
                assertEquals(next.get("id"), prompt.get("id"));
                assertEquals(next.getAsJsonObject("effect").get("amount").getAsInt(), 5);
                assertEquals(refreshed.getAsJsonObject("semantics").get("amount").getAsInt(), 5);
                assertEquals(episode(refreshed), episode(before));
                assertEquals(prompt.getAsJsonObject("effect").get("amount").getAsInt(), 3);
                assertTrue(before.getAsJsonObject("semantics").get("amount").isJsonNull());
                assertFalse(fixture.forest.isTapped()); assertEquals(fixture.actor().getManaPool().totalMana(), 0);
                fixture.record("retained", before); fixture.record("refreshed", refreshed);
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void aReentrantTargetPublicationKeepsTheNewSameOwnerCohortReadable() throws Exception {
        try (var fixture = new Fixture("reentrant")) {
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.ability.getMapParams().put("NumDmg", "3"); fixture.table.publish();
                var previous = fixture.observation();
                var inner = new ArrayList<JsonObject>();
                fixture.ability.beforeNextDamageRead = () -> {
                    fixture.ability.getMapParams().put("NumDmg", "5");
                    fixture.table.publish();
                    inner.add(JSON.toJsonTree(fixture.session().state()).getAsJsonObject());
                };
                fixture.table.publish();
                assertNull(fixture.ability.beforeNextDamageRead);
                assertEquals(inner.size(), 1, "A real target projection must exercise the nested publication");
                var published = inner.get(0);
                long board = published.get("boardRevision").getAsLong();
                assertTrue(board > previous.getAsJsonObject("state").get("boardRevision").getAsLong());
                for (var audience : fixture.table.automatedSeats())
                    assertEquals(((Number)audience.state().get("boardRevision")).longValue(), board);
                assertEquals(((Number)fixture.table.spectatorState().get("boardRevision")).longValue(), board);
                assertEquals(JSON.toJsonTree(fixture.session().state()), published,
                        "The obsolete outer target projection cannot replace the inner cohort");
                var recovered = fixture.observation();
                assertEquals(recovered.get("state"), published, "Recovery uses the inner capture without another publication");
                assertEquals(published.getAsJsonObject("prompt").getAsJsonObject("effect").get("amount").getAsInt(), 5);
                assertEquals(recovered.getAsJsonObject("semantics").get("amount").getAsInt(), 5);
                assertEquals(episode(recovered), episode(previous));
                fixture.record("before", previous); fixture.record("recovered", recovered);
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void targetStopCleanupCannotPublishTheCompletedOwnerAgain() throws Exception {
        try (var fixture = new Fixture("stop")) {
            HeadlessPlatform.query(fixture.table, () -> {
                var old = fixture.current();
                var cleanup = new ArrayList<JsonObject>();
                var originalGui = fixture.controller().getGui();
                var probeGui = (IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
                    Object result;
                    try { result = method.invoke(originalGui, args); }
                    catch (InvocationTargetException failure) { throw failure.getCause(); }
                    if (method.getName().equals("clearSelectables")) {
                        // A real cleanup callback can publish while the old
                        // input is still on the queue. No replacement Input or
                        // simulated stop path hides that exact boundary.
                        assertFalse(fixture.input.isTargetSelectionActive());
                        fixture.session().publishInput();
                        cleanup.add(JSON.toJsonTree(fixture.session().state()).getAsJsonObject());
                    }
                    return result;
                });
                fixture.controller().setGui(probeGui);
                try { fixture.input.stop(); }
                finally { fixture.controller().setGui(originalGui); }
                assertFalse(fixture.input.isTargetSelectionActive());
                assertFalse(cleanup.isEmpty(), "The actual stop path must exercise GUI cleanup publication");
                for (var state : cleanup) assertTrue(!state.has("prompt") || state.get("prompt").isJsonNull(), state.toString());
                fixture.session().publishInput();
                var completed = fixture.observation();
                assertTrue(completed.getAsJsonObject("state").get("prompt").isJsonNull());
                assertFalse(completed.getAsJsonObject("semantics").has("selectedPlayers"));
                assertThrows(IllegalArgumentException.class, () -> fixture.session().action(request(old,
                        Map.of("action", "player", "playerId", fixture.player(1).getId()))));
                fixture.record("completed", completed);
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void actualPlayerTogglesRefreshOneEpisodeAndAQueuedDepartedTargetIsRejected() throws Exception {
        try (var fixture = new Fixture("players")) {
            var initial = fixture.current(); String episode = episode(fixture.observation());
            int chosenId = fixture.player(1).getId();
            var chosen = fixture.answer(initial, Map.of("action", "player", "playerId", chosenId));
            assertEquals(chosen.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of(chosenId)));
            assertEquals(episode(chosen), episode); assertSame(fixture.controller().getInputQueue().getInput(), fixture.input);
            var undone = fixture.answer(input(chosen), Map.of("action", "player", "playerId", chosenId));
            assertEquals(undone.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of()));
            assertEquals(episode(undone), episode);
            assertEquals(chosen.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of(chosenId)));
            var current = input(undone);
            var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
            HeadlessPlatform.later(() -> {
                entered.countDown();
                try (var suspended = fixture.table.game.getAction().suspendState()) {
                    if (!release.await(5, TimeUnit.SECONDS)) throw new AssertionError("Target queue barrier expired");
                } catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
            });
            assertTrue(entered.await(3, TimeUnit.SECONDS));
            try {
                HeadlessPlatform.later(() -> ((PlayerControllerHuman)fixture.player(2).getController()).concede());
                fixture.session().action(request(current, Map.of("action", "player", "playerId", fixture.player(2).getId())));
            } finally { release.countDown(); }
            HeadlessPlatform.query(fixture.table, () -> {
                assertTrue(fixture.player(2).hasLost());
                var recovered = fixture.observation();
                var prompt = recovered.getAsJsonObject("state").getAsJsonObject("prompt");
                assertEquals(prompt.get("id"), current.prompt().get("id"));
                assertEquals(episode(recovered), episode); assertSame(fixture.controller().getInputQueue().getInput(), fixture.input);
                assertEquals(recovered.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of()));
                assertFalse(prompt.getAsJsonArray("playerChoices").asList().stream().anyMatch(value -> value.getAsInt() == fixture.player(2).getId()));
                assertTrue(recovered.getAsJsonObject("state").get("notices").toString().contains("That target changed"));
                assertEquals(recovered.getAsJsonObject("state").get("prompt"), JSON.toJsonTree(fixture.session().state()).getAsJsonObject().get("prompt"));
                fixture.record("chosen", chosen); fixture.record("undone", undone); fixture.record("recovered", recovered);
                return null;
            });
        }
    }

    private static Input input(JsonObject observation) {
        var state = observation.getAsJsonObject("state"); return new Input(state.get("viewerId").getAsInt(), state, state.getAsJsonObject("prompt"));
    }
    private static String episode(JsonObject observation) { return text(observation.getAsJsonObject("semantics"), "episodeId"); }
    private record UnmodeledValue(String value) { }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final Card source, forest; final ProbeCost cost = new ProbeCost();
        final GuardedAbility ability; final InputSelectTargets input; final Path evidence; final List<Object> trace = new ArrayList<>();
        Fixture(String name) throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            evidence = ApiTestWorkspace.evidence("target-publication-" + name);
            var players = java.util.stream.IntStream.range(0, 4)
                    .mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Policy " + seat)).toList();
            table = new MatchTable("Constructed", players, resources, evidence, true); HeadlessPlatform.activate(table);
            source = add("Prodigal Pyromancer", actor()); forest = add("Forest", actor());
            for (int seat = 1; seat < 4; seat++) add("Grizzly Bears", player(seat));
            ability = new GuardedAbility(source, cost); ability.setActivatingPlayer(actor());
            ability.setSVar("X", "Count$xPaid"); ability.setXManaCostPaid(3);
            input = new InputSelectTargets(controller(), new ArrayList<>(table.game.getCardsIn(ZoneType.Battlefield)), ability, false, null, null, null, false);
            controller().getInputQueue().setInput(input);
            HeadlessPlatform.query(table, () -> { session().publishInput(); table.publish(); return null; });
        }
        Player actor() { return player(0); }
        Player player(int seat) { return table.game.getRegisteredPlayers().get(seat); }
        PlayerControllerHuman controller() { return (PlayerControllerHuman)actor().getController(); }
        MatchSession session() { return table.automatedSeat(actor().getId()); }
        Card add(String name, Player player) { var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), player); player.getZone(ZoneType.Battlefield).add(card); return card; }
        JsonObject observation() { return JSON.toJsonTree(session().automatedObservation()).getAsJsonObject(); }
        Input current() { return HeadlessPlatform.query(table, () -> input(observation())); }
        JsonObject answer(Input current, Map<String, ?> action) {
            session().action(request(current, action));
            return HeadlessPlatform.query(table, () -> {
                var observed = observation();
                assertEquals(observed.get("state"), JSON.toJsonTree(session().state()),
                        "The current player target facts and human-compatible prompt share one publication");
                return observed;
            });
        }
        void record(String label, JsonObject state) { trace.add(Map.of("label", label, "observation", state.deepCopy())); }
        @Override public void close() throws Exception {
            ability.forbidFacts = false; ability.forbidCostReads = false; cost.forbidProbes = false;
            try { Files.writeString(evidence.resolve("trace.json"), JSON.toJson(trace)); Files.writeString(evidence.resolve("private-final-views.json"), JSON.toJson(table.testViews())); }
            finally { table.close(); }
        }
    }

    private static final class ProbeCost extends Cost {
        boolean forbidProbes;
        ProbeCost() { super("1 R", true); }
        @Override public boolean canPay(SpellAbility ability, Player payer, boolean effect) {
            if (forbidProbes) throw new AssertionError("Target facts ran a discarded affordability/funding probe");
            return super.canPay(ability, payer, effect);
        }
    }
    private static final class GuardedAbility extends AbilityApiBased {
        boolean forbidFacts, forbidCostReads; Runnable beforeNextDamageRead;
        GuardedAbility(Card source, Cost cost) { super(ApiType.DealDamage, source, cost, new TargetRestrictions(params()), params()); }
        private static Map<String, String> params() { return Map.of("AB", "DealDamage", "Cost", "1 R", "ValidTgts", "Any", "TargetMin", "1", "TargetMax", "3", "NumDmg", "X"); }
        private void factRead() { if (forbidFacts) throw new AssertionError("A retained target projection reopened its live ability"); }
        @Override public int getMinTargets() { factRead(); return super.getMinTargets(); }
        @Override public int getMaxTargets() { factRead(); return super.getMaxTargets(); }
        @Override public TargetChoices getTargets() { factRead(); return super.getTargets(); }
        @Override public String getParamOrDefault(String key, String fallback) { factRead(); return super.getParamOrDefault(key, fallback); }
        @Override public String getParam(String key) {
            factRead(); String value = super.getParam(key);
            if (key.equals("NumDmg")) { var callback = beforeNextDamageRead; beforeNextDamageRead = null; if (callback != null) callback.run(); }
            return value;
        }
        @Override public Cost getPayCosts() {
            if (forbidCostReads) throw new AssertionError("A retained target projection reopened its cost");
            return super.getPayCosts();
        }
    }
}
