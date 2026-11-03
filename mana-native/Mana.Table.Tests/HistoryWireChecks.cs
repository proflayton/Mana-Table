using System.Text.Json;
using System.Text.Json.Nodes;
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
    }
    private static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
    private static bool Reject(JsonNode value) { try { ForgeHistoryDecoder.Decode(Element(value)); return false; } catch (IOException) { return true; } }
}
