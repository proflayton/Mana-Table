package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import org.testng.*;

import java.io.IOException;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.lang.management.ManagementFactory;
import java.lang.management.MemoryType;
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;

import static java.nio.file.StandardOpenOption.*;

/** One bootstrap and durable accounting owner per Surefire JVM, not per TestNG execution. */
public final class ApiTestListener implements IExecutionListener, ITestListener, IConfigurationListener {
    private static State process;

    private static synchronized State state() {
        if (process == null) {
            process = new State(ApiTestWorkspace.current());
            State owner = process;
            Runtime.getRuntime().addShutdownHook(new Thread(() -> {
                try { owner.finish(); }
                catch (Throwable failure) {
                    failure.printStackTrace(System.err);
                    // A missing final receipt must never look like a successful test process.
                    Runtime.getRuntime().halt(1);
                }
            }, "API test receipt"));
            process.bootstrap();
        }
        return process;
    }

    @Override public void onExecutionStart() { state().executionStarted(); }
    @Override public void onExecutionFinish() { state().executionFinished(); }
    @Override public void onTestStart(ITestResult result) { state().started(result, false); }
    @Override public void onTestSuccess(ITestResult result) { state().completed(result, false, "passed"); }
    @Override public void onTestFailure(ITestResult result) { state().completed(result, false, "failed"); }
    @Override public void onTestFailedWithTimeout(ITestResult result) { onTestFailure(result); }
    @Override public void onTestFailedButWithinSuccessPercentage(ITestResult result) { onTestFailure(result); }
    @Override public void onTestSkipped(ITestResult result) { state().completed(result, false, "skipped"); }
    @Override public void beforeConfiguration(ITestResult result) { state().started(result, true); }
    @Override public void beforeConfiguration(ITestResult result, ITestNGMethod method) { beforeConfiguration(result); }
    @Override public void onConfigurationSuccess(ITestResult result) { state().completed(result, true, "passed"); }
    @Override public void onConfigurationSuccess(ITestResult result, ITestNGMethod method) { onConfigurationSuccess(result); }
    @Override public void onConfigurationFailure(ITestResult result) { state().completed(result, true, "failed"); }
    @Override public void onConfigurationFailure(ITestResult result, ITestNGMethod method) { onConfigurationFailure(result); }
    @Override public void onConfigurationSkip(ITestResult result) { state().completed(result, true, "skipped"); }
    @Override public void onConfigurationSkip(ITestResult result, ITestNGMethod method) { onConfigurationSkip(result); }

    record Identity(String className, String methodName, List<String> parameterTypes, String parameterSha256) { }
    private record Started(Identity identity, long nanos, long epochMs, boolean configuration) { }
    private record Completed(boolean configuration, String status) { }
    private static final class ClassRun {
        final String sha256;
        final long startedAtEpochMs = System.currentTimeMillis();
        long completedAtEpochMs = startedAtEpochMs;
        ClassRun(String sha256) { this.sha256 = sha256; }
    }

    static final class State {
        private static final Gson JSON = new GsonBuilder().disableHtmlEscaping().create();
        private final ApiTestWorkspace workspace;
        private final Map<String, Object> metadata = new LinkedHashMap<>();
        private final Map<String, ClassRun> classes = new TreeMap<>();
        private final Map<ITestResult, Started> active = new IdentityHashMap<>();
        private final Map<ITestResult, Completed> finished = new IdentityHashMap<>();
        private final Map<String, Map<String, Object>> invocations = new TreeMap<>();
        private final Map<String, Integer> summary = new LinkedHashMap<>();
        private final Path start;
        private final Path journalPath;
        private final FileChannel journal;
        private final long lifecycleStart = System.nanoTime();
        private long bootstrapNanos;
        private int executions;
        private boolean closed;

        State(ApiTestWorkspace workspace) {
            this.workspace = workspace;
            try {
                metadata.put("schemaVersion", 1);
                metadata.put("trialId", workspace.trialId);
                metadata.put("mode", workspace.mode);
                metadata.put("forkId", Integer.parseInt(workspace.forkId));
                metadata.put("pid", ProcessHandle.current().pid());
                String source = System.getProperty("mana.tests.sourceRevision",
                        Objects.requireNonNullElse(System.getenv("GITHUB_SHA"), "unknown"));
                if (!workspace.mode.equals("default") && !source.matches("[0-9a-f]{40}"))
                    throw new IllegalArgumentException("A comparison requires an explicit source revision");
                metadata.put("sourceRevision", source);
                metadata.put("testClassSetSha256", classSetFingerprint(testClassRoot()));
                metadata.put("jdk", Map.of("runtimeVersion", System.getProperty("java.runtime.version"),
                        "vendor", System.getProperty("java.vendor"), "vmName", System.getProperty("java.vm.name")));
                metadata.put("workspace", workspace.root.toString());
                metadata.put("startedAtEpochMs", ManagementFactory.getRuntimeMXBean().getStartTime());
                start = workspace.root.resolve("process-start.json");
                var first = new LinkedHashMap<>(metadata); first.put("completed", false);
                Files.writeString(start, JSON.toJson(first) + "\n", StandardCharsets.UTF_8, CREATE_NEW, WRITE);
                journalPath = workspace.root.resolve("invocations.jsonl");
                journal = FileChannel.open(journalPath, CREATE_NEW, WRITE);
                for (String name : List.of("passed", "failed", "skipped", "configurationFailed", "configurationSkipped"))
                    summary.put(name, 0);
            } catch (Exception failure) { throw new IllegalStateException("Cannot begin API test accounting", failure); }
        }

