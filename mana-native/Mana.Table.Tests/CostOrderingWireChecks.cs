using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class CostOrderingWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            [{"seatId":0,"state":{"id":"cost-order-wire","status":"playing","viewerId":0,
              "players":[{"id":0,"name":"Actor","life":40,"mana":{},"zones":[]}],"stack":[],
              "prompt":{"id":"cost-order","kind":"choice","task":"choice","ordered":true,"min":3,"max":3,
                "choices":[{"index":17,"label":"First"},{"index":29,"label":"Second"},{"index":5,"label":"Third"}]}},
              "domain":{"version":0,"coverage":"NotApplicable","sources":[],"actions":[]},
              "semantics":{"selectionIsCost":true,"orderingPurpose":"CostPayment","defaultOrder":[17,29,5]}}]
            """)!;
        AutomatedSeatObservation Decode(JsonNode value)
            => ForgeEngine.DecodeAutomatedObservations(JsonSerializer.SerializeToElement(value)).Single();
        bool Reject(JsonNode value) {
            try { Decode(value); return false; }
            catch (ForgeAutomatedObservationException) { return true; }
        }
        var accepted = Decode(wire);
        check(accepted.State.Decision!.Payload is OrderingPayload
                && accepted.Semantics.OrderingPurpose == DecisionOrderingPurpose.CostPayment
                && accepted.Semantics.DefaultOrder.SequenceEqual([17, 29, 5]),
            "The automated adapter preserves exact typed cost ordering across nonconsecutive provider indices");
        var legacy = wire.DeepClone(); legacy[0]!["semantics"] = new JsonObject();
        check(Decode(legacy).Semantics is { OrderingPurpose: DecisionOrderingPurpose.Unknown, DefaultOrder.IsEmpty: true },
            "A legacy automated ordering observation without default metadata remains unknown");
        foreach (string metadata in new[] {
            """{"selectionIsCost":true,"orderingPurpose":"CostPayment","defaultOrder":null}""",
            """{"selectionIsCost":true,"orderingPurpose":"CostPayment","defaultOrder":[17,29]}""",
            """{"selectionIsCost":true,"orderingPurpose":"CostPayment","defaultOrder":[17,29,29]}""",
            """{"selectionIsCost":true,"orderingPurpose":"CostPayment","defaultOrder":[5,17,29]}""",
            """{"selectionIsCost":true,"orderingPurpose":"Unknown","defaultOrder":[17,29,5]}""",
            """{"selectionIsCost":true,"orderingPurpose":"FuturePurpose","defaultOrder":[17,29,5]}""",
            """{"selectionIsCost":true,"orderingPurpose":1,"defaultOrder":[17,29,5]}"""
        }) {
            var malformed = wire.DeepClone(); malformed[0]!["semantics"] = JsonNode.Parse(metadata);
            check(Reject(malformed), "Malformed automated cost-order purpose or default is rejected before publishing a batch");
        }
        var partial = wire.DeepClone(); partial[0]!["state"]!["prompt"]!["min"] = 0;
        var options = wire.DeepClone(); options[0]!["state"]!["prompt"]!["ordered"] = false;
        check(Reject(partial) && Reject(options),
            "The wire boundary rejects a cost-order default attached to a partial or unordered selection");
    }
}
