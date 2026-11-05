using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class MatchHistoryRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var spell = new ExecutionOccurrence("spell", "spell", ExecutionKind.Spell, ExecutionStep.Preparation) {
            ActorPlayerId = 0, SourceName = "Paid spell", SourceCardId = "spell-face" };
        var mana = new ExecutionOccurrence("mana", "spell", ExecutionKind.Mana, ExecutionStep.Preparation) {
            ParentId = "spell", ActorPlayerId = 0, SourceName = "The Eternity Elevator", SourceCardId = "elevator" };
        HistoryRecord Execution(long sequence, ExecutionOccurrence occurrence) => new(sequence, 57, "MAIN1", "public") {
            ExecutionId = occurrence.Id, ParentId = occurrence.ParentId, SourceName = occurrence.SourceName,
            SourceCardId = occurrence.SourceCardId, Execution = new(occurrence, new(sequence, occurrence.Id, occurrence.Step) { Outcome = occurrence.Outcome }) };
        HistoryRecord Payment(long sequence, string execution, string name, HistoryPayment payment) => new(sequence, 57, "MAIN1", "public") {
            ExecutionId = execution, ParentId = execution == "mana" ? "spell" : null, SourceName = name, Payment = payment };
        var records = ImmutableArray.Create(
            Execution(1, spell),
            Payment(2, "spell", "Paid spell", new("opened", 0, "auto") { BillBefore = [new("{3}", 1)] }),
            Execution(3, mana),
            Payment(4, "mana", "The Eternity Elevator", new("produced", 0, "auto") {
                ProducerExecutionId = "mana", ProducerMode = "Add X mana of any one color.",
                Produced = Mana("U", 32), PoolAfter = Mana("U", 32), SourceCounters = Mana("Charge", 32) }),
            Payment(5, "spell", "Paid spell", new("spent", 0, "auto") {
                BillBefore = [new("{3}", 1)], BillAfter = [], Spent = Mana("U", 3), PoolAfter = Mana("U", 29) }),
            Execution(6, mana with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }),
            Execution(7, spell with { Step = ExecutionStep.Stack }),
            Execution(8, spell with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved }));
        var batch = Batch(records);
        var model = MatchHistoryReadModel.Empty("game", 0).Append(batch, out var admitted);
        var paid = model.Actions.Single(action => action.Id == "spell");
        var producer = model.Actions.Single(action => action.Id == "mana");
        check(admitted?.Records.Length == 8 && paid.Children.SequenceEqual(["mana"]) && producer.ParentId == "spell"
            && paid.Outcome == ExecutionOutcome.Resolved && !paid.HistoryIncomplete
            && producer.Records.Single(record => record.Payment?.Kind == "produced").Payment is {
                Produced: { } produced, SourceCounters: { } counters } && produced["U"] == 32 && counters["Charge"] == 32
            && paid.Records.Single(record => record.Payment?.Kind == "spent").Payment is { PoolAfter: { } pool } && pool["U"] == 29,
            "History retains an exact symbolic bill, nested Elevator mode,32 produced,3 spent,29 remaining and unchanged charge count without a live engine");

        var copied = JsonSerializer.Deserialize<HistoryBatch>(JsonSerializer.Serialize(batch))!;
        check(ReferenceEquals(model.Append(copied, out var duplicate), model) && duplicate == null,
            "Repeated equivalent history batches advance neither the model nor its archival admission event");
        check(Reject(() => model.Append(batch with { Records = records.SetItem(0, records[0] with { SourceName = "Changed old fact" }) }, out _)),
            "A provider cannot rewrite an already retained occurrence under its existing sequence");

        var independent = new HistoryRecord(9, 57, "MAIN1", "public") {
            ExecutionId = "other", Activity = new(90, 57, "MAIN1", "draw", 1, "Drew seven cards.", null, null) {
                ExecutionId = "other", ExecutionStep = ExecutionStep.Resolution, Count = 7 } };
        model = model.Append(Batch([independent]), out _);
        check(model.Actions.Single(action => action.Id == "other").Records.Length == 1
            && model.Actions.Single(action => action.Id == "spell").Records.Length == 5,
            "An unrelated same-poll action cannot inherit the paid spell's mana or results");

        var sparse = MatchHistoryReadModel.Empty("game", 0).Append(new(1, "game", 0, 1, 8, 8, false,
            [records[1], records[4]]), out _);
        check(sparse.CoverageComplete && sparse.Cursor == 8,
            "Sparse visible source records use provider retention fences rather than treating filtered ordinals as missing events");
        var later = new HistoryRecord(20, 57, "MAIN1", "public") {
            Activity = new(200, 57, "MAIN1", "draw", 0, "Drew a card.", null, null) { Count = 1 } };
        sparse = sparse.Append(new(1, "game", 0, 20, 21, 21, false, [later]), out _);
        check(!sparse.CoverageComplete && sparse.Gaps.Single() is { FirstSequence: 9, LastSequence: 19, Reason: "provider-retention" },
            "Eviction beyond the accepted cursor leaves an explicit missing range instead of claiming complete history");
        var archived = MatchHistoryReadModel.Build(model.GameId, model.ViewerId, model.Records, model.Gaps, model.Cursor);
        check(archived.Actions.Select(action => action.Id).SequenceEqual(model.Actions.Select(action => action.Id))
            && archived.Records.All(record => record.Activity?.CardId != "command-key")
            && typeof(HistoryRecord).GetProperties().All(property => property.PropertyType != typeof(Card)),
            "An archived typed record rebuilds the same reading model without cards, action handles, a renderer or a rules process");

        foreach (var invalid in new[] {
            batch with { SchemaVersion = 3 }, batch with { ViewerId = -1 }, batch with { HasMore = true },
            batch with { Records = [records[0] with { Exposure = "hostPrivate" }] },
            batch with { Records = [records[0] with { Payment = new("opened", 0, "auto") }] },
            batch with { Records = [records[3] with { Payment = new("produced", 0, "auto") { Produced = Mana("neutral", 32) } }] },
            batch with { Records = [records[1] with { Payment = new("opened", 0, "auto") { BillBefore = [new("{C}", 0)] } }] },
            batch with { Records = [records[0] with { Execution = new(spell with { Step = ExecutionStep.Complete }) }] }
        }) check(Reject(() => HistoryFacts.Validate(invalid)),
            "Malformed history schema, disclosure, union, quantities, bill and terminal state cannot enter the shared reader");
        var unknown = records[1] with { Payment = new("opened", 0, "unknown") };
        var knownEmpty = records[1] with { Payment = new("completed", 0, "unknown") { BillAfter = [], Spent = ImmutableDictionary<string, int>.Empty } };
        check(unknown.Payment!.BillBefore == null && knownEmpty.Payment!.BillAfter is { IsEmpty: true }
            && knownEmpty.Payment.Spent is { Count: 0 },
            "Unreported payment facts remain distinct from a known empty bill or zero spending");
        UnicodeBudgets(check);

        var manual = new MatchHistorySession(null, _ => throw new Exception("Unsupported history queried."));
        manual.Observe(State());
        int notifications = 0;
        manual.Accepted += _ => throw new IOException("Failed archive subscriber.");
        manual.Accepted += _ => notifications++;
        check(manual.Status == MatchHistoryStatus.Unsupported && manual.Accept(batch) && !manual.Accept(batch)
            && notifications == 1 && manual.Model.Cursor == 8,
            "One exact-audience history admission reaches other consumers even when an evidence observer fails; duplicate admission is silent");
        manual.Observe(State() with { ViewerId = 1 });
        check(manual.Model.Records.IsEmpty && !manual.Accept(batch),
            "A viewer boundary revokes the previous seat's private history before late records can be admitted");
        manual.Close();

        await QueryLifecycle(check, batch);
    }

    private static async Task QueryLifecycle(Action<bool, string> check, HistoryBatch batch)
    {
        var driver = new Driver();
        driver.Session.Observe(State());
        var first = await driver.Request(1);
        driver.Session.Observe(State() with { ViewerId = 1 });
        first.Result.SetResult(batch with { Records = batch.Records.Select(record => record with { Exposure = "seatPrivate" }).ToImmutableArray() });
        var second = await driver.Request(2);
        check(driver.Session.Model.ViewerId == 1 && driver.Session.Model.Records.IsEmpty && driver.Admitted == 0,
            "A delayed prior viewer query cannot publish its private records into the new audience");
        second.Result.SetResult(new(1, "game", 1, 1, 0, 0, false, []));
        await driver.Until(() => driver.Session.Status == MatchHistoryStatus.Ready);
        driver.Session.Observe(State() with { ViewerId = 1 });
        check(driver.Provider.Requests.Count == 2 && driver.Session.IsCaughtUp && driver.Session.ObservedRevision == 1,
            "An unchanged accepted revision does not repeat a scanned range; an empty successful query certifies catch-up to that observation");
        driver.Session.Observe(State() with { ViewerId = 1, Revision = 2 });
        var failing = await driver.Request(3);
        check(!driver.Session.IsCaughtUp && driver.Session.ObservedRevision == 1,
            "A newer accepted state revokes catch-up while preserving the last successfully observed history revision");
        failing.Result.SetException(new TimeoutException("History query timed out."));
        await driver.Until(() => driver.Session.Status == MatchHistoryStatus.Failed);
        check(driver.Session.Model.ViewerId == 1 && driver.Admitted == 0 && !driver.Session.IsCaughtUp && driver.Session.ObservedRevision == 1,
            "A history timeout becomes recorder/read status rather than a match-command or connection failure");
        driver.Session.Retry();
        var retry = await driver.Request(4);
        driver.Session.Close();
        retry.Result.SetResult(new(1, "game", 1, 1, 0, 0, false, []));
        await driver.Until(() => driver.Applied == 4);
        check(driver.Session.Model.GameId == "" && driver.Admitted == 0,
            "Closing the history owner revokes a final in-flight read without restoring a private model");

        var chunks = new Driver();
        chunks.Session.Observe(State());
        var chunk = await chunks.Request(1);
        chunk.Result.SetResult(batch with { LastSequence = 9, HasMore = true });
        var tail = await chunks.Request(2);
        check(tail.After == 8 && chunks.Admitted == 1 && chunks.Updates.SequenceEqual([false]),
            "History catch-up requests its next bounded chunk from the last admitted provider scan cursor");
        tail.Result.SetResult(new(1, "game", 0, 1, 9, 9, false, []));
        await chunks.Until(() => chunks.Session.Status == MatchHistoryStatus.Ready);
        check(chunks.Session.Model.Cursor == 9 && chunks.Admitted == 2 && chunks.Updates.SequenceEqual([false, true]),
            "An empty advanced range is archived before its caught-up lifecycle notification without inventing an event or closing an unfinished chunk");
        chunks.Session.Close();
    }

    private static void UnicodeBudgets(Action<bool, string> check)
    {
        var plain = new HistoryRecord(1, 57, "MAIN1", "public") {
            SourceName = "a", Activity = new(1, 57, "MAIN1", "resolved", 0, "Event", null, null) };
        string unicode = "\u6D77\uD83C\uDF0A";
        var multibyte = plain with { SourceName = unicode };
        check(HistoryFacts.RecordBytes(multibyte) == HistoryFacts.RecordBytes(plain) - 1 + Encoding.UTF8.GetByteCount(unicode),
            "Provider-sized history counts BMP and supplementary characters as UTF-8 rather than native ASCII or surrogate escapes");
        var near = multibyte with { Activity = plain.Activity! with {
            Message = new string('\u6D77', 10000), Detail = string.Concat(Enumerable.Repeat("\uD83C\uDF0A", 8000)) } };
        int padding = HistoryFacts.MaximumRecordBytes - HistoryFacts.RecordBytes(near);
        near = near with { Activity = near.Activity! with { Message = near.Activity.Message + new string('a', padding) } };
        HistoryFacts.Validate(Batch([near]));
        var tooLarge = near with { Activity = near.Activity! with { Message = near.Activity.Message + "a" } };
        check(HistoryFacts.RecordBytes(near) == HistoryFacts.MaximumRecordBytes && Reject(() => HistoryFacts.Validate(Batch([tooLarge]))),
            "A near-cap emoji and BMP record is admitted at the provider byte boundary and rejected one actual UTF-8 byte beyond it");
        var eight = Enumerable.Range(1, 8).Select(index => near with {
            Sequence = index, Activity = near.Activity! with { Id = index } }).ToImmutableArray();
        HistoryFacts.Validate(Batch(eight));
        check(Reject(() => HistoryFacts.Validate(Batch(eight.Add(near with { Sequence = 9, Activity = near.Activity! with { Id = 9 } })))),
            "The shared batch budget admits eight bounded64KiB records but rejects an oversized aggregate before archival queue admission");
    }

    private static ImmutableDictionary<string, int> Mana(string symbol, int amount) =>
        ImmutableDictionary<string, int>.Empty.Add(symbol, amount);
    private static HistoryBatch Batch(ImmutableArray<HistoryRecord> records) =>
        new(1, "game", 0, 1, records[^1].Sequence, records[^1].Sequence, false, records);
    private static GameSnapshot State() => new("game", "playing", null, null, 0, 57, "Main phase", "MAIN1", 0, [], [], null, null) { Revision = 1 };
    private static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }

    private sealed class Provider : IMatchHistory
    {
        internal sealed record Request(long After, TaskCompletionSource<HistoryBatch> Result);
        internal ConcurrentQueue<Request> Requests { get; } = new();
        public Task<HistoryBatch> HistoryAsync(string gameId, long afterSequence, int limit = 128)
        {
            var result = new TaskCompletionSource<HistoryBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Enqueue(new(afterSequence, result)); return result.Task;
        }
    }
    private sealed class Driver
    {
        internal Provider Provider { get; } = new();
        private readonly ConcurrentQueue<Action> completions = new();
        internal MatchHistorySession Session { get; }
        internal int Applied { get; private set; }
        internal int Admitted { get; private set; }
        internal List<bool> Updates { get; } = [];
        internal Driver() {
            Session = new(Provider, completions.Enqueue); Session.Accepted += _ => Admitted++;
            Session.Updated += () => throw new IOException("Failed lifecycle subscriber.");
            Session.Updated += () => Updates.Add(Session.IsCaughtUp);
        }
        internal async Task<Provider.Request> Request(int count) {
            await Until(() => Provider.Requests.Count == count);
            return Provider.Requests.ToArray()[count - 1];
        }
        internal async Task Until(Func<bool> ready) {
            var watch = Stopwatch.StartNew();
            while (!ready()) {
                while (completions.TryDequeue(out var completion)) { completion(); Applied++; }
                if (ready()) return;
                if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("History fixture did not complete.");
                await Task.Delay(1);
            }
        }
    }
}
