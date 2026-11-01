using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Mana.Client;
using Mana.Contracts;

internal static class MatchHistoryArchiveRegression
{
    private static readonly AppBuild Build = new("0.2.317", true, new string('a', 40));
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var mixed = PrivacyBatch().Records.AddRange(new HistoryRecord[] {
            new HistoryRecord(6, 57, "MAIN1", "public") { ExecutionId = "private-execution", SourceName = "Public source",
                Activity = new(6, 57, "MAIN1", "damage", 0, "Public effect settled.", "public-card", "Public source") {
                    ExecutionId = "private-execution", Detail = "UNCLASSIFIED-SECRET-REMEMBERED-CARD",
                    Cause = new("public-card", "Public source", "Deal 5 damage.", 0, "activated", "private-execution"),
                    Impact = new("damage", 5, PlayerId: 1, SourceName: "Public source", Combat: false) } },
            new HistoryRecord(7, 57, "MAIN1", "public") { ExecutionId = "private-execution", SourceName = "The Eternity Elevator",
                Payment = new("produced", 0, "auto") { ProducerExecutionId = "private-execution",
                    RestoredExecutionId = "private-execution", Produced = Mana("R", 32), PoolAfter = Mana("R", 32) } }
        });
        var publicMixed = MatchHistoryArchive.PublicRecords(mixed);
        var publicActivity = publicMixed.Single(record => record.Sequence == 6);
        var publicPayment = publicMixed.Single(record => record.Sequence == 7);
        check(publicActivity.ExecutionId == null && publicActivity.Activity is { ExecutionId: "", Detail: "", Cause.Id: "" }
                && publicActivity.Activity.Cause.Name == "Public source" && publicActivity.Activity.Cause.Ability == "Deal 5 damage."
                && publicActivity.Activity.Impact == mixed.Single(record => record.Sequence == 6).Activity!.Impact,
            "Public report export removes unclassified stack prose and private cause/reference IDs while retaining declared rules and actual impacts");
        check(publicPayment.ExecutionId == null && publicPayment.Payment is { ProducerExecutionId: null, RestoredExecutionId: null }
                && publicPayment.Payment.Produced!.GetValueOrDefault("R") == 32
                && publicMixed.Single(record => record.Sequence == 2).Execution!.Occurrence.Id == "public-producer"
                && mixed.Single(record => record.Sequence == 6).Activity!.Detail == "UNCLASSIFIED-SECRET-REMEMBERED-CARD",
            "A public payment cannot self-certify a private preparation ID; public production and genuine public occurrence admissions survive without mutating the viewer archive");
        string temporary = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temporary, "mana-history-archive-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        bool passed = false;
        try {
            string profile = Path.Combine(root, "privacy");
            string archiveId;
            await using (var archive = new MatchHistoryArchive(profile, Build)) {
                var batch = PrivacyBatch();
                archive.Record(batch); archive.Record(batch);
                archive.MarkClosed(batch.GameId, batch.ViewerId, finished: true);
                var listed = await archive.ListAsync().WaitAsync(Deadline);
                archiveId = listed.Single().ArchiveId;
                var document = await archive.ReadAsync(archiveId).WaitAsync(Deadline);
                check(document is { Summary.RecordCount: 5, Summary.LastSequence: 5, Summary.Finished: true,
                        Summary.State: HistoryArchiveState.Closed, Summary.HasGaps: false }
                        && document.Issues.IsEmpty && document.Summary.LastHash.Length == 64,
                    "The history archive stores typed receipts once, closes after queued batches and retains verified coverage");
                check(Guid.TryParseExact(archiveId, "N", out _) && Directory.GetDirectories(Path.Combine(profile, "history")).Length == 1,
                    "A provider game identity becomes metadata rather than a directory or export filename");
                using var publicOutput = new MemoryStream();
                await archive.ExportAsync(archiveId, publicOutput, focusSequence: 3);
                string publicText = Encoding.UTF8.GetString(publicOutput.ToArray());
                check(!publicText.Contains("SECRET-HAND", StringComparison.Ordinal)
                        && !publicText.Contains("private-execution", StringComparison.Ordinal)
                        && !publicText.Contains("{37}", StringComparison.Ordinal)
                        && publicText.Contains("The Eternity Elevator", StringComparison.Ordinal)
                        && publicText.Contains("\"R\":32", StringComparison.Ordinal),
                    "Default history export keeps public mana production while removing private preparation, bills and parent/root links");
                var publicReport = JsonNode.Parse(publicText)!;
                check(publicReport["selectedRecordSequence"]!.GetValue<long>() == 3 && !publicReport["focusOmitted"]!.GetValue<bool>(),
                    "An exported report identifies the exact selected receipt only when it survives the chosen disclosure scope");
                using var privateFocusOutput = new MemoryStream();
                await archive.ExportAsync(archiveId, privateFocusOutput, focusSequence: 1);
                var privateFocusReport = JsonNode.Parse(Encoding.UTF8.GetString(privateFocusOutput.ToArray()))!;
                check(privateFocusReport["selectedRecordSequence"] == null && privateFocusReport["focusOmitted"]!.GetValue<bool>()
                        && !privateFocusReport.AsObject().ContainsKey("requestedFocusSequence"),
                    "A private selected receipt becomes an omitted-focus marker without echoing its hidden sequence or identity");
                using var ownOutput = new MemoryStream();
                await archive.ExportAsync(archiveId, ownOutput, HistoryExportScope.IncludeOwnPrivate);
                string ownText = Encoding.UTF8.GetString(ownOutput.ToArray());
                check(ownText.Contains("SECRET-HAND", StringComparison.Ordinal) && ownText.Contains("{37}", StringComparison.Ordinal)
                        && ownOutput.CanWrite && publicOutput.CanWrite,
                    "An explicit own-private export preserves the owner's receipts and never takes ownership of the chosen stream");
                check(await Throws<ArgumentException>(() => archive.ReadAsync("../outside")),
                    "History reads reject path-shaped archive identifiers before touching the filesystem");
                using var invalidFocusOutput = new MemoryStream();
                check(await Throws<ArgumentOutOfRangeException>(() => archive.ExportAsync(archiveId, invalidFocusOutput, focusSequence: 0))
                        && invalidFocusOutput.Length == 0,
                    "An invalid selected sequence is rejected before writing a report");
            }
            await using (var reloaded = new MatchHistoryArchive(profile, Build)) {
                var listed = await reloaded.ListAsync().WaitAsync(Deadline);
                var read = await reloaded.ReadAsync(archiveId).WaitAsync(Deadline);
                check(listed is [{ ArchiveId: var id, RecordCount: 5, State: HistoryArchiveState.Closed }] && id == archiveId
                        && read!.Records.Length == 5,
                    "A fresh archive instance lists and reads a completed match without the engine or its original session");
            }

            string finalProfile = Path.Combine(root, "late-final");
            await using (var final = new MatchHistoryArchive(finalProfile, Build)) {
                final.Record(Batch("late-final", 1));
                final.MarkClosed("late-final", 0, finished: true);
                var closed = (await final.ListAsync().WaitAsync(Deadline)).Single();
                final.Record(Batch("late-final", 2));
                var document = (await final.ReadAsync(closed.ArchiveId).WaitAsync(Deadline))!;
                check(document is { Summary.RecordCount: 2, Summary.LastSequence: 2, Summary.Finished: true,
                        Summary.State: HistoryArchiveState.Closed, Summary.HasGaps: false } && document.Issues.IsEmpty,
                    "A final accepted history chunk arriving after a terminal board is appended to the retained match rather than silently dropped");
            }

            string incompleteProfile = Path.Combine(root, "incomplete-close");
            string incompleteId;
            await using (var incomplete = new MatchHistoryArchive(incompleteProfile, Build)) {
                incomplete.Record(Batch("incomplete-close", 1));
                incomplete.MarkClosed("incomplete-close", 0, finished: true, incomplete: true);
                incompleteId = (await incomplete.ListAsync().WaitAsync(Deadline)).Single().ArchiveId;
                var document = (await incomplete.ReadAsync(incompleteId).WaitAsync(Deadline))!;
                using var output = new MemoryStream();
                await incomplete.ExportAsync(incompleteId, output);
                string report = Encoding.UTF8.GetString(output.ToArray());
                check(document is { Summary.State: HistoryArchiveState.Closed, Summary.Finished: true, Summary.HasGaps: true }
                        && document.Issues.Any(item => item.Kind == "unobserved-tail")
                        && report.Contains("unobserved-tail", StringComparison.Ordinal),
                    "Closing before history catches up distinguishes a finished game from incomplete retained coverage in the default export");
            }
            await using (var restarted = new MatchHistoryArchive(incompleteProfile, Build)) {
                var document = (await restarted.ReadAsync(incompleteId).WaitAsync(Deadline))!;
                check(document.Summary.HasGaps && document.Issues.Any(item => item.Kind == "unobserved-tail"),
                    "An explicitly unobserved final history tail remains marked after restarting the archive reader");
            }

            string archivePath = Path.Combine(profile, "history", archiveId);
            string manifestPath = Path.Combine(archivePath, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["closed"] = false;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            await File.AppendAllTextAsync(Path.Combine(archivePath, "receipts.jsonl"), "{incomplete");
            await using (var interrupted = new MatchHistoryArchive(profile, Build)) {
                var document = (await interrupted.ReadAsync(archiveId).WaitAsync(Deadline))!;
                check(document is { Summary.State: HistoryArchiveState.Interrupted, Summary.HasGaps: true }
                        && document.Records.Length == 5 && document.Issues.Any(issue => issue.Kind == "invalid-or-incomplete-tail"),
                    "Restart preserves a verified prefix and labels an unfinished capture and incomplete tail without inventing a crash cause");
            }

            string tamperedProfile = Path.Combine(root, "tampered");
            string tamperedId;
            await using (var original = new MatchHistoryArchive(tamperedProfile, Build)) {
                original.Record(Batch("hash-check", 1)); original.MarkClosed("hash-check", 0, true);
                tamperedId = (await original.ListAsync().WaitAsync(Deadline)).Single().ArchiveId;
            }
            string tamperedPath = Path.Combine(tamperedProfile, "history", tamperedId, "receipts.jsonl");
            string originalText = await File.ReadAllTextAsync(tamperedPath);
            await File.WriteAllTextAsync(tamperedPath, originalText.Replace("\"R\":32", "\"R\":31", StringComparison.Ordinal));
            await using (var verified = new MatchHistoryArchive(tamperedProfile, Build)) {
                var document = (await verified.ReadAsync(tamperedId).WaitAsync(Deadline))!;
                check(document.Records.IsEmpty && document.Summary.HasGaps
                        && document.Issues.Any(issue => issue.Kind == "manifest-journal-mismatch"),
                    "Modified receipt contents fail the chain check and cannot become trusted history or an exported mana result");
            }

            string overflowProfile = Path.Combine(root, "overflow");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var smallQueue = new HistoryArchiveLimits(MaximumQueuedBatches: 1);
            await using (var queued = new MatchHistoryArchive(overflowProfile, Build, smallQueue, start.Task)) {
                queued.Record(Batch("overflow", 1)); queued.Record(Batch("overflow", 2));
                queued.MarkClosed("overflow", 0, false);
                check(!Directory.Exists(Path.Combine(overflowProfile, "history")) && queued.Health.DroppedBatches == 1,
                    "A blocked history writer leaves recording nonblocking and bounds its queue without writing from the caller");
                start.SetResult();
                var summary = (await queued.ListAsync().WaitAsync(Deadline)).Single();
                var document = (await queued.ReadAsync(summary.ArchiveId).WaitAsync(Deadline))!;
                check(document.Summary.HasGaps && document.Records.Length == 1
                        && document.Issues.Any(issue => issue.Kind == "queue-loss" && issue.Count == 1),
                    "A dropped accepted batch becomes a persisted loss marker rather than a false complete history");
            }

            string oversizedProfile = Path.Combine(root, "oversized-admission");
            await using (var admission = new MatchHistoryArchive(oversizedProfile, Build)) {
                string mode = new('x', 16000);
                var records = Enumerable.Range(1, HistoryFacts.MaximumBatchRecords).Select(sequence => {
                    var record = Batch("oversized-admission", sequence).Records[0];
                    return record with { Payment = record.Payment! with { ProducerMode = mode } };
                }).ToImmutableArray();
                admission.Record(new(1, "oversized-admission", 0, 1, records.Length, records.Length, false, records));
                admission.MarkClosed("oversized-admission", 0, false);
                var summary = (await admission.ListAsync().WaitAsync(Deadline)).Single();
                var document = (await admission.ReadAsync(summary.ArchiveId).WaitAsync(Deadline))!;
                check(admission.Health.DroppedBatches == 1 && summary.RecordCount == 0 && summary.HasGaps
                        && document.Issues.Any(item => item.Kind == "invalid-batch"),
                    "The shared byte bound rejects individually valid but collectively oversized batches before they enter the background queue");
            }

            string unicodeProfile = Path.Combine(root, "utf8-admission");
            await using (var unicode = new MatchHistoryArchive(unicodeProfile, Build)) {
                string mode = string.Concat(Enumerable.Repeat("界<", 700));
                var records = Enumerable.Range(1, 64).Select(sequence => {
                    var record = Batch("utf8-admission", sequence).Records[0];
                    return record with { Payment = record.Payment! with { ProducerMode = mode } };
                }).ToImmutableArray();
                unicode.Record(new(1, "utf8-admission", 0, 1, records.Length, records.Length, false, records));
                unicode.MarkClosed("utf8-admission", 0, true);
                var summary = (await unicode.ListAsync().WaitAsync(Deadline)).Single();
                var document = (await unicode.ReadAsync(summary.ArchiveId).WaitAsync(Deadline))!;
                check(document.Records.Length == 64 && !summary.HasGaps && document.Issues.IsEmpty
                        && document.Records.All(record => record.Payment!.ProducerMode == mode),
                    "Valid UTF-8 chunks round-trip without ASCII escape expansion falsely exhausting the archive's physical batch budget");
            }

            string emojiProfile = Path.Combine(root, "emoji-wire-cap");
            await using (var emoji = new MatchHistoryArchive(emojiProfile, Build)) {
                string mode = string.Concat(Enumerable.Repeat("\U0001F9D9", 8000));
                var records = Enumerable.Range(1, 15).Select(sequence => {
                    var record = Batch("emoji-wire-cap", sequence).Records[0];
                    return record with { Payment = record.Payment! with { ProducerMode = mode } };
                }).ToImmutableArray();
                long canonicalBytes = records.Sum(record => (long)HistoryFacts.RecordBytes(record));
                emoji.Record(new(1, "emoji-wire-cap", 0, 1, records.Length, records.Length, false, records));
                emoji.MarkClosed("emoji-wire-cap", 0, true);
                var summary = (await emoji.ListAsync().WaitAsync(Deadline)).Single();
                var document = (await emoji.ReadAsync(summary.ArchiveId).WaitAsync(Deadline))!;
                check(canonicalBytes > HistoryFacts.MaximumBatchBytes * .9 && canonicalBytes <= HistoryFacts.MaximumBatchBytes
                        && summary.JournalBytes > HistoryFacts.MaximumBatchBytes * 2 && !summary.HasGaps
                        && emoji.Health.DroppedBatches == 0 && document.Records.Length == 15 && document.Issues.IsEmpty
                        && document.Records.All(record => record.Payment!.ProducerMode == mode),
                    "A near-wire-cap emoji batch survives local surrogate expansion, hash verification and reload without false retention loss");
            }

            string boundedProfile = Path.Combine(root, "bounded");
            var bounded = new HistoryArchiveLimits(MaximumMatchBytes: 4096, MaximumTotalBytes: 16384, MaximumBatchBytes: 1024);
            await using (var limited = new MatchHistoryArchive(boundedProfile, Build, bounded, Task.CompletedTask)) {
                var huge = Batch("bounded", 1);
                huge = huge with { Records = [huge.Records[0] with { Payment = huge.Records[0].Payment! with { ProducerMode = new string('x', 2000) } }] };
                limited.Record(huge); limited.Record(Batch("bounded", 2)); limited.MarkClosed("bounded", 0, false);
                var summary = (await limited.ListAsync().WaitAsync(Deadline)).Single();
                var document = (await limited.ReadAsync(summary.ArchiveId).WaitAsync(Deadline))!;
                check(summary.HasGaps && summary.JournalBytes <= 4096 && summary.RecordCount == 0
                        && document.Issues.Any(issue => issue.Kind == "retention-limit"),
                    "A match byte or entry limit preserves an explicit bounded loss receipt and does not silently resume an incomplete stream");
            }

            string retentionProfile = Path.Combine(root, "retention");
            var twoMatches = new HistoryArchiveLimits(MaximumMatches: 2);
            await using (var retained = new MatchHistoryArchive(retentionProfile, Build, twoMatches, Task.CompletedTask)) {
                for (int i = 0; i < 4; i++) { retained.Record(Batch("retained-" + i, 1)); retained.MarkClosed("retained-" + i, 0, true); }
                var listed = await retained.ListAsync().WaitAsync(Deadline);
                check(listed.Length == 2 && listed.All(item => item.GameId is "retained-2" or "retained-3")
                        && Directory.GetDirectories(Path.Combine(retentionProfile, "history")).Length == 2,
                    "Retention removes older closed captures within validated archive children while keeping the newest matches");
            }

            string unavailableProfile = Path.Combine(root, "not-a-directory");
            await File.WriteAllTextAsync(unavailableProfile, "preserve");
            await using (var failed = new MatchHistoryArchive(unavailableProfile, Build)) {
                failed.Record(Batch("unavailable", 1));
                var listed = await failed.ListAsync().WaitAsync(Deadline);
                check(!failed.Health.Available && failed.Health.Issue == "storage-failed" && listed.IsEmpty
                        && await File.ReadAllTextAsync(unavailableProfile) == "preserve",
                    "Unavailable storage reports archive health without changing gameplay or overwriting an unrelated file");
            }
            passed = true;
        } finally {
            if (!passed) Console.Error.WriteLine("Retained match history archive evidence: " + root);
            else if (Directory.Exists(root)) {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                string verified = Path.GetFullPath(root);
                if (!verified.StartsWith(temporary.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
                    || !Path.GetFileName(verified).StartsWith("mana-history-archive-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing history fixture cleanup outside its generated temporary root.");
                Directory.Delete(verified, true);
            }
        }
    }

    private static HistoryBatch Batch(string game, long sequence) => new(1, game, 0, 1, sequence, sequence, false,
        [new(sequence, 57, "MAIN1", "public") { ExecutionId = "producer", SourceName = "The Eternity Elevator",
            Payment = new("produced", 0, "auto") { ProducerExecutionId = "producer", Produced = Mana("R", 32),
                PoolAfter = Mana("R", 32), SourceCounters = ImmutableDictionary<string, int>.Empty.Add("CHARGE", 32) } }]);

    private static HistoryBatch PrivacyBatch()
    {
        var hidden = new ExecutionOccurrence("private-execution", "private-execution", ExecutionKind.Spell, ExecutionStep.Preparation) {
            SourceName = "SECRET-HAND", ActorPlayerId = 0 };
        var producer = new ExecutionOccurrence("public-producer", "private-execution", ExecutionKind.Mana, ExecutionStep.Complete) {
            ParentId = "private-execution", SourceName = "The Eternity Elevator", Outcome = ExecutionOutcome.Resolved, ActorPlayerId = 0 };
        var bill = ImmutableArray.Create(new ManaBillPart("{37}", 1));
        return new(1, "provider/game/../../metadata-only", 0, 1, 5, 5, false, [
            new(1, 57, "MAIN1", "seatPrivate") { ExecutionId = hidden.Id, SourceName = hidden.SourceName, Execution = new(hidden) },
            new(2, 57, "MAIN1", "public") { ExecutionId = producer.Id, ParentId = hidden.Id, SourceName = producer.SourceName, Execution = new(producer) },
            new(3, 57, "MAIN1", "public") { ExecutionId = producer.Id, ParentId = hidden.Id, SourceName = producer.SourceName,
                Payment = new("produced", 0, "auto") { ProducerExecutionId = producer.Id, ProducerMode = "Add X mana of any one color",
                    BillBefore = bill, BillAfter = bill, Produced = Mana("R", 32), PoolAfter = Mana("R", 32),
                    SourceCounters = ImmutableDictionary<string, int>.Empty.Add("CHARGE", 32) } },
            new(4, 57, "MAIN1", "public") { ExecutionId = producer.Id, Activity = new(4, 57, "MAIN1", "ability", 0,
                "The Eternity Elevator produced mana.", "public-card", producer.SourceName) { ExecutionId = producer.Id } },
            new(5, 57, "MAIN1", "seatPrivate") { ExecutionId = hidden.Id, Payment = new("spent", 0, "auto") {
                BillBefore = bill, BillAfter = [], Spent = Mana("R", 3), PoolAfter = Mana("R", 29) } }
        ]);
    }
    private static ImmutableDictionary<string, int> Mana(string color, int amount) => ImmutableDictionary<string, int>.Empty.Add(color, amount);
    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception { try { await action(); return false; } catch (T) { return true; } }
}
