using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class ClientDiagnosticJournalRegression
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly AppBuild Build = new("0.2.290", true, new string('A', 40));

    internal static async Task RunAsync(Action<bool, string> check)
    {
        string temporary = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temporary, "mana-client-diagnostics-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        bool passed = false;
        try {
            string identity = Profile(root, "identity");
            await using var journal = new ClientDiagnosticJournal(identity, Build);
            journal.Record(Sample(2));
            await Finish(journal);
            var identityLines = Read(Current(identity));
            var header = identityLines[0];
            check(header.GetProperty("type").GetString() == "header" && header.GetProperty("schema").GetInt32() == 1
                    && header.GetProperty("version").GetString() == Build.Version
                    && header.GetProperty("sourceRevision").GetString() == Build.SourceRevision.ToLowerInvariant()
                    && header.GetProperty("release").GetBoolean(),
                "The local decision journal starts with validated build identity and schema rather than raw app metadata");
            check(header.EnumerateObject().Count() == 7
                    && Guid.TryParseExact(header.GetProperty("runId").GetString(), "N", out _)
                    && header.GetProperty("startedAt").GetDateTimeOffset().Offset == TimeSpan.Zero,
                "The diagnostic header contains only build identity, a generated local run identity and UTC start time");
            var entry = identityLines[1];
            var diagnostic = entry.GetProperty("diagnostic");
            check(entry.GetProperty("sequence").GetInt64() == 1
                    && entry.GetProperty("recordedAt").GetDateTimeOffset().Offset == TimeSpan.Zero
                    && diagnostic.GetProperty("stage").GetString() == "Admitted"
                    && diagnostic.GetProperty("command").GetString() == "ChooseNumber"
                    && diagnostic.GetProperty("submittedNumber").GetInt32() == 2
                    && diagnostic.GetProperty("facts").GetProperty("decisionSourceName").GetString() == "Crackle with Power",
                "A persisted diagnostic preserves its allowlisted facts with readable stage and command names");
            journal.Record(Sample(999));
            check(!await journal.FlushAsync().WaitAsync(Deadline) && Read(Current(identity)).Length == 2,
                "Closing the journal rejects later records and completes later flush requests without changing its evidence");

            string rotation = Profile(root, "rotation");
            await using var rotating = new ClientDiagnosticJournal(rotation, Build);
            for (int i = 0; i < 63; i++) rotating.Record(Sample(i));
            check(await rotating.FlushAsync().WaitAsync(Deadline), "The actual writer flushes the first 63 records before the rotation boundary");
            rotating.Record(Sample(63)); rotating.Record(Sample(64));
            check(await rotating.FlushAsync().WaitAsync(Deadline), "The same writer crosses its 64th and 65th record without a queue overflow");
            for (int i = 65; i < 128; i++) rotating.Record(Sample(i));
            check(await rotating.FlushAsync().WaitAsync(Deadline), "A subsequent full file reaches its retention boundary through the same queue");
            rotating.Record(Sample(128));
            await Finish(rotating);
            var previous = Read(Previous(rotation));
            var current = Read(Current(rotation));
            check(Numbers(previous).SequenceEqual(Enumerable.Range(64, 64)) && Numbers(current).SequenceEqual([128]),
                "Repeated in-process rotation retains exactly the newest 64-record previous file and the current record");
            check(previous[0].GetProperty("runId").GetString() == current[0].GetProperty("runId").GetString()
                    && Directory.GetFiles(Path.Combine(rotation, "diagnostics")).Length == 2,
                "Rotation writes the build header in each file and retains only one previous file for the same local run");
            Bounds(rotation, check);
            await using var restarted = new ClientDiagnosticJournal(rotation, Build with { Version = "0.2.291" });
            restarted.Record(Sample(200)); await Finish(restarted);
            check(Numbers(Read(Previous(rotation))).SequenceEqual([128])
                    && Read(Current(rotation))[0].GetProperty("version").GetString() == "0.2.291"
                    && Read(Current(rotation))[0].GetProperty("runId").GetString() != current[0].GetProperty("runId").GetString(),
                "A new local run replaces the older retained file and writes its own exact build and run identity");

            string overflow = Profile(root, "overflow");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var queued = new ClientDiagnosticJournal(overflow, Build, start.Task);
            for (int i = 0; i < 74; i++) queued.Record(Sample(i));
            check(!Directory.Exists(Path.Combine(overflow, "diagnostics")) && !queued.Completion.IsCompleted,
                "Recording a full burst returns while the actual filesystem worker is still gated");
            Task close = queued.DisposeAsync().AsTask();
            queued.Record(Sample(999));
            start.SetResult(); await close.WaitAsync(Deadline); await queued.Completion.WaitAsync(Deadline);
            var queuedLines = Read(Current(overflow));
            check(Numbers(queuedLines).SequenceEqual(Enumerable.Range(10, 64)),
                "A blocked writer retains the newest 64 events and rejects a record submitted after close");
            check(queuedLines[1].GetProperty("droppedSincePreviousWrite").GetInt64() == 10
                    && queuedLines[1].GetProperty("sequence").GetInt64() == 11
                    && queuedLines[^1].GetProperty("sequence").GetInt64() == 74,
                "Queue overflow records the ten lost events and an honest accounting sequence gap");
            Bounds(overflow, check);

            string barrierProfile = Profile(root, "dropped-barrier");
            var barrierStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var barriers = new ClientDiagnosticJournal(barrierProfile, Build, barrierStart.Task);
            Task<bool> droppedBarrier = barriers.FlushAsync();
            for (int i = 0; i < 64; i++) barriers.Record(Sample(i));
            check(!await droppedBarrier.WaitAsync(Deadline), "Dropping an internal same-queue barrier completes its waiter immediately");
            Task barrierClose = barriers.DisposeAsync().AsTask();
            barrierStart.SetResult(); await barrierClose.WaitAsync(Deadline); await barriers.Completion.WaitAsync(Deadline);
            check(Numbers(Read(Current(barrierProfile))).Count() == 64
                    && Read(Current(barrierProfile)).Skip(1).All(row => row.GetProperty("droppedSincePreviousWrite").GetInt64() == 0),
                "A dropped test barrier neither loses real records nor invents an event loss");

            string bounded = Profile(root, "bounded-fields");
            await using var normalizing = new ClientDiagnosticJournal(bounded, new AppBuild(new string('v', 20_000), true, new string('s', 20_000)));
            var oversized = Sample(3) with { Facts = Sample(3).Facts with {
                DecisionSourceName = new string('q', 20_000),
                RemainingMana = Enumerable.Repeat(new ClientDiagnosticManaPart("{2147483647}", int.MaxValue), 128).ToImmutableArray()
            } };
            normalizing.Record(oversized);
            check(await normalizing.FlushAsync().WaitAsync(Deadline), "A bounded caller event reaches durable storage before a later invalid outcome");
            normalizing.Record(Sample(4) with { Stage = (ClientDiagnosticStage)int.MaxValue });
            normalizing.Record(Sample(5));
            normalizing.Record(Sample(6) with { Facts = Sample(6).Facts with {
                RemainingMana = [new("arbitrary", 1), new("{U}", 0), new("{R}", 1)]
            } });
            await Finish(normalizing);
            var boundedLines = Read(Current(bounded));
            var boundedFacts = boundedLines[1].GetProperty("diagnostic").GetProperty("facts");
            check(boundedLines[0].GetProperty("version").ValueKind == JsonValueKind.Null
                    && boundedLines[0].GetProperty("sourceRevision").ValueKind == JsonValueKind.Null
                    && boundedLines[1].GetProperty("inputTruncated").GetBoolean()
                    && boundedFacts.GetProperty("decisionSourceName").ValueKind == JsonValueKind.Null
                    && boundedFacts.GetProperty("remainingMana").GetArrayLength() == 32
                    && boundedFacts.GetProperty("manaBillUnknownOrTruncated").GetBoolean(),
                "Oversized caller fields are bounded or omitted and marked incomplete without persisting raw build strings");
            check(Numbers(boundedLines).SequenceEqual([3, 5, 6])
                    && boundedLines[2].GetProperty("droppedSincePreviousWrite").GetInt64() == 1
                    && boundedLines[2].GetProperty("sequence").GetInt64() == 3,
                "An undefined stage is dropped and counted instead of being rewritten as a valid command outcome");
            var boundedMana = boundedLines[3].GetProperty("diagnostic").GetProperty("facts");
            check(boundedMana.GetProperty("remainingMana").GetArrayLength() == 1
                    && boundedMana.GetProperty("remainingMana")[0].GetProperty("symbol").GetString() == "{R}"
                    && boundedMana.GetProperty("manaBillUnknownOrTruncated").GetBoolean(),
                "Noncanonical mana text and nonpositive quantities are omitted through the shared strict validator");
            Bounds(bounded, check);

            string prior = Profile(root, "oversized-priors");
            Directory.CreateDirectory(Path.Combine(prior, "diagnostics"));
            await File.WriteAllBytesAsync(Current(prior), new byte[ClientDiagnosticJournal.MaximumFileBytes + 1]);
            string[] seed = File.ReadAllLines(Current(identity));
            await File.WriteAllLinesAsync(Previous(prior), new[] { seed[0] }.Concat(Enumerable.Repeat(seed[1], 65)));
            await using var replacing = new ClientDiagnosticJournal(prior, Build);
            replacing.Record(Sample(6)); await Finish(replacing);
            check(!File.Exists(Previous(prior)) && Numbers(Read(Current(prior))).SequenceEqual([6]),
                "Startup removes prior files exceeding either the byte cap or record cap instead of retaining oversized evidence");
            Bounds(prior, check);

            string interrupted = Profile(root, "interrupted-write");
            Directory.CreateDirectory(Path.Combine(interrupted, "diagnostics"));
            string partial = string.Join('\n', seed) + "\n{\"type\":\"event\",\"sequence\":2,";
            await File.WriteAllTextAsync(Current(interrupted), partial);
            await using var afterCrash = new ClientDiagnosticJournal(interrupted, Build);
            afterCrash.Record(Sample(9)); await Finish(afterCrash);
            check(await File.ReadAllTextAsync(Previous(interrupted)) == partial
                    && Numbers(Read(Current(interrupted))).SequenceEqual([9]),
                "Restart preserves the complete prefix and bounded partial final line from an interrupted write");
            Bounds(interrupted, check);

            string failure = Path.Combine(root, "not-a-directory");
            await File.WriteAllTextAsync(failure, "unchanged");
            var failureStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var failing = new ClientDiagnosticJournal(failure, Build, failureStart.Task);
            Task<bool> failedFlush = failing.FlushAsync();
            failing.Record(Sample(7)); failureStart.SetResult();
            await failing.Completion.WaitAsync(Deadline);
            failing.Record(Sample(8)); await failing.DisposeAsync();
            check(!await failedFlush.WaitAsync(Deadline) && await File.ReadAllTextAsync(failure) == "unchanged",
                "A real filesystem failure completes queued waiters, disables the journal and never escapes into gameplay");

            string cancellation = Profile(root, "bounded-close");
            var neverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var stopping = new ClientDiagnosticJournal(cancellation, Build, neverStarted.Task);
            Task<bool> abandoned = stopping.FlushAsync();
            var elapsed = Stopwatch.StartNew(); stopping.Dispose();
            elapsed.Stop();
            await stopping.Completion.WaitAsync(Deadline);
            check(elapsed.Elapsed < Deadline && !await abandoned.WaitAsync(Deadline),
                "Synchronous exit cancels a stalled writer after its bounded wait and releases queued completion waiters");
            passed = true;
        } finally {
            if (!passed) Console.Error.WriteLine("Retained client diagnostic journal regression evidence: " + root);
            else if (Directory.Exists(root)) {
                // Verify the final absolute target before recursive cleanup.
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                string parent = Directory.GetParent(root)?.FullName ?? "";
                if (!string.Equals(parent, temporary.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison)
                    || !Path.GetFileName(root).StartsWith("mana-client-diagnostics-", StringComparison.Ordinal))
                    throw new InvalidOperationException("The diagnostic test directory is outside its temporary parent.");
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task Finish(ClientDiagnosticJournal journal)
    {
        await journal.DisposeAsync();
        await journal.Completion.WaitAsync(Deadline);
    }

    private static ClientDiagnostic Sample(int number) => new(1, ClientDiagnosticOrigin.Direct,
        ClientDiagnosticStage.Admitted, ClientDiagnosticCommand.ChooseNumber, ClientDiagnosticFailure.None, number,
        new(true, 0, 10, ClientDiagnosticDecision.Number, 0, ExecutionStep.Choice, "Crackle with Power",
            1, true, false, [new("{6}", 1), new("{R}", 2)], false));

    private static string Profile(string root, string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    private static string Current(string profile) => Path.Combine(profile, "diagnostics", "client-decisions.jsonl");
    private static string Previous(string profile) => Path.Combine(profile, "diagnostics", "client-decisions.previous.jsonl");
    private static JsonElement[] Read(string path) => File.ReadAllLines(path).Select(line => {
        using var json = JsonDocument.Parse(line); return json.RootElement.Clone();
    }).ToArray();
    private static IEnumerable<int> Numbers(JsonElement[] lines) => lines.Skip(1)
        .Where(row => row.GetProperty("type").GetString() == "event")
        .Select(row => row.GetProperty("diagnostic").GetProperty("submittedNumber").GetInt32());

    private static void Bounds(string profile, Action<bool, string> check)
    {
        var paths = Directory.GetFiles(Path.Combine(profile, "diagnostics"), "client-decisions*.jsonl");
        check(paths.Length is >= 1 and <= 2 && paths.All(path => new FileInfo(path).Length <= ClientDiagnosticJournal.MaximumFileBytes
                && File.ReadAllLines(path).Length <= ClientDiagnosticJournal.MaximumEvents + 1
                && File.ReadAllLines(path).All(line => Encoding.UTF8.GetByteCount(line) + 1 <= ClientDiagnosticJournal.MaximumEntryBytes)),
            "Every retained diagnostic file and record respects the fixed byte, count and line limits: " + Path.GetFileName(profile));
    }
}
