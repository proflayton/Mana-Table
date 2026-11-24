using System.Text.Json;
using Mana.Forge;

namespace Mana.Table;

// Real child pipes and production request handling; these authored replies do
// not simulate Magic rules. Stderr output is finite and deliberately oversized.
internal static class ForgeDiagnosticsProtocolProbe
{
    internal const string Marker = "--forge-diagnostics-protocol-probe";
    internal const string Sentinel = "diagnostics-terminal-sentinel";
    internal static int Serve(string[] arguments)
    {
        int marker = Array.IndexOf(arguments, Marker);
        if (arguments[marker + 1] == "startup-failure") {
            Console.Error.Write("authored startup stderr"); Console.Error.Flush();
            Console.WriteLine(JsonSerializer.Serialize(new { @event = "error", message = "authored original startup failure" }));
            Console.Out.Flush(); return 2;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version,
            capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats") }));
        Console.Out.Flush();
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var request = JsonDocument.Parse(line);
            long id = request.RootElement.GetProperty("id").GetInt64();
            string method = request.RootElement.GetProperty("method").GetString()!;
            if (method == "automatedEnable") {
                string chunk = new('d', 2048);
                for (int index = 0; index < 1024; index++) Console.Error.Write(chunk);
                Console.Error.Write(Sentinel); Console.Error.Flush();
                Console.WriteLine(JsonSerializer.Serialize(new { id, result = new { enabled = true } }));
            } else Console.WriteLine(JsonSerializer.Serialize(new { id, result = new { id = "probe", name = "Probe" } }));
            Console.Out.Flush();
            if (method == "import") return 0;
        }
        return 0;
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        await EngineDiagnosticsChecks.RunAsync(check);
        await CheckDeniedOpen(profile, check);
        await CheckBlockedOpen(profile, check);
        await CheckBlockedWrite(profile, check);
        await CheckChildSinkFailures(profile, check);
        await CheckReplacement(profile, check);
        await CheckHealthyFile(profile, "normal-file", check);
        await CheckStartupFailure(profile, check);
    }

