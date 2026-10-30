package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.card.MagicColor;
import forge.deck.Deck;
import forge.game.ability.AbilityApiBased;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardCollection;
import forge.game.cost.Cost;
import forge.game.cost.CostPayment;
import forge.game.mana.Mana;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectCardsFromList;
import forge.gamemodes.match.input.InputSelectEntitiesFromList;
import forge.gamemodes.match.input.InputSelectManyBase;
import forge.gui.interfaces.IGuiGame;
import forge.player.HumanCostDecision;
import forge.player.PlayerControllerHuman;
import forge.util.collect.FCollection;
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
import java.util.function.BiConsumer;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real list inputs retain one published selection and one actual cost owner. */
public class ManyPublicationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(timeOut = 30000)
    public void retainedPlayerChoicesDoNotReopenTheAbilityAndRefreshWithoutRewritingOldFacts() throws Exception {
        try (var fixture = new Fixture("retained", false)) {
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.cost.forbidProbes = true;
                fixture.table.publish();
                var raw = fixture.session().automatedObservation();
                var before = JSON.toJsonTree(raw).getAsJsonObject();
                var prompt = before.getAsJsonObject("state").getAsJsonObject("prompt");
                assertFalse(prompt.has("min")); assertFalse(prompt.has("max"), "List selection adds no target-count wire fields");
                assertEquals(prompt.get("playerChoices"), JSON.toJsonTree(List.of(2, 0, 3, 1).stream()
                        .map(seat -> fixture.player(seat).getId()).toList()), "The provider's actual choice order survives publication");
                assertEquals(before.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of()));
                var facts = (Map<?, ?>)raw.get("semantics");
                assertThrows(UnsupportedOperationException.class, ((List<?>)facts.get("selectedPlayers"))::clear);
                var operations = (List<?>)((Map<?, ?>)facts.get("operationGraph")).get("operations");
                assertFalse(operations.isEmpty());
                assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)operations.get(0))::clear);
                var publishedPrompt = (Map<?, ?>)((Map<?, ?>)raw.get("state")).get("prompt");
                assertThrows(UnsupportedOperationException.class, ((List<?>)publishedPrompt.get("playerChoices"))::clear);
                fixture.ability.forbidReads = true;
                try {
                    for (int read = 0; read < 3; read++) {
                        assertEquals(JSON.toJsonTree(fixture.session().automatedObservation()), before);
                        assertEquals(JSON.toJsonTree(fixture.session().state()).getAsJsonObject().get("prompt"), prompt);
                    }
                } finally { fixture.ability.forbidReads = false; }
                fixture.record("retained", before);
                return null;
            });
            var before = fixture.current();
            int chosen = fixture.player(1).getId();
            var selected = fixture.answer(before, Map.of("action", "player", "playerId", chosen));
            assertEquals(selected.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of(chosen)));
            assertEquals(episode(selected), episode(before));
            var deselected = fixture.answer(selected, Map.of("action", "player", "playerId", chosen));
            assertEquals(deselected.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of()));
            assertEquals(episode(deselected), episode(before));
            assertEquals(selected.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of(chosen)),
                    "A retained observation cannot alias the mutable selected collection");
            assertSame(fixture.controller().getInputQueue().getInput(), fixture.input);
            assertFalse(fixture.forest.isTapped()); assertEquals(fixture.actor().getManaPool().totalMana(), 0);
            HeadlessPlatform.query(fixture.table, () -> {
                ((PlayerControllerHuman)fixture.player(2).getController()).concede();
                assertTrue(fixture.player(2).hasLost());
                assertTrue(fixture.input.canSelectEntity(fixture.player(2)),
                        "A generic list retains its explicit registered-player membership; target death rules do not replace it");
                fixture.session().publishInput();
                return null;
            });
            var departedSelected = fixture.answer(fixture.current(), Map.of("action", "player", "playerId", fixture.player(2).getId()));
            assertEquals(departedSelected.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of(fixture.player(2).getId())));
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.choices.remove(fixture.player(2)); fixture.input.showMessage(); fixture.session().publishInput();
                var narrowed = fixture.observation();
                assertFalse(fixture.input.canSelectEntity(fixture.player(2)));
                assertFalse(narrowed.getAsJsonObject("state").getAsJsonObject("prompt").getAsJsonArray("playerChoices").asList().stream()
                        .anyMatch(value -> value.getAsInt() == fixture.player(2).getId()));
                assertThrows(IllegalArgumentException.class, () -> fixture.session().action(request(input(narrowed),
                        Map.of("action", "player", "playerId", fixture.player(2).getId()))));
                assertEquals(fixture.observation(), narrowed, "Selected membership alone cannot authorize a removed candidate's click");
                fixture.record("narrowed", narrowed);
                return null;
            });
            fixture.record("selected", selected); fixture.record("deselected", deselected);
        }
    }

    @Test(timeOut = 30000)
    public void aReentrantListPublicationKeepsTheNewSameOwnerCohortReadable() throws Exception {
        try (var fixture = new Fixture("reentrant", false)) {
            HeadlessPlatform.query(fixture.table, () -> {
                var before = fixture.observation(); var inner = new ArrayList<JsonObject>();
                fixture.ability.beforeNextAmountRead = () -> {
                    fixture.input.selectPlayer(fixture.player(1), null);
                    fixture.table.publish();
                    inner.add(JSON.toJsonTree(fixture.session().state()).getAsJsonObject());
                };
                fixture.table.publish();
                assertNull(fixture.ability.beforeNextAmountRead);
                assertEquals(inner.size(), 1, "The actual selection projection must reach the one-shot nested publication");
                var published = inner.get(0);
                assertCohort(fixture.table, published.get("boardRevision").getAsLong());
                assertEquals(JSON.toJsonTree(fixture.session().state()), published,
                        "The obsolete outer list projection cannot replace the inner selected cohort");
                var recovered = fixture.observation();
                assertEquals(recovered.get("state"), published, "The new capture remains usable without another publication");
                assertEquals(recovered.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of(fixture.player(1).getId())));
                assertEquals(episode(recovered), episode(before));
                assertEquals(before.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of()));
                fixture.record("before", before); fixture.record("recovered", recovered);
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void publicationObserversSeeTheCompleteSelectionCohortAndItsCurrentAuthority() throws Exception {
        try (var fixture = new Fixture("observer-cohort", false)) {
            var current = HeadlessPlatform.query(fixture.table, () -> {
                var handNames = List.of("Island", "Mountain", "Swamp", "Plains");
                for (int seat = 0; seat < handNames.size(); seat++) {
                    var owner = fixture.player(seat);
                    owner.getZone(ZoneType.Hand).add(Card.fromPaperCard(
                            StaticData.instance().getCommonCards().getCard(handNames.get(seat)), owner));
                }
                fixture.session().publishInput();
                var before = fixture.observation();
                long previousBoard = before.getAsJsonObject("state").get("boardRevision").getAsLong();
                var cohorts = new ArrayList<List<JsonObject>>();
                var spectators = new ArrayList<JsonObject>();
                boolean[] armed = { false };
                fixture.session().observePlaytest(ignored -> {
                    if (!armed[0]) return;
                    armed[0] = false;
                    cohorts.add(fixture.table.automatedSeats().stream()
                            .map(audience -> JSON.toJsonTree(audience.state()).getAsJsonObject()).toList());
                    spectators.add(JSON.toJsonTree(fixture.table.spectatorState()).getAsJsonObject());
                });
                assertTrue(cohorts.isEmpty(), "The immediate subscription frame is deliberately disarmed");
                armed[0] = true;
                fixture.session().publishInput();
                assertFalse(armed[0], "The actual owned input publication must notify its observer");
                assertEquals(cohorts.size(), 1); assertEquals(spectators.size(), 1);
                var views = cohorts.get(0); var spectator = spectators.get(0);
                for (var view : views) fixture.record("observer-seat-" + view.get("viewerId"), view);
                fixture.record("observer-spectator", spectator); fixture.record("before-observer", before);
                assertEquals(views.size(), 4);
                long board = views.get(0).get("boardRevision").getAsLong();
                assertTrue(board > previousBoard, "The callback must describe this new input publication");
                for (int seat = 0; seat < views.size(); seat++) {
                    var view = views.get(seat); int viewerId = fixture.player(seat).getId();
                    assertEquals(text(view, "id"), fixture.table.id);
                    assertEquals(view.get("viewerId").getAsInt(), viewerId);
                    assertEquals(view.get("boardRevision").getAsLong(), board,
                            "An observer must not receive one new audience beside retained older audiences");
                    assertEquals(view.get("prompt").isJsonNull(), seat != 0,
                            "Only the actual selection controller receives its question");
                    for (int owner = 0; owner < handNames.size(); owner++) {
                        var hand = zone(player(view, fixture.player(owner).getId()), "Hand");
                        assertEquals(hand.get("count").getAsInt(), 1);
                        assertEquals(hand.getAsJsonArray("cards").size(), owner == seat ? 1 : 0,
                                "A complete cohort still filters each other seat's real hand");
                        if (owner == seat) assertEquals(text(hand.getAsJsonArray("cards").get(0).getAsJsonObject(), "name"), handNames.get(owner));
                    }
                }
                assertEquals(spectator.get("boardRevision").getAsLong(), board,
                        "The public spectator must commit before publication observers run");
                assertEquals(spectator.get("viewerId").getAsInt(), -1); assertTrue(spectator.get("prompt").isJsonNull());
                for (int owner = 0; owner < handNames.size(); owner++) {
                    var hand = zone(player(spectator, fixture.player(owner).getId()), "Hand");
                    assertEquals(hand.get("count").getAsInt(), 1); assertTrue(hand.getAsJsonArray("cards").isEmpty());
                }
                assertSame(fixture.controller().getInputQueue().getInput(), fixture.input);
                assertSame(fixture.controller().getInputProxy().getInput(), fixture.input); assertTrue(fixture.input.isSelectionActive());
                var observed = fixture.observation();
                assertEquals(observed.get("state"), views.get(0),
                        "The callback's owner view and current retained decision authority must describe the same committed frame");
                assertThrows(IllegalArgumentException.class, () -> fixture.table.automatedSeat(fixture.player(1).getId())
                        .action(request(input(observed), Map.of("action", "player", "playerId", fixture.player(1).getId()))));
                return observed;
            });
            var selected = fixture.answer(current, Map.of("action", "player", "playerId", fixture.player(1).getId()));
            assertEquals(selected.getAsJsonObject("semantics").get("selectedPlayers"), JSON.toJsonTree(List.of(fixture.player(1).getId())));
            assertEquals(fixture.input.getSelected().size(), 1); assertTrue(fixture.input.getSelected().contains(fixture.player(1)));
            assertFalse(fixture.forest.isTapped()); assertEquals(fixture.actor().getManaPool().totalMana(), 0);
            fixture.record("after-observer-selection", selected);
        }
    }

    @Test(timeOut = 30000)
    public void anEqualReplacementCostEntryCannotAdoptAnInFlightSelectionsFacts() throws Exception {
        try (var fixture = new Fixture("cost-entry", true)) {
            var opened = new CountDownLatch(1);
            var originalGui = fixture.controller().getGui();
            fixture.controller().setGui(intercept(originalGui, (name, args) -> {
                if (name.equals("updateButtons") && fixture.controller().getInputQueue().getInput() instanceof InputSelectManyBase<?>) opened.countDown();
            }));
            var completion = new CompletableFuture<Boolean>();
            fixture.table.game.getAction().invoke(() -> {
                try { completion.complete(new CostPayment(fixture.cost, fixture.ability).payCost(
                        new HumanCostDecision(fixture.controller(), fixture.actor(), fixture.ability, false, ""))); }
                catch (Throwable failure) { completion.completeExceptionally(failure); }
            });
            try {
                assertTrue(opened.await(5, TimeUnit.SECONDS), "The real tap-cost payment must open its selection");
                HeadlessPlatform.query(fixture.table, () -> {
                    var actual = (InputSelectManyBase<?>)fixture.controller().getInputQueue().getInput();
                    assertTrue(actual.isSelectionActive());
                    var before = fixture.observation();
                    assertNotNull(before.getAsJsonObject("semantics").getAsJsonObject("costPayment"));
                    fixture.ability.forbidReads = true;
                    try {
                        for (int read = 0; read < 3; read++) assertEquals(JSON.toJsonTree(fixture.session().automatedObservation()), before,
                                "Retained cost selections do not reopen the ability or repeat its funding query");
                    } finally { fixture.ability.forbidReads = false; }
                    var old = fixture.table.game.costPaymentStack.peek();
                    var replacement = new ArrayList<Object>();
                    fixture.ability.beforeNextFundingRead = () -> {
                        assertSame(fixture.table.game.costPaymentStack.pop(), old);
                        var next = fixture.table.game.costPaymentStack.push(old.cost(), old.payment());
                        assertEquals(next, old); assertNotSame(next, old);
                        replacement.add(next);
                    };
                    fixture.table.publish();
                    assertNull(fixture.ability.beforeNextFundingRead);
                    assertEquals(replacement.size(), 1, "The actual funding query must replace the current entry");
                    var withdrawn = JSON.toJsonTree(fixture.session().state()).getAsJsonObject();
                    assertEquals(text(withdrawn, "status"), "playing");
                    assertEquals(text(withdrawn, "actionClass"), "resolving");
                    assertTrue(withdrawn.get("decisionOwnerId").isJsonNull()); assertTrue(withdrawn.get("prompt").isJsonNull());
                    assertCohort(fixture.table, withdrawn.get("boardRevision").getAsLong());
                    for (var audience : fixture.table.automatedSeats()) assertNull(audience.state().get("prompt"));
                    assertThrows(IllegalArgumentException.class, () -> fixture.session().action(request(input(before), Map.of("action", "cancel"))));
                    fixture.session().publishInput();
                    var recovered = fixture.observation();
                    assertSame(fixture.controller().getInputQueue().getInput(), actual);
                    assertTrue(actual.isSelectionActive());
                    assertNotEquals(text(recovered.getAsJsonObject("state").getAsJsonObject("prompt"), "id"),
                            text(before.getAsJsonObject("state").getAsJsonObject("prompt"), "id"));
                    assertEquals(episode(recovered), episode(before));
                    assertEquals(recovered.getAsJsonObject("semantics").get("costPayment"), before.getAsJsonObject("semantics").get("costPayment"));
                    fixture.record("before", before); fixture.record("withdrawn", withdrawn); fixture.record("recovered", recovered);
                    actual.selectButtonCancel();
                    return null;
                });
                assertFalse(completion.get(5, TimeUnit.SECONDS), "Cancellation still reaches the real cost payment continuation");
                assertFalse(fixture.source.isTapped()); assertFalse(fixture.forest.isTapped());
                assertEquals(fixture.actor().getManaPool().totalMana(), 1, "The read-only funding query spends no floating mana");
            } finally {
                fixture.ability.beforeNextFundingRead = null;
                HeadlessPlatform.query(fixture.table, () -> {
                    if (fixture.controller().getInputQueue().getInput() instanceof InputSelectManyBase<?> actual) actual.stop();
                    return null;
                });
                fixture.controller().setGui(originalGui);
            }
        }
    }

    @Test(timeOut = 30000)
    public void realCancelAndDirectStopCloseTheOwnerBeforeClearingSelectionHighlights() throws Exception {
        for (boolean cancel : List.of(true, false)) try (var fixture = new Fixture("cleanup-" + cancel, false)) {
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.input.stop();
                var cards = new InputSelectCardsFromList(fixture.controller(), 0, 2,
                        new CardCollection(List.of(fixture.source, fixture.forest)), fixture.ability);
                cards.setCancelAllowed(true); fixture.controller().getInputQueue().setInput(cards);
                return null;
            });
            HeadlessPlatform.query(fixture.table, () -> {
                var cards = (InputSelectCardsFromList)fixture.controller().getInputQueue().getInput();
                assertTrue(cards.selectCard(fixture.source, null, null));
                fixture.session().publishInput(); var before = fixture.observation();
                assertEquals(cards.getSelected().size(), 1);
                var cleanup = new ArrayList<JsonObject>(); var originalGui = fixture.controller().getGui();
                fixture.controller().setGui(intercept(originalGui, (name, args) -> {
                    if (name.equals("clearSelectables") || name.equals("setHighlighted") && Boolean.FALSE.equals(args[1])) {
                        assertFalse(cards.isSelectionActive(), "The real owner must retire before any cleanup callback can publish");
                        fixture.session().publishInput();
                        cleanup.add(JSON.toJsonTree(fixture.session().state()).getAsJsonObject());
                    }
                }));
                try { if (cancel) cards.selectButtonCancel(); else cards.stop(); }
                finally { fixture.controller().setGui(originalGui); }
                assertFalse(cleanup.isEmpty()); assertFalse(cards.isSelectionActive());
                if (cancel) { assertTrue(cards.hasCancelled()); assertTrue(cards.getSelected().isEmpty()); }
                for (var state : cleanup) assertTrue(state.get("prompt").isJsonNull(), state.toString());
                assertThrows(IllegalArgumentException.class, () -> fixture.session().action(request(input(before), Map.of("action", "cancel"))));
                fixture.record("before", before); cleanup.forEach(state -> fixture.record("cleanup", state));
                return null;
            });
        }
    }

    static JsonObject assertCurrentSelection(MatchTable table, MatchSession session) {
        var observation = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
        assertEquals(observation.get("state"), JSON.toJsonTree(session.state()), "Human and AI consume the same published selection frame");
        assertEquals(JSON.toJsonTree(session.automatedObservation()), observation, "Retained selection reads are pure within one state cut");
        return observation;
    }

    private static void assertCohort(MatchTable table, long board) {
        for (var audience : table.automatedSeats()) assertEquals(((Number)audience.state().get("boardRevision")).longValue(), board);
        assertEquals(((Number)table.spectatorState().get("boardRevision")).longValue(), board);
    }
    private static String episode(JsonObject observation) { return text(observation.getAsJsonObject("semantics"), "episodeId"); }
    private static Input input(JsonObject observation) {
        var state = observation.getAsJsonObject("state"); return new Input(0, state, state.getAsJsonObject("prompt"));
    }
    static IGuiGame intercept(IGuiGame original, BiConsumer<String, Object[]> after) {
        return (IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
            Object result;
            try { result = method.invoke(original, args); }
            catch (InvocationTargetException failure) { throw failure.getCause(); }
            after.accept(method.getName(), args); return result;
        });
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final Card source, forest; final ProbeCost cost; final GuardedAbility ability;
        final FCollection<Player> choices = new FCollection<>();
        final InputSelectEntitiesFromList<Player> input; final Path evidence; final List<Object> trace = new ArrayList<>();
        Fixture(String name, boolean payment) throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            evidence = ApiTestWorkspace.evidence("many-publication-" + name);
            var players = java.util.stream.IntStream.range(0, 4)
                    .mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Policy " + seat)).toList();
            table = new MatchTable("Constructed", players, resources, evidence, true); HeadlessPlatform.activate(table);
            source = add("Grizzly Bears"); forest = add("Forest");
            cost = new ProbeCost(payment ? "tapXType<1/Creature>" : "0");
            ability = new GuardedAbility(source, cost); ability.setActivatingPlayer(actor());
            for (int seat : List.of(2, 0, 3, 1)) choices.add(player(seat));
            input = new InputSelectEntitiesFromList<>(controller(), 0, 3, choices, ability);
            if (payment) {
                var mana = forest.getManaAbilities().get(0);
                actor().getManaPool().addManaNoEvent(new Mana(MagicColor.GREEN, forest, mana.getManaPart(), actor()));
            } else {
                controller().getInputQueue().setInput(input);
                HeadlessPlatform.query(table, () -> { session().publishInput(); table.publish(); return null; });
            }
        }
        Player actor() { return player(0); }
        Player player(int seat) { return table.game.getRegisteredPlayers().get(seat); }
        PlayerControllerHuman controller() { return (PlayerControllerHuman)actor().getController(); }
        MatchSession session() { return table.automatedSeat(actor().getId()); }
        Card add(String name) { var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor()); actor().getZone(ZoneType.Battlefield).add(card); return card; }
        JsonObject observation() { return assertCurrentSelection(table, session()); }
        JsonObject current() { return HeadlessPlatform.query(table, this::observation); }
        JsonObject answer(JsonObject current, Map<String, ?> action) {
            session().action(request(input(current), action));
            return HeadlessPlatform.query(table, this::observation);
        }
        void record(String label, JsonObject state) { trace.add(Map.of("label", label, "observation", state.deepCopy())); }
        @Override public void close() throws Exception {
            ability.forbidReads = false; ability.beforeNextAmountRead = null; ability.beforeNextFundingRead = null; cost.forbidProbes = false;
            try { Files.writeString(evidence.resolve("trace.json"), JSON.toJson(trace)); Files.writeString(evidence.resolve("private-final-views.json"), JSON.toJson(table.testViews())); }
            finally { table.close(); }
        }
    }
    private static final class ProbeCost extends Cost {
        boolean forbidProbes;
        ProbeCost(String cost) { super(cost, true); }
        @Override public boolean canPay(SpellAbility ability, Player payer, boolean effect) {
            if (forbidProbes) throw new AssertionError("Selection facts ran a discarded affordability/funding probe");
            return super.canPay(ability, payer, effect);
        }
    }
    private static final class GuardedAbility extends AbilityApiBased {
        boolean forbidReads; Runnable beforeNextAmountRead, beforeNextFundingRead;
        GuardedAbility(Card source, Cost cost) { super(ApiType.Draw, source, cost, null, Map.of("AB", "Draw", "NumCards", "1")); }
        private void read() { if (forbidReads) throw new AssertionError("A retained selection reopened its live ability or funding"); }
        @Override public String getParam(String key) {
            read(); String value = super.getParam(key);
            amountRead(key);
            return value;
        }
        @Override public String getParamOrDefault(String key, String fallback) {
            read(); String value = super.getParamOrDefault(key, fallback); amountRead(key); return value;
        }
        private void amountRead(String key) {
            if (key.equals("NumCards")) { var callback = beforeNextAmountRead; beforeNextAmountRead = null; if (callback != null) callback.run(); }
        }
        @Override public Cost getPayCosts() { read(); return super.getPayCosts(); }
        @Override public boolean allowsPayingWithShard(Card source, byte color) {
            read(); var callback = beforeNextFundingRead; beforeNextFundingRead = null; if (callback != null) callback.run();
            return super.allowsPayingWithShard(source, color);
        }
    }
}
