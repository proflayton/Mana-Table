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
        var numericStep = wire.DeepClone(); numericStep["records"]![0]!["execution"]!["occurrence"]!["step"] = 2;
        check(Reject(numericStep), "Execution steps keep their closed named wire values inside archived history");
        var duplicate = wire.DeepClone(); duplicate["records"]![2]!["sequence"] = 2;
        check(Reject(duplicate), "A source batch cannot assign one ordinal to two historical payloads");
        using var unavailable = JsonDocument.Parse("""{"error":"Different text","errorCode":"history-unavailable"}""");
        using var prose = JsonDocument.Parse("""{"error":"history-unavailable"}""");
        check(ForgeEngine.RequestFailure("matchHistory", unavailable.RootElement) is NotSupportedException
            && ForgeEngine.RequestFailure("matchState", unavailable.RootElement) is not NotSupportedException
            && ForgeEngine.RequestFailure("matchHistory", prose.RootElement) is not NotSupportedException,
            "Only the exact typed history-unavailable response on the read-only history method negotiates unsupported history");
    }
    private static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
    private static bool Reject(JsonNode value) { try { ForgeHistoryDecoder.Decode(Element(value)); return false; } catch (IOException) { return true; } }
}