    private static async Task CheckDeniedOpen(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "diagnostics-denied-open");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "engine.log"));
        var engine = new ForgeEngine(Paths(path));
        Exception? firstFailure = null;
        try {
            await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await EnableAndDrain(engine);
            check(engine.DiagnosticStatus is { FailureStage: EngineDiagnosticStage.Opening, ReadCharacters: > 2 * 1024 * 1024 }
                    && engine.DiagnosticTail.EndsWith(Sentinel, StringComparison.Ordinal),
                "A denied engine.log open still drains 2 MiB of unterminated child stderr and receives its ordinary protocol reply");
        } catch (Exception error) { firstFailure = error; throw; }
        finally {
            try { await engine.DisposeAsync(); }
            catch when (firstFailure != null) { /* Preserve the original liveness witness. */ }
        }
    }

    private static async Task CheckBlockedOpen(string profile, Action<bool, string> check)
    {
        string path = NewProfile(profile, "blocked-open");
        var entered = EngineDiagnosticsChecks.NewSignal(); var release = EngineDiagnosticsChecks.NewSignal();
        var sink = new EngineDiagnosticsChecks.Sink();
        IEngineDiagnosticSink Open(string _) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return sink; }
        var engine = new ForgeEngine(Paths(path), null, Open);
        try {
            await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await EnableAndDrain(engine);
            check(engine.DiagnosticStatus is { QueueHighWater: EngineDiagnostics.QueueCapacity, DroppedCharacters: > 0 }
                    && engine.DiagnosticTail.EndsWith(Sentinel, StringComparison.Ordinal),
                "A synchronous blocked sink constructor cannot delay child ready, saturate stderr, or suppress a normal reply");
        } finally { release.TrySetResult(); await engine.DisposeAsync(); }
    }

    private static async Task CheckBlockedWrite(string profile, Action<bool, string> check)
    {
        string path = NewProfile(profile, "blocked-write");
        var sink = new EngineDiagnosticsChecks.Sink(hold: EngineDiagnosticStage.Writing, synchronousHold: true);
        var engine = new ForgeEngine(Paths(path), null, _ => sink);
        try {
            await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await EnableAndDrain(engine);
            check(engine.DiagnosticStatus is { PendingChunks: <= EngineDiagnostics.QueueCapacity, DroppedCharacters: > 0 }
                    && engine.DiagnosticTail.Length == EngineDiagnostics.TailCharacters && engine.DiagnosticTail.EndsWith(Sentinel, StringComparison.Ordinal),
                "A noncooperative synchronous file write leaves a noisy real child responsive and diagnostic memory bounded");
        } finally { sink.Release.TrySetResult(); await engine.DisposeAsync(); }
        check(engine.DiagnosticStatus is { EndOfStream: true, DrainCompleted: true, RetirementAbandoned: false }
                && engine.DiagnosticTail.Length == 0 && sink.Text.Contains("diagnostics omitted", StringComparison.Ordinal),
            "After releasing its sink, real pipe EOF and file cleanup retire normally and private tail reads become empty");
    }

    private static async Task CheckChildSinkFailures(string profile, Action<bool, string> check)
    {
        foreach (var stage in new[] { EngineDiagnosticStage.Writing, EngineDiagnosticStage.Flushing, EngineDiagnosticStage.Disposing }) {
            var sink = new EngineDiagnosticsChecks.Sink(fail: stage);
            var engine = new ForgeEngine(Paths(NewProfile(profile, "child-failure-" + stage)), null, _ => sink);
            try {
                await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await EnableAndDrain(engine);
                await EnableAndDrain(engine);
                check(engine.DiagnosticTail.EndsWith(Sentinel, StringComparison.Ordinal),
                    "An actual child continues ordinary replies after a diagnostic " + stage + " failure control");
            } finally { await engine.DisposeAsync(); }
            check(engine.DiagnosticStatus is { DrainCompleted: true, EndOfStream: true, RetirementAbandoned: false } status
                    && status.FailureStage == stage,
                "Real child shutdown preserves the original " + stage + " diagnostic category without failing the provider");
        }
    }

    private static async Task CheckReplacement(string profile, Action<bool, string> check)
    {
        var clock = new EngineDiagnosticsChecks.Clock();
        var oldSink = new EngineDiagnosticsChecks.Sink(hold: EngineDiagnosticStage.Writing, failAfterRelease: true);
        int factories = 0;
        IEngineDiagnosticSink Open(string _) { Interlocked.Increment(ref factories); return oldSink; }
        var engine = new ForgeEngine(Paths(NewProfile(profile, "replacement")), null, Open, clock);
        try {
            await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await oldSink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await EnableAndDrain(engine);
            int first = engine.DiagnosticStatus!.ProcessId;
            await engine.ImportAsync("retire", "protocol only").WaitAsync(TimeSpan.FromSeconds(5));
            await EngineDiagnosticsChecks.Until(() => engine.DiagnosticStatus?.EndOfStream == true, "A's actual stderr EOF");
            var replacement = engine.InitializeAsync();
            await EngineDiagnosticsChecks.Until(() => clock.PendingTimers == 1, "A's one diagnostic retirement budget");
            clock.Advance(EngineDiagnostics.RetirementBudget);
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            await EnableAndDrain(engine);
            var beforeLate = engine.DiagnosticStatus!;
            check(beforeLate.ProcessId != first && beforeLate.LoggingSuppressed && factories == 1
                    && beforeLate.ReadCharacters > 2 * 1024 * 1024 && engine.DiagnosticTail.EndsWith(Sentinel, StringComparison.Ordinal),
                "After one abandoned A writer, replacement B drains real stderr and replies without constructing another file worker");
            oldSink.Release.TrySetResult(); await oldSink.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(engine.DiagnosticStatus == beforeLate && factories == 1,
                "A's late sink failure cannot change B's process, diagnostic evidence or monotonic logging suppression");
            await EnableAndDrain(engine);
            check(engine.DiagnosticStatus!.ProcessId == beforeLate.ProcessId && factories == 1,
                "B keeps accepting fresh ordinary requests after A's late failure, with no old command replay or new log worker");
            await engine.ImportAsync("retire again", "protocol only").WaitAsync(TimeSpan.FromSeconds(5));
            await EngineDiagnosticsChecks.Until(() => engine.DiagnosticStatus?.EndOfStream == true, "B's actual stderr EOF");
            await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await EnableAndDrain(engine);
            check(engine.DiagnosticStatus is { LoggingSuppressed: true } third
                    && third.ProcessId != beforeLate.ProcessId && factories == 1,
                "A third actual process still drains and replies without rebuilding a log worker after the original abandoned writer finishes");
            await CheckHealthyFile(profile, "independent-instance", check);
        } finally { oldSink.Release.TrySetResult(); await engine.DisposeAsync(); }
        var frozen = engine.DiagnosticStatus;
        check(engine.DiagnosticStatus == frozen && engine.DiagnosticTail == "" && clock.PendingTimers == 0,
            "Disposed provider reads retain only frozen current evidence and no private tail or live retirement timer");
    }

    private static async Task CheckHealthyFile(string profile, string name, Action<bool, string> check)
    {
        string path = NewProfile(profile, name);
        var engine = new ForgeEngine(Paths(path));
        try {
            await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await EnableAndDrain(engine);
        } finally { await engine.DisposeAsync(); }
        check(engine.DiagnosticStatus is { EndOfStream: true, DrainCompleted: true, RetirementAbandoned: false, LoggingSuppressed: false }
                && File.ReadAllText(Path.Combine(path, "engine.log")).Contains(Sentinel, StringComparison.Ordinal),
            "The " + name + " engine uses real file logging and retires its actual stderr pipe, independently of another instance's suppression");
    }

    private static async Task CheckStartupFailure(string profile, Action<bool, string> check)
    {
        var sink = new EngineDiagnosticsChecks.Sink(fail: EngineDiagnosticStage.Disposing);
        var engine = new ForgeEngine(Paths(NewProfile(profile, "startup-failure"), "startup-failure"), null, _ => sink);
        Exception? failure = null;
        try { await engine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) { failure = error; }
        finally { await engine.DisposeAsync(); }
        check(failure is IOException && failure.Message == "authored original startup failure"
                && engine.DiagnosticStatus is { CleanupFailed: true, FailureStage: EngineDiagnosticStage.Disposing },
            "A real child's original startup error survives diagnostic disposal failure and remains separate from private sink status");
    }

    private static async Task EnableAndDrain(ForgeEngine engine)
    {
        long expected = (engine.DiagnosticStatus?.ReadCharacters ?? 0) + 2 * 1024 * 1024 + Sentinel.Length;
        await engine.EnableAutomatedSeatsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // Stdout acknowledgement and the last stderr read travel on independent
        // pipes. Observe the finite burst's actual drain before inspecting it.
        await EngineDiagnosticsChecks.Until(() => engine.DiagnosticStatus?.ReadCharacters == expected
            && engine.DiagnosticTail.EndsWith(Sentinel, StringComparison.Ordinal), "the complete authored stderr burst");
    }

    private static string NewProfile(string profile, string name)
    {
        string path = Path.Combine(profile, "diagnostics-" + name); Directory.CreateDirectory(path); return path;
    }
    internal static ForgePaths Paths(string profile, string mode = "diagnostics") => new(
        Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, profile);
}
