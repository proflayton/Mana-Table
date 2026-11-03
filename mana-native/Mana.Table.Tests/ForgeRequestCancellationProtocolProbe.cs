using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Real child pipes and codec, authored public state only. No rules simulation,
// real decks, network service, renderer or user profile participates.
internal static class ForgeRequestCancellationProtocolProbe
{
    internal const string Marker = "--forge-request-cancellation-protocol-probe";
    private const string Game = "request-cancellation-game";
    private const int Seat = 29;

    internal static int Serve(string[] arguments)
    {
        int marker = Array.IndexOf(arguments, Marker);
        string mode = arguments[marker + 1], profile = Path.GetDirectoryName(arguments[marker + 2])!;
        Directory.CreateDirectory(profile);
        void Reply(long id, object result) {
            Console.WriteLine(JsonSerializer.Serialize(new { id, result })); Console.Out.Flush();
        }
        void Signal(string name) => File.WriteAllText(Path.Combine(profile, name), "ready\n", Encoding.UTF8);
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version,
            capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats") }));
        Console.Out.Flush();
        int enables = 0, observations = 0, actions = 0;
        long? heldObservation = null, heldCommand = null;
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            long id = request.GetProperty("id").GetInt64();
            string method = request.GetProperty("method").GetString()!;
            File.AppendAllText(Path.Combine(profile, "probe-events.jsonl"), JsonSerializer.Serialize(new {
                id, method, characters = line.Length }) + "\n", Encoding.UTF8);
            if (method == "automatedEnable") {
                Reply(id, new { enabled = true });
                if (++enables == 2 && mode.StartsWith("blocked-", StringComparison.Ordinal)) {
                    // The parent sends more than an anonymous pipe can buffer.
                    // The child does not read even a partial next request yet.
                    Signal("not-reading.txt");
                    var deadline = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(profile, "resume-reading.txt"))) {
                        if (deadline.Elapsed > TimeSpan.FromSeconds(15)) return 3;
                        Thread.Sleep(5);
                    }
                }
                continue;
            }
            if (method == "import") { Reply(id, new { id = "authored-bulk-deck", name = "Transport bytes only" }); continue; }
            if (method == "automatedState") {
                observations++;
                if (observations == 2 && mode is "late-observation" or "blocked-shared-budget") {
                    heldObservation = id; Signal("held-observation.txt"); continue;
                }
                if (heldCommand is { } oldCommand) { Reply(oldCommand, new { accepted = true }); heldCommand = null; }
                Reply(id, Owned(observations == 1 ? "baseline-priority" : "current-priority"));
                continue;
            }
            if (method == "automatedAction") {
                if (++actions == 1 && mode == "late-command") {
                    heldCommand = id; Signal("held-command.txt"); continue;
                }
                if (heldObservation is { } oldObservation) {
                    Reply(oldObservation, Owned("canceled-priority")); heldObservation = null;
                    Signal("late-observation-sent.txt");
                }
                Reply(id, new { accepted = true }); continue;
            }
            Reply(id, new { acknowledged = true });
        }
        return 0;
    }

    private static JsonNode Owned(string decision)
    {
        var state = ConfiguredSimulationWireChecks.State(); state["id"] = Game; state["viewerId"] = Seat;
        state["prompt"] = JsonSerializer.SerializeToNode(new {
            id = decision, kind = "input", task = "priority", ok = "Pass", okEnabled = true });
        return new JsonArray(new JsonObject { ["seatId"] = Seat, ["state"] = state,
            ["domain"] = JsonNode.Parse("""{"version":0,"coverage":"NotApplicable","sources":[],"actions":[]}"""),
            ["semantics"] = new JsonObject() });
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        await CheckQueuedCancellation(profile, check);
        await CheckInterruptedWrite(profile, check);
        await CheckSharedDeadline(profile, check);
        await CheckLateObservation(profile, check);
        await CheckLateCommand(profile, check);
    }

    private static async Task CheckQueuedCancellation(string profile, Action<bool, string> check)
    {
        await using var probe = await Probe.Start(profile, "blocked-cancel");
        await probe.BlockInput();
        var large = probe.Engine.ImportAsync("Transport bytes only", new string('x', 1024 * 1024));
        await Until(() => probe.Clock.PendingTimers == 1, "the blocked write's one request deadline");
        using var cancellation = new CancellationTokenSource();
        var waiting = probe.Engine.ObserveAutomatedSeatsAsync(cancellation.Token);
        await Until(() => probe.Clock.PendingTimers == 2, "the second request waiting behind the actual blocked pipe");
        check(!large.IsCompleted && probe.Count("import") == 0,
            "A child that has stopped reading stdin holds the actual large request before its complete frame reaches the provider");
        cancellation.Cancel();
        check(await Failure(waiting) is OperationCanceledException && !large.IsCompleted,
            "Caller cancellation interrupts the second automated observation while it waits at the write gate");
        probe.ResumeInput();
        await large.WaitAsync(TimeSpan.FromSeconds(5));
        var owned = (await probe.Engine.ObserveAutomatedSeatsAsync()).Single();
        await probe.Engine.ExecuteAutomatedAsync(owned.SeatId, new PassPriority(owned.State.Id, owned.State.Decision!.Id));
        check(probe.Count("automatedState") == 2 && probe.Count("import") == 1 && probe.Count("automatedAction") == 1,
            "A request canceled before writing never reaches the child or replays, and intact framing still permits a fresh owned response");
    }

    private static async Task CheckInterruptedWrite(string profile, Action<bool, string> check)
    {
        await using var probe = await Probe.Start(profile, "blocked-deadline");
        await probe.BlockInput();
        var large = probe.Engine.ImportAsync("Transport bytes only", new string('x', 1024 * 1024));
        await Until(() => probe.Clock.PendingTimers == 1, "the interrupted write's deadline timer");
        probe.Clock.Advance(TimeSpan.FromSeconds(44));
        check(!large.IsCompleted && probe.Count("import") == 0,
            "The transport's existing 45-second deadline does not expire a blocked real pipe at 44 virtual seconds");
        // Start this request at 44 seconds so its own deadline cannot explain
        // failure when the first writer poisons the connection a second later.
        var queued = probe.Engine.ObserveAutomatedSeatsAsync();
        await Until(() => probe.Clock.PendingTimers == 2, "the uncanceled observation already queued behind the interrupted writer");
        check(!queued.IsCompleted,
            "A second uncanceled request is waiting at the real write gate before the first writer's deadline expires");
        probe.Clock.Advance(TimeSpan.FromSeconds(1));
        check(await Failure(large) is EngineRequestTimeoutException { Operation: "engine-request", Phase: EngineRequestPhase.Writing },
            "The same 45-second deadline identifies the ordinary request's actual blocked Writing phase without exposing its arguments");
        var queuedFailure = await Failure(queued);
        var rejected = await Failure(probe.Engine.ObserveAutomatedSeatsAsync());
        check(queuedFailure is IOException && rejected is IOException && probe.Count("automatedState") == 1 && probe.Count("import") == 0,
            "Interrupted framing rejects both the already-queued uncanceled observer and a future observer before dispatch, without waiting for their own deadlines");
    }

    private static async Task CheckSharedDeadline(string profile, Action<bool, string> check)
    {
        await using var probe = await Probe.Start(profile, "blocked-shared-budget");
        await probe.BlockInput();
        var large = probe.Engine.ImportAsync("Transport bytes only", new string('x', 1024 * 1024));
        await Until(() => probe.Clock.PendingTimers == 1, "the first request's gate-holding deadline");
        var waiting = probe.Engine.ObserveAutomatedSeatsAsync();
        await Until(() => probe.Clock.PendingTimers == 2, "both existing request deadline timers");
        probe.Clock.Advance(TimeSpan.FromSeconds(30));
        probe.ResumeInput();
        await large.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => probe.Signal("held-observation.txt"), "the second request reaching its reply phase after the gate opens");
        probe.Clock.Advance(TimeSpan.FromSeconds(14));
        check(!waiting.IsCompleted, "A request remains pending at 44 total virtual seconds across gate, write, flush and reply");
        probe.Clock.Advance(TimeSpan.FromSeconds(1));
        check(await Failure(waiting) is EngineRequestTimeoutException { Operation: "automation-observe", Phase: EngineRequestPhase.Reply },
            "One 45-second budget identifies automation-observe Reply expiry after 30 seconds at the gate and 15 awaiting the reply, without resetting or exposing arguments");
        var current = (await probe.Engine.ObserveAutomatedSeatsAsync()).Single();
        await probe.Engine.ExecuteAutomatedAsync(current.SeatId, new PassPriority(current.State.Id, current.State.Decision!.Id));
        await probe.Engine.ExecuteAutomatedAsync(current.SeatId, new PassPriority(current.State.Id, current.State.Decision!.Id));
        check(probe.Signal("late-observation-sent.txt") && probe.Count("automatedState") == 3 && probe.Count("automatedAction") == 2,
            "A reply-wait timeout preserves intact framing, and its late observation cannot replace the newly accepted automated scope");
    }

    private static async Task CheckLateObservation(string profile, Action<bool, string> check)
    {
        await using var probe = await Probe.Start(profile, "late-observation");
        using var cancellation = new CancellationTokenSource();
        var old = probe.Engine.ObserveAutomatedSeatsAsync(cancellation.Token);
        await Until(() => probe.Signal("held-observation.txt"), "the canceled observation arriving at the child");
        cancellation.Cancel();
        check(await Failure(old) is OperationCanceledException,
            "An automated observation waiting for a complete reply honors caller cancellation");
        var current = (await probe.Engine.ObserveAutomatedSeatsAsync()).Single();
        await probe.Engine.ExecuteAutomatedAsync(current.SeatId, new PassPriority(current.State.Id, current.State.Decision!.Id));
        var stale = await Failure(probe.Engine.ExecuteAutomatedAsync(Seat, new PassPriority(Game, "canceled-priority")));
        await probe.Engine.ExecuteAutomatedAsync(current.SeatId, new PassPriority(current.State.Id, current.State.Decision!.Id));
        check(stale is EngineCommandRejectedException && old.IsCanceled && probe.Signal("late-observation-sent.txt")
                && probe.Count("automatedAction") == 2 && probe.Count("automatedState") == 3,
            "A canceled late frame installs no stale decision authority and cannot erase or replace a fresh accepted automated-seat scope");
    }

    private static async Task CheckLateCommand(string profile, Action<bool, string> check)
    {
        await using var probe = await Probe.Start(profile, "late-command");
        var before = probe.Initial;
        using var cancellation = new CancellationTokenSource();
        var command = probe.Engine.ExecuteAutomatedAsync(before.SeatId,
            new PassPriority(before.State.Id, before.State.Decision!.Id), cancellation.Token);
        await Until(() => probe.Signal("held-command.txt"), "the accepted command's held reply");
        cancellation.Cancel();
        check(await Failure(command) is OperationCanceledException && probe.Count("automatedAction") == 1,
            "Canceling an automated command after its complete frame was dispatched returns without resubmitting that uncertain action");
        var current = (await probe.Engine.ObserveAutomatedSeatsAsync()).Single();
        var oldScope = await Failure(probe.Engine.ExecuteAutomatedAsync(before.SeatId,
            new PassPriority(before.State.Id, before.State.Decision!.Id)));
        await probe.Engine.ExecuteAutomatedAsync(current.SeatId, new PassPriority(current.State.Id, current.State.Decision!.Id));
        using var alreadyCanceled = new CancellationTokenSource(); alreadyCanceled.Cancel();
        var notSent = await Failure(probe.Engine.ExecuteAutomatedAsync(current.SeatId,
            new PassPriority(current.State.Id, current.State.Decision!.Id), alreadyCanceled.Token));
        await probe.Engine.EnableAutomatedSeatsAsync(); // Child barrier after the late acknowledgement.
        check(oldScope is EngineCommandRejectedException && notSent is OperationCanceledException
                && probe.Count("automatedAction") == 2 && command.IsCanceled,
            "A late command acknowledgement creates no replay or stale scope; only one explicit newly observed response reaches the still-usable provider");
    }

    private static async Task Until(Func<bool> ready, string description)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        if (!ready()) throw new TimeoutException("Protocol fixture did not reach " + description);
    }

    private static async Task<Exception?> Failure(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); return null; }
        catch (TimeoutException error) when (!operation.IsCompleted) {
            throw new InvalidOperationException("The protocol operation did not honor the signaled cancellation or virtual deadline.", error);
        }
        catch (Exception error) { return error; }
    }

    private sealed class Probe(string path, ForgeEngine engine, Clock clock, AutomatedSeatObservation initial) : IAsyncDisposable
    {
        internal ForgeEngine Engine => engine;
        internal Clock Clock => clock;
        internal AutomatedSeatObservation Initial => initial;
        internal bool Signal(string name) => File.Exists(Path.Combine(path, name));
        internal void ResumeInput() => File.WriteAllText(Path.Combine(path, "resume-reading.txt"), "resume\n", Encoding.UTF8);
        internal async Task BlockInput() {
            await engine.EnableAutomatedSeatsAsync();
            await Until(() => Signal("not-reading.txt"), "the child explicitly ceasing stdin reads");
        }
        internal int Count(string method) {
            string events = Path.Combine(path, "probe-events.jsonl");
            if (!File.Exists(events)) return 0;
            using var stream = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');
            int count = 0;
            // Ignore any last line still being appended by the child. Only a
            // complete newline-terminated receipt establishes dispatch evidence.
            foreach (var line in lines.Take(lines.Length - 1)) {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.GetProperty("method").GetString() == method) count++;
            }
            return count;
        }
        internal static async Task<Probe> Start(string profile, string mode) {
            string path = Path.Combine(profile, "request-cancellation-" + mode);
            Directory.CreateDirectory(path);
            if (Directory.EnumerateFiles(path).Any()) throw new IOException("Protocol regression requires a fresh private fixture profile.");
            var clock = new Clock();
            var executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
            var engine = new ForgeEngine(new(executable, Marker, mode, path), clock);
            try {
                await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
                var initial = (await engine.ObserveAutomatedSeatsAsync()).Single();
                return new(path, engine, clock, initial);
            } catch { await engine.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { ResumeInput(); await engine.DisposeAsync(); }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() { lock (gate) return DateTimeOffset.UnixEpoch.AddTicks(ticks); }
        internal int PendingTimers { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer(this, callback, state, dueTime, period);
        internal void Advance(TimeSpan elapsed) {
            List<Timer> ready;
            lock (gate) {
                ticks += elapsed.Ticks;
                ready = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer : ITimer
        {
            private readonly Clock clock;
            internal readonly TimerCallback Callback;
            internal readonly object? State;
            internal long Due;
            internal TimeSpan Period;
            private bool disposed;
            internal Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) {
                this.clock = clock; Callback = callback; State = state;
                lock (clock.gate) { clock.timers.Add(this); Change(due, period); }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) {
                lock (clock.gate) {
                    if (disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                    Period = period; return true;
                }
            }
            public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
