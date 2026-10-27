using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class ManaColorPaymentWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"choose-color","kind":"choice","task":"choice","min":1,"max":1,
             "choices":[{"index":3,"label":"Blue","mana":"{U}"},{"index":8,"label":"Red","mana":"{R}"}],
             "sourceCardId":"producer","sourceCard":{"name":"Producer","visualId":"producer"},"sourceZone":"Battlefield",
             "manaPaymentContext":{"payerId":0,"unpaidManaParts":[{"symbol":"{U}","count":1},{"symbol":"{R}","count":1}],
               "source":{"cardId":"paying","card":{"name":"Paying card","visualId":"paying"},"controllerId":0,"zone":"Hand"}}}
            """)!;
        Decision Decode(JsonNode node) => ForgeDecisionDecoder.Decode(JsonSerializer.SerializeToElement(node), new HashSet<int> { 0 });
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var choice = Decode(wire);
        var payload = (OptionsPayload)choice.Payload;
        check(payload.ManaPaymentContext is { PayerId: 0, Source.CardId: "paying" } payment
            && payment.UnpaidManaParts.SequenceEqual([new("{U}", 1), new("{R}", 1)])
            && choice.Context.Source?.CardId == "producer" && payload.Selection.Choices.Select(option => option.Index).SequenceEqual([3, 8]),
            "A color callback retains distinct producer, exact paying source, bill and original offered color indices");
        var state = new GameSnapshot("mana-color-wire", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [], [], null, choice);
        check(GameCommandPermissions.Allows(state, new ChooseOptions(state.Id, choice.Id, [8]))
            && !GameCommandPermissions.Allows(state, new ChooseOptions(state.Id, choice.Id, [0]))
            && !GameCommandPermissions.Allows(state, new PayMana(state.Id, choice.Id))
            && !GameCommandPermissions.Allows(state, new CancelPayment(state.Id, choice.Id))
            && !GameCommandPermissions.Allows(state, new SpendPoolMana(state.Id, choice.Id, "R")),
            "Owned payment context grants only the child's existing color options, never parent payment or pool commands");
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["manaPaymentContext"]!["payerId"] = 17,
            node => node["manaPaymentContext"]!["unpaidManaParts"]![0]!["count"] = 0,
            node => node["manaPaymentContext"]!["actionId"] = "invented",
            node => node["manaPaymentContext"]!["source"]!["card"]!["key"] = "not-a-source-offer",
            node => node["manaPaymentContext"]!["source"]!["card"]!["key"] = null,
            node => node["manaPaymentContext"]!["source"]!["card"]!["visualId"] = null,
            node => node["manaPaymentContext"]!["source"]!["card"]!["combatId"] = null,
            node => node["manaPaymentContext"]!["source"]!["card"]!["name"] = null,
            node => node["choices"]![0]!["mana"] = "",
            node => node["max"] = 2,
            node => { node["task"] = "priority"; node["kind"] = "input"; }
        }) {
            var invalid = wire.DeepClone(); mutate(invalid);
            check(Reject(invalid), "The color-payment wire rejects foreign payers, malformed bills, handles, untyped colors and unrelated choice authority");
        }
        var ordinary = wire.DeepClone(); ((JsonObject)ordinary).Remove("manaPaymentContext");
        check(Decode(ordinary).Payload is OptionsPayload { ManaPaymentContext: null },
            "An ordinary color choice or older provider does not acquire remembered payment context");
    }
}
