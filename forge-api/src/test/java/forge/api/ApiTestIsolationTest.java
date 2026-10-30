package forge.api;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.testng.ITestNGMethod;
import org.testng.ITestResult;
import org.testng.annotations.Test;
import org.testng.internal.ConstructorOrMethod;

import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;

import static org.testng.Assert.*;

public class ApiTestIsolationTest {
    @Test public void eagerBootstrapOwnsTheActualMemoizedProfileAndCache() {
        ApiTestWorkspace.current().bootstrap();
    }

    @Test public void nestedParametersHaveStableTypedIdentityWithoutMapIterationOrder() {
        var first = new LinkedHashMap<Integer, List<String>>();
        first.put(1, List.of("Spellbook")); first.put(0, List.of("Forest"));
        var second = new LinkedHashMap<Integer, List<String>>();
        second.put(0, List.of("Forest")); second.put(1, List.of("Spellbook"));
        Object[] left = {"cleanup", first, new Integer[]{null, 7}, new int[]{8, 9}};
        Object[] right = {"cleanup", second, new Integer[]{null, 7}, new int[]{8, 9}};
        assertEquals(ApiTestParameters.sha256(left), ApiTestParameters.sha256(right));
        ((int[]) right[3])[1] = 10;
        assertNotEquals(ApiTestParameters.sha256(left), ApiTestParameters.sha256(right));
        assertNotEquals(ApiTestParameters.canonical(new int[]{1}), ApiTestParameters.canonical(new Integer[]{1}));
        assertNotEquals(ApiTestParameters.canonical(1), ApiTestParameters.canonical(1L));
        assertNotEquals(ApiTestParameters.canonical(List.of(1, 2)), ApiTestParameters.canonical(List.of(2, 1)));
    }

    @Test public void unknownObjectsNeverBecomeIdentityThroughToString() {
        var calls = new AtomicInteger();
        Object unknown = new Object() { @Override public String toString() { calls.incrementAndGet(); throw new AssertionError("called"); } };
        assertThrows(IllegalArgumentException.class, () -> ApiTestParameters.sha256(new Object[]{Map.of("key", unknown)}));
        assertEquals(calls.get(), 0);
        var cycle = new ArrayList<Object>(); cycle.add(cycle);
        assertThrows(IllegalArgumentException.class, () -> ApiTestParameters.canonical(cycle));
    }

    @Test public void workspacesAndRepeatedScenarioAttemptsNeverShareFiles() throws Exception {
        Path run = ApiTestWorkspace.evidence("workspace-ownership");
        var one = ApiTestWorkspace.create(run, "trial", "default", "1");
        var two = ApiTestWorkspace.create(run, "trial", "default", "1");
        var three = ApiTestWorkspace.create(run, "trial", "parallel", "2");
        assertNotEquals(one.root, two.root); assertNotEquals(one.root, three.root);
        assertEquals(one.root, one.root.toRealPath());
        Path first = one.directory("evidence", "same-case");
        Path next = one.directory("evidence", "same-case");
        Files.writeString(first.resolve("trace.json"), "first");
        Files.writeString(next.resolve("trace.json"), "next");
        assertEquals(Files.readString(first.resolve("trace.json")), "first");
        assertTrue(first.startsWith(one.root)); assertTrue(one.directory("profiles", "guest").startsWith(one.root));
        assertTrue(Files.isDirectory(one.root.resolve("bootstrap/engine-profile")));
        assertTrue(Files.isDirectory(one.root.resolve("bootstrap/engine-cache")));
    }

    @Test public void workspaceLabelsCannotEscapeTheirOwnerOrSelectUnknownModes() {
        Path run = ApiTestWorkspace.evidence("workspace-invalid");
        var owner = ApiTestWorkspace.create(run, "trial", "default", "1");
        for (String bad : List.of("../escape", "..", "/absolute", "x/y", "x\\y", ""))
            assertThrows(IllegalArgumentException.class, () -> owner.directory("evidence", bad));
        assertThrows(IllegalArgumentException.class, () -> ApiTestWorkspace.create(run, "trial", "Parallel", "1"));
        assertThrows(IllegalArgumentException.class, () -> ApiTestWorkspace.create(run, "trial", "parallel", "../2"));
        assertThrows(IllegalArgumentException.class, () -> owner.directory("other", "test"));
    }

    @Test public void forkLabelsRequireTheExactExpandedPrefixAndPositiveNumericIdentity() {
        assertEquals(ApiTestWorkspace.parseForkLabel("fork-1"), "1");
        assertEquals(ApiTestWorkspace.parseForkLabel("fork-2"), "2");
        for (String label : List.of("", "1", "fork-0", "fork--1", "Fork-1", "x/fork-1", "fork-${surefire.forkNumber}"))
            assertThrows(IllegalArgumentException.class, () -> ApiTestWorkspace.parseForkLabel(label));
        assertThrows(IllegalArgumentException.class, () -> ApiTestWorkspace.parseForkLabel(null));
    }

