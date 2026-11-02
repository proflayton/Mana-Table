package forge.api;

import org.testng.annotations.Test;
import java.io.IOException;
import java.nio.file.*;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import static org.testng.Assert.*;

public class EncounterProgressStoreTest {
    @Test(timeOut = 15000) public void receiptSurvivesTheProgressWriteCrashWindowAndReplaysExactlyOnce() throws Exception {
        var profile = ApiTestWorkspace.profile("encounter-journal-replay");
        var failCheckpoint = new AtomicBoolean(true);
        var receipt = receipt(UUID.randomUUID().toString(), "win", true);
        try (var store = new EncounterProgressStore(profile, (path, bytes) -> {
            if (path.getParent().getFileName().toString().equals("parties") && failCheckpoint.get()) throw new IOException("Controlled crash after receipt");
            EncounterProgressStore.atomicWrite(path, bytes);
        })) {
            store.startup().get(5, TimeUnit.SECONDS);
            assertThrows(ExecutionException.class, () -> store.commit(receipt).get(5, TimeUnit.SECONDS));
            assertTrue(Files.isRegularFile(profile.resolve("encounters-v1/receipts").resolve(receipt.gameId() + ".json")));
            assertEquals(store.current(receipt.partyId()).completedEncounters(), 0);
        }
        try (var recovered = new EncounterProgressStore(profile)) {
            recovered.startup().get(5, TimeUnit.SECONDS);
            assertEquals(recovered.current(receipt.partyId()).completedEncounters(), 1);
            assertEquals(recovered.current(receipt.partyId()).victories(), 1);
            assertEquals(recovered.current(receipt.partyId()).lastReceipt(), receipt);
            assertEquals(recovered.commit(receipt).get(5, TimeUnit.SECONDS).completedEncounters(), 1);
            var conflict = new EncounterProgressStore.Receipt(UUID.randomUUID().toString(), receipt.partyId(), UUID.randomUUID().toString(),
                    receipt.gameId(), receipt.rulesetId(), receipt.partyTeamId(), receipt.outcome(), receipt.participants(), receipt.completedUtc());
            assertThrows(ExecutionException.class, () -> recovered.commit(conflict).get(5, TimeUnit.SECONDS));
            assertEquals(recovered.current(receipt.partyId()).completedEncounters(), 1);
        }
        try (var reopened = new EncounterProgressStore(profile)) {
            reopened.startup().get(5, TimeUnit.SECONDS);
            assertEquals(reopened.current(receipt.partyId()).appliedGameIds(), List.of(receipt.gameId()));
        }
    }

    @Test(timeOut = 15000) public void failureBeforeReceiptAndAnInterruptedGameCannotAwardProgress() throws Exception {
        var profile = ApiTestWorkspace.profile("encounter-before-receipt");
        var receipt = receipt(UUID.randomUUID().toString(), "win", true);
        try (var store = new EncounterProgressStore(profile, (path, bytes) -> { throw new IOException("Controlled first write failure"); })) {
            store.startup().get(5, TimeUnit.SECONDS);
            assertThrows(ExecutionException.class, () -> store.commit(receipt).get(5, TimeUnit.SECONDS));
            assertEquals(store.current(receipt.partyId()).completedEncounters(), 0);
        }
        // A process can die after fsync but before atomic rename. An unfinished
        // temporary write is not a durable receipt and must not prevent recovery.
        Files.writeString(profile.resolve("encounters-v1/receipts").resolve(UUID.randomUUID() + ".json." + UUID.randomUUID() + ".tmp"), "unfinished");
        try (var store = new EncounterProgressStore(profile)) {
            store.startup().get(5, TimeUnit.SECONDS);
            assertEquals(store.current(receipt.partyId()).completedEncounters(), 0);
            var interrupted = new EncounterProgressStore.Receipt(receipt.receiptId(), receipt.partyId(), receipt.encounterId(), receipt.gameId(),
                    receipt.rulesetId(), receipt.partyTeamId(), new MatchOutcomeProjection.Outcome(receipt.gameId(), "win", List.of(1), List.of(10), "AllHumansLost"),
                    receipt.participants(), receipt.completedUtc());
            assertThrows(ExecutionException.class, () -> store.commit(interrupted).get(5, TimeUnit.SECONDS));
            assertEquals(store.current(receipt.partyId()).completedEncounters(), 0);
        }
    }

    @Test(timeOut = 15000) public void naturalLossDrawAndConcurrentRetriesHaveDistinctIdempotentAccounting() throws Exception {
        var profile = ApiTestWorkspace.profile("encounter-concurrent-counts");
        String party = UUID.randomUUID().toString();
        var loss = receipt(party, "win", false); var draw = receipt(party, "draw", false);
        try (var first = new EncounterProgressStore(profile); var second = new EncounterProgressStore(profile)) {
            first.startup().get(5, TimeUnit.SECONDS); second.startup().get(5, TimeUnit.SECONDS);
            CompletableFuture.allOf(first.commit(loss), second.commit(draw), first.commit(loss), second.commit(draw)).get(5, TimeUnit.SECONDS);
        }
        try (var recovered = new EncounterProgressStore(profile)) {
            recovered.startup().get(5, TimeUnit.SECONDS);
            var progress = recovered.current(party);
            assertEquals(progress.completedEncounters(), 2); assertEquals(progress.victories(), 0);
            assertEquals(new HashSet<>(progress.appliedGameIds()), Set.of(loss.gameId(), draw.gameId()));
        }
    }

    private static EncounterProgressStore.Receipt receipt(String party, String kind, boolean partyWins) {
        String game = UUID.randomUUID().toString(), encounter = UUID.randomUUID().toString();
        var players = List.of(new EncounterProgressStore.Participant(UUID.randomUUID().toString(), 0, 1, 10, "party", UUID.randomUUID().toString()),
                new EncounterProgressStore.Participant(UUID.randomUUID().toString(), 1, 2, 20, "opposition", UUID.randomUUID().toString()));
        var outcome = new MatchOutcomeProjection.Outcome(game, kind, kind.equals("draw") ? List.of() : List.of(partyWins ? 1 : 2),
                kind.equals("draw") ? List.of() : List.of(partyWins ? 10 : 20), kind.equals("draw") ? "Draw" : "AllOpposingTeamsLost");
        return new EncounterProgressStore.Receipt(encounter, party, encounter, game, MatchSetupSpecification.COOPERATIVE, 10, outcome, players, Instant.now().toString());
    }
}
