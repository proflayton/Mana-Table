using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class HandReductionWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"cleanup-wire","status":"playing","viewerId":0,"turn":7,"phaseKey":"CLEANUP","decisionOwnerId":0,
             "players":[{"id":0,"name":"Acting seat","life":40,"mana":{},"maxHandSize":1,"zones":[
                {"name":"Hand","count":2,"cards":[{"name":"Same name","visualId":"hand-a","combatId":"hand-a"},
                    {"name":"Same name","visualId":"hand-b","combatId":"hand-b"}]}]},
                {"id":1,"name":"Other seat","life":40,"mana":{},"zones":[]}],"stack":[],
             "prompt":{"id":"cleanup-choice","kind":"choice","task":"choice","min":1,"max":1,
                "choices":[{"index":5,"label":"Same name","cardId":"hand-a","card":{"name":"Same name","visualId":"hand-a","combatId":"hand-a"}},
                    {"index":11,"label":"Same name","cardId":"hand-b","card":{"name":"Same name","visualId":"hand-b","combatId":"hand-b"}}],
                "handReduction":{"playerId":0,"handSize":2,"maximumHandSize":1}}}
            """)!;
        GameSnapshot Decode(JsonNode node) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node))!;
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var snapshot = Decode(wire);
        check(HandReductionFacts.IsCurrent(snapshot) && snapshot.Decision!.Payload is OptionsPayload { HandReduction: { PlayerId: 0, HandSize: 2, MaximumHandSize: 1 } }
            && GameCommandPermissions.Allows(snapshot, new ChooseOptions(snapshot.Id, snapshot.Decision.Id, [11])),
            "The production decoder preserves a whole owned hand reduction as one indexed discard choice with distinct same-name occurrences");
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["prompt"]!["handReduction"]!["playerId"] = 99,
            node => node["prompt"]!["handReduction"]!["playerId"] = 1,
            node => node["prompt"]!["handReduction"]!["handSize"] = 3,
            node => node["prompt"]!["handReduction"]!["maximumHandSize"] = 0,
            node => ((JsonObject)node["prompt"]!["handReduction"]!).Remove("maximumHandSize"),
            node => node["prompt"]!["handReduction"]!["handSize"] = "two",
            node => node["prompt"]!["max"] = 2,
            node => node["prompt"]!["ordered"] = true,
            node => node["prompt"]!["purpose"] = "abilities",
            node => node["prompt"]!["manaPaymentContext"] = new JsonObject(),
            node => node["prompt"]!["counterPaymentChoice"] = new JsonObject(),
            node => node["prompt"]!["disclosedCards"] = new JsonArray(),
            node => node["prompt"]!["libraryCards"] = new JsonArray(),
            node => node["prompt"]!["choices"]![1]!["cardId"] = "hand-a",
            node => node["prompt"]!["choices"]![0]!["cardId"] = "foreign-occurrence",
            node => node["prompt"]!["choices"]![0]!["card"]!["key"] = "new-action-key",
            node => node["prompt"]!["choices"]![0]!["card"]!["selectable"] = true,
            node => node["prompt"]!["choices"]![0]!["card"] = null,
            node => node["players"]![0]!["maxHandSize"] = null,
            node => node["players"]![0]!["zones"]![0]!["count"] = 3,
            node => ((JsonArray)node["players"]![0]!["zones"]![0]!["cards"]!).RemoveAt(0),
            node => node["players"]![0]!["zones"]![0]!["cards"]![0]!["visualId"] = "different-occurrence"
        }) {
            var changed = wire.DeepClone(); mutate(changed);
            check(Reject(changed), "Production cleanup ingress rejects stale ownership, hand facts, missing occurrences and incompatible authority before projection");
        }
        var generic = wire.DeepClone(); ((JsonObject)generic["prompt"]!).Remove("handReduction");
        ((JsonArray)generic["players"]![0]!["zones"]![0]!["cards"]!).Clear();
        check(Decode(generic).Decision!.Payload is OptionsPayload { HandReduction: null },
            "A generic atomic discard remains answerable when a blinded hand cannot publish full occurrence corroboration for keeper complements");
        var wrongTask = wire.DeepClone(); wrongTask["prompt"]!["task"] = "selection"; wrongTask["prompt"]!["kind"] = "input";
        check(Reject(wrongTask), "Cleanup hand facts cannot be smuggled onto an entity-selection input");
    }
}
