using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.Client;
using Mana.Contracts;

internal static class HistoryAnnouncementRegression
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ArchiveJson = new(Json) {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var batch = Batch();
        HistoryFacts.Validate(batch);
        var model = MatchHistoryReadModel.Read([batch]);
        var action = model.Actions.Single(item => item.Id == "announced-execution");
        check(action.Announcements.Select(item => item.Sequence).SequenceEqual([2L, 5L])
                && action.Announcements[0].Announcement!.Targets[0] is { Name: "Forest", Type: "Basic Land", Zone: "Battlefield", ControllerId: 1 }
                && action.Announcements[1].Announcement!.Targets[0] is { Name: "Other creature", ControllerId: 2 },
            "An execution retains its original announced target and later retargeting as separate ordered event-time snapshots");
        var restored = JsonSerializer.Deserialize<HistoryBatch>(JsonSerializer.Serialize(batch, Json), Json)!;
        HistoryFacts.Validate(restored);
        check(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(batch, Json), JsonSerializer.SerializeToElement(restored, Json))
                && model.Append(restored, out var duplicate).Records.Length == batch.Records.Length && duplicate == null,
            "Typed announced targets roundtrip and duplicate delivery cannot add or overwrite a historical target fact");
        var changed = batch with { Records = batch.Records.SetItem(1, batch.Records[1] with {
            Announcement = batch.Records[1].Announcement! with { Targets = [Land() with { ControllerId = 3, Zone = "Exile" }] }
        }) };
        check(Throws<ArgumentException>(() => model.Append(changed, out _))
                && action.Announcements[0].Announcement!.Targets[0].ControllerId == 1,
            "A changed later controller or zone cannot rewrite an already admitted announcement with the same sequence");

        var legacy = new HistoryBatch(1, "targets-game", 0, 1, 1, 1, false,
            [new(1, 20, "UPKEEP", "public") { ExecutionId = "legacy-execution", Execution = new(Occurrence("legacy-execution")) }]);
        HistoryFacts.Validate(legacy);
        var legacyWire = JsonNode.Parse(JsonSerializer.Serialize(legacy, Json))!;
        var legacyRecord = legacyWire["records"]![0]!;
        var actualAnnouncementWire = JsonNode.Parse(JsonSerializer.Serialize(batch, Json))!["records"]![1]!;
        check(!legacyRecord.AsObject().ContainsKey("announcement")
                && JsonSerializer.Deserialize<LegacyRecord>(legacyRecord.ToJsonString(), Json)?.Sequence == 1
                && actualAnnouncementWire["announcement"]?["targetKnowledge"]?.GetValue<string>() == "Known",
            "Legacy record serialization omits the new absent field while schema two keeps actual announced target facts");
        var forbiddenNull = legacyRecord.DeepClone(); forbiddenNull["announcement"] = null;
        check(Throws<JsonException>(() => JsonSerializer.Deserialize<LegacyRecord>(forbiddenNull.ToJsonString(), Json)),
            "The frozen strict schema-one record rejects even a null announcement field, guarding the actual omission contract");
        check(MatchHistoryReadModel.Read([legacy]).Actions.Single().Announcements.IsEmpty
                && batch.Records[6].Announcement is { TargetKnowledge: "Known", Targets.IsEmpty: true }
                && batch.Records[7].Announcement is { TargetKnowledge: "Unknown", Targets.IsEmpty: true },
            "A legacy missing announcement means not reported, distinct from a known targetless spell or an undisclosed target set");
        foreach (var invalid in InvalidAnnouncements())
            check(Throws<ArgumentException>(() => HistoryFacts.Validate(batch with { Records = [batch.Records[1] with { Sequence = 1, Announcement = invalid }] })),
                "Malformed announcement kinds, identities, slots, snapshot fields and bounds cannot enter the historical ledger");
        check(Throws<ArgumentException>(() => HistoryFacts.Validate(batch with { SchemaVersion = 1 }))
                && Throws<ArgumentException>(() => HistoryFacts.Validate(batch with { SchemaVersion = 3 }))
                && Throws<ArgumentException>(() => HistoryFacts.Validate(batch with { Records = [batch.Records[1] with { ExecutionId = null }] })),
            "Announcements require negotiated schema two and their affected execution; unknown future schemas remain rejected");
        var opaque = JsonNode.Parse(JsonSerializer.Serialize(batch, Json))!;
        opaque["records"]![1]!["announcement"]!["targets"]![0]!["actionKey"] = "UNAUTHORIZED-CURRENT-CARD-KEY";
        check(Throws<JsonException>(() => JsonSerializer.Deserialize<HistoryBatch>(opaque.ToJsonString(), Json)),
            "An announcement target cannot smuggle a live action key or raw Card attachment into the typed archive");
        var huge = batch.Records[1]!.Announcement! with { Targets = Enumerable.Range(0, 128).Select(index =>
            Land() with { TargetIndex = index, Name = new string('n', 1024), Type = new string('t', 1024) }).ToImmutableArray() };
        check(Throws<ArgumentException>(() => HistoryFacts.Validate(batch with { Records = [batch.Records[1] with { Sequence = 1, Announcement = huge }] })),
            "A bounded target count still obeys the existing total record byte limit rather than bypassing archive capacity");
        var publicRecords = MatchHistoryArchive.PublicRecords(batch.Records);
        HistoryFacts.Validate(batch with { Records = publicRecords });
        check(publicRecords.Where(record => record.Announcement != null).Select(record => record.Sequence).SequenceEqual([2L, 5L, 7L])
                && publicRecords.Single(record => record.Sequence == 5).Announcement!.CauseExecutionId == null,
            "Public announcements keep their independently disclosed execution identities and close private redirect references without invalid target records");
        foreach (var retained in new[] { batch.Records.RemoveAt(0), batch.Records.SetItem(0, batch.Records[0] with { Exposure = "seatPrivate" }) }) {
            var filtered = MatchHistoryArchive.PublicRecords(retained);
            HistoryFacts.Validate(batch with { Records = filtered });
            check(filtered.All(record => record.ExecutionId != "announced-execution")
                    && retained.Any(record => record.Announcement?.StackId == "stack-occurrence"),
                "An evicted or private defining occurrence cannot be promoted by an announcement reference; only the public export omits it");
        }

        string directory = Path.Combine(Path.GetTempPath(), "mana-announced-history-" + Guid.NewGuid().ToString("N"));
        bool passed = false;
        try {
            string id;
            await using (var archive = new MatchHistoryArchive(directory, new("0.2.393", true, new string('a', 40)))) {
                archive.Record(batch); archive.Record(batch); archive.MarkClosed(batch.GameId, batch.ViewerId, true);
                id = (await archive.ListAsync()).Single().ArchiveId;
                var document = await archive.ReadAsync(id);
                check(document is { Summary.HasGaps: false, Summary.RecordCount: 8 }
                        && document.Issues.IsEmpty && document.Records[1].Announcement!.Targets[0] == Land(),
                    "The actual single-owner archive persists the complete typed target snapshot with its original hash-chain coverage");
                using var publicOutput = new MemoryStream();
                await archive.ExportAsync(id, publicOutput);
                string text = Encoding.UTF8.GetString(publicOutput.ToArray());
                var report = JsonNode.Parse(text)!;
                check(report["schemaVersion"]!.GetValue<int>() == 2 && text.Contains("Forest", StringComparison.Ordinal)
                        && text.Contains("Other creature", StringComparison.Ordinal)
                        && !text.Contains("PRIVATE-TARGET-CANARY", StringComparison.Ordinal)
                        && !text.Contains("private-cause", StringComparison.Ordinal)
                        && report["authority"]!.GetValue<string>() == "none",
                    "A public target report retains announced/retargeted public snapshots and strips private target facts and redirect-cause references");
                using var omittedFocus = new MemoryStream();
                await archive.ExportAsync(id, omittedFocus, focusSequence: 8);
                var omitted = JsonNode.Parse(Encoding.UTF8.GetString(omittedFocus.ToArray()))!;
                check(omitted["focusOmitted"]!.GetValue<bool>() && omitted["selectedRecordSequence"] == null
                        && omitted["omittedRecords"]!.GetValue<int>() == 3,
                    "Public export explicitly reports omitted unbound announcement focus and private records rather than inventing a public identity");
                using var ownOutput = new MemoryStream();
                await archive.ExportAsync(id, ownOutput, HistoryExportScope.IncludeOwnPrivate);
                check(Encoding.UTF8.GetString(ownOutput.ToArray()).Contains("PRIVATE-TARGET-CANARY", StringComparison.Ordinal)
                        && document.Records[4].Announcement!.CauseExecutionId == "private-cause",
                    "Public export does not mutate the viewer's original private announcement receipt or its explicit own-private export");
            }
            await using (var reopened = new MatchHistoryArchive(directory, new("0.2.393", true, new string('b', 40)))) {
                var document = await reopened.ReadAsync(id);
                check(document is { Summary.HasGaps: false } && document.Issues.IsEmpty
                        && MatchHistoryReadModel.Read(document.Batches).Actions.Single(item => item.Id == "announced-execution")
                            .Announcements[0].Announcement!.Targets[0] == Land(),
                    "Offline restart reads historical target control/type/zone without looking up any current game, face or command handle");
            }
            string legacyDirectory = Path.Combine(directory, "legacy-case");
            await using (var archive = new MatchHistoryArchive(legacyDirectory, new("0.2.393", true, new string('c', 40)))) {
                archive.Record(legacy); archive.MarkClosed(legacy.GameId, legacy.ViewerId, true);
                string legacyId = (await archive.ListAsync()).Single().ArchiveId;
                var journal = File.ReadLines(Path.Combine(legacyDirectory, "history", legacyId, "receipts.jsonl"))
                    .Select(line => JsonNode.Parse(line)!).Single(row => row["payload"]?["batch"] != null)["payload"]!["batch"]!;
                using var output = new MemoryStream(); await archive.ExportAsync(legacyId, output);
                var report = JsonNode.Parse(Encoding.UTF8.GetString(output.ToArray()))!;
                check(journal["schemaVersion"]!.GetValue<int>() == 1 && report["schemaVersion"]!.GetValue<int>() == 1
                        && new[] { journal, report }.All(envelope => envelope["records"]!.AsArray().All(record =>
                            !record!.AsObject().ContainsKey("announcement")
                            && JsonSerializer.Deserialize<LegacyRecord>(record.ToJsonString(), ArchiveJson)?.Sequence == 1)),
                    "The actual legacy archive writer and public schema-one report remain readable by the strict old record contract without a null announcement field");
            }
            passed = true;
        } finally { if (passed) Directory.Delete(directory, true); }
    }

    private static IEnumerable<HistoryAnnouncement> InvalidAnnouncements()
    {
        var good = new HistoryAnnouncement("announced", "stack-occurrence", "Known", [Land()]);
        yield return good with { Kind = "guessed" }; yield return good with { StackId = "" };
        yield return good with { TargetKnowledge = "guessed" }; yield return good with { Targets = default };
        yield return good with { CauseExecutionId = " " }; yield return good with { AnnouncedX = -1 };
        yield return good with { Targets = [Land(), Land()] };
        foreach (var target in new[] { Land() with { GroupIndex = -1 }, Land() with { TargetIndex = -1 },
            Land() with { GroupIndex = 4096 }, Land() with { TargetIndex = int.MaxValue },
            Land() with { Kind = "current-card" }, Land() with { PlayerId = 1 }, Land() with { Zone = "UnknownZone" },
            Land() with { Name = "bad\nname" }, Land() with { AllocatedAmount = -1 },
            new HistoryAnnouncedTarget("player", 0, 0), new HistoryAnnouncedTarget("stack", 0, 0) })
            yield return good with { Targets = [target] };
        yield return good with { Targets = Enumerable.Range(0, 4097).Select(index => Land() with { TargetIndex = index }).ToImmutableArray() };
    }
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
    private static ExecutionOccurrence Occurrence(string id) => new(id, id, ExecutionKind.Spell, ExecutionStep.Stack) {
        ActorPlayerId = 0, SourceCardId = "announced-source", SourceName = "Announced spell" };
    private static HistoryAnnouncedTarget Land() => new("card", 0, 0) {
        CardId = "historical-land", Name = "Forest", Type = "Basic Land", Zone = "Battlefield", ControllerId = 1, OwnerId = 1 };
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record LegacyRecord([property: JsonRequired] long Sequence, [property: JsonRequired] int Turn,
        [property: JsonRequired] string PhaseKey, [property: JsonRequired] string Exposure)
    {
        public string? ExecutionId { get; init; }
        public string? ParentId { get; init; }
        public string? SourceCardId { get; init; }
        public string? SourceName { get; init; }
        public ActivityEntry? Activity { get; init; }
        public HistoryExecution? Execution { get; init; }
        public HistoryPayment? Payment { get; init; }
    }
    private static HistoryBatch Batch() => new(2, "targets-game", 0, 1, 8, 8, false, [
        new(1, 20, "UPKEEP", "public") { ExecutionId = "announced-execution", Execution = new(Occurrence("announced-execution")) },
        new(2, 20, "UPKEEP", "public") { ExecutionId = "announced-execution",
            Announcement = new("announced", "stack-occurrence", "Known", [Land()]) },
        new(3, 20, "UPKEEP", "seatPrivate") { ExecutionId = "private-cause", Execution = new(Occurrence("private-cause")) },
        new(4, 20, "UPKEEP", "seatPrivate") { ExecutionId = "private-cause",
            Announcement = new("announced", "private-stack", "Known", [Land() with { Name = "PRIVATE-TARGET-CANARY" }]) },
        new(5, 20, "UPKEEP", "public") { ExecutionId = "announced-execution", Announcement = new("retargeted", "stack-occurrence", "Known",
            [new("card", 0, 0) { CardId = "other-creature", Name = "Other creature", Type = "Creature", Zone = "Battlefield", ControllerId = 2 }]) {
                CauseExecutionId = "private-cause" } },
        new(6, 20, "UPKEEP", "public") { ExecutionId = "targetless-execution", Execution = new(Occurrence("targetless-execution")) },
        new(7, 20, "UPKEEP", "public") { ExecutionId = "targetless-execution", Announcement = new("announced", "targetless-stack", "Known", []) },
        new(8, 20, "UPKEEP", "public") { ExecutionId = "unknown-execution", Announcement = new("announced", "unknown-stack", "Unknown", []) }
    ]);
}