    @Test public void classInventoryBindsRelativeNamesAndBytesWithoutFilesystemOrder() throws Exception {
        Path one = ApiTestWorkspace.evidence("class-inventory-one");
        Path two = ApiTestWorkspace.evidence("class-inventory-two");
        Files.createDirectories(one.resolve("p")); Files.createDirectories(two.resolve("p"));
        Files.write(one.resolve("p/B.class"), new byte[]{2}); Files.write(one.resolve("p/A.class"), new byte[]{1});
        Files.write(two.resolve("p/A.class"), new byte[]{1}); Files.write(two.resolve("p/B.class"), new byte[]{2});
        String expected = "p/A.class\0" + ApiTestParameters.hash(new byte[]{1}) + "\n"
                + "p/B.class\0" + ApiTestParameters.hash(new byte[]{2}) + "\n";
        assertEquals(ApiTestListener.classSetFingerprint(one), ApiTestParameters.hash(expected.getBytes(StandardCharsets.UTF_8)));
        assertEquals(ApiTestListener.classSetFingerprint(one), ApiTestListener.classSetFingerprint(two));
        Files.write(two.resolve("p/B.class"), new byte[]{3});
        assertNotEquals(ApiTestListener.classSetFingerprint(one), ApiTestListener.classSetFingerprint(two));
    }

    @Test public void receiptsCaptureIdentityBeforeTestMutatesItsInputAndBindTheJournal() throws Exception {
        var workspace = fixtureWorkspace("identity-receipt");
        var state = new ApiTestListener.State(workspace);
        var parameters = new LinkedHashMap<Integer, List<String>>(); parameters.put(0, List.of("Forest"));
        Object[] values = {parameters, new Integer[]{null, 7}};
        String original = ApiTestParameters.sha256(values);
        ITestResult result = result("sample", values, null);
        state.executionStarted(); state.started(result, false);
        parameters.put(0, List.of("Island"));
        state.completed(result, false, "passed");
        state.completed(result, false, "passed"); // Matching callback overloads cannot create a second result.
        state.executionFinished(); state.finish();
        JsonObject receipt = receipt(workspace);
        assertEquals(receipt.getAsJsonObject("summary").get("passed").getAsInt(), 1);
        var invocation = receipt.getAsJsonArray("invocations").get(0).getAsJsonObject();
        assertEquals(invocation.get("parameterSha256").getAsString(), original);
        assertEquals(invocation.get("count").getAsInt(), 1);
        assertEquals(invocation.getAsJsonArray("parameterTypes").get(0).getAsString(), Map.class.getName());
        assertEquals(receipt.getAsJsonObject("files").get("journalSha256").getAsString(),
                ApiTestParameters.hash(Files.readAllBytes(workspace.root.resolve("invocations.jsonl"))));
        assertEquals(receipt.getAsJsonObject("files").get("startSha256").getAsString(),
                ApiTestParameters.hash(Files.readAllBytes(workspace.root.resolve("process-start.json"))));
        assertTrue(receipt.get("completed").getAsBoolean());
        assertTrue(receipt.get("completedAtEpochMs").getAsLong() >= receipt.get("startedAtEpochMs").getAsLong());
    }

    @Test public void firstFailureAndConfigurationSkipAreDurableBeforeFinalReceipt() throws Exception {
        var workspace = fixtureWorkspace("failure-receipt");
        var state = new ApiTestListener.State(workspace);
        ITestResult failure = result("empty", new Object[0], new IllegalArgumentException("first failure retained"));
        state.started(failure, false); state.completed(failure, false, "failed");
        String retained = Files.readString(workspace.root.resolve("invocations.jsonl"));
        assertTrue(retained.contains("first failure retained"));
        assertFalse(Files.exists(workspace.root.resolve("receipt.json")));
        ITestResult skip = result("empty", new Object[0], new IllegalStateException("configuration skipped"));
        state.started(skip, true); state.completed(skip, true, "skipped"); state.finish();
        var summary = receipt(workspace).getAsJsonObject("summary");
        assertEquals(summary.get("failed").getAsInt(), 1);
        assertEquals(summary.get("configurationSkipped").getAsInt(), 1);
        assertEquals(receipt(workspace).getAsJsonArray("invocations").size(), 1);
    }