        void bootstrap() {
            long before = System.nanoTime();
            try { workspace.bootstrap(); }
            catch (Throwable failure) { infrastructureFailure("bootstrap", failure); throw new IllegalStateException(failure); }
            finally { bootstrapNanos = System.nanoTime() - before; }
        }

        synchronized void executionStarted() { executions++; }
        synchronized void executionFinished() {
            if (--executions < 0) throw new IllegalStateException("Unmatched TestNG execution completion");
            force();
        }

        synchronized void started(ITestResult result, boolean configuration) {
            if (active.containsKey(result) || finished.containsKey(result)) return;
            try {
                if (!configuration && active.values().stream().anyMatch(item -> !item.configuration))
                    throw new IllegalStateException("API test methods must remain serial within each JVM");
                Identity identity = identity(result);
                rememberClass(result.getMethod().getRealClass());
                active.put(result, new Started(identity, System.nanoTime(), System.currentTimeMillis(), configuration));
            } catch (Throwable failure) { infrastructureFailure("identity", failure); throw new IllegalStateException(failure); }
        }

        synchronized void completed(ITestResult result, boolean configuration, String status) {
            Completed previous = finished.get(result);
            if (previous != null) {
                if (previous.configuration == configuration && previous.status.equals(status)) return;
                var failure = new IllegalStateException("TestNG delivered conflicting completion kinds or statuses");
                infrastructureFailure("conflictingCompletion", failure);
                throw failure;
            }
            Started beginning = active.remove(result);
            try {
                if (beginning == null && !configuration && !status.equals("skipped"))
                    throw new IllegalStateException("A completed test has no captured start identity");
                Identity identity = beginning == null ? identity(result) : beginning.identity;
                rememberClass(result.getMethod().getRealClass());
                if (beginning != null && beginning.configuration != configuration)
                    throw new IllegalStateException("TestNG changed an invocation's kind");
                long duration = beginning == null ? Math.max(0, result.getEndMillis() - result.getStartMillis())
                        : Math.max(0, (System.nanoTime() - beginning.nanos) / 1_000_000);
                long end = System.currentTimeMillis();
                classes.get(identity.className).completedAtEpochMs = end;
                var row = row(identity, configuration, status, duration, beginning == null ? end : beginning.epochMs, end, result.getThrowable());
                append(row, !status.equals("passed"));
                account(row);
                finished.put(result, new Completed(configuration, status));
            } catch (Throwable failure) { infrastructureFailure("completion", failure); throw new IllegalStateException(failure); }
        }

        private void rememberClass(Class<?> type) throws IOException {
            if (!classes.containsKey(type.getName())) {
                try (var bytes = type.getResourceAsStream("/" + type.getName().replace('.', '/') + ".class")) {
                    if (bytes == null) throw new IOException("Missing executed test class: " + type.getName());
                    classes.put(type.getName(), new ClassRun(ApiTestParameters.hash(bytes.readAllBytes())));
                }
            }
        }

        private static Identity identity(ITestResult result) {
            Method method = result.getMethod().getConstructorOrMethod().getMethod();
            if (method == null) throw new IllegalArgumentException("Constructor test identities are unsupported");
            return new Identity(result.getMethod().getRealClass().getName(), method.getName(),
                    Arrays.stream(method.getParameterTypes()).map(Class::getName).toList(),
                    ApiTestParameters.sha256(result.getParameters()));
        }

        private static Map<String, Object> row(Identity identity, boolean configuration, String status, long duration,
                                               long startEpochMs, long endEpochMs, Throwable error) {
            var row = new LinkedHashMap<String, Object>();
            row.put("kind", configuration ? "configuration" : "test");
            row.put("className", identity.className); row.put("methodName", identity.methodName);
            row.put("parameterTypes", identity.parameterTypes); row.put("parameterSha256", identity.parameterSha256);
            row.put("status", status); row.put("count", 1); row.put("durationMillis", duration);
            row.put("startedAtEpochMs", startEpochMs); row.put("completedAtEpochMs", endEpochMs);
            if (error != null) {
                var trace = new StringWriter(); error.printStackTrace(new PrintWriter(trace));
                var detail = new LinkedHashMap<String, Object>();
                detail.put("type", error.getClass().getName()); detail.put("message", error.getMessage());
                detail.put("stackTrace", trace.toString()); row.put("failure", detail);
            }
            return row;
        }

