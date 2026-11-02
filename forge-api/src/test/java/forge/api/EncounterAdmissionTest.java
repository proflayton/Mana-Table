package forge.api;

import com.google.gson.*;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;
import java.io.IOException;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.Predicate;
import static org.testng.Assert.*;

/** Admission through actual local/TCP seat channels, with a blocked durable writer. */
public class EncounterAdmissionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static String text(JsonObject value, String key) { var field = value.get(key); return field == null || field.isJsonNull() ? "" : field.getAsString(); }
    private static JsonObject begin(JsonObject lobby) { return object(Map.of("tableId", text(lobby, "tableId"),
            "matchSetupConsent", text(lobby, "matchSetupConsent"), "tempoRevision", text(lobby.getAsJsonObject("tempo"), "revision"))); }
    private static JsonObject attemptScope(JsonObject lobby) { return object(Map.of("tableId", text(lobby, "tableId"),
            "attemptId", text(lobby.getAsJsonObject("startState"), "attemptId"))); }

    @DataProvider public Object[][] transports() { return new Object[][] {{false}, {true}}; }
    @Test(dataProvider = "transports", timeOut = 45000)
    public void blockedAdmissionReturnsPendingAndBothSeatChannelsStayResponsive(boolean network) throws Exception {
        var profile = ApiTestWorkspace.profile("admission-responsive-" + network); var gate = new Gate(true, false, false);
        try (var store = new EncounterProgressStore(profile, gate);
             var game = new CooperativeEncounterTest.Fixture("admission-responsive-" + network, 2, 2, 1, store, network)) {
            var workers = Executors.newCachedThreadPool();
            try {
                var reviewed = object(game.host.state()); var request = begin(reviewed);
                assertTrue(reviewed.get("encounterStartSupported").getAsBoolean());
                assertThrows(IllegalArgumentException.class, () -> game.host.start());
                var pending = object(workers.submit(() -> game.host.startEncounter(request)).get(2, TimeUnit.SECONDS));
                assertEquals(text(pending.getAsJsonObject("startState"), "phase"), "preparing");
                String attempt = text(pending.getAsJsonObject("startState"), "attemptId");
                assertFalse(pending.get("matchActive").getAsBoolean()); assertTrue(gate.entered.await(5, TimeUnit.SECONDS));
                var hostView = object(workers.submit(game.host::state).get(2, TimeUnit.SECONDS));
                var guestView = object(workers.submit(game.guests.get(0)::state).get(2, TimeUnit.SECONDS));
                assertEquals(text(hostView.getAsJsonObject("startState"), "phase"), "recording");
                assertEquals(text(guestView.getAsJsonObject("startState"), "attemptId"), attempt);
                assertNull(workers.submit(() -> game.host.match().state()).get(2, TimeUnit.SECONDS), "No provisional or previous game's snapshot");
                assertTrue(game.host.automatedSeats().isEmpty()); assertFalse(hostView.get("canStart").getAsBoolean());
                assertEquals(text(object(game.host.startEncounter(request)).getAsJsonObject("startState"), "attemptId"), attempt);
                assertThrows(IllegalArgumentException.class, () -> game.guests.get(0).startEncounter(request));
                assertThrows(IllegalArgumentException.class, () -> game.guests.get(0).cancelEncounterStart(attemptScope(hostView)));
                game.host.chat(object(Map.of("tableId", text(hostView, "tableId"), "kind", "chat", "text", "Ready for the encounter")));
                game.host.rename(object(Map.of("tableId", text(hostView, "tableId"), "name", "Reviewed host")));
                assertEquals(text(object(game.host.state()).getAsJsonObject("startState"), "phase"), "recording", "Chat/name changes are not new rules consent");
                gate.release.countDown();
                var active = await(game.host, state -> state.get("matchActive").getAsBoolean());
                assertEquals(text(active.getAsJsonObject("startState"), "phase"), "starting");
                assertEquals(text(active.getAsJsonObject("partyProgress"), "encounterId"), attempt);
                assertEquals(gate.writes.get(), 1); game.main();
                assertEquals(text(object(game.host.match().state()), "encounterId"), attempt);
                assertEquals(store.unresolved(gate.attempt.partyId()).get(3, TimeUnit.SECONDS), List.of(gate.attempt));
                game.concedeOpposition(); game.saved(1); game.host.returnToLobby();
            } finally { gate.release.countDown(); workers.shutdownNow(); }
        }
    }

    @Test(timeOut = 90000) public void inputRevocationAndOldTransportCallbacksCannotLaunchARecordedAttempt() throws Exception {
        for (String change : List.of("unready", "deck", "enrollment", "reconnect", "rules", "tempo", "cancel", "epoch")) {
            var profile = ApiTestWorkspace.profile("admission-revoke-" + change); var gate = new Gate(true, false, false);
            try (var store = new EncounterProgressStore(profile, gate);
                 var game = new CooperativeEncounterTest.Fixture("admission-revoke-" + change, 2, 2, 1, store, change.equals("reconnect"))) {
                var pending = object(game.host.startEncounter(begin(object(game.host.state()))));
                assertTrue(gate.entered.await(5, TimeUnit.SECONDS)); var current = object(game.host.state());
                switch (change) {
                    case "unready" -> game.guests.get(0).ready(false);
                    case "deck" -> game.host.clearDeck(text(current, "tableId"));
                    case "enrollment" -> game.guests.get(0).close();
                    case "reconnect" -> {
                        game.guests.get(0).reconnect();
                        var retained = object(game.host.state());
                        assertEquals(retained.get("slots"), current.get("slots"), "A transport-only replacement preserves the authenticated enrollment and decks");
                        assertEquals(retained.get("matchSetupConsent"), current.get("matchSetupConsent"));
                    }
                    case "rules" -> game.host.configureMatch(game.configure(current, new MatchSetupSpecification(MatchSetupSpecification.COOPERATIVE,
                            List.of(new MatchSetupSpecification.Seat(0, 17, "party"), new MatchSetupSpecification.Seat(1, 17, "party"), new MatchSetupSpecification.Seat(2, 29, "opposition")))));
                    case "tempo" -> game.host.configureTempo(object(Map.of("tableId", text(current, "tableId"), "preset", "fast", "expiry", "pass")));
                    case "cancel" -> game.host.cancelEncounterStart(attemptScope(current));
                    case "epoch" -> {
                        game.guests.get(0).close(); game.host.close();
                        game.host.host("Commander", 2, false, "local");
                    }
                    default -> throw new AssertionError();
                }
                if (!change.equals("epoch")) {
                    var cancelled = object(game.host.state());
                    assertEquals(text(cancelled.getAsJsonObject("startState"), "phase"), "cancelled");
                    assertFalse(cancelled.get("matchActive").getAsBoolean());
                    assertFalse(cancelled.getAsJsonObject("startState").get("retryAllowed").getAsBoolean());
                    assertThrows(IllegalArgumentException.class, () -> game.host.retryEncounterStart(attemptScope(pending)));
                }
                gate.release.countDown();
                // The actual serial writer has completed before the private
                // coordinator barrier, so this observes the late callback too.
                if (change.equals("epoch")) assertTrue(gate.finished.await(5, TimeUnit.SECONDS));
                else store.openAttempt(gate.attempt).get(5, TimeUnit.SECONDS);
                drainAdmission(game.host);
                var after = object(game.host.state()); assertFalse(after.get("matchActive").getAsBoolean(), change);
                assertTrue(game.host.automatedSeats().isEmpty(), change);
                if (change.equals("epoch")) assertFalse(after.has("startState"), "Old callback cannot publish into a new table");
                try (var inspected = new EncounterProgressStore(profile)) {
                    inspected.startup().get(3, TimeUnit.SECONDS);
                    assertEquals(inspected.unresolved(gate.attempt.partyId()).get(3, TimeUnit.SECONDS), List.of(gate.attempt));
                    assertEquals(inspected.current(gate.attempt.partyId()).completedEncounters(), 0);
                }
            } finally { gate.release.countDown(); }
        }
    }

    @Test(timeOut = 45000) public void aCancelledCooperativeAttemptCannotReplaceOrLabelALaterFreeForAllMatch() throws Exception {
        var profile = ApiTestWorkspace.profile("admission-later-ffa"); var gate = new Gate(true, false, false);
        try (var store = new EncounterProgressStore(profile, gate);
             var game = new CooperativeEncounterTest.Fixture("admission-later-ffa", 2, 2, 1, store, false)) {
            game.host.startEncounter(begin(object(game.host.state()))); assertTrue(gate.entered.await(5, TimeUnit.SECONDS));
            var current = object(game.host.state());
            game.host.configureMatch(game.configure(current, MatchSetupSpecification.freeForAll(3)));
            assertEquals(text(object(game.host.state()).getAsJsonObject("startState"), "phase"), "cancelled");
            game.ready(); game.host.start(); game.main();
            var active = object(game.host.state()); var actual = object(game.host.match().state());
            assertFalse(active.has("startState")); assertFalse(active.has("partyProgress")); assertTrue(actual.get("encounterId").isJsonNull());
            String gameId = text(actual, "id"); gate.release.countDown();
            store.openAttempt(gate.attempt).get(5, TimeUnit.SECONDS); drainAdmission(game.host);
            assertEquals(text(object(game.host.match().state()), "id"), gameId);
            assertFalse(object(game.host.state()).has("startState"));
        } finally { gate.release.countDown(); }
    }

    @Test(timeOut = 15000) public void startingRequiresTheDisplayedSetupTempoAndAllHumanReadyAcknowledgements() throws Exception {
        try (var game = new CooperativeEncounterTest.Fixture("admission-start-consent", 2, 2, 1, null, false)) {
            var current = object(game.host.state()); var wrongSetup = begin(current);
            wrongSetup.addProperty("matchSetupConsent", UUID.randomUUID().toString());
            assertThrows(IllegalArgumentException.class, () -> game.host.startEncounter(wrongSetup));
            game.host.configureTempo(object(Map.of("tableId", text(current, "tableId"), "preset", "standard", "expiry", "pass")));
            var enabled = object(game.host.state());
            assertThrows(IllegalArgumentException.class, () -> game.host.startEncounter(begin(enabled)));
            game.ready(); var wrongTempo = begin(object(game.host.state()));
            wrongTempo.addProperty("tempoRevision", text(current.getAsJsonObject("tempo"), "revision"));
            assertThrows(IllegalArgumentException.class, () -> game.host.startEncounter(wrongTempo));
            assertFalse(object(game.host.state()).has("startState")); assertTrue(game.host.automatedSeats().isEmpty());
        }
    }

    @DataProvider public Object[][] failures() { return new Object[][] {{false}, {true}}; }
    @Test(dataProvider = "failures", timeOut = 45000)
    public void retryBeforeWriteOrAfterAcknowledgementLossUsesExactlyTheSamePreparedGame(boolean acknowledgementLoss) throws Exception {
        var profile = ApiTestWorkspace.profile("admission-retry-" + acknowledgementLoss); var gate = new Gate(false, true, acknowledgementLoss);
        try (var store = new EncounterProgressStore(profile, gate);
             var game = new CooperativeEncounterTest.Fixture("admission-retry-" + acknowledgementLoss, 1, 1, 1, store, false)) {
            var request = begin(object(game.host.state())); var pending = object(game.host.startEncounter(request));
            var failed = await(game.host, state -> text(state.getAsJsonObject("startState"), "phase").equals("failed"));
            var start = failed.getAsJsonObject("startState"); assertFalse(failed.get("matchActive").getAsBoolean());
            assertTrue(start.get("retryAllowed").getAsBoolean()); assertFalse(text(start, "error").contains("PRIVATE_ADMISSION_SENTINEL"));
            var identity = gate.attempt;
            assertEquals(text(object(game.host.startEncounter(request)).getAsJsonObject("startState"), "attemptId"), identity.encounterId());
            assertEquals(gate.writes.get(), 1, "Repeated Begin is not an implicit retry");
            var stale = attemptScope(failed); stale.addProperty("attemptId", UUID.randomUUID().toString());
            assertThrows(IllegalArgumentException.class, () -> game.host.retryEncounterStart(stale));
            game.host.retryEncounterStart(attemptScope(pending));
            await(game.host, state -> state.get("matchActive").getAsBoolean()); game.main();
            assertEquals(gate.attempt, identity); assertEquals(gate.writes.get(), acknowledgementLoss ? 1 : 2,
                    "An acknowledgement retry reads the immutable durable open instead of writing a second one");
            var actual = object(game.host.match().state());
            assertEquals(text(actual, "id"), identity.gameId()); assertEquals(text(actual, "encounterId"), identity.encounterId());
            assertThrows(IllegalArgumentException.class, () -> game.host.cancelEncounterStart(attemptScope(pending)));
            game.concedeOpposition(); game.saved(1); game.host.returnToLobby();
        }
    }

    private static JsonObject await(SyncLobby host, Predicate<JsonObject> predicate) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(8); JsonObject state;
        do { state = object(host.state()); if (predicate.test(state)) return state; Thread.sleep(3); } while (System.nanoTime() < deadline);
        throw new AssertionError("Admission did not reach its bounded phase: " + state);
    }
    private static void drainAdmission(SyncLobby host) throws Exception {
        // Test-only scheduling barrier on the real private executor, without a
        // product command that could expose or control internal task ordering.
        var field = SyncLobby.class.getDeclaredField("startCoordinator"); field.setAccessible(true);
        var executor = (ExecutorService)field.get(host); if (executor != null) executor.submit(() -> { }).get(3, TimeUnit.SECONDS);
    }
    private static final class Gate implements EncounterProgressStore.Writer {
        final CountDownLatch entered = new CountDownLatch(1), finished = new CountDownLatch(1), release;
        final boolean failOnce, acknowledgementLoss; final AtomicInteger writes = new AtomicInteger();
        volatile EncounterProgressStore.AttemptOpened attempt;
        Gate(boolean block, boolean failOnce, boolean acknowledgementLoss) {
            release = new CountDownLatch(block ? 1 : 0); this.failOnce = failOnce; this.acknowledgementLoss = acknowledgementLoss;
        }
        @Override public void write(Path target, byte[] bytes) throws IOException {
            if (!target.getParent().getFileName().toString().equals("attempts")) { EncounterProgressStore.atomicWrite(target, bytes); return; }
            int count = writes.incrementAndGet();
            var value = JSON.fromJson(new String(bytes, java.nio.charset.StandardCharsets.UTF_8), EncounterProgressStore.AttemptOpened.class);
            if (attempt != null && !attempt.equals(value)) throw new IOException("Retry changed the prepared attempt");
            attempt = value; entered.countDown();
            try { if (!release.await(15, TimeUnit.SECONDS)) throw new IOException("Controlled admission latch timeout"); }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); throw new IOException(interrupted); }
            try {
                if (failOnce && count == 1 && !acknowledgementLoss) throw new IOException("PRIVATE_ADMISSION_SENTINEL before write");
                EncounterProgressStore.atomicWrite(target, bytes);
                if (failOnce && count == 1) throw new IOException("PRIVATE_ADMISSION_SENTINEL after forced write");
            } finally { finished.countDown(); }
        }
    }
}
