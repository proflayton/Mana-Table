package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.cost.Cost;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.SpellPermanent;
import forge.game.zone.ZoneType;
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
import java.util.concurrent.atomic.AtomicInteger;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual announcement callbacks retain one immutable, audience-scoped numeric context. */
public class NumericPublicationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(timeOut = 30000)
    public void retainedAnnouncementsDoNotReopenCostsOrFundingAndUnavailableFactsStayNullable() throws Exception {
        try (var fixture = new Fixture("retained")) {
            var running = fixture.start(new Request(fixture.walker, 0, 4));
            running.await(0);
            HeadlessPlatform.query(fixture.table, () -> {
                var raw = fixture.session().automatedObservation();
                var before = JSON.toJsonTree(raw).getAsJsonObject();
                assertPublished(fixture.session(), before);
                var numeric = before.getAsJsonObject("semantics").getAsJsonObject("numeric");
                assertEquals(symbols(numeric), List.of("{X}", "{X}"));
                assertEquals(numeric.getAsJsonObject("funding").getAsJsonArray("sources").size(), 1);
                assertEquals(text(numeric, "manaCostKnowledge"), "Unknown");
                assertEquals(text(numeric.getAsJsonObject("funding"), "knowledge"), "Unknown");
                assertEquals(text(numeric, "sourceCardId"), text(before.getAsJsonObject("state").getAsJsonObject("prompt"), "sourceCardId"));
                assertImmutableNumeric((Map<?, ?>)((Map<?, ?>)raw.get("semantics")).get("numeric"));
                fixture.walker.forbidReads = true;
                try {
                    for (int read = 0; read < 3; read++) {
                        assertEquals(JSON.toJsonTree(fixture.session().automatedObservation()), before,
                                "Retained numeric facts cannot reopen the actual announcement's cost or funding routes");
                        assertPublished(fixture.session(), before);
                    }
                } finally { fixture.walker.forbidReads = false; }

                fixture.walker.setPayCosts(new Cost("X", false));
                assertEquals(fixture.observation(), before, "Changing the provider's original cost cannot rewrite an already published context");
                fixture.forest.setTapped(true); fixture.table.publish();
                var refreshed = fixture.observation();
                assertEquals(refreshed.getAsJsonObject("state").getAsJsonObject("prompt").get("id"),
                        before.getAsJsonObject("state").getAsJsonObject("prompt").get("id"));
                assertEquals(symbols(refreshed.getAsJsonObject("semantics").getAsJsonObject("numeric")), List.of("{X}"));
                assertTrue(refreshed.getAsJsonObject("semantics").getAsJsonObject("numeric").getAsJsonObject("funding").getAsJsonArray("sources").isEmpty());
                assertEquals(symbols(numeric), List.of("{X}", "{X}"));
                assertEquals(numeric.getAsJsonObject("funding").getAsJsonArray("sources").size(), 1);
                assertEquals(JSON.toJsonTree(raw), before, "The original raw nested context is a value copy, not just a copied JSON assertion");
                assertPrivate(fixture);

                assertTrue(fixture.walker.getHostCard().turnFaceDown(true));
                fixture.table.publish();
                var unavailable = fixture.observation();
                assertTrue(unavailable.getAsJsonObject("semantics").has("numeric"));
                assertTrue(unavailable.getAsJsonObject("semantics").get("numeric").isJsonNull(),
                        "An active typed announcement with undisclosed source context retains the established nullable shape");
                assertEquals(text(unavailable.getAsJsonObject("state").getAsJsonObject("prompt"), "kind"), "number");
                fixture.walker.forbidReads = true;
                try { assertEquals(fixture.observation(), unavailable, "Unavailable captured metadata is retained without another live query"); }
                finally { fixture.walker.forbidReads = false; }
                assertPrivate(fixture);
                fixture.record("before", before); fixture.record("refreshed", refreshed); fixture.record("unavailable", unavailable);
                return null;
            });
            fixture.answer(fixture.current(), 0);
            assertEquals(running.result.get(5, TimeUnit.SECONDS), List.of(0), "Unknown funding metadata does not answer or disable the required announcement");
            assertNull(fixture.walker.getXManaCostPaid(), "Only the calling rules continuation can invest the returned announcement");
            assertEquals(fixture.actor().getManaPool().totalMana(), 0);
        }
    }

    @Test(timeOut = 30000)
    public void aReentrantFundingPublicationKeepsTheNewNumericCohortReadable() throws Exception {
        try (var fixture = new Fixture("reentrant")) {
            var running = fixture.start(new Request(fixture.walker, 0, 4));
            running.await(0);
            HeadlessPlatform.query(fixture.table, () -> {
                var before = fixture.observation(); var inner = new ArrayList<JsonObject>();
                fixture.walker.beforeNextFundingRead = () -> {
                    fixture.forest.setTapped(true); fixture.table.publish();
                    inner.add(JSON.toJsonTree(fixture.session().state()).getAsJsonObject());
                };
                fixture.table.publish();
                assertNull(fixture.walker.beforeNextFundingRead);
                assertEquals(inner.size(), 1, "The real funding query must exercise the one-shot nested publication");
                var published = inner.get(0); long board = published.get("boardRevision").getAsLong();
                for (var audience : fixture.table.automatedSeats()) assertEquals(((Number)audience.state().get("boardRevision")).longValue(), board);
                assertEquals(((Number)fixture.table.spectatorState().get("boardRevision")).longValue(), board);
                assertEquals(JSON.toJsonTree(fixture.session().state()), published,
                        "The obsolete outer funding query cannot overwrite the inner cohort");
                var recovered = fixture.observation();
                assertEquals(recovered.get("state"), published, "The next current read uses the inner capture without another publication");
                assertTrue(recovered.getAsJsonObject("semantics").getAsJsonObject("numeric").getAsJsonObject("funding").getAsJsonArray("sources").isEmpty());
                assertEquals(before.getAsJsonObject("semantics").getAsJsonObject("numeric").getAsJsonObject("funding").getAsJsonArray("sources").size(), 1);
                assertPublished(fixture.session(), recovered);
                fixture.record("before", before); fixture.record("recovered", recovered);
                return null;
            });
            fixture.answer(fixture.current(), 2);
            assertEquals(running.result.get(5, TimeUnit.SECONDS), List.of(2));
            assertNull(fixture.walker.getXManaCostPaid());
        }
    }

    @Test(timeOut = 30000)
    public void sequentialNumericOwnersRejectOldAnswersAndLeavePlainNumbersUntyped() throws Exception {
        try (var fixture = new Fixture("owners")) {
            var running = fixture.start(new Request(fixture.walker, 0, 4), new Request(fixture.serpent, 0, 1), new Request(null, 0, 2));
            running.await(0); var first = fixture.current();
            fixture.answer(first, 2);
            running.await(1);
            HeadlessPlatform.query(fixture.table, () -> {
                var second = fixture.observation(); var prompt = second.getAsJsonObject("state").getAsJsonObject("prompt");
                assertNotEquals(prompt.get("id"), first.getAsJsonObject("state").getAsJsonObject("prompt").get("id"));
                assertEquals(text(prompt.getAsJsonObject("sourceCard"), "name"), "Stonecoil Serpent");
                assertEquals(prompt.get("max").getAsInt(), 1);
                assertEquals(symbols(second.getAsJsonObject("semantics").getAsJsonObject("numeric")), List.of("{X}"));
                assertThrows(IllegalArgumentException.class, () -> fixture.answer(first, 0));
                assertThrows(IllegalArgumentException.class, () -> fixture.answer(second, 2));
                assertFalse(running.result.isDone(), "Neither a stale answer nor an out-of-range answer can complete the new callback");
                assertEquals(fixture.observation(), second, "Rejected answers preserve the exact current numeric owner and retained facts");
                fixture.record("first", first); fixture.record("second", second);
                return null;
            });
            var second = fixture.current(); fixture.answer(second, 1);
            running.await(2);
            HeadlessPlatform.query(fixture.table, () -> {
                var plain = fixture.observation();
                assertEquals(text(plain.getAsJsonObject("state").getAsJsonObject("prompt"), "kind"), "number");
                assertFalse(plain.getAsJsonObject("semantics").has("numeric"), "An ordinary integer callback does not acquire announcement metadata");
                assertThrows(IllegalArgumentException.class, () -> fixture.answer(second, 0));
                assertFalse(running.result.isDone()); assertEquals(fixture.observation(), plain);
                fixture.record("plain", plain);
                return null;
            });
            fixture.answer(fixture.current(), 0);
            assertEquals(running.result.get(5, TimeUnit.SECONDS), List.of(2, 1, 0), "Each real callback receives exactly its own accepted answer");
            assertNull(fixture.walker.getXManaCostPaid()); assertNull(fixture.serpent.getXManaCostPaid());
            assertFalse(fixture.forest.isTapped()); assertEquals(fixture.actor().getManaPool().totalMana(), 0);
        }
    }

    static void assertImmutableNumeric(Map<?, ?> numeric) {
        assertThrows(UnsupportedOperationException.class, numeric::clear);
        var costs = (List<?>)numeric.get("costs");
        assertFalse(costs.isEmpty()); assertThrows(UnsupportedOperationException.class, costs::clear);
        assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)costs.get(0))::clear);
        var funding = (Map<?, ?>)numeric.get("funding");
        assertThrows(UnsupportedOperationException.class, funding::clear);
        var sources = (List<?>)funding.get("sources");
        assertFalse(sources.isEmpty()); assertThrows(UnsupportedOperationException.class, sources::clear);
        var source = (Map<?, ?>)sources.get(0); assertThrows(UnsupportedOperationException.class, source::clear);
        var route = (Map<?, ?>)((List<?>)source.get("routes")).get(0);
        assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)((List<?>)route.get("costs")).get(0))::clear);
        var alternatives = (List<?>)((Map<?, ?>)route.get("production")).get("alternatives");
        assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)((Map<?, ?>)alternatives.get(0)).get("colors"))::clear);
    }
    private static void assertPublished(MatchSession session, JsonObject observation) {
        assertEquals(observation.get("state"), JSON.toJsonTree(session.state()), "Numeric meaning and the human-compatible prompt share one published frame");
    }
    private static List<String> symbols(JsonObject numeric) {
        return numeric.getAsJsonArray("costs").asList().stream().map(value -> value.getAsJsonObject())
                .filter(cost -> text(cost, "kind").equals("Mana"))
                .flatMap(cost -> cost.getAsJsonArray("manaSymbols").asList().stream()).map(value -> value.getAsString()).toList();
    }
    private static void assertPrivate(Fixture fixture) {
        for (var audience : fixture.table.automatedSeats()) if (audience != fixture.session()) {
            var other = JSON.toJsonTree(audience.automatedObservation()).getAsJsonObject();
            assertFalse(other.getAsJsonObject("semantics").has("numeric"));
            assertFalse(other.toString().contains("Hangarback Walker"));
        }
        var spectator = JSON.toJsonTree(fixture.table.spectatorState()).getAsJsonObject();
        assertTrue(!spectator.has("prompt") || spectator.get("prompt").isJsonNull());
        assertFalse(spectator.has("semantics")); assertFalse(spectator.toString().contains("Hangarback Walker"));
    }

    private record Request(SpellAbility ability, int minimum, int maximum) { }
    private record Running(List<CountDownLatch> opened, CompletableFuture<List<Integer>> result) {
        void await(int index) throws InterruptedException { assertTrue(opened.get(index).await(5, TimeUnit.SECONDS), "The actual required number callback must open"); }
    }
    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final GuardedSpell walker, serpent; final Card forest; final Path evidence;
        final List<Object> trace = new ArrayList<>(); CompletableFuture<List<Integer>> worker; final IGuiGame originalGui;
        Fixture(String name) throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            evidence = ApiTestWorkspace.evidence("numeric-publication-" + name);
            var players = java.util.stream.IntStream.range(0, 4).mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Policy " + seat)).toList();
            table = new MatchTable("Constructed", players, resources, evidence, true); HeadlessPlatform.activate(table);
            walker = spell("Hangarback Walker"); serpent = spell("Stonecoil Serpent"); forest = add("Forest", ZoneType.Battlefield);
            originalGui = controller().getGui();
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        PlayerControllerHuman controller() { return (PlayerControllerHuman)actor().getController(); }
        MatchSession session() { return table.automatedSeat(actor().getId()); }
        Card add(String name, ZoneType zone) {
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor()); actor().getZone(zone).add(card); return card;
        }
        GuardedSpell spell(String name) { var ability = new GuardedSpell(add(name, ZoneType.Hand)); ability.setActivatingPlayer(actor()); return ability; }
        Running start(Request... requests) {
            var opened = java.util.stream.IntStream.range(0, requests.length).mapToObj(ignored -> new CountDownLatch(1)).toList();
            var index = new AtomicInteger();
            controller().setGui((IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
                // The real worker holds the rules boundary until the original
                // callback publishes and awaits its response. A subsequent
                // state-cut read therefore observes that published callback.
                if (method.getName().equals("getIntegerForAnnouncement") || method.getName().equals("getInteger")) opened.get(index.getAndIncrement()).countDown();
                try { return method.invoke(originalGui, args); }
                catch (InvocationTargetException failure) { throw failure.getCause(); }
            }));
            worker = new CompletableFuture<>();
            table.game.getAction().invoke(() -> {
                try {
                    var answers = new ArrayList<Integer>();
                    for (var request : requests) answers.add(request.ability() == null
                            ? controller().getGui().getInteger("An ordinary required number", request.minimum(), request.maximum(), request.maximum())
                            : controller().announceRequirements(request.ability(), request.minimum(), request.maximum(), "X"));
                    worker.complete(List.copyOf(answers));
                } catch (Throwable failure) { worker.completeExceptionally(failure); }
            });
            return new Running(opened, worker);
        }
        JsonObject observation() {
            var result = JSON.toJsonTree(session().automatedObservation()).getAsJsonObject(); assertPublished(session(), result); return result;
        }
        JsonObject current() { return HeadlessPlatform.query(table, this::observation); }
        void answer(JsonObject observation, int value) {
            var state = observation.getAsJsonObject("state");
            session().action(request(new Input(0, state, state.getAsJsonObject("prompt")), Map.of("action", "number", "value", value, "expectedTask", "number")));
        }
        void record(String label, JsonObject observation) { trace.add(Map.of("label", label, "observation", observation.deepCopy())); }
        @Override public void close() throws Exception {
            walker.forbidReads = false; serpent.forbidReads = false; walker.beforeNextFundingRead = null; serpent.beforeNextFundingRead = null;
            try { Files.writeString(evidence.resolve("trace.json"), JSON.toJson(trace));
                Files.writeString(evidence.resolve("private-final-views.json"), JSON.toJson(table.automatedSeats().stream().map(MatchSession::state).toList())); }
            finally {
                controller().setGui(originalGui); table.close();
                if (worker != null) worker.handle((value, failure) -> null).get(5, TimeUnit.SECONDS);
            }
        }
    }
    private static final class GuardedSpell extends SpellPermanent {
        boolean forbidReads; Runnable beforeNextFundingRead;
        GuardedSpell(Card source) { super(source); }
        private void read() { if (forbidReads) throw new AssertionError("A retained numeric context reopened its cost or funding query"); }
        @Override public Cost getPayCosts() { read(); return super.getPayCosts(); }
        @Override public boolean allowsPayingWithShard(Card source, byte color) {
            read(); boolean result = super.allowsPayingWithShard(source, color);
            var callback = beforeNextFundingRead; beforeNextFundingRead = null; if (callback != null) callback.run();
            return result;
        }
    }
}
