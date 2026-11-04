package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.api.sync.SyncTransport;
import forge.card.MagicColor;
import forge.game.mana.Mana;
import forge.game.player.Player;
import forge.game.ability.ApiType;
import forge.game.ability.AbilityApiBased;
import forge.game.cost.Cost;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.io.ByteArrayOutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Stage controls for actual current-input reads. Reentrant getter/event hooks
 * deliberately inject publication or resource changes; they do not identify
 * the cause of the separate intermittent native source-read failure. */
public class SourceReadBoundaryTest {
    private static final Gson JSON = new Gson();
    private static final String SUPERSEDED = "source-read-superseded", CHANGED = "source-read-changed", FAILED = "source-read-failed";

    @Test(timeOut = 90000)
    public void anIndependentQueuedPublicationSupersedesAnOtherwiseCurrentSourceBeforeItsReadStarts() throws Exception {
        try (var game = game("source-read-pre")) {
            main(game); HeadlessPlatform.query(game.table, () -> null);
            var before = game.state(0); var source = card(before, "Hand", "Manalith");
            var original = query(game, before, source);
            var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
            var worker = Executors.newSingleThreadExecutor();
            try {
                HeadlessPlatform.later(() -> {
                    entered.countDown();
                    try { if (!release.await(3, TimeUnit.SECONDS)) throw new IllegalStateException("Display gate timed out"); }
                    catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new IllegalStateException(failure); }
                });
                assertTrue(entered.await(3, TimeUnit.SECONDS));
                var pending = worker.submit(() -> rejected(() -> query(game, before, source)));
                release.countDown();
                var failure = pending.get(3, TimeUnit.SECONDS);
                assertCode(failure, SUPERSEDED);
                var current = game.state(0);
                assertTrue(current.get("revision").getAsLong() > before.get("revision").getAsLong());
                assertEquals(current.get("prompt"), before.get("prompt"));
                assertBoardExceptRevision(current, before);
                var fresh = query(game, current, card(current, "Hand", "Manalith"));
                var oldFacts = original.deepCopy(); oldFacts.remove("revision");
                var newFacts = fresh.deepCopy(); newFacts.remove("revision"); assertEquals(newFacts, oldFacts);
                assertEquals(query(game, current, card(current, "Hand", "Manalith")), fresh);
                assertEquals(game.state(0), current, "Neither rejected nor successful reads publish or execute anything");
                Files.writeString(game.evidence.resolve("pre-read-publication.json"), JSON.toJson(Map.of("before", before, "current", current, "fresh", fresh)));
            } finally { release.countDown(); worker.shutdownNow(); }
        }
    }

    @DataProvider public Object[][] previewChanges() { return new Object[][] {{false}, {true}}; }
    @Test(dataProvider = "previewChanges", timeOut = 90000)
    public void realEligibilityOrPublicationChangesDuringPreviewAreChangedNotSafePreReadSupersession(boolean publish) throws Exception {
        try (var game = game("source-read-preview-" + publish)) {
            main(game); HeadlessPlatform.query(game.table, () -> null);
            HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0); var actor = game.table.game.getRegisteredPlayers().get(0);
                var source = card(before, "Hand", "Manalith");
                query(game, before, source); // Force the actual cached-catalog path into its lazy preview too.
                var calls = new AtomicInteger();
                try (var hook = new AutomatedObservationTest.DuringDiscovery(actor, ZoneType.Battlefield,
                        NumericDecisionProjection.class, "funding", () -> {
                            calls.incrementAndGet(); addMana(actor);
                            if (publish) game.table.publish();
                        })) {
                    assertCode(rejected(() -> query(game, before, source)), CHANGED);
                }
                assertEquals(calls.get(), 1, "The real lazy funding preview executes the controlled zone-read event once");
                assertEquals(actor.getManaPool().totalMana(), 1); assertFalse(actor.getCardsIn(ZoneType.Battlefield).get(0).isTapped());
                if (!publish) assertEquals(game.state(0), before, "Unpublished live eligibility cannot mint offers against the retained frame");
                else assertTrue(game.state(0).get("revision").getAsLong() > before.get("revision").getAsLong());
                game.table.publish(); var fresh = game.state(0);
                var result = query(game, fresh, card(fresh, "Hand", "Manalith"));
                assertEquals(result.getAsJsonArray("actions").get(0).getAsJsonObject().getAsJsonObject("costPreview")
                        .getAsJsonObject("funding").getAsJsonObject("pool").get("G").getAsInt(), 1);
                assertEquals(query(game, fresh, card(fresh, "Hand", "Manalith")), result);
                Files.writeString(game.evidence.resolve("during-preview.json"), JSON.toJson(Map.of("publish", publish, "before", before, "fresh", fresh, "catalog", result)));
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void aControlledDiscoveryGetterCannotReturnOffersAfterItChangesRealSourceEligibility() throws Exception {
        try (var game = game("source-read-discovery")) {
            main(game); HeadlessPlatform.query(game.table, () -> null);
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0); var forest = actor.getCardsIn(ZoneType.Battlefield).get(0);
                var armed = new AtomicBoolean(); var calls = new AtomicInteger();
                // Controlled test getter on an actual current source/input,
                // never executed as a card's rules ability.
                var probe = new AbilityApiBased(ApiType.Draw, forest, Cost.Zero, null, Map.of("AB", "Draw", "NumCards", "1")) {
                    @Override public boolean canPlay() { return true; }
                    @Override public String getDescription() {
                        if (armed.get() && inSourceDiscovery() && armed.compareAndSet(true, false)) { calls.incrementAndGet(); addMana(actor); }
                        return "Controlled source-read getter";
                    }
                };
                forest.addSpellAbility(probe); game.table.publish();
                var before = game.state(0); armed.set(true);
                assertCode(rejected(() -> query(game, before, card(before, "Battlefield", "Forest"))), CHANGED);
                assertEquals(calls.get(), 1); assertEquals(actor.getManaPool().totalMana(), 1); assertFalse(forest.isTapped());
                assertEquals(game.state(0), before);
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void malformedFutureOrForeignRequestsNeverAcquireASupersededReadSignal() throws Exception {
        try (var game = game("source-read-rejected")) {
            main(game); HeadlessPlatform.query(game.table, () -> null);
            HeadlessPlatform.query(game.table, () -> {
                var state = game.state(0); var source = card(state, "Hand", "Manalith"); long revision = state.get("revision").getAsLong();
                for (var expected : List.of(-1L, revision + 1, true, "old", List.of(1), 1.25)) {
                    var request = request(state, Map.of("key", text(source, "key"), "expectedRevision", expected));
                    assertFalse(rejected(() -> game.session(0).sourceActions(request)) instanceof SyncTransport.CodedFailure);
                }
                for (String field : List.of("sessionId", "promptId", "key")) {
                    var request = request(state, Map.of("key", text(source, "key"), "expectedRevision", revision - 1));
                    request.addProperty(field, "foreign");
                    assertFalse(rejected(() -> game.session(0).sourceActions(request)) instanceof SyncTransport.CodedFailure,
                            "A stale number cannot turn invalid owner/access into safe supersession");
                }
                assertEquals(game.state(0), state);
                var legacy = game.session(0).sourceActions(request(state, Map.of("key", text(source, "key"))));
                assertEquals(JSON.toJsonTree(legacy), query(game, state, source), "Legacy current reads retain their existing behavior");
                return null;
            });
        }
    }

    @DataProvider public Object[][] unexpectedFailures() { return new Object[][] {{false}, {true}}; }
    @Test(dataProvider = "unexpectedFailures", timeOut = 90000)
    public void anUnexpectedPreviewFailureRetainsItsRawCauseAndIsNeverAStageSupersession(boolean argumentFailure) throws Exception {
        try (var game = game("source-read-failure")) {
            main(game); HeadlessPlatform.query(game.table, () -> null);
            HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0); var actor = game.table.game.getRegisteredPlayers().get(0);
                RuntimeException sentinel = argumentFailure ? new IllegalArgumentException("SOURCE-PRIVATE-CANARY query argument failure")
                        : new IllegalStateException("SOURCE-PRIVATE-CANARY source preview failure");
                try (var hook = new AutomatedObservationTest.DuringDiscovery(actor, ZoneType.Battlefield,
                        NumericDecisionProjection.class, "funding", () -> { throw sentinel; })) {
                    var failure = rejected(() -> query(game, before, card(before, "Hand", "Manalith")));
                    var reply = DesktopEngine.errorReply(JSON.toJsonTree(Map.of("id", 1, "method", "matchSourceActions")).getAsJsonObject(), (Exception)failure);
                    assertFalse(reply.toString().contains("SOURCE-PRIVATE-CANARY"), "Unexpected private query details cannot enter the protocol");
                    assertCode(failure, FAILED); assertSame(failure.getCause(), sentinel, "The intentional boundary wrapper preserves the exact private cause");
                    assertEquals(text(reply, "errorCode"), FAILED);
                }
                assertEquals(game.state(0), before); assertEquals(actor.getManaPool().totalMana(), 0);
                assertFalse(query(game, before, card(before, "Hand", "Manalith")).getAsJsonArray("actions").isEmpty());
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void aFatalQueryErrorKeepsItsExactIdentityAndBrokenDiagnosticsCannotMakeItRecoverable() throws Exception {
        try (var game = game("source-read-fatal-error")) {
            main(game); HeadlessPlatform.query(game.table, () -> null);
            var original = System.err;
            var broken = new PrintStream(java.io.OutputStream.nullOutputStream()) {
                @Override public void write(byte[] value, int offset, int length) { throw new IllegalStateException("Broken private source-error sink"); }
            };
            try {
                System.setErr(broken);
                var before = HeadlessPlatform.query(game.table, () -> {
                    var state = game.state(0); var actor = game.table.game.getRegisteredPlayers().get(0);
                    var fatal = new AssertionError("FATAL-SOURCE-QUERY-CANARY");
                    try (var hook = new AutomatedObservationTest.DuringDiscovery(actor, ZoneType.Battlefield,
                            NumericDecisionProjection.class, "funding", () -> { throw fatal; })) {
                        assertSame(rejected(() -> query(game, state, card(state, "Hand", "Manalith"))), fatal,
                                "This source boundary must not convert an Error into a recoverable coded protocol exception");
                    }
                    assertEquals(game.state(0), state); assertEquals(actor.getManaPool().totalMana(), 0);
                    return state;
                });
                // The fixture deliberately caught its injected Error; this is
                // a logging/identity control, not arbitrary game-error recovery.
                assertFalse(query(game, before, card(before, "Hand", "Manalith")).getAsJsonArray("actions").isEmpty());
                assertEquals(game.state(0), before);
            } finally { System.setErr(original); broken.close(); }
        }
    }

    @Test(timeOut = 90000)
    public void aNestedUnexpectedFailureEmitsOnceOnlyAfterTheActualOwnerScopesHaveReturned() throws Exception {
        try (var game = game("source-read-safe-diagnostic")) {
            main(game); HeadlessPlatform.query(game.table, () -> null);
            var bytes = new ByteArrayOutputStream(); var wrongScope = new AtomicBoolean(); var original = System.err;
            var sink = new PrintStream(bytes, true, StandardCharsets.UTF_8) {
                @Override public void write(byte[] value, int offset, int length) {
                    if (forge.game.GameAction.currentGame() != null || Thread.holdsLock(game.table.gate)) wrongScope.set(true);
                    super.write(value, offset, length);
                }
            };
            try {
                System.setErr(sink);
                var before = HeadlessPlatform.query(game.table, () -> {
                    var state = game.state(0); var actor = game.table.game.getRegisteredPlayers().get(0);
                    for (int count = 0; count < 2; count++) {
                        var failure = new IllegalStateException("FIRST-UNEXPECTED-" + count);
                        try (var hook = new AutomatedObservationTest.DuringDiscovery(actor, ZoneType.Battlefield,
                                NumericDecisionProjection.class, "funding", () -> { throw failure; })) {
                            var wrapped = rejected(() -> query(game, state, card(state, "Hand", "Manalith")));
                            assertCode(wrapped, FAILED); assertSame(wrapped.getCause(), failure);
                        }
                    }
                    assertEquals(bytes.size(), 0, "A still-enclosing rules owner keeps the bounded receipt pending");
                    return state;
                });
                assertFalse(query(game, before, card(before, "Hand", "Manalith")).getAsJsonArray("actions").isEmpty());
                int size = bytes.size(); assertTrue(size > 0); assertTrue(size <= 8192); assertFalse(wrongScope.get());
                String text = bytes.toString(StandardCharsets.UTF_8); assertTrue(text.contains("FIRST-UNEXPECTED-0")); assertFalse(text.contains("FIRST-UNEXPECTED-1"));
                query(game, before, card(before, "Hand", "Manalith")); assertEquals(bytes.size(), size);
                assertEquals(game.state(0), before);
            } finally { System.setErr(original); sink.close(); }
        }
    }

    @Test public void stageCodesSurviveActualTcpButDesktopForwardsThemOnlyForSourceReads() throws Exception {
        for (String code : List.of(SUPERSEDED, CHANGED, FAILED)) {
            try (var server = new SyncTransport.Server((seat, method, arguments) -> { throw new SyncTransport.CodedFailure(code, "SOURCE-WIRE-CANARY"); }, () -> 0)) {
                try (var remote = new SyncTransport.Remote("127.0.0.1:" + server.listen())) {
                    var failure = rejected(() -> remote.request("sourceActions", new JsonObject())); assertCode(failure, code);
                    for (String method : List.of("matchSourceActions", "matchAction", "matchDecisionObservation", "automatedState", "matchState")) {
                        var reply = DesktopEngine.errorReply(JSON.toJsonTree(Map.of("id", 1, "method", method)).getAsJsonObject(), (Exception)failure);
                        assertEquals(reply.has("errorCode"), method.equals("matchSourceActions"));
                        if (method.equals("matchSourceActions")) assertEquals(text(reply, "errorCode"), code);
                        if (method.equals("matchSourceActions") && code.equals(FAILED)) assertFalse(reply.toString().contains("SOURCE-WIRE-CANARY"));
                    }
                }
            }
        }
    }

    @Test public void unexpectedPreDispatchSourceFailuresAreFixedTextWhileLegacyUnknownAndValidationRepliesStayCompatible() {
        var source = JSON.toJsonTree(Map.of("id", 1, "method", "matchSourceActions")).getAsJsonObject();
        var failure = DesktopEngine.errorReply(source, new IllegalStateException("SOURCE-DISPATCH-CANARY"));
        assertFalse(failure.toString().contains("SOURCE-DISPATCH-CANARY"), "The source dispatch fallback cannot publish a raw failure message");
        assertEquals(text(failure, "errorCode"), FAILED);
        var legacy = DesktopEngine.errorReply(source, new IllegalArgumentException("Unknown synchronization command"));
        assertFalse(legacy.has("errorCode")); assertEquals(text(legacy, "error"), "Unknown synchronization command");
        var validation = DesktopEngine.errorReply(source, new IllegalArgumentException("Card is not visible in this prompt"));
        assertFalse(validation.has("errorCode")); assertEquals(text(validation, "error"), "Card is not visible in this prompt");
        source.addProperty("method", "matchAction");
        var command = DesktopEngine.errorReply(source, new IllegalStateException("Action failure"));
        assertFalse(command.has("errorCode")); assertEquals(text(command, "error"), "Action failure");
    }

    private static boolean inSourceDiscovery() {
        return StackWalker.getInstance().walk(frames -> frames.anyMatch(frame -> frame.getClassName().equals(SourceActions.class.getName())
                && frame.getMethodName().equals("describe")));
    }
    private static void addMana(Player actor) {
        var forest = actor.getCardsIn(ZoneType.Battlefield).get(0);
        actor.getManaPool().addMana(new Mana(MagicColor.GREEN, forest, forest.getManaAbilities().get(0).getManaPart(), actor));
    }
    private static void assertCode(Throwable failure, String code) {
        assertTrue(failure instanceof SyncTransport.CodedFailure, "The source read must retain its typed stage, not only an error sentence: " + failure);
        assertEquals(((SyncTransport.CodedFailure)failure).code(), code);
    }
    private static Throwable rejected(org.testng.Assert.ThrowingRunnable work) {
        try { work.run(); } catch (Throwable failure) { return failure; }
        throw new AssertionError("The retired/invalid/faulting read must not return a catalog");
    }
    private static void assertBoardExceptRevision(JsonObject current, JsonObject prior) {
        var now = current.deepCopy(); var before = prior.deepCopy();
        for (var value : List.of(now, before)) { value.remove("revision"); value.remove("boardRevision"); }
        assertEquals(now, before);
    }
    private static RulesScenario game(String name) throws Exception {
        return new RulesScenario(name, Map.of(0, List.of("Forest", "Forest", "Forest")), "Manalith", "Rhys the Redeemed");
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static JsonObject query(RulesScenario game, JsonObject state, JsonObject source) {
        return JSON.toJsonTree(game.session(0).sourceActions(request(state,
                Map.of("key", text(source, "key"), "expectedRevision", state.get("revision").getAsLong())))).getAsJsonObject();
    }
    private static JsonObject request(JsonObject state, Map<String, ?> body) {
        return RulesScenario.request(new Input(0, state, state.getAsJsonObject("prompt")), body);
    }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
}
