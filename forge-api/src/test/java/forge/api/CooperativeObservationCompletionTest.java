package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import static org.testng.Assert.*;
import java.lang.management.ManagementFactory;
import java.lang.management.ThreadInfo;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;
import java.util.concurrent.locks.ReentrantLock;

/** Actual completion/read overlap, isolated so a regressed lock cycle cannot wedge the test rules executor. */
public class CooperativeObservationCompletionTest {
    @Test(timeOut = 60000)
    public void aCurrentOwnReadAndRealCompletionCannotDeadlockDurableEncounterRetirement() throws Exception {
        Path evidence = ApiTestWorkspace.evidence("cooperative-observation-completion");
        String java = Path.of(System.getProperty("java.home"), "bin",
                System.getProperty("os.name").startsWith("Windows") ? "java.exe" : "java").toString();
        String classpath = System.getProperty("surefire.test.class.path", System.getProperty("java.class.path"));
        var child = new ProcessBuilder(java, "-Xmx768m", "-Dmana.test=true",
                "-Dmana.tests.trialId=cooperative-completion-child", "-Dmana.tests.runRoot=" + evidence.resolve("child-workspace"),
                "-cp", classpath, CooperativeObservationCompletionTest.class.getName(), evidence.resolve("child-proof").toString())
                .redirectErrorStream(true).redirectOutput(evidence.resolve("child.log").toFile()).start();
        try {
            boolean exited = child.waitFor(45, TimeUnit.SECONDS);
            Files.writeString(evidence.resolve("child-exit.txt"), exited ? Integer.toString(child.exitValue()) : "time-cutoff");
            assertTrue(exited, "The isolated completion regression did not finish; preserve its cutoff and child.log");
            assertEquals(child.exitValue(), 0, "The real completion/read boundary failed; inspect child-proof and child.log");
            var proof = new GsonBuilder().create().fromJson(Files.readString(evidence.resolve("child-proof/completion-observation-lock-proof.json")), JsonObject.class);
            assertTrue(proof.get("completionWorkerHeldRulesState").getAsBoolean());
            assertFalse(proof.get("deadlockDetected").getAsBoolean());
            assertTrue(proof.get("readCompleted").getAsBoolean());
            assertTrue(proof.get("readFailure").isJsonNull());
            assertTrue(proof.get("hookFailure").isJsonNull());
            var saved = proof.getAsJsonObject("savedEncounter");
            assertEquals(saved.get("completedEncounters").getAsInt(), 1);
            assertEquals(saved.getAsJsonObject("lastReceipt").get("gameId"), proof.get("actualGameId"));
            assertTrue(proof.get("retiredToLobby").getAsBoolean());
            assertEquals(proof.get("completedAfterRepeatedReturn").getAsInt(), 1);
        } finally {
            if (child.isAlive()) { child.destroyForcibly(); child.waitFor(5, TimeUnit.SECONDS); }
        }
    }

    public static void main(String[] args) {
        try { run(args); }
        catch (Throwable failure) {
            failure.printStackTrace(System.err);
            System.err.flush();
            System.exit(3);
        }
    }

