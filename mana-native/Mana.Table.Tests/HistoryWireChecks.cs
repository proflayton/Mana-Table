using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class HistoryWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"schemaVersion":1,"gameId":"history-game","viewerId":0,"firstSequence":1,"lastSequence":3,
             "nextSequence":3,"hasMore":false,"records":[
              {"sequence":1,"turn":57,"phaseKey":"MAIN1","exposure":"public","executionId":"paid",
               "execution":{"occurrence":{"id":"paid","budgetRootId":"paid","kind":"spell","step":"cost","sourceName":"Paid spell"}}},
              {"sequence":2,"turn":57,"phaseKey":"MAIN1","exposure":"public","executionId":"mana","parentId":"paid",
               "sourceCardId":"historical-elevator","sourceName":"The Eternity Elevator",
               "payment":{"kind":"produced","payerId":0,"origin":"auto","producerExecutionId":"mana",
                "producerMode":"Add X mana of any one color.","produced":{"U":32},"sourceCounters":{"Charge":32},"poolAfter":{"U":32}}},
              {"sequence":3,"turn":57,"phaseKey":"MAIN1","exposure":"seatPrivate","executionId":"paid",
               "payment":{"kind":"spent","payerId":0,"origin":"auto","billBefore":[{"symbol":"{3}","count":1}],
                "billAfter":[],"spent":{"U":3},"poolAfter":{"U":29}}}]}
            """)!;
        var decoded = ForgeHistoryDecoder.Decode(Element(wire));
        check(decoded.Records[1].Payment?.Produced?["U"] == 32 && decoded.Records[2].Payment?.PoolAfter?["U"] == 29
            && decoded.Records[2].Exposure == "seatPrivate" && decoded.Records[0].Execution?.Occurrence.Id == "paid",
            "The shipping history decoder preserves typed payment quantities, causal identities and exact disclosure without minting card authority");
        foreach (string member in new[] { "schemaVersion", "viewerId", "nextSequence", "records" }) {
            var malformed = wire.DeepClone(); malformed.AsObject().Remove(member);
            check(Reject(malformed), "History wire requires its explicit " + member + " fence");
        }
        foreach (string member in new[] { "card", "snapshot", "actionKey", "hostObservation" }) {
            var malformed = wire.DeepClone(); malformed["records"]![1]![member] = new JsonObject { ["secret"] = "must-not-enter-history" };
            check(Reject(malformed), "History wire rejects an unexpected " + member + " payload rather than silently retaining raw provider data");
        }
        var otherExposure = wire.DeepClone(); otherExposure["records"]![2]!["exposure"] = "hostPrivate";
        check(Reject(otherExposure), "A host-private payload cannot masquerade as a player historical disclosure");
        var hiddenNested = wire.DeepClone(); hiddenNested["records"]![2]!["payment"]!["hand"] = new JsonArray("secret");
        check(Reject(hiddenNested), "A payment receipt cannot carry an untyped hidden-card attachment");
        var foreignPrivate = wire.DeepClone(); foreignPrivate["records"]![2]!["payment"]!["payerId"] = 1;
        check(Reject(foreignPrivate), "A same-viewer history batch cannot smuggle another payer's private bill or pool");
        var foreignPublic = foreignPrivate.DeepClone(); foreignPublic["records"]![2]!["exposure"] = "public";
        check(ForgeHistoryDecoder.Decode(Element(foreignPublic)).Records[2].Payment?.PayerId == 1,
            "Actual public payment facts from another payer remain readable without acquiring that player's private ledger");
        var numericStep = wire.DeepClone(); numericStep["records"]![0]!["execution"]!["occurrence"]!["step"] = 2;
        check(Reject(numericStep), "Execution steps keep their closed named wire values inside archived history");
        var duplicate = wire.DeepClone(); duplicate["records"]![2]!["sequence"] = 2;
        check(Reject(duplicate), "A source batch cannot assign one ordinal to two historical payloads");
        using var unavailable = JsonDocument.Parse("""{"error":"Different text","errorCode":"history-unavailable"}""");
        using var prose = JsonDocument.Parse("""{"error":"history-unavailable"}""");
        check(ForgeEngine.RequestFailure("matchHistory", unavailable.RootElement) is NotSupportedException
            && ForgeEngine.RequestFailure("automatedMatchHistory", unavailable.RootElement) is NotSupportedException
            && ForgeEngine.RequestFailure("matchState", unavailable.RootElement) is not NotSupportedException
            && ForgeEngine.RequestFailure("matchHistory", prose.RootElement) is not NotSupportedException,
            "Only the exact typed history-unavailable response on the read-only history method negotiates unsupported history");
        var owned = ForgeEngine.DecodeAutomatedHistory(Element(wire), "history-game", 0, 0, 128);
        check(owned.GameId == decoded.GameId && owned.ViewerId == decoded.ViewerId
            && owned.Records[2].Payment?.Spent?["U"] == 3 && owned.Records[2].Exposure == "seatPrivate",
            "Automated history preserves the existing typed ledger's exact audience, committed payment and private exposure");
        bool RejectOwned(JsonNode value, string game = "history-game", int seat = 0, long cursor = 0, int limit = 128)
        { try { ForgeEngine.DecodeAutomatedHistory(Element(value), game, seat, cursor, limit); return false; } catch (IOException) { return true; } }
        check(RejectOwned(wire, game: "foreign-game") && RejectOwned(wire, seat: 1),
            "Automated history cannot substitute another game or player's audience even with otherwise valid typed facts");
        check(RejectOwned(wire, cursor: 3) && RejectOwned(wire, limit: 2),
            "A history provider cannot replay retired cursor records or exceed the caller's narrower chunk budget");
        Announcements(check);
    }

    private static void Announcements(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"schemaVersion":2,"gameId":"announced-game","viewerId":0,"firstSequence":1,"lastSequence":3,
             "nextSequence":3,"hasMore":false,"records":[
              {"sequence":1,"turn":8,"phaseKey":"MAIN1","exposure":"public","executionId":"historical-source",
               "announcement":{"kind":"announced","stackId":"announced-stack","targetKnowledge":"Known","announcedX":5,
                "targets":[
                 {"kind":"card","groupIndex":0,"targetIndex":0,"cardId":"historical-target","name":"Old target","type":"Creature","zone":"Battlefield","controllerId":1,"ownerId":2,"allocatedAmount":2},
                 {"kind":"card","groupIndex":1,"targetIndex":0,"cardId":"historical-target","name":"Old target","type":"Creature","zone":"Battlefield","controllerId":1,"ownerId":2,"allocatedAmount":3},
                 {"kind":"player","groupIndex":2,"targetIndex":0,"playerId":3,"name":"Opponent"},
                 {"kind":"stack","groupIndex":3,"targetIndex":0,"stackId":"older-stack","zone":"Stack"}]}},
              {"sequence":2,"turn":8,"phaseKey":"MAIN1","exposure":"public","executionId":"historical-source",
               "announcement":{"kind":"retargeted","stackId":"announced-stack","causeExecutionId":"redirect-effect","targetKnowledge":"Known","targets":[]}},
              {"sequence":3,"turn":8,"phaseKey":"MAIN1","exposure":"public","executionId":"unknown-source",
               "announcement":{"kind":"announced","stackId":"unknown-stack","targetKnowledge":"Unknown","targets":[]}}]}
            """)!;
        var decoded = ForgeHistoryDecoder.Decode(Element(wire));
        var announced = decoded.Records[0].Announcement!;
        check(decoded.SchemaVersion == 2 && announced.AnnouncedX == 5 && announced.Targets.Length == 4
            && announced.Targets[0] is { CardId: "historical-target", GroupIndex: 0, AllocatedAmount: 2, OwnerId: 2, ControllerId: 1 }
            && announced.Targets[1] is { CardId: "historical-target", GroupIndex: 1, AllocatedAmount: 3 }
            && announced.Targets[2].PlayerId == 3 && announced.Targets[3].StackId == "older-stack",
            "History schema2 preserves ordered target groups, repeated historical targets, allocations, announced X and independent owner/controller facts");
        check(decoded.Records[1].Announcement is { Kind: "retargeted", CauseExecutionId: "redirect-effect", TargetKnowledge: "Known", Targets.IsEmpty: true }
            && decoded.Records[2].Announcement is { TargetKnowledge: "Unknown", Targets.IsEmpty: true },
            "Retargeting and known-empty announcements remain distinct from unknown historical target knowledge");
        var roundTrip = ForgeHistoryDecoder.Decode(JsonSerializer.SerializeToElement(decoded));
        check(roundTrip.Records[0].Announcement!.Targets.SequenceEqual(announced.Targets)
            && roundTrip.Records[1].Announcement is { Kind: "retargeted", CauseExecutionId: "redirect-effect", TargetKnowledge: "Known", Targets.IsEmpty: true },
            "The typed history round trip retains event-time announcement facts without rebinding a current occurrence");
        check(Reject(wire, HistoryFacts.LegacySchemaVersion),
            "A schema2 history response is rejected when its read negotiated only legacy schema1");
        var legacy = wire.DeepClone(); legacy["schemaVersion"] = 1;
        check(Reject(legacy), "A legacy history envelope cannot smuggle an announcement record without schema2");

        foreach (string member in new[] { "kind", "stackId", "targetKnowledge", "targets" }) {
            var malformed = wire.DeepClone(); malformed["records"]![0]!["announcement"]!.AsObject().Remove(member);
            check(Reject(malformed), "An announcement requires its explicit " + member + " contract");
        }
        foreach (string member in new[] { "key", "selectable", "snapshot", "knownFace" }) {
            var malformed = wire.DeepClone(); malformed["records"]![0]!["announcement"]!["targets"]![0]![member] = "must-not-enter-history";
            check(Reject(malformed), "An announced target cannot carry current " + member + " authority or raw face data");
        }
        foreach (var (name, mutate) in new (string, Action<JsonNode>)[] {
            ("unknown target kind", n => n["kind"] = "permanent"),
            ("negative group", n => n["groupIndex"] = -1),
            ("negative target index", n => n["targetIndex"] = -1),
            ("negative allocation", n => n["allocatedAmount"] = -1),
            ("mixed player/card identity", n => n["playerId"] = 1),
            ("control character identity", n => n["cardId"] = "bad\nidentity") }) {
            var malformed = wire.DeepClone(); mutate(malformed["records"]![0]!["announcement"]!["targets"]![0]!);
            check(Reject(malformed), "History rejects an announced target with " + name);
        }
        var duplicateSlot = wire.DeepClone(); duplicateSlot["records"]![0]!["announcement"]!["targets"]![1]!["groupIndex"] = 0;
        check(Reject(duplicateSlot), "Two targets cannot claim the same announcement group and target ordinal");
        var mixedVariant = wire.DeepClone(); mixedVariant["records"]![0]!["payment"] = new JsonObject { ["kind"] = "completed", ["payerId"] = 0, ["origin"] = "manual" };
        check(Reject(mixedVariant), "One history record cannot combine an announcement with a second payload variant");
        var unknownKnowledge = wire.DeepClone(); unknownKnowledge["records"]![0]!["announcement"]!["targetKnowledge"] = "Probably";
        check(Reject(unknownKnowledge), "Historical target knowledge remains a closed explicit Known or Unknown value");
    }
    private static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
    private static bool Reject(JsonNode value, int maximumVersion = HistoryFacts.SchemaVersion)
    { try { ForgeHistoryDecoder.Decode(Element(value), maximumVersion); return false; } catch (IOException) { return true; } }
}
