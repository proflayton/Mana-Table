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
import forge.game.cost.CostPayment;
import forge.game.player.Player;
import forge.game.spellability.AbilitySub;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.SpellAbilityView;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectManyBase;
import forge.gui.interfaces.IGuiGame;
import forge.player.HumanCostDecision;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real required choices publish copied option meaning without speculative action queries. */
public class ChoicePublicationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(timeOut = 30000)
    public void retainedOptionsAreImmutableDoNotProbeActionsAndPreserveTheirOwnGraphRoots() throws Exception {
        try (var fixture = new Fixture("retained")) {
            fixture.cost.forbidProbes = true;
            var running = fixture.start(fixture::chooseAbilities); running.await();
            HeadlessPlatform.query(fixture.table, () -> {
                var raw = fixture.session().automatedObservation(); var before = JSON.toJsonTree(raw).getAsJsonObject();
                assertPublished(fixture.session(), before); assertOptions(fixture, before, 1);
                var options = (List<?>)((Map<?, ?>)raw.get("semantics")).get("options");
                assertImmutableOptions(options);
                fixture.draw.forbidReads = true;
                try {
                    for (int read = 0; read < 3; read++) {
                        assertEquals(JSON.toJsonTree(fixture.session().automatedObservation()), before);
                        assertPublished(fixture.session(), before);
                    }
                } finally { fixture.draw.forbidReads = false; }
                fixture.draw.getMapParams().put("NumCards", "5");
                assertEquals(fixture.observation(), before, "Original ability changes cannot rewrite retained option facts");
                fixture.table.publish();
                var refreshed = fixture.observation(); assertOptions(fixture, refreshed, 5);
                assertEquals(refreshed.getAsJsonObject("state").getAsJsonObject("prompt").get("id"), before.getAsJsonObject("state").getAsJsonObject("prompt").get("id"));
                assertEquals(JSON.toJsonTree(raw), before, "The original raw nested option tree is detached from the rules objects");
                assertOptions(fixture, before, 1); assertPrivate(fixture);
                fixture.record("before", before); fixture.record("refreshed", refreshed);
                return null;
            });
            var current = fixture.current(); int chosen = option(current, "Draw").get("index").getAsInt();
            fixture.answer(current, chosen);
            var selected = running.result.get(5, TimeUnit.SECONDS); assertEquals(selected.size(), 1); assertSame(selected.get(0), fixture.draw);

            var ordinary = fixture.start(() -> fixture.controller().getGui().getChoices("An ordinary nullable choice", 1, 1, Arrays.asList(null, "Continue")));
            ordinary.await(); var nullChoice = fixture.current();
            var offered = nullChoice.getAsJsonObject("state").getAsJsonObject("prompt").getAsJsonArray("choices");
            assertEquals(offered.size(), 2); assertEquals(text(offered.get(0).getAsJsonObject(), "label"), "null");
            assertTrue(nullChoice.getAsJsonObject("semantics").getAsJsonArray("options").isEmpty());
            fixture.answer(nullChoice, 0);
            var values = ordinary.result.get(5, TimeUnit.SECONDS); assertEquals(values.size(), 1); assertNull(values.get(0), "The original null option keeps its actual response index");
            assertFalse(fixture.source.isTapped()); assertFalse(fixture.forest.isTapped()); assertEquals(fixture.actor().getManaPool().totalMana(), 0);
        }
    }

    @Test(timeOut = 30000)
    public void aReentrantOptionProjectionCannotOverwriteTheNewSameOwnerCohort() throws Exception {
        try (var fixture = new Fixture("reentrant")) {
            var running = fixture.start(fixture::chooseAbilities); running.await();
            HeadlessPlatform.query(fixture.table, () -> {
                var before = fixture.observation(); var inner = new ArrayList<JsonObject>();
                fixture.draw.beforeNextAmountRead = () -> {
                    fixture.draw.getMapParams().put("NumCards", "5"); fixture.table.publish();
                    inner.add(fixture.observation());
                };
                fixture.table.publish();
                assertNull(fixture.draw.beforeNextAmountRead); assertEquals(inner.size(), 1);
                var published = inner.get(0); assertOptions(fixture, published, 5);
                long board = published.getAsJsonObject("state").get("boardRevision").getAsLong();
                assertCohort(fixture, board);
                assertEquals(fixture.observation(), published,
                        "An obsolete outer option builder cannot mutate the already committed Pending's newer semantic facts");
                assertOptions(fixture, before, 1);
                fixture.record("before", before); fixture.record("inner", published);
                return null;
            });
            var current = fixture.current(); fixture.answer(current, option(current, "LifeGain").get("index").getAsInt());
            var selected = running.result.get(5, TimeUnit.SECONDS); assertEquals(selected.size(), 1); assertSame(selected.get(0), fixture.life);
        }
    }

    @Test(timeOut = 30000)
    public void aChangedAmbientCostEntryRetainsTheNestedChoiceFutureAndResumesItsRealPayment() throws Exception {
        try (var fixture = new Fixture("cost-entry")) {
            var parentOpened = new CountDownLatch(1); var childOpened = new CountDownLatch(1);
            var childResult = new CompletableFuture<List<SpellAbility>>(); var replaced = new ArrayList<Object>();
            fixture.choiceOpened = childOpened;
            fixture.beforeChoice = () -> {
                var old = fixture.table.game.costPaymentStack.peek(); assertNotNull(old);
                assertSame(old.payment().getPayer(), fixture.actor());
                fixture.draw.beforeNextAmountRead = () -> {
                    assertSame(fixture.table.game.costPaymentStack.pop(), old);
                    var next = fixture.table.game.costPaymentStack.push(old.cost(), old.payment());
                    assertEquals(next, old); assertNotSame(next, old); replaced.add(next);
                };
            };
            fixture.parentOpened = parentOpened;
            fixture.afterSelected = () -> {
                try { childResult.complete(fixture.chooseAbilities()); }
                catch (RuntimeException | Error failure) { childResult.completeExceptionally(failure); throw failure; }
            };
            var parentCost = new Cost("tapXType<1/Creature>", true);
            var parentAbility = new AbilityApiBased(ApiType.Draw, fixture.source, parentCost, null, Map.of("AB", "Draw", "NumCards", "1"));
            parentAbility.setActivatingPlayer(fixture.actor());
            var payment = fixture.startWorker(() -> new CostPayment(parentCost, parentAbility).payCost(
                    new HumanCostDecision(fixture.controller(), fixture.actor(), parentAbility, false, "")));
            assertTrue(parentOpened.await(5, TimeUnit.SECONDS), "The real parent cost must open its input");
            var parent = fixture.current();
            var source = zone(viewer(parent.getAsJsonObject("state")), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Grizzly Bears")).findFirst().orElseThrow();
            assertTrue(source.get("selectable").getAsBoolean());
            fixture.session().action(request(input(parent), Map.of("action", "card", "key", text(source, "key"))));
            assertTrue(childOpened.await(5, TimeUnit.SECONDS), "The real selection callback must open its nested choice");
            // The UI executor owns this blocking child; its retained facts are
            // inspected in one rules cut, never by queuing work behind it.
            var child = fixture.table.game.getAction().readState(() -> {
                var current = fixture.observation(); assertOptions(fixture, current, 1);
                assertNull(fixture.draw.beforeNextAmountRead); assertEquals(replaced.size(), 1);
                var semantics = current.getAsJsonObject("semantics");
                assertEquals(text(semantics, "intent"), "Unknown"); assertTrue(semantics.get("amount").isJsonNull());
                assertFalse(semantics.get("selectionIsCost").getAsBoolean()); assertEquals(text(semantics, "counterType"), "");
                assertFalse(semantics.has("costPayment")); assertFalse(semantics.has("orderingPurpose"));
                var prompt = current.getAsJsonObject("state").getAsJsonObject("prompt");
                assertEquals(prompt.get("min").getAsInt(), 1); assertEquals(prompt.get("max").getAsInt(), 1);
                assertEquals(prompt.getAsJsonArray("choices").size(), 2); assertFalse(childResult.isDone()); assertFalse(payment.isDone());
                assertEquals(fixture.observation(), current, "Unknown ambient cost metadata cannot be reconstructed from the replacement entry on a retained read");
                assertCohort(fixture, current.getAsJsonObject("state").get("boardRevision").getAsLong());
                fixture.record("child", current); return current;
            });
            fixture.answer(child, option(child, "Draw").get("index").getAsInt());
            var selected = childResult.get(5, TimeUnit.SECONDS); assertEquals(selected.size(), 1); assertSame(selected.get(0), fixture.draw);
            assertTrue(payment.get(5, TimeUnit.SECONDS), "Answering the retained child must wake the actual parent cost continuation");
            assertTrue(fixture.source.isTapped()); assertFalse(fixture.forest.isTapped());
            assertNull(fixture.table.game.costPaymentStack.peek()); assertEquals(fixture.actor().getManaPool().totalMana(), 0);
            assertThrows(IllegalArgumentException.class, () -> fixture.answer(child, 0));
        }
    }

    private static void assertOptions(Fixture fixture, JsonObject observation, int drawAmount) {
        var options = observation.getAsJsonObject("semantics").getAsJsonArray("options"); assertEquals(options.size(), 2);
        var draw = option(observation, "Draw"); var life = option(observation, "LifeGain");
        assertEquals(draw.get("amount").getAsInt(), drawAmount); assertEquals(life.get("amount").getAsInt(), 3);
        for (var value : options) {
            var option = value.getAsJsonObject(); int index = option.get("index").getAsInt();
            assertSame(fixture.offered.get(index), text(option, "effect").equals("Draw") ? fixture.draw : fixture.life);
            var nodes = option.getAsJsonObject("operationGraph").getAsJsonArray("operations");
            assertEquals(nodes.size(), 1, "An option graph starts at that exact offered ability, not its unchosen parent recipe");
            assertEquals(text(nodes.get(0).getAsJsonObject(), "effect"), text(option, "effect"));
            assertEquals(nodes.get(0).getAsJsonObject().get("amount"), option.get("amount"));
        }
    }
    private static JsonObject option(JsonObject observation, String effect) {
        return observation.getAsJsonObject("semantics").getAsJsonArray("options").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "effect").equals(effect)).findFirst().orElseThrow();
    }
    static void assertImmutableOptions(List<?> options) {
        assertFalse(options.isEmpty()); assertThrows(UnsupportedOperationException.class, options::clear);
        var first = (Map<?, ?>)options.get(0); assertThrows(UnsupportedOperationException.class, first::clear);
        var graph = (Map<?, ?>)first.get("operationGraph"); assertThrows(UnsupportedOperationException.class, graph::clear);
        var nodes = (List<?>)graph.get("operations"); assertThrows(UnsupportedOperationException.class, nodes::clear);
        assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)nodes.get(0))::clear);
    }
    private static void assertPublished(MatchSession session, JsonObject observation) {
        assertEquals(observation.get("state"), JSON.toJsonTree(session.state()), "Current option meaning and visible choices share one accepted publication");
    }
    private static void assertCohort(Fixture fixture, long board) {
        for (var audience : fixture.table.automatedSeats()) assertEquals(((Number)audience.state().get("boardRevision")).longValue(), board);
        assertEquals(((Number)fixture.table.spectatorState().get("boardRevision")).longValue(), board);
    }
    private static void assertPrivate(Fixture fixture) {
        for (var audience : fixture.table.automatedSeats()) if (audience != fixture.session()) {
            var other = JSON.toJsonTree(audience.automatedObservation()).getAsJsonObject();
            assertTrue(other.getAsJsonObject("semantics").getAsJsonArray("options").isEmpty()); assertTrue(other.getAsJsonObject("state").get("prompt").isJsonNull());
        }
        var spectator = JSON.toJsonTree(fixture.table.spectatorState()).getAsJsonObject();
        assertFalse(spectator.has("semantics")); assertTrue(!spectator.has("prompt") || spectator.get("prompt").isJsonNull());
    }
    private static Input input(JsonObject observation) {
        var state = observation.getAsJsonObject("state"); return new Input(0, state, state.getAsJsonObject("prompt"));
    }
    private record Running<T>(CountDownLatch opened, CompletableFuture<T> result) {
        void await() throws InterruptedException { assertTrue(opened.await(5, TimeUnit.SECONDS), "The actual required choice callback must open"); }
    }
    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final Card source, forest; final ProbeCost cost = new ProbeCost(); final GuardedAbility draw;
        final AbilitySub life; final SpellAbility parent; final IGuiGame originalGui; final Path evidence; final List<Object> trace = new ArrayList<>();
        List<SpellAbility> offered = List.of(); CountDownLatch choiceOpened, parentOpened; Runnable beforeChoice, afterSelected;
        CompletableFuture<?> worker;
        Fixture(String name) throws Exception {
            var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
            evidence = ApiTestWorkspace.evidence("choice-publication-" + name);
            var seats = java.util.stream.IntStream.range(0, 4).mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Policy " + seat)).toList();
            table = new MatchTable("Constructed", seats, resources, evidence, true); HeadlessPlatform.activate(table);
            source = add("Grizzly Bears"); forest = add("Forest");
            draw = new GuardedAbility(source, cost); draw.setActivatingPlayer(actor());
            parent = new AbilityApiBased(ApiType.Draw, source, Cost.Zero, null, Map.of("AB", "Draw", "NumCards", "99")); parent.setActivatingPlayer(actor());
            life = new AbilitySub(ApiType.GainLife, source, null, Map.of("DB", "GainLife", "LifeAmount", "3")); parent.setSubAbility(life); life.setActivatingPlayer(actor());
            originalGui = controller().getGui();
            controller().setGui((IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
                if (method.getName().equals("getChoices")) {
                    var values = (List<?>)args[3]; var exact = new ArrayList<SpellAbility>();
                    for (var value : values) if (value instanceof SpellAbilityView view) exact.add(controller().getDecisionAbility(view));
                    offered = exact;
                    var callback = beforeChoice; beforeChoice = null; if (callback != null) callback.run();
                    if (choiceOpened != null) choiceOpened.countDown();
                }
                Object result;
                try { result = method.invoke(originalGui, args); }
                catch (InvocationTargetException failure) { throw failure.getCause(); }
                if (method.getName().equals("updateButtons") && parentOpened != null && controller().getInputQueue().getInput() instanceof InputSelectManyBase<?>) parentOpened.countDown();
                if (method.getName().equals("setHighlighted") && Boolean.TRUE.equals(args[1])) {
                    var callback = afterSelected; afterSelected = null; if (callback != null) callback.run();
                }
                return result;
            }));
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        PlayerControllerHuman controller() { return (PlayerControllerHuman)actor().getController(); }
        MatchSession session() { return table.automatedSeat(actor().getId()); }
        Card add(String name) { var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor()); actor().getZone(ZoneType.Battlefield).add(card); return card; }
        List<SpellAbility> chooseAbilities() { return controller().chooseSpellAbilitiesForEffect(List.of(draw, life), parent, "Choose an actual ability", 1, null); }
        <T> Running<T> start(Supplier<T> callback) { choiceOpened = new CountDownLatch(1); return new Running<>(choiceOpened, startWorker(callback)); }
        <T> CompletableFuture<T> startWorker(Supplier<T> callback) {
            var result = new CompletableFuture<T>(); worker = result;
            table.game.getAction().invoke(() -> { try { result.complete(callback.get()); } catch (Throwable failure) { result.completeExceptionally(failure); } }); return result;
        }
        JsonObject observation() { var current = JSON.toJsonTree(session().automatedObservation()).getAsJsonObject(); assertPublished(session(), current); return current; }
        JsonObject current() { return HeadlessPlatform.query(table, this::observation); }
        void answer(JsonObject current, int index) { session().action(request(input(current), Map.of("action", "choice", "choices", List.of(index), "expectedTask", "choice"))); }
        void record(String label, JsonObject current) { trace.add(Map.of("label", label, "observation", current.deepCopy())); }
        @Override public void close() throws Exception {
            draw.forbidReads = false; cost.forbidProbes = false; draw.beforeNextAmountRead = null; beforeChoice = null; afterSelected = null;
            try { Files.writeString(evidence.resolve("trace.json"), JSON.toJson(trace));
                Files.writeString(evidence.resolve("private-final-views.json"), JSON.toJson(table.automatedSeats().stream().map(MatchSession::state).toList())); }
            finally { controller().setGui(originalGui); table.close(); if (worker != null) worker.handle((value, failure) -> null).get(5, TimeUnit.SECONDS); }
        }
    }
    private static final class ProbeCost extends Cost {
        boolean forbidProbes;
        ProbeCost() { super("1 U", true); }
        @Override public boolean canPay(SpellAbility ability, Player payer, boolean effect) {
            if (forbidProbes) throw new AssertionError("Option meaning ran a discarded affordability query"); return super.canPay(ability, payer, effect);
        }
    }
    private static final class GuardedAbility extends AbilityApiBased {
        boolean forbidReads; Runnable beforeNextAmountRead;
        GuardedAbility(Card source, Cost cost) { super(ApiType.Draw, source, cost, null, Map.of("AB", "Draw", "NumCards", "1")); }
        private void read() { if (forbidReads) throw new AssertionError("A retained choice reopened the actual option's semantic getters"); }
        @Override public Cost getPayCosts() { read(); return super.getPayCosts(); }
        @Override public String getParam(String key) { read(); String value = super.getParam(key); amountRead(key); return value; }
        @Override public String getParamOrDefault(String key, String fallback) { read(); String value = super.getParamOrDefault(key, fallback); amountRead(key); return value; }
        private void amountRead(String key) { if (key.equals("NumCards")) { var callback = beforeNextAmountRead; beforeNextAmountRead = null; if (callback != null) callback.run(); } }
        @Override public boolean allowsPayingWithShard(Card source, byte color) { throw new AssertionError("Option meaning ran a discarded funding query"); }
    }
}