    private static void run(String[] args) throws Exception {
        Path evidence = Path.of(args[0]);
        Files.createDirectories(evidence);
        ApiTestWorkspace.current().bootstrap();
        var fixture = new CooperativeEncounterTest.Fixture("completion-observation-cycle", 1, 1, 1, null, false);
        fixture.start();
        fixture.main();
        var tableField = SyncLobby.class.getDeclaredField("table");
        tableField.setAccessible(true);
        var table = (MatchTable)tableField.get(fixture.host);
        var lockField = forge.game.GameAction.class.getDeclaredField("stateBoundary");
        lockField.setAccessible(true);
        var stateLock = (ReentrantLock)lockField.get(table.game.getAction());
        var completion = table.start();
        if (completion.isDone()) throw new AssertionError("Actual encounter must still be running");
        var entered = new CountDownLatch(1);
        var release = new CountDownLatch(1);
        var worker = new AtomicLong();
        var heldState = new AtomicBoolean();
        var hookFailure = new AtomicReference<Throwable>();
        completion.whenComplete((ignored, failure) -> {
            worker.set(Thread.currentThread().getId());
            heldState.set(stateLock.isHeldByCurrentThread());
            entered.countDown();
            try {
                if (!release.await(10, TimeUnit.SECONDS)) throw new AssertionError("Diagnostic completion hook was not released");
            } catch (Throwable error) { hookFailure.set(error); }
        });
        fixture.concedeOpposition();
        if (!entered.await(5, TimeUnit.SECONDS)) throw new AssertionError("Actual rules completion did not enter its hook");
        if (!heldState.get()) throw new AssertionError("Completion did not retain the actual rules state boundary");
        var readDone = new CountDownLatch(1);
        var readFailure = new AtomicReference<Throwable>();
        Thread reader = new Thread(() -> {
            try { fixture.host.match().decisionObservation(); }
            catch (Throwable error) { readFailure.set(error); }
            finally { readDone.countDown(); }
        }, "owned-observation-during-real-encounter-completion");
        reader.setDaemon(true);
        reader.start();
        var threads = ManagementFactory.getThreadMXBean();
        ThreadInfo blocked = null;
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        do {
            var infos = threads.getThreadInfo(new long[] {reader.getId()}, true, true);
            if (infos[0] != null && infos[0].getLockOwnerId() == worker.get()) { blocked = infos[0]; break; }
            Thread.sleep(3);
        } while (System.nanoTime() < deadline);
        if (blocked == null) throw new AssertionError("Own read never waited on the completing rules worker");
        boolean readHoldsLobby = Arrays.stream(blocked.getLockedMonitors())
                .anyMatch(monitor -> monitor.getIdentityHashCode() == System.identityHashCode(fixture.host));
        release.countDown();
        long[] deadlocked = null;
        deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        do {
            var cycle = threads.findDeadlockedThreads();
            if (cycle != null && Arrays.stream(cycle).anyMatch(id -> id == reader.getId())
                    && Arrays.stream(cycle).anyMatch(id -> id == worker.get())) { deadlocked = cycle; break; }
            if (readDone.getCount() == 0) break;
            Thread.sleep(3);
        } while (System.nanoTime() < deadline);
        var proof = new LinkedHashMap<String, Object>();
        proof.put("actualEncounterId", table.encounterId());
        proof.put("actualGameId", table.id);
        proof.put("completionWorkerHeldRulesState", heldState.get());
        proof.put("ownReadHeldLobbyWhileWaitingRulesState", readHoldsLobby);
        proof.put("ownReadThreadId", reader.getId());
        proof.put("completionWorkerThreadId", worker.get());
        proof.put("deadlockDetected", deadlocked != null);
        proof.put("readCompleted", readDone.getCount() == 0);
        proof.put("readFailure", readFailure.get() == null ? null : readFailure.get().toString());
        proof.put("hookFailure", hookFailure.get() == null ? null : hookFailure.get().toString());
        proof.put("blockedRead", blocked.toString());
        if (deadlocked != null) {
            proof.put("deadlockedThreads", Arrays.stream(threads.getThreadInfo(deadlocked, true, true))
                    .map(info -> info == null ? "absent" : info.toString()).toList());
        } else if (readDone.getCount() == 0 && readFailure.get() == null && hookFailure.get() == null) {
            proof.put("savedEncounter", fixture.saved(1));
            var returned = new GsonBuilder().create().toJsonTree(fixture.host.returnToLobby()).getAsJsonObject();
            proof.put("retiredToLobby", !returned.get("matchActive").getAsBoolean());
            var repeated = new GsonBuilder().create().toJsonTree(fixture.host.returnToLobby()).getAsJsonObject();
            proof.put("completedAfterRepeatedReturn", repeated.getAsJsonObject("partyProgress").get("completedEncounters").getAsInt());
            fixture.close();
        }
        Files.writeString(evidence.resolve("completion-observation-lock-proof.json"),
                new GsonBuilder().serializeNulls().setPrettyPrinting().create().toJson(proof));
        System.out.println("DIAGNOSTIC " + Map.of("deadlockDetected", deadlocked != null,
                "ownReadHeldLobby", readHoldsLobby, "readCompleted", readDone.getCount() == 0));
        System.out.flush();
        // This child deliberately contains a cycle on the baseline. Do not
        // attempt shared-JVM teardown while its real rules locks are wedged.
        System.exit(deadlocked != null ? 42 : readDone.getCount() == 0 && readFailure.get() == null && hookFailure.get() == null ? 0 : 2);
    }
}
