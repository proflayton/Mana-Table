package forge.api;

import com.google.gson.Gson;
import org.testng.annotations.Test;
import java.io.IOException;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import static forge.api.EncounterProgressStoreTest.*;
import static org.testng.Assert.*;

/** Real atomic journal cut points, with no invented rules outcome for an opened attempt. */
public class EncounterAdmissionStoreTest {
    @Test(timeOut = 15000) public void durableOpenWithoutReceiptStaysUnknownAndAcknowledgementRetryKeepsItsIdentity() throws Exception {
        var profile = ApiTestWorkspace.profile("admission-ack-window");
        var receipt = receipt(UUID.randomUUID().toString(), "win", true); var attempt = opened(receipt);
        var loseAcknowledgement = new AtomicBoolean(true);
        try (var store = new EncounterProgressStore(profile, (path, bytes) -> {
            EncounterProgressStore.atomicWrite(path, bytes);
            if (path.getParent().getFileName().toString().equals("attempts") && loseAcknowledgement.getAndSet(false))
                throw new IOException("Controlled acknowledgement failure after durable open");
        })) {
            store.startup().get(3, TimeUnit.SECONDS);
            assertThrows(ExecutionException.class, () -> store.commit(receipt).get(3, TimeUnit.SECONDS));
            assertThrows(ExecutionException.class, () -> store.openAttempt(attempt).get(3, TimeUnit.SECONDS));
            assertEquals(store.openAttempt(attempt).get(3, TimeUnit.SECONDS), attempt);
            assertEquals(store.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS), List.of(attempt));
            assertEquals(store.current(receipt.partyId()).completedEncounters(), 0);
        }
        try (var reopened = new EncounterProgressStore(profile)) {
            reopened.startup().get(3, TimeUnit.SECONDS);
            assertEquals(reopened.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS), List.of(attempt), "Open-before-launch and launch-before-receipt have the same unknown outcome");
            assertEquals(reopened.current(receipt.partyId()).completedEncounters(), 0);
            var changed = new EncounterProgressStore.AttemptOpened(1, attempt.partyId(), attempt.encounterId(), UUID.randomUUID().toString(),
                    attempt.rulesetId(), attempt.partyTeamId(), attempt.matchSetupConsent(), attempt.tempoRevision(), attempt.participants(), attempt.openedUtc());
            assertThrows(ExecutionException.class, () -> reopened.openAttempt(changed).get(3, TimeUnit.SECONDS));
            var reusedGame = new EncounterProgressStore.AttemptOpened(1, attempt.partyId(), UUID.randomUUID().toString(), attempt.gameId(),
                    attempt.rulesetId(), attempt.partyTeamId(), attempt.matchSetupConsent(), attempt.tempoRevision(), attempt.participants(), attempt.openedUtc());
            assertThrows(ExecutionException.class, () -> reopened.openAttempt(reusedGame).get(3, TimeUnit.SECONDS));
            var members = new ArrayList<>(receipt.participants()); var first = members.get(0);
            members.set(0, new EncounterProgressStore.Participant(first.participantId(), first.index(), first.playerId(), first.teamId(), first.role(), UUID.randomUUID().toString()));
            var mismatch = new EncounterProgressStore.Receipt(receipt.receiptId(), receipt.partyId(), receipt.encounterId(), receipt.gameId(),
                    receipt.rulesetId(), receipt.partyTeamId(), receipt.outcome(), members, receipt.completedUtc());
            assertThrows(ExecutionException.class, () -> reopened.commit(mismatch).get(3, TimeUnit.SECONDS));
            assertEquals(reopened.commit(receipt).get(3, TimeUnit.SECONDS).completedEncounters(), 1);
            assertTrue(reopened.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS).isEmpty());
        }
    }

    @Test(timeOut = 15000) public void captureBeforeWriteIsAbsentAndLegacy337ReceiptsReplayWithoutInventingAdmissions() throws Exception {
        var profile = ApiTestWorkspace.profile("admission-legacy"); var receipt = receipt(UUID.randomUUID().toString(), "win", false);
        try (var store = new EncounterProgressStore(profile, (path, bytes) -> { throw new IOException("Controlled before admission write"); })) {
            store.startup().get(3, TimeUnit.SECONDS);
            assertThrows(ExecutionException.class, () -> store.openAttempt(opened(receipt)).get(3, TimeUnit.SECONDS));
        }
        try (var store = new EncounterProgressStore(profile)) {
            store.startup().get(3, TimeUnit.SECONDS);
            assertTrue(store.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS).isEmpty());
            assertEquals(store.current(receipt.partyId()).completedEncounters(), 0);
        }
        var raw = profile.resolve("encounters-v1/receipts").resolve(receipt.gameId() + ".json");
        EncounterProgressStore.atomicWrite(raw, new Gson().toJson(receipt).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        try (var legacy = new EncounterProgressStore(profile)) {
            legacy.startup().get(3, TimeUnit.SECONDS);
            assertEquals(legacy.current(receipt.partyId()).completedEncounters(), 1);
            assertEquals(legacy.current(receipt.partyId()).victories(), 0);
            assertEquals(legacy.commit(receipt).get(3, TimeUnit.SECONDS).completedEncounters(), 1);
            assertTrue(legacy.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS).isEmpty());
        }
    }

    @Test(timeOut = 15000) public void liveStoreInspectionSeesAnotherOwnersOpenAndReceiptWithoutTakingOverItsGame() throws Exception {
        var profile = ApiTestWorkspace.profile("admission-live-inventory"); var receipt = receipt(UUID.randomUUID().toString(), "win", true);
        var attempt = opened(receipt);
        try (var first = new EncounterProgressStore(profile); var second = new EncounterProgressStore(profile)) {
            first.startup().get(3, TimeUnit.SECONDS); second.startup().get(3, TimeUnit.SECONDS);
            assertTrue(second.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS).isEmpty());
            first.openAttempt(attempt).get(3, TimeUnit.SECONDS);
            assertEquals(second.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS), List.of(attempt));
            assertEquals(second.current(receipt.partyId()).completedEncounters(), 0);
            first.commit(receipt).get(3, TimeUnit.SECONDS);
            assertTrue(second.unresolved(receipt.partyId()).get(3, TimeUnit.SECONDS).isEmpty());
            assertEquals(second.commit(receipt).get(3, TimeUnit.SECONDS).completedEncounters(), 1);
        }
    }

    @Test(timeOut = 15000) public void unknownVersionAndForeignInventoryCannotBeMistakenForLegacyOrAdmittedGames() throws Exception {
        for (boolean malformedAttempt : List.of(false, true)) {
            var profile = ApiTestWorkspace.profile("admission-corrupt-" + malformedAttempt);
            try (var empty = new EncounterProgressStore(profile)) { empty.startup().get(3, TimeUnit.SECONDS); }
            var folder = profile.resolve("encounters-v1/" + (malformedAttempt ? "attempts" : "receipts"));
            Files.writeString(folder.resolve(UUID.randomUUID() + ".json"), "{\"schemaVersion\":999}");
            try (var corrupt = new EncounterProgressStore(profile)) {
                assertThrows(ExecutionException.class, () -> corrupt.startup().get(3, TimeUnit.SECONDS));
            }
        }
        var profile = ApiTestWorkspace.profile("admission-foreign-inventory");
        try (var empty = new EncounterProgressStore(profile)) { empty.startup().get(3, TimeUnit.SECONDS); }
        Files.writeString(profile.resolve("encounters-v1/attempts/foreign.txt"), "unknown");
        try (var corrupt = new EncounterProgressStore(profile)) { assertThrows(ExecutionException.class, () -> corrupt.startup().get(3, TimeUnit.SECONDS)); }
    }
}
