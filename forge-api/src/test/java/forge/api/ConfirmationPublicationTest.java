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
import forge.game.player.LibrarySearchOffer;
import forge.game.player.Player;
import forge.game.player.PlayerActionConfirmMode;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputConfirm;
import forge.gui.interfaces.IGuiGame;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Ordinary confirmations retain published meaning until their actual input retires. */
public class ConfirmationPublicationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(timeOut = 30000)
    public void retainedConfirmationFactsAreImmutablePrivateAndDoNotReopenTheirAbility() throws Exception {
        try (var fixture = new Fixture("retained")) {
            fixture.cost.forbidProbes = true;
            var running = fixture.start(() -> fixture.controller().confirmAction(fixture.draw,
                    PlayerActionConfirmMode.OptionalChoose, "Use the actual draw ability?", List.of("Use", "Decline"), fixture.forest, Map.of()));
            running.await();
            HeadlessPlatform.query(fixture.table, () -> {
                var raw = fixture.session().automatedObservation(); var before = JSON.toJsonTree(raw).getAsJsonObject();
                assertDraw(fixture, before, 1); assertPublished(fixture.session(), before);
                var portrait = prompt(before).getAsJsonObject("sourceCard");
                assertEquals(text(portrait, "name"), "Forest"); assertEquals(text(portrait, "key"), "");
                assertFalse(portrait.get("selectable").getAsBoolean()); assertFalse(portrait.get("highlighted").getAsBoolean());
                var actualSource = zone(viewer(before.getAsJsonObject("state")), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Grizzly Bears")).findFirst().orElseThrow();
                String sourceId = text(actualSource, "combatId");
                var source = operation(before).getAsJsonObject("sources").getAsJsonArray("entities").get(0).getAsJsonObject();
                assertEquals(text(source, "cardId"), sourceId);
                assertNotEquals(sourceId, text(portrait, "combatId"), "A separate readable portrait cannot become the ability's semantic source");
                assertImmutableGraph(((Map<?, ?>)raw.get("semantics")).get("operationGraph"));
                fixture.draw.forbidReads = true;
                try {
                    for (int read = 0; read < 3; read++) {
                        assertEquals(JSON.toJsonTree(fixture.session().automatedObservation()), before);
                        assertPublished(fixture.session(), before);
                    }
                } finally { fixture.draw.forbidReads = false; }
                fixture.draw.getMapParams().put("NumCards", "5");
                assertEquals(fixture.observation(), before, "The actual ability cannot rewrite already published confirmation facts");
                fixture.table.publish(); var refreshed = fixture.observation(); assertDraw(fixture, refreshed, 5);
                assertEquals(prompt(refreshed).get("id"), prompt(before).get("id"));
                assertEquals(JSON.toJsonTree(raw), before, "Retained raw maps are detached from later ability and publication changes");
                assertPrivate(fixture); fixture.record("before", before); fixture.record("refreshed", refreshed);
                return null;
            });
            fixture.answer(fixture.current(), "ok"); assertTrue(running.result.get(5, TimeUnit.SECONDS));

            var search = new GuardedAbility(fixture.source, fixture.cost, ApiType.ChangeZone,
                    Map.of("AB", "ChangeZone", "Origin", "Library", "Destination", "Hand", "ChangeType", "Card.namedPir; Imaginative Rascal"));
            search.setActivatingPlayer(fixture.actor()); fixture.abilities.add(search);
            var offer = LibrarySearchOffer.optionalTrigger(search); assertNotNull(offer);
            var searching = fixture.start(() -> fixture.controller().confirmAction(search,
                    PlayerActionConfirmMode.OptionalChoose, "Search the actual library?", List.of(), fixture.forest,
                    Map.of(LibrarySearchOffer.CONTEXT_KEY, offer)));
            searching.await();
            var current = HeadlessPlatform.query(fixture.table, () -> {
                var raw = fixture.session().automatedObservation(); var observed = JSON.toJsonTree(raw).getAsJsonObject();
                var metadata = prompt(observed).getAsJsonObject("librarySearch");
                assertEquals(metadata.get("libraryOwnerId").getAsInt(), fixture.actor().getId());
                assertEquals(text(metadata, "destination"), "Hand"); assertEquals(text(metadata, "cardName"), "Pir, Imaginative Rascal");
                assertEquals(text(prompt(observed), "ok"), "Search library");
                assertEquals(text(observed.getAsJsonObject("semantics"), "intent"), "Search");
                var rawPrompt = (Map<?, ?>)((Map<?, ?>)raw.get("state")).get("prompt");
                assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)rawPrompt.get("librarySearch"))::clear);
                assertImmutableGraph(((Map<?, ?>)raw.get("semantics")).get("operationGraph"));
                search.forbidReads = true;
                try { for (int read = 0; read < 3; read++) assertEquals(fixture.observation(), observed); }
                finally { search.forbidReads = false; }
                assertPrivate(fixture);
                for (var audience : fixture.table.automatedSeats()) {
                    var state = JSON.toJsonTree(audience.state()).getAsJsonObject();
                    for (var player : state.getAsJsonArray("players")) {
                        var library = zone(player.getAsJsonObject(), "Library");
                        assertTrue(library.getAsJsonArray("cards").isEmpty(), "A search offer does not inspect any library");
                        assertTrue(!library.has("topCard") || library.get("topCard").isJsonNull());
                    }
                }
                assertEquals(fixture.actor().getCardsIn(ZoneType.Library).size(), 1);
                fixture.record("search", observed); return observed;
            });
            fixture.answer(current, "cancel"); assertFalse(searching.result.get(5, TimeUnit.SECONDS));
            assertEquals(fixture.actor().getCardsIn(ZoneType.Library).size(), 1, "Declining the real callback never looks through or moves the hidden card");
            fixture.assertUnchangedResources();
        }
    }

    @Test(timeOut = 30000)
    public void reentrantConfirmationProjectionPreservesTheNewCohortAndItsRealReplacementOwner() throws Exception {
        try (var fixture = new Fixture("reentrant")) {
            var running = fixture.start(() -> InputConfirm.confirm(fixture.controller(), fixture.draw, "Continue the real draw?", false, List.of("Continue", "Decline")));
            running.await();
            var replacement = new InputConfirm(fixture.controller(), "A real replacement question", "Replace", "Decline", false);
            HeadlessPlatform.query(fixture.table, () -> {
                var before = fixture.observation(); var inner = new ArrayList<JsonObject>();
                fixture.draw.beforeNextAmountRead = () -> {
                    fixture.draw.getMapParams().put("NumCards", "5"); fixture.table.publish(); inner.add(fixture.observation());
                };
                fixture.table.publish(); assertNull(fixture.draw.beforeNextAmountRead); assertEquals(inner.size(), 1);
                var committed = inner.get(0); assertDraw(fixture, committed, 5);
                assertCohort(fixture, committed.getAsJsonObject("state").get("boardRevision").getAsLong());
                assertEquals(fixture.observation(), committed, "The obsolete outer confirmation builder cannot overwrite the newer same-owner cohort");
                assertDraw(fixture, before, 1); fixture.record("before", before); fixture.record("inner", committed);

                var original = fixture.confirmation();
                fixture.draw.beforeNextAmountRead = () -> fixture.controller().getInputQueue().setInput(replacement);
                fixture.table.publish(); assertNull(fixture.draw.beforeNextAmountRead);
                assertSame(fixture.confirmation(), replacement); assertSame(fixture.controller().getInputQueue().getRootInput(), original);
                assertFalse(running.result.isDone(), "Replacing the displayed owner cannot answer the original blocking callback");
                assertThrows(IllegalArgumentException.class, () -> fixture.answer(committed, "ok"));
                var withdrawn = fixture.observation();
                assertTrue(withdrawn.getAsJsonObject("state").get("prompt").isJsonNull(),
                        "The old published question loses authority before the replacement's queued display callback runs");
                fixture.record("withdrawn", withdrawn);
                return null;
            });
            // InputProxy installs ownership immediately and queues its real
            // display. This barrier lets that ordinary callback publish it.
            var replaced = HeadlessPlatform.query(fixture.table, () -> {
                var current = fixture.observation(); assertSame(fixture.confirmation(), replacement);
                assertEquals(text(prompt(current), "message"), "A real replacement question");
                assertNull(replacement.getSpellAbility());
                var semantics = current.getAsJsonObject("semantics");
                assertEquals(text(semantics, "intent"), "Unknown"); assertTrue(semantics.get("amount").isJsonNull());
                assertFalse(semantics.get("selectionIsCost").getAsBoolean());
                assertFalse(semantics.has("operationGraph"), "An ordinary replacement question cannot inherit the old ability's graph");
                assertCohort(fixture, current.getAsJsonObject("state").get("boardRevision").getAsLong());
                fixture.record("replacement", current); return current;
            });
            fixture.answer(replaced, "ok");
            var resumed = fixture.current();
            assertTrue(replacement.getResult(), "The actual replacement receives its own accepted reply");
            assertEquals(text(prompt(resumed), "message"), "Continue the real draw?");
            assertFalse(running.result.isDone()); fixture.answer(resumed, "cancel");
            assertFalse(running.result.get(5, TimeUnit.SECONDS)); fixture.assertUnchangedResources();
        }
    }

    @Test(timeOut = 30000)
    public void stoppingAnActualConfirmationRetiresBeforeAwaitAndRejectsQueuedRepliesExactlyOnce() throws Exception {
        for (String queued : List.of("direct", "ok", "cancel")) try (var fixture = new Fixture("stopped-" + queued)) {
            boolean defaultYes = queued.equals("cancel");
            var running = fixture.start(() -> InputConfirm.confirm(fixture.controller(), fixture.draw,
                    "A stoppable real confirmation", defaultYes, List.of("Continue", "Decline")));
            running.await(); var before = fixture.current(); var awaits = new ArrayList<JsonObject>();
            var actual = HeadlessPlatform.query(fixture.table, () -> {
                var input = fixture.confirmation(); assertTrue(input.isConfirmationActive());
                fixture.onAwait = () -> {
                    assertFalse(input.isConfirmationActive(), "The real input retires before awaitNextInput can publish or receive another gesture");
                    assertSame(fixture.controller().getInputQueue().getInput(), input);
                    assertSame(fixture.controller().getInputProxy().getInput(), input);
                    assertThrows(IllegalArgumentException.class, () -> fixture.answer(before, "ok"));
                    assertThrows(IllegalArgumentException.class, () -> fixture.answer(before, "cancel"));
                    fixture.session().publishInput();
                    var stopped = JSON.toJsonTree(fixture.session().state()).getAsJsonObject();
                    assertTrue(stopped.get("prompt").isJsonNull(), "A cleanup callback cannot republish the just-stopped confirmation");
                    awaits.add(stopped);
                };
                // This is the actual UI lane: the admitted reply is queued
                // behind this callback, and the real owner stops first.
                if (!queued.equals("direct")) { fixture.answer(before, queued); input.stop(); }
                return input;
            });
            if (queued.equals("direct")) fixture.table.game.getAction().withState(() -> {
                // The original rules callback is awaiting this real input.
                // Stop outside the UI lane: inherited EDT cleanup is deferred,
                // so only early retirement can protect its queued owner.
                assertSame(fixture.confirmation(), actual); actual.stop();
            });
            assertFalse(actual.isConfirmationActive());
            assertEquals(actual.getResult(), defaultYes, "Stopping preserves the configured default rather than inventing a reply");
            HeadlessPlatform.query(fixture.table, () -> {
                assertEquals(awaits.size(), 1, "Early retirement and inherited cleanup share the idempotent await callback");
                assertNull(fixture.session().state().get("prompt"), "A late queued reply cannot restore consumed confirmation authority");
                assertThrows(IllegalArgumentException.class, () -> fixture.answer(before, "ok"));
                assertThrows(IllegalArgumentException.class, () -> fixture.answer(before, "cancel"));
                return null;
            });
            assertEquals(running.result.get(5, TimeUnit.SECONDS).booleanValue(), defaultYes);
            fixture.record("before", before); awaits.forEach(value -> fixture.record("await", value)); fixture.assertUnchangedResources();
        }
    }

    private static JsonObject prompt(JsonObject observation) { return observation.getAsJsonObject("state").getAsJsonObject("prompt"); }
    private static JsonObject operation(JsonObject observation) {
        return observation.getAsJsonObject("semantics").getAsJsonObject("operationGraph").getAsJsonArray("operations").get(0).getAsJsonObject();
    }
    private static void assertDraw(Fixture fixture, JsonObject observation, int amount) {
        assertEquals(text(prompt(observation), "inputType"), "InputConfirm");
        assertEquals(text(prompt(observation), "task"), "confirmation");
        assertEquals(text(observation.getAsJsonObject("semantics"), "intent"), "Benefit");
        assertEquals(observation.getAsJsonObject("semantics").get("amount").getAsInt(), amount);
        assertEquals(text(operation(observation), "effect"), "Draw"); assertEquals(operation(observation).get("amount").getAsInt(), amount);
    }
    private static void assertImmutableGraph(Object value) {
        var graph = (Map<?, ?>)value; assertThrows(UnsupportedOperationException.class, graph::clear);
        var operations = (List<?>)graph.get("operations"); assertFalse(operations.isEmpty());
        assertThrows(UnsupportedOperationException.class, operations::clear);
        var operation = (Map<?, ?>)operations.get(0); assertThrows(UnsupportedOperationException.class, operation::clear);
        var sources = (Map<?, ?>)operation.get("sources"); assertThrows(UnsupportedOperationException.class, sources::clear);
        var entities = (List<?>)sources.get("entities"); assertFalse(entities.isEmpty());
        assertThrows(UnsupportedOperationException.class, entities::clear); assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)entities.get(0))::clear);
        var costs = (List<?>)operation.get("costs"); assertFalse(costs.isEmpty());
        assertThrows(UnsupportedOperationException.class, costs::clear); assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)costs.get(0))::clear);
    }
    private static void assertPublished(MatchSession session, JsonObject observation) {
        assertEquals(observation.get("state"), JSON.toJsonTree(session.state()), "Human and automated confirmation facts share the accepted frame");
    }
    private static void assertPrivate(Fixture fixture) {
        for (var audience : fixture.table.automatedSeats()) if (audience != fixture.session()) {
            var observed = JSON.toJsonTree(audience.automatedObservation()).getAsJsonObject();
            assertTrue(observed.getAsJsonObject("state").get("prompt").isJsonNull());
            assertFalse(observed.getAsJsonObject("semantics").has("operationGraph"));
        }
        var spectator = fixture.table.spectatorState(); assertNull(spectator.get("prompt")); assertFalse(spectator.containsKey("semantics"));
    }
    private static void assertCohort(Fixture fixture, long board) {
        for (var audience : fixture.table.automatedSeats()) assertEquals(((Number)audience.state().get("boardRevision")).longValue(), board);
        assertEquals(((Number)fixture.table.spectatorState().get("boardRevision")).longValue(), board);
    }
    private static Input input(JsonObject observation) {
        var state = observation.getAsJsonObject("state"); return new Input(0, state, state.getAsJsonObject("prompt"));
    }
    private record Running(CountDownLatch opened, CompletableFuture<Boolean> result) {
        void await() throws InterruptedException { assertTrue(opened.await(5, TimeUnit.SECONDS), "The actual blocking confirmation callback must open"); }
    }
    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final Card source, forest; final ProbeCost cost = new ProbeCost(); final GuardedAbility draw;
        final List<GuardedAbility> abilities = new ArrayList<>(); final Path evidence; final IGuiGame originalGui; final List<Object> trace = new ArrayList<>();
        CountDownLatch opened; Runnable onAwait; CompletableFuture<Boolean> worker;
        Fixture(String name) throws Exception {
            var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
            evidence = ApiTestWorkspace.evidence("confirmation-publication-" + name);
            var seats = java.util.stream.IntStream.range(0, 4).mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Policy " + seat)).toList();
            table = new MatchTable("Constructed", seats, resources, evidence, true); HeadlessPlatform.activate(table);
            source = add("Grizzly Bears", ZoneType.Battlefield); forest = add("Forest", ZoneType.Battlefield); add("Island", ZoneType.Library);
            draw = new GuardedAbility(source, cost, ApiType.Draw, Map.of("AB", "Draw", "NumCards", "1")); draw.setActivatingPlayer(actor()); abilities.add(draw);
            originalGui = controller().getGui();
            controller().setGui((IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
                Object result;
                try { result = method.invoke(originalGui, args); }
                catch (InvocationTargetException failure) { throw failure.getCause(); }
                if (method.getName().equals("updateButtons") && controller().getInputQueue().getInput() instanceof InputConfirm && opened != null) opened.countDown();
                if (method.getName().equals("awaitNextInput") && onAwait != null) onAwait.run();
                return result;
            }));
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        PlayerControllerHuman controller() { return (PlayerControllerHuman)actor().getController(); }
        MatchSession session() { return table.automatedSeat(actor().getId()); }
        InputConfirm confirmation() { return (InputConfirm)controller().getInputQueue().getInput(); }
        Card add(String name, ZoneType zone) { var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor()); actor().getZone(zone).add(card); return card; }
        Running start(Supplier<Boolean> callback) {
            opened = new CountDownLatch(1); worker = new CompletableFuture<>(); var result = worker;
            table.game.getAction().invoke(() -> { try { result.complete(callback.get()); } catch (Throwable failure) { result.completeExceptionally(failure); } });
            return new Running(opened, result);
        }
        JsonObject observation() { var value = JSON.toJsonTree(session().automatedObservation()).getAsJsonObject(); assertPublished(session(), value); return value; }
        JsonObject current() { return HeadlessPlatform.query(table, this::observation); }
        void answer(JsonObject current, String action) { session().action(request(input(current), Map.of("action", action, "expectedTask", "confirmation"))); }
        void record(String label, JsonObject current) { trace.add(Map.of("label", label, "observation", current.deepCopy())); }
        void assertUnchangedResources() { assertFalse(source.isTapped()); assertFalse(forest.isTapped()); assertEquals(actor().getManaPool().totalMana(), 0); assertNull(session().state().get("error")); }
        @Override public void close() throws Exception {
            for (var ability : abilities) { ability.forbidReads = false; ability.beforeNextAmountRead = null; }
            cost.forbidProbes = false; onAwait = null;
            try { Files.writeString(evidence.resolve("trace.json"), JSON.toJson(trace)); Files.writeString(evidence.resolve("private-final-views.json"), JSON.toJson(table.automatedSeats().stream().map(MatchSession::state).toList())); }
            finally { controller().setGui(originalGui); table.close(); if (worker != null) worker.handle((value, failure) -> null).get(5, TimeUnit.SECONDS); }
        }
    }
    private static final class ProbeCost extends Cost {
        boolean forbidProbes;
        ProbeCost() { super("1 U", true); }
        @Override public boolean canPay(SpellAbility ability, Player payer, boolean effect) {
            if (forbidProbes) throw new AssertionError("Confirmation meaning ran a discarded affordability query"); return super.canPay(ability, payer, effect);
        }
    }
    private static final class GuardedAbility extends AbilityApiBased {
        boolean forbidReads; Runnable beforeNextAmountRead;
        GuardedAbility(Card source, Cost cost, ApiType api, Map<String, String> parameters) { super(api, source, cost, null, parameters); }
        private void read() { if (forbidReads) throw new AssertionError("A retained confirmation reopened its actual ability's semantic getters"); }
        @Override public Cost getPayCosts() { read(); return super.getPayCosts(); }
        @Override public String getParam(String key) { read(); String value = super.getParam(key); amountRead(key); return value; }
        @Override public String getParamOrDefault(String key, String fallback) { read(); String value = super.getParamOrDefault(key, fallback); amountRead(key); return value; }
        private void amountRead(String key) { if (key.equals("NumCards")) { var callback = beforeNextAmountRead; beforeNextAmountRead = null; if (callback != null) callback.run(); } }
        @Override public boolean allowsPayingWithShard(Card source, byte color) { throw new AssertionError("Confirmation meaning ran a discarded funding query"); }
    }
}
