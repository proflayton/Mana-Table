using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class ManaReductionWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"mana-contribution","kind":"input","task":"selection","ok":"Done","okEnabled":true,"cancelEnabled":false,
             "sourceCardId":"paying","sourceCard":{"name":"Paying spell","visualId":"paying"},"sourceZone":"Stack",
             "manaReduction":{"remainingManaParts":[{"symbol":"{G}","count":1},{"symbol":"{1}","count":2}]}}
            """)!;
        Decision Decode(JsonNode node) => ForgeDecisionDecoder.Decode(JsonSerializer.SerializeToElement(node), new HashSet<int> { 0 });
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var choice = Decode(wire);
        check(choice is { Payload: EntitySelectionPayload { ManaReduction: { } remaining }, Context.Source.CardId: "paying" }
            && remaining.RemainingManaParts.SequenceEqual([new("{G}", 1), new("{1}", 2)]),
            "Mana-contribution selection exposes the same pending spell and exact current counted cost without inventing a payment input");
        var zero = wire.DeepClone(); zero["manaReduction"]!["remainingManaParts"] = new JsonArray();
        var covered = Decode(zero);
        var absent = wire.DeepClone(); ((JsonObject)absent).Remove("manaReduction");
        check(covered.Payload is EntitySelectionPayload { ManaReduction.RemainingManaParts.IsEmpty: true }
            && Decode(absent).Payload is EntitySelectionPayload { ManaReduction: null },
            "An explicitly empty remaining contribution bill is covered; an older or unknown context never becomes known zero");
        GameSnapshot State(Decision decision) => new("reduction-wire", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [], [], null, decision);
        check(GameCommandPermissions.Allows(State(covered), new CompleteSelection("reduction-wire", covered.Id))
            && !GameCommandPermissions.Allows(State(covered), new PayMana("reduction-wire", covered.Id))
            && !GameCommandPermissions.Allows(State(covered), new SpendPoolMana("reduction-wire", covered.Id, "G"))
            && !GameCommandPermissions.Allows(State(covered), new CancelPayment("reduction-wire", covered.Id)),
            "Known reduction progress grants no parent Auto pay, floating-pool spending or cancellation capability");
        var disabled = zero.DeepClone(); disabled["okEnabled"] = false;
        check(!GameCommandPermissions.Allows(State(Decode(disabled)), new CompleteSelection("reduction-wire", covered.Id)),
            "A covered bill cannot enable a completion which the actual current rules input disabled");
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["manaReduction"] = new JsonObject(),
            node => node["manaReduction"]!["remainingManaParts"] = null,
            node => node["manaReduction"]!["remainingManaParts"]![0]!["count"] = 0,
            node => node["manaReduction"]!["actionId"] = "invented",
            node => node["manaReduction"]!["sourceCardKey"] = "invented",
            node => node["playerChoices"] = new JsonArray(0),
            node => node["counterPayment"] = new JsonObject { ["amount"] = 1, ["remaining"] = 0 },
            node => node["task"] = "mana",
            node => { node["purpose"] = "targets"; node["min"] = 0; node["max"] = 1; }
        }) {
            var invalid = wire.DeepClone(); mutate(invalid);
            check(Reject(invalid), "Mana-reduction progress rejects missing/invalid bills, injected handles and unrelated selection authority");
        }
    }
}
