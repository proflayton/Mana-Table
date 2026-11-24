package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.ability.AbilityApiBased;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CounterEnumType;
import forge.game.cost.Cost;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gui.interfaces.IGuiGame;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual card-script mechanics through real owned blocking callbacks. The
 * first test additionally starts a normal game and reaches its real end step. */
public class PunisherChoicePublicationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final String PALANTIR = "Palant\u00edr of Orthanc";

    @Test(timeOut = 120000)
    public void actualEndStepTargetsTheChooserAndPublishesUnknownLossAfterScryPermissionEnds() throws Exception {
        try (var scenario = new RulesScenario("punisher-real-end-step", Map.of(0, List.of(PALANTIR)))) {
            boolean targeted = false, inspected = false;
            for (int step = 0; step < 5 && !inspected; step++) {
                var input = scenario.until(next -> text(next.prompt(), "inputType").equals("InputSelectTargets")
                        || text(next.prompt(), "kind").equals("choice"));
                if (text(input.prompt(), "inputType").equals("InputSelectTargets")) {
                    assertEquals(input.seat(), 0);
                    int chooser = scenario.table.game.getRegisteredPlayers().get(1).getId();
                    assertTrue(scenario.answer(input, Map.of("action", "player", "playerId", chooser))); targeted = true;
                } else if (input.seat() == 0) {
                    assertEquals(input.prompt().getAsJsonArray("choices").size(), 2);
                    boolean ordered = input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean();
                    assertTrue(scenario.answer(input, Map.of("choices", ordered ? List.of(0, 1) : List.of())));
                } else {
                    assertEquals(input.seat(), 1); assertTrue(targeted);
                    var observed = JSON.toJsonTree(scenario.session(1).decisionObservation()).getAsJsonObject();
                    assertCertificate(observed, 1);
                    assertTrue(scenario.answer(input, Map.of("choices", List.of(option(observed, "Draw").get("index").getAsInt()))));
                    inspected = true;
                }
            }
            assertTrue(inspected, "The actual first end step must reach the chosen opponent's effect choice");
        }
    }

    @DataProvider(name = "publicSnapshots")
    public Object[][] publicSnapshots() {
        return new Object[][] { { 40, 1, false }, { 40, 2, true }, { 40, 4, false },
                { 12, 1, true }, { 12, 2, false }, { 12, 4, true }, { 12, 0, false } };
    }

    @Test(dataProvider = "publicSnapshots", timeOut = 90000)
    public void actualScriptOwnedCallbacksKeepDynamicAmountsUnknownAcrossPublicLifeCounterAndRenaming(int life, int counters, boolean renamed) throws Exception {
        try (var fixture = new Fixture("snapshot-" + life + "-" + counters + "-" + renamed, life, counters, renamed)) {
            fixture.start(); var current = fixture.current(); assertSnapshot(current, counters);
            var before = current.deepCopy();
            HeadlessPlatform.query(fixture.table, () -> {
                var raw = fixture.session().automatedObservation();
                ChoicePublicationTest.assertImmutableOptions((List<?>)((Map<?, ?>)raw.get("semantics")).get("options"));
                fixture.source.setCounters(CounterEnumType.INFLUENCE, counters + 1);
                assertEquals(fixture.observation(), before, "Changing a live source cannot rewrite a retained immutable publication");
                fixture.table.publish(); assertCertificate(fixture.observation(), counters + 1);
                assertSnapshot(before, counters); return null;
            });
            fixture.answer(fixture.current());
            assertNull(fixture.controller().getEffectChoiceContext(), "The actual callback restores its prior owner context");
        }
    }

    @Test(timeOut = 90000)
    public void unrelatedMemoryAndUnsupportedMechanicsDeclineOnlyTheCertificateWhileRecipientsRemainExplicit() throws Exception {
        try (var fixture = new Fixture("negative-memory-and-mechanics", 12, 4, false)) {
            fixture.start(); assertCertificate(fixture.current(), 4);
            HeadlessPlatform.query(fixture.table, () -> {
                // Both private low/high objects decline. Their identity, type,
                // quantity and mana value are never used by the projection.
                for (String privateName : List.of("Plains", "Chromatic Orrery")) {
                    var hidden = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(privateName), fixture.actor());
                    fixture.actor().getZone(ZoneType.Library).add(hidden);
                    fixture.source.addRemembered(hidden); fixture.table.publish();
                    assertDeclined(fixture.observation(), true);
                    fixture.source.clearRemembered(); fixture.actor().getZone(ZoneType.Library).remove(hidden);
                }
                fixture.source.addRemembered(new Object()); fixture.table.publish(); assertDeclined(fixture.observation(), true);
                fixture.source.clearRemembered();
                var mill = fixture.mill(); var loss = mill.getSubAbility();
                for (var mutation : List.of(Map.entry("RememberMilled", "False"), Map.entry("Optional", "True"),
                        Map.entry("Destination", "Exile"), Map.entry("ConditionCheckSVar", "X"),
                        Map.entry("NumCards", "Count$CardCounters.INFLUENCE/Plus.1"), Map.entry("ForgetOtherRemembered", "True"))) {
                    var old = mill.getMapParams().put(mutation.getKey(), mutation.getValue());
                    fixture.table.publish(); assertDeclined(fixture.observation(), true);
                    if (old == null) mill.getMapParams().remove(mutation.getKey()); else mill.getMapParams().put(mutation.getKey(), old);
                }
                var oldY = loss.getSVar("Y");
                for (String unsupported : List.of("Remembered$CardManaCost/Plus.1", "RememberedLKI$CardManaCost", "SVar$Other")) {
                    loss.setSVar("Y", unsupported); fixture.table.publish(); assertDeclined(fixture.observation(), true);
                }
                loss.setSVar("Y", oldY);
                fixture.owner.getMapParams().put("TempRemember", "True"); fixture.table.publish(); assertDeclined(fixture.observation(), true);
                fixture.owner.getMapParams().remove("TempRemember");
                fixture.owner.getTargets().add(fixture.actor()); fixture.table.publish(); assertDeclined(fixture.observation(), false);
                fixture.owner.resetTargets(); fixture.owner.getTargets().add(fixture.chooser());
                fixture.table.publish(); assertCertificate(fixture.observation(), 4);
                return null;
            });
            fixture.answer(fixture.current());
        }
    }

    @Test(timeOut = 90000)
    public void matchingNamesAndSourceCannotBindAnotherOwnerOrAnUnownedProspectiveGraph() throws Exception {
        try (var fixture = new Fixture("negative-parent", 12, 4, false)) {
            fixture.start();
            HeadlessPlatform.query(fixture.table, () -> {
                var mill = fixture.mill();
                var prospective = ActionOperationProjection.describe(mill, fixture.chooser(), view -> fixture.table.identities.id(view));
                var prospectiveLoss = nodes(JSON.toJsonTree(prospective).getAsJsonObject()).stream()
                        .filter(node -> text(node, "effect").equals("LifeLoss")).findFirst().orElseThrow();
                assertEquals(text(prospectiveLoss.getAsJsonObject("recipients"), "knowledge"), "Unknown");
                var foreignOwner = AbilityFactory.getAbility(fixture.source.getSVar("DBChoice"), fixture.source);
                foreignOwner.setActivatingPlayer(fixture.actor()); foreignOwner.getTargets().add(fixture.chooser());
                ((forge.game.spellability.AbilitySub)mill).setParent(foreignOwner);
                fixture.table.publish(); assertDeclined(fixture.observation(), false);
                ((forge.game.spellability.AbilitySub)mill).setParent(fixture.owner);
                fixture.owner.resetTargets(); fixture.table.publish(); assertDeclined(fixture.observation(), false);
                fixture.owner.getTargets().add(fixture.chooser()); fixture.table.publish(); assertCertificate(fixture.observation(), 4);
                return null;
            });
            fixture.answer(fixture.current());
        }
    }

    @Test(timeOut = 90000)
    public void reentrantPublicationAfterNestedContextRestorationKeepsTheNewerCohortAndControllingProxy() throws Exception {
        try (var fixture = new Fixture("reentrant-context-proxy", 12, 4, false)) {
            var guarded = new GuardedOwner(fixture.source, fixture.owner.getMapParams());
            guarded.setAdditionalAbilityList("Choices", fixture.owner.getAdditionalAbilityList("Choices"));
            guarded.setActivatingPlayer(fixture.actor()); guarded.getTargets().add(fixture.chooser()); fixture.owner = guarded;
            var controller = fixture.controller(); var proxyController = (PlayerControllerHuman)fixture.actor().getController();
            fixture.chooser().addController(123, fixture.actor(), controller, false);
            fixture.start(); assertCertificate(fixture.current(), 4);
            HeadlessPlatform.query(fixture.table, () -> {
                var before = fixture.observation(); var inner = new ArrayList<JsonObject>();
                var outerContext = controller.getEffectChoiceContext(); assertNotNull(outerContext);
                assertSame(proxyController.getEffectChoiceContext(), outerContext);
                guarded.beforeNextChoiceAmount = () -> {
                    var gui = controller.getGui();
                    var nestedOwner = AbilityFactory.getAbility(fixture.source.getSVar("DBChoice"), fixture.source);
                    nestedOwner.setActivatingPlayer(fixture.actor()); nestedOwner.getTargets().add(fixture.chooser());
                    controller.setGui((IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
                        if (method.getName().equals("getChoices")) {
                            var nested = controller.getEffectChoiceContext(); assertNotSame(nested, outerContext);
                            assertSame(nested.owner(), nestedOwner); assertSame(proxyController.getEffectChoiceContext(), nested);
                            throw new IllegalStateException("controlled nested callback failure");
                        }
                        try { return method.invoke(gui, args); }
                        catch (InvocationTargetException failure) { throw failure.getCause(); }
                    }));
                    try {
                        assertThrows(IllegalStateException.class, () -> controller.chooseSpellAbilitiesForEffect(
                                new ArrayList<>(nestedOwner.getAdditionalAbilityList("Choices")), nestedOwner, "Nested", 1, Map.of()));
                    } finally { controller.setGui(gui); }
                    assertSame(controller.getEffectChoiceContext(), outerContext, "Finally restores the exact outer owner after a nested failure");
                    assertSame(proxyController.getEffectChoiceContext(), outerContext, "The controlling proxy restores the same owner too");
                    // Same rules board, newer publication generation. Restoring
                    // the outer context must not revive an obsolete builder.
                    fixture.table.publish(); inner.add(fixture.observation());
                };
                fixture.table.publish(); assertNull(guarded.beforeNextChoiceAmount); assertEquals(inner.size(), 1);
                var retained = inner.get(0); assertCertificate(retained, 4);
                assertTrue(retained.getAsJsonObject("state").get("boardRevision").getAsLong()
                        > before.getAsJsonObject("state").get("boardRevision").getAsLong());
                assertEquals(fixture.observation(), retained, "An obsolete outer capture cannot overwrite the newer same-board cohort after context ABA");
                long board = retained.getAsJsonObject("state").get("boardRevision").getAsLong();
                for (var seat : fixture.table.automatedSeats()) assertEquals(((Number)seat.state().get("boardRevision")).longValue(), board);
                assertEquals(((Number)fixture.table.spectatorState().get("boardRevision")).longValue(), board);
                return null;
            });
            fixture.answer(fixture.current());
            assertNull(controller.getEffectChoiceContext()); assertNull(proxyController.getEffectChoiceContext());
            fixture.chooser().removeController(123, false);
        }
    }

    private static JsonObject option(JsonObject observation, String effect) {
        return observation.getAsJsonObject("semantics").getAsJsonArray("options").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "effect").equals(effect)).findFirst().orElseThrow();
    }
    private static List<JsonObject> nodes(JsonObject graph) {
        return graph.getAsJsonArray("operations").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static void privateLibraries(JsonObject observation) {
        for (var player : observation.getAsJsonObject("state").getAsJsonArray("players"))
            assertTrue(zone(player.getAsJsonObject(), "Library").getAsJsonArray("cards").isEmpty(), "No future library face may enter the owned policy frame");
    }
    private static void assertCertificate(JsonObject observation, int counters) {
        privateLibraries(observation);
        var mill = option(observation, "Mill"); var certificate = mill.getAsJsonObject("rememberedMillLifeLoss");
        assertNotNull(certificate); assertEquals(certificate.get("version").getAsInt(), 1);
        assertEquals(certificate.get("observedCount").getAsInt(), counters); assertEquals(text(certificate, "counterType"), CounterEnumType.INFLUENCE.getName());
        var graph = mill.getAsJsonObject("operationGraph"); assertEquals(text(graph, "knowledge"), "Unknown");
        assertEquals(nodes(graph).size(), 3, "The already chosen target ancestor must not be imported into this branch graph");
        var loss = nodes(graph).stream().filter(node -> text(node, "effect").equals("LifeLoss")).findFirst().orElseThrow();
        assertEquals(certificate.get("lifeLossOperationId"), loss.get("id"));
        for (var node : nodes(graph)) if (!text(node, "effect").equals("Cleanup")) {
            assertTrue(node.get("amount").isJsonNull()); assertEquals(text(node, "amountKnowledge"), "Unknown"); assertEquals(text(node, "knowledge"), "Unknown");
        }
        var recipients = loss.getAsJsonObject("recipients"); assertEquals(text(recipients, "binding"), "Explicit");
        assertEquals(text(recipients, "knowledge"), "Known"); assertTrue(recipients.get("targetOperationId").isJsonNull());
        assertEquals(recipients.getAsJsonArray("entities").size(), 1);
        assertEquals(recipients.getAsJsonArray("entities").get(0).getAsJsonObject().get("playerId"), observation.getAsJsonObject("state").get("viewerId"));
        String sourceId = text(certificate, "sourceCardId");
        var source = observation.getAsJsonObject("state").getAsJsonArray("players").asList().stream()
                .flatMap(value -> zone(value.getAsJsonObject(), "Battlefield").getAsJsonArray("cards").asList().stream())
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "visualId").equals(sourceId)).findFirst().orElseThrow();
        var publicCount = source.getAsJsonObject("counters").get(text(certificate, "counterType"));
        assertEquals(publicCount == null ? 0 : publicCount.getAsInt(), counters);
    }
    private static void assertSnapshot(JsonObject observation, int counters) {
        if (counters == 0) assertDeclined(observation, true); else assertCertificate(observation, counters);
    }
    private static void assertDeclined(JsonObject observation, boolean explicitRecipient) {
        privateLibraries(observation); var option = option(observation, "Mill"); assertFalse(option.has("rememberedMillLifeLoss"));
        var loss = nodes(option.getAsJsonObject("operationGraph")).stream().filter(node -> text(node, "effect").equals("LifeLoss")).findFirst().orElseThrow();
        assertEquals(text(loss.getAsJsonObject("recipients"), "knowledge"), explicitRecipient ? "Known" : "Unknown");
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final Card source; SpellAbility owner; final IGuiGame originalGui; final Path evidence;
        final CountDownLatch opened = new CountDownLatch(1); final CompletableFuture<List<SpellAbility>> result = new CompletableFuture<>();
        Fixture(String name, int life, int counters, boolean renamed) throws Exception {
            var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
            evidence = ApiTestWorkspace.evidence("punisher-" + name);
            var seats = java.util.stream.IntStream.range(0, 4).mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Seat " + seat)).toList();
            table = new MatchTable("Constructed", seats, resources, evidence, true); HeadlessPlatform.activate(table);
            source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(PALANTIR), actor()); actor().getZone(ZoneType.Battlefield).add(source);
            source.setCounters(CounterEnumType.INFLUENCE, counters); chooser().setLife(life, null);
            owner = AbilityFactory.getAbility(source.getSVar("DBChoice"), source); owner.setActivatingPlayer(actor()); owner.getTargets().add(chooser());
            if (renamed) {
                source.setName("Unrelated test artifact");
                mill().getMapParams().put("SpellDescription", "Untrusted text says draw safely");
                mill().getMapParams().put("NumCards", "RenamedCount"); mill().setSVar("RenamedCount", "Count$CardCounters.INFLUENCE");
                mill().getSubAbility().getMapParams().put("LifeAmount", "RenamedSum"); mill().getSubAbility().setSVar("RenamedSum", "Remembered$CardManaCost");
            }
            originalGui = controller().getGui();
            controller().setGui((IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
                if (method.getName().equals("getChoices")) opened.countDown();
                try { return method.invoke(originalGui, args); }
                catch (InvocationTargetException failure) { throw failure.getCause(); }
            }));
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Player chooser() { return table.game.getRegisteredPlayers().get(1); }
        PlayerControllerHuman controller() { return (PlayerControllerHuman)chooser().getController(); }
        MatchSession session() { return table.automatedSeat(chooser().getId()); }
        SpellAbility mill() { return owner.getAdditionalAbilityList("Choices").stream().filter(ability -> ability.getApi() == forge.game.ability.ApiType.Mill).findFirst().orElseThrow(); }
        void start() throws Exception {
            table.game.getAction().invoke(() -> {
                try { result.complete(controller().chooseSpellAbilitiesForEffect(new ArrayList<>(owner.getAdditionalAbilityList("Choices")), owner, "Untrusted label", 1, Map.of())); }
                catch (Throwable failure) { result.completeExceptionally(failure); }
            });
            assertTrue(opened.await(5, TimeUnit.SECONDS));
        }
        JsonObject observation() { return JSON.toJsonTree(session().automatedObservation()).getAsJsonObject(); }
        JsonObject current() { return HeadlessPlatform.query(table, this::observation); }
        void answer(JsonObject observation) throws Exception {
            var state = observation.getAsJsonObject("state"); var prompt = state.getAsJsonObject("prompt");
            session().action(request(new Input(1, state, prompt), Map.of("choices", List.of(option(observation, "Draw").get("index").getAsInt()))));
            assertEquals(result.get(5, TimeUnit.SECONDS).size(), 1);
        }
        @Override public void close() throws Exception {
            try { Files.writeString(evidence.resolve("last-observation.json"), JSON.toJson(session().automatedObservation())); }
            finally { controller().setGui(originalGui); table.close(); result.handle((value, failure) -> null).get(5, TimeUnit.SECONDS); }
        }
    }
    private static final class GuardedOwner extends AbilityApiBased {
        Runnable beforeNextChoiceAmount;
        GuardedOwner(Card source, Map<String, String> params) { super(ApiType.GenericChoice, source, Cost.Zero, null, params); }
        @Override public String getParamOrDefault(String key, String fallback) {
            String result = super.getParamOrDefault(key, fallback);
            if (key.equals("ChoiceAmount")) { var callback = beforeNextChoiceAmount; beforeNextChoiceAmount = null; if (callback != null) callback.run(); }
            return result;
        }
    }
}