        private void account(Map<String, Object> row) {
            String status = (String) row.get("status");
            if (row.get("kind").equals("configuration")) {
                if (!status.equals("passed")) summary.compute(status.equals("failed") ? "configurationFailed" : "configurationSkipped", (key, count) -> count + 1);
                return;
            }
            summary.compute(status, (key, count) -> count + 1);
            var item = new LinkedHashMap<String, Object>();
            for (String key : List.of("className", "methodName", "parameterTypes", "parameterSha256", "status")) item.put(key, row.get(key));
            String key = JSON.toJson(item);
            var accumulated = invocations.get(key);
            if (accumulated == null) { item.put("count", 1); invocations.put(key, item); }
            else accumulated.put("count", (int) accumulated.get("count") + 1);
        }

        private synchronized void infrastructureFailure(String operation, Throwable failure) {
            long now = System.currentTimeMillis();
            var row = row(new Identity(ApiTestListener.class.getName(), operation, List.of(), ApiTestParameters.sha256(new Object[0])),
                    true, "failed", 0, now, now, failure);
            append(row, true); account(row);
        }

        private void append(Map<String, Object> row, boolean durable) {
            try {
                ByteBuffer bytes = StandardCharsets.UTF_8.encode(JSON.toJson(row) + "\n");
                while (bytes.hasRemaining()) journal.write(bytes);
                if (durable) journal.force(true);
            } catch (IOException failure) { throw new IllegalStateException("Cannot retain API invocation evidence", failure); }
        }
        private void force() {
            try { journal.force(true); }
            catch (IOException failure) { throw new IllegalStateException(failure); }
        }

        synchronized void finish() throws IOException {
            if (closed) return;
            if (executions != 0 || !active.isEmpty()) infrastructureFailure("incompleteExecution",
                    new IllegalStateException("TestNG left " + executions + " executions and " + active.size() + " invocations open"));
            if (!Objects.equals(metadata.get("testClassSetSha256"), classSetFingerprint(testClassRoot())))
                infrastructureFailure("changedClasses", new IllegalStateException("Compiled test classes changed during execution"));
            force(); journal.close();
            var receipt = new LinkedHashMap<>(metadata);
            receipt.put("completed", true);
            receipt.put("completedAtEpochMs", System.currentTimeMillis());
            receipt.put("elapsedNanos", System.nanoTime() - lifecycleStart);
            receipt.put("bootstrapNanos", bootstrapNanos);
            var peaks = new TreeMap<String, Long>();
            ManagementFactory.getMemoryPoolMXBeans().stream().filter(pool -> pool.getType() == MemoryType.HEAP)
                    .forEach(pool -> peaks.put(pool.getName(), pool.getPeakUsage().getUsed()));
            receipt.put("heapPoolPeakUsedBytes", peaks);
            receipt.put("executedClasses", classes.entrySet().stream().map(item -> Map.of("name", item.getKey(),
                    "sha256", item.getValue().sha256, "startedAtEpochMs", item.getValue().startedAtEpochMs,
                    "completedAtEpochMs", item.getValue().completedAtEpochMs)).toList());
            receipt.put("invocations", List.copyOf(invocations.values())); receipt.put("summary", Map.copyOf(summary));
            receipt.put("files", Map.of("startSha256", ApiTestParameters.hash(Files.readAllBytes(start)),
                    "journalSha256", ApiTestParameters.hash(Files.readAllBytes(journalPath))));
            Files.writeString(workspace.root.resolve("receipt.json"), JSON.toJson(receipt) + "\n", StandardCharsets.UTF_8, CREATE_NEW, WRITE);
            closed = true;
        }
    }

    static Path testClassRoot() {
        try {
            Path path = Path.of(ApiTestListener.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            if (!Files.isDirectory(path)) throw new IllegalStateException("API tests require a compiled class directory");
            return path;
        } catch (Exception failure) { throw new IllegalStateException(failure); }
    }

    static String classSetFingerprint(Path root) throws IOException {
        var inventory = new StringBuilder();
        try (var files = Files.walk(root)) {
            for (Path file : files.filter(path -> Files.isRegularFile(path) && path.getFileName().toString().endsWith(".class"))
                    .sorted(Comparator.comparing(path -> root.relativize(path).toString().replace('\\', '/'))).toList())
                inventory.append(root.relativize(file).toString().replace('\\', '/')).append('\0')
                        .append(ApiTestParameters.hash(Files.readAllBytes(file))).append('\n');
        }
        if (inventory.isEmpty()) throw new IllegalArgumentException("No compiled API test classes");
        return ApiTestParameters.hash(inventory.toString().getBytes(StandardCharsets.UTF_8));
    }
}