    @Test public void unfinishedExecutionsAndUnknownParameterTypesCannotProduceCleanAccounting() throws Exception {
        var unfinished = fixtureWorkspace("unfinished-receipt");
        var state = new ApiTestListener.State(unfinished);
        state.executionStarted(); state.started(result("empty", new Object[0], null), false); state.finish();
        assertEquals(receipt(unfinished).getAsJsonObject("summary").get("configurationFailed").getAsInt(), 1);
        var unsupported = fixtureWorkspace("unsupported-receipt");
        var other = new ApiTestListener.State(unsupported);
        assertThrows(IllegalStateException.class, () -> other.started(result("empty", new Object[]{new Object()}, null), false));
        assertTrue(Files.readString(unsupported.root.resolve("invocations.jsonl")).contains("Unsupported API test parameter type"));
        other.finish();
        assertEquals(receipt(unsupported).getAsJsonObject("summary").get("configurationFailed").getAsInt(), 1);
    }

    @Test public void nonSkippedCompletionsRequireTheirOriginalStartIdentity() throws Exception {
        var workspace = fixtureWorkspace("missing-start-receipt");
        var state = new ApiTestListener.State(workspace);
        assertThrows(IllegalStateException.class, () -> state.completed(result("empty", new Object[0], null), false, "passed"));
        state.finish();
        assertEquals(receipt(workspace).getAsJsonObject("summary").get("configurationFailed").getAsInt(), 1);
        assertTrue(receipt(workspace).getAsJsonArray("invocations").isEmpty());
    }

    @Test public void conflictingCallbacksCannotRewriteTheFirstResultOrChangeItsKind() throws Exception {
        var workspace = fixtureWorkspace("conflicting-result-receipt");
        var state = new ApiTestListener.State(workspace);
        ITestResult result = result("empty", new Object[0], null);
        state.started(result, false); state.completed(result, false, "passed");
        assertThrows(IllegalStateException.class, () -> state.completed(result, false, "failed"));
        assertThrows(IllegalStateException.class, () -> state.completed(result, true, "passed"));
        state.finish();
        assertEquals(receipt(workspace).getAsJsonObject("summary").get("configurationFailed").getAsInt(), 2);
        assertEquals(receipt(workspace).getAsJsonObject("summary").get("passed").getAsInt(), 1);
        assertEquals(receipt(workspace).getAsJsonArray("invocations").size(), 1);
    }

    @Test public void reusedJvmKeepsDistinctInvocationsAcrossTestNgExecutionsUntilOneFinalReceipt() throws Exception {
        var workspace = fixtureWorkspace("reused-execution-receipt");
        var state = new ApiTestListener.State(workspace);
        for (int execution = 0; execution < 2; execution++) {
            state.executionStarted();
            ITestResult result = result("empty", new Object[0], null);
            state.started(result, false); state.completed(result, false, "passed"); state.executionFinished();
            assertFalse(Files.exists(workspace.root.resolve("receipt.json")), "A class execution is not the process lifetime");
        }
        state.finish(); state.finish();
        assertEquals(receipt(workspace).getAsJsonObject("summary").get("passed").getAsInt(), 2);
        assertEquals(receipt(workspace).getAsJsonArray("invocations").get(0).getAsJsonObject().get("count").getAsInt(), 2);
        assertEquals(Files.readAllLines(workspace.root.resolve("invocations.jsonl")).size(), 2);
    }

    private static ApiTestWorkspace fixtureWorkspace(String name) {
        return ApiTestWorkspace.create(ApiTestWorkspace.evidence(name), "listener-unit", "default", "1");
    }
    private static JsonObject receipt(ApiTestWorkspace workspace) throws Exception {
        return JsonParser.parseString(Files.readString(workspace.root.resolve("receipt.json"))).getAsJsonObject();
    }
    private static ITestResult result(String name, Object[] parameters, Throwable failure) throws Exception {
        Method method = name.equals("sample") ? Probe.class.getDeclaredMethod(name, Map.class, Integer[].class)
                : Probe.class.getDeclaredMethod(name);
        ITestNGMethod testMethod = (ITestNGMethod) Proxy.newProxyInstance(ITestNGMethod.class.getClassLoader(), new Class<?>[]{ITestNGMethod.class},
                (proxy, called, args) -> switch (called.getName()) {
                    case "getRealClass" -> Probe.class;
                    case "getConstructorOrMethod" -> new ConstructorOrMethod(method);
                    default -> throw new AssertionError("Unexpected method query: " + called.getName());
                });
        return (ITestResult) Proxy.newProxyInstance(ITestResult.class.getClassLoader(), new Class<?>[]{ITestResult.class},
                (proxy, called, args) -> switch (called.getName()) {
                    case "getMethod" -> testMethod;
                    case "getParameters" -> parameters;
                    case "getThrowable" -> failure;
                    case "getStartMillis", "getEndMillis" -> System.currentTimeMillis();
                    default -> throw new AssertionError("Unexpected result query: " + called.getName());
                });
    }
    private static final class Probe {
        void sample(Map<Integer, List<String>> values, Integer[] limits) { }
        void empty() { }
    }
}
