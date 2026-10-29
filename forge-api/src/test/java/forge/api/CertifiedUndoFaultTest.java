package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Adversarial scheduling and publication through real inputs. The fault hook
 * interrupts publication after the actual inverse; it never substitutes rules. */
public class CertifiedUndoFaultTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aPublicationFaultRetainsOneConsistentBoardAndMarkerForEveryAudience() throws Exception {
        try (var game = new RulesScenario("certified-undo-publication-fault", Map.of(0, List.of("Sol Ring")))) {
            var active = activate(game, main(game));
            var injected = new AtomicBoolean();
            game.table.game.getAction().withState(() -> game.session(0).observePlaytest(frame -> {
                if (game.table.game.getReversibleManaAction().restorationVersion() > 0
                        && injected.compareAndSet(false, true))
                    throw new IllegalStateException("Injected failure after the certified inverse and before cohort publication");
            }));
            List<JsonObject> committed;
            try (var pause = new UiPause(game)) {
                assertTrue(game.answer(active, undo(active)));
                committed = audience(game);
                assertTrue(committed.get(0).get("prompt").isJsonNull(), "The admitted Undo has consumed its action scope");
                assertEquals(marker(committed.get(0)), 0L, "Admission is not a restoration");
            }
            assertTrue(injected.get(), "The fault must run after the real inverse, rather than rejecting Undo admission");
            assertEquals(game.table.game.getReversibleManaAction().restorationVersion(), 1L,
                    "The rules owner completed the inverse before publication failed");
            assertFaultCohort(committed, audience(game));
            game.table.publish(); // Also exercise the ordinary rules-finalizer publication path.
            assertFaultCohort(committed, audience(game));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aForeignConcessionRevokesAnAdmittedUndoBeforeItsQueuedInverse() throws Exception {
        try (var game = new RulesScenario("certified-undo-concession-race", Map.of(0, List.of("Sol Ring")))) {
            var active = activate(game, main(game));
            var waiting = controller(game).getInputQueue().getInput();
            try (var pause = new UiPause(game)) {
                assertTrue(game.answer(active, undo(active)));
                assertTrue(game.state(0).get("prompt").isJsonNull());
                var concession = new JsonObject(); concession.addProperty("sessionId", text(active.state(), "id"));
                game.session(1).concede(concession);
                awaitConcession(game.table, game.session(1));
                assertNull(game.table.game.getReversibleManaAction().current(),
                        "A real accepted departure revokes the queued inverse before the GUI owner resumes");
            }
            var current = game.state(0);
            assertEquals(marker(current), 0L);
            assertEquals(mana(current), 2);
            assertTrue(source(current).get("tapped").getAsBoolean());
            assertTrue(current.getAsJsonObject("undo").get("offer").isJsonNull());
            assertSame(controller(game).getInputQueue().getInput(), waiting,
                    "Rejection retains the actual unanswered input rather than inventing a second continuation");
            assertTrue(((InputPassPriority) waiting).isPriorityActive());
            assertNotEquals(text(current.getAsJsonObject("prompt"), "id"), text(active.prompt(), "id"),
                    "The consumed action scope cannot reappear after the queued recheck");
            assertNoRestoration(current);
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(active, undo(active))));
            var fresh = new Input(0, current, current.getAsJsonObject("prompt"));
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(fresh, undo(active))));
            assertTrue(game.answer(fresh, Map.of("action", "passPriority", "expectedTask", "priority")),
                    "The surviving player's unchanged waiting input remains normally answerable");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void closingTheTableBeforeTheQueuedInverseCannotRestoreOrReopenADecision() throws Exception {
        try (var game = new RulesScenario("certified-undo-close-race", Map.of(0, List.of("Sol Ring")))) {
            var active = activate(game, main(game));
            try (var pause = new UiPause(game)) {
                assertTrue(game.answer(active, undo(active)));
                game.table.close();
            }
            assertTrue(game.table.game.isGameOver());
            assertEquals(game.table.game.getReversibleManaAction().restorationVersion(), 0L);
            for (var state : audience(game)) {
                assertEquals(marker(state), 0L);
                assertTrue(state.get("prompt").isJsonNull(), "A closed table cannot rearm its consumed Undo decision");
                assertTrue(state.getAsJsonObject("undo").get("offer").isJsonNull());
                var actor = player(state, active.state().get("viewerId").getAsInt());
                assertEquals(actor.getAsJsonObject("mana").get("C").getAsInt(), 2);
                assertTrue(source(actor, "Sol Ring").get("tapped").getAsBoolean());
                assertNoRestoration(state);
            }
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(active, undo(active))));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void actualSourceQueriesPreserveTheOfferAndRestorationReissuesLegalSourceActions() throws Exception {
        try (var game = new RulesScenario("certified-undo-source-queries", Map.of(0, List.of("Sol Ring")),
                "Iron Myr", "Rhys the Redeemed")) {
            var active = activate(game, main(game));
            var before = game.state(0);
            var offer = before.getAsJsonObject("undo").getAsJsonObject("offer").deepCopy();
            HeadlessPlatform.query(game.table, () -> {
                controller(game).setYieldPref(forge.localinstance.properties.ForgePreferences.FPref.YIELD_AUTO_PASS_NO_ACTIONS, "true");
                return null;
            });
            assertEquals(game.state(0), before, "An availability preference refresh cannot consume a manual correction window");
            var tapped = query(game, active, source(active.state()));
            assertTrue(tapped.getAsJsonArray("actions").isEmpty());
            var hand = zone(viewer(active.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var cast = query(game, active, hand);
            assertTrue(cast.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(action -> text(action, "kind").equals("cast") && action.has("costPreview")),
                    "The real read traverses legal spell and mana-funding preview paths");
            assertEquals(game.state(0), before, "Source catalogs and previews cannot silently consume or change the offer");
            assertEquals(game.state(0).getAsJsonObject("undo").getAsJsonObject("offer"), offer);
            assertTrue(game.answer(active, undo(active)));
            var restored = freshPriority(game, active);
            assertEquals(marker(restored.state()), 1L);
            assertEquals(mana(restored.state()), 0);
            assertFalse(source(restored.state()).get("tapped").getAsBoolean());
            var available = query(game, restored, source(restored.state()));
            assertFalse(available.getAsJsonArray("actions").isEmpty(),
                    "The restored occurrence supplies fresh executable source actions on the real input");
            assertNotEquals(text(available, "decisionId"), text(tapped, "decisionId"));
            assertEquals(text(available, "sourceCardId"), text(source(restored.state()), "visualId"));
            assertThrows(IllegalArgumentException.class, () -> game.session(0).sourceActions(
                    request(active, Map.of("key", text(source(active.state()), "key")))));
        }
    }

    private static void assertFaultCohort(List<JsonObject> committed, List<JsonObject> stopped) {
        assertEquals(stopped.size(), committed.size());
        for (int seat = 0; seat < stopped.size(); seat++) {
            var state = stopped.get(seat); var previous = committed.get(seat);
            assertEquals(text(state, "status"), "error");
            assertTrue(state.get("result").isJsonNull(), "An interrupted inverse is not a rules defeat");
            assertTrue(state.get("prompt").isJsonNull());
            assertEquals(state.get("boardRevision"), previous.get("boardRevision"));
            assertEquals(state.get("players"), previous.get("players"), "Every viewer retains its own immutable committed board");
            assertEquals(marker(state), marker(previous), "A retained old board cannot carry the unpublished restoration marker");
            assertTrue(state.getAsJsonObject("undo").get("offer").isJsonNull());
            assertNoRestoration(state);
        }
    }

    private static void assertNoRestoration(JsonObject state) {
        assertFalse(state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(row -> text(row, "kind").equals("restoration")));
    }
    private static List<JsonObject> audience(RulesScenario game) {
        return game.table.testViews().stream().map(CertifiedUndoFaultTest::object).toList();
    }
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static long marker(JsonObject state) { return state.getAsJsonObject("undo").get("restorationVersion").getAsLong(); }
    private static int mana(JsonObject state) { return viewer(state).getAsJsonObject("mana").get("C").getAsInt(); }
    private static JsonObject source(JsonObject state) { return source(viewer(state), "Sol Ring"); }
    private static JsonObject source(JsonObject player, String name) {
        return zone(player, "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElseThrow();
    }
    private static PlayerControllerHuman controller(RulesScenario game) {
        return (PlayerControllerHuman) game.table.game.getRegisteredPlayers().get(0).getController();
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input freshPriority(RulesScenario game, Input previous) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && !text(input.prompt(), "id").equals(text(previous.prompt(), "id")));
    }
    private static Input activate(RulesScenario game, Input input) throws Exception {
        var catalog = query(game, input, source(input.state()));
        var ability = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "expectedTask", "priority",
                "key", text(catalog, "sourceCardKey"), "actionId", text(ability, "id"))));
        var active = freshPriority(game, input);
        HeadlessPlatform.query(game.table, () -> null);
        var settled = game.state(0);
        assertEquals(text(settled.getAsJsonObject("prompt"), "id"), text(active.prompt(), "id"));
        active = new Input(0, settled, settled.getAsJsonObject("prompt"));
        assertFalse(active.state().getAsJsonObject("undo").get("offer").isJsonNull(),
                "The adversarial sequence must start with a real certified completed activation");
        return active;
    }
    private static JsonObject query(RulesScenario game, Input input, JsonObject card) {
        return object(game.session(0).sourceActions(request(input, Map.of("key", text(card, "key")))));
    }
    private static Map<String, Object> undo(Input input) {
        var offer = input.state().getAsJsonObject("undo").getAsJsonObject("offer");
        return Map.of("action", "undoCertifiedAction", "expectedTask", "priority", "offerId", text(offer, "id"),
                "actionId", text(offer, "actionId"), "undoBarrierVersion", offer.get("barrierVersion").getAsLong());
    }

    /** Occupy the actual GUI queue while relinquishing all rules-state holds. */
    private static final class UiPause implements AutoCloseable {
        private final RulesScenario game;
        private final CompletableFuture<Void> release = new CompletableFuture<>();
        UiPause(RulesScenario game) throws Exception {
            this.game = game;
            var entered = new CompletableFuture<Void>();
            game.table.game.getAction().withState(() -> HeadlessPlatform.later(() -> {
                entered.complete(null);
                try { game.table.game.getAction().awaitState(release, 10, TimeUnit.SECONDS); }
                catch (Exception failure) { throw new AssertionError("The regression GUI barrier did not release", failure); }
            }));
            entered.get(5, TimeUnit.SECONDS);
        }
        @Override public void close() {
            release.complete(null);
            HeadlessPlatform.query(game.table, () -> null);
        }
    }
}
