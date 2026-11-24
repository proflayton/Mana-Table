using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class RememberedMillLifeLossWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
        var wire = JsonNode.Parse("""
            {"state":{"id":"mill-loss-wire","status":"playing","viewerId":0,"decisionOwnerId":0,
              "players":[{"id":0,"name":"Chooser","life":12,"human":false,"lost":false,"mana":{},"zones":[]},
                {"id":1,"name":"Controller","life":40,"human":false,"lost":false,"mana":{},
                 "zones":[{"name":"Battlefield","count":1,"cards":[{"visualId":"source","name":"Opaque artifact","type":"Artifact","counters":{"Influence":4}}]}]}],
              "stack":[],"prompt":{"id":"current-choice","kind":"choice","task":"choice","min":1,"max":1,
                "choices":[{"index":7,"label":"Untrusted label"},{"index":3,"label":"Another label"}]}},
             "domain":{"version":0,"coverage":"NotApplicable","sources":[],"actions":[]},"semantics":{}}
            """)!;
        var graph = Graph();
        wire["semantics"] = JsonSerializer.SerializeToNode(new DecisionSemantics { Options = [new(7, ActionEffectKind.Mill) {
            OperationGraph = graph, RememberedMillLifeLoss = new(1, "source", "Influence", 4, 29) }] }, json);
        OwnedDecisionObservation Decode(JsonNode value) => ForgeEngine.DecodeOwnedDecisionObservation(JsonSerializer.SerializeToElement(value))!;
        bool Reject(JsonNode value) { try { Decode(value); return false; } catch (IOException) { return true; } }
        var accepted = Decode(wire);
        check(accepted.Semantics.Options[0].RememberedMillLifeLoss is { Version: 1, SourceCardId: "source", CounterType: "Influence", ObservedCount: 4, LifeLossOperationId: 29 }
            && accepted.Semantics.Options[0].OperationGraph.Knowledge == SemanticKnowledge.Unknown,
            "The production owned decoder admits one closed public dependency while retaining unknown future loss");
        using (var automated = JsonDocument.Parse("[" + wire.ToJsonString().Insert(1, "\"seatId\":0,") + "]"))
            check(ForgeEngine.DecodeAutomatedObservations(automated.RootElement)[0].Semantics.Options[0].RememberedMillLifeLoss == accepted.Semantics.Options[0].RememberedMillLifeLoss,
                "Automated and ordinary owned observation ports share exactly the same certificate admission");
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["observedCount"] = 3,
            node => node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["sourceCardId"] = "foreign",
            node => { node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["counterType"] = "invented";
                node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["observedCount"] = 0; },
            node => node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["lifeLossOperationId"] = 999,
            node => node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["version"] = 2,
            node => node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["observedCount"] = "4",
            node => ((JsonObject)node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!).Remove("observedCount"),
            node => node["semantics"]!["options"]![0]!["rememberedMillLifeLoss"]!["actionId"] = "invented",
            node => node["state"]!["decisionOwnerId"] = 1,
            node => node["state"]!["prompt"]!["min"] = 0,
            node => node["state"]!["prompt"]!["ordered"] = true,
            node => node["state"]!["players"]![1]!["zones"]![0]!["cards"]![0]!["faceDown"] = true,
            node => node["semantics"]!["options"]![0]!["operationGraph"]!["operations"]![1]!["amount"] = 28,
            node => node["semantics"]!["options"]![0]!["operationGraph"]!["operations"]![0]!["links"]![0]!["kind"] = "Additional"
        }) {
            var changed = wire.DeepClone(); mutate(changed);
            check(Reject(changed), "The production owned decoder rejects malformed, foreign, quantified and unowned dependency evidence");
        }
        var duplicate = wire.ToJsonString().Replace("\"observedCount\":4", "\"observedCount\":4,\"ObservedCount\":4", StringComparison.Ordinal);
        bool duplicateRejected;
        try { ForgeEngine.DecodeOwnedDecisionObservation(JsonDocument.Parse(duplicate).RootElement); duplicateRejected = false; }
        catch (IOException) { duplicateRejected = true; }
        check(duplicateRejected, "Duplicate certificate keys are rejected instead of selecting the last value");
        var old = wire.DeepClone(); ((JsonObject)old["semantics"]!["options"]![0]!).Remove("rememberedMillLifeLoss");
        check(Decode(old).Semantics.Options[0].RememberedMillLifeLoss == null,
            "An actual old-wire option stays readable and cannot acquire the certificate from labels or graph shape");
        check(GameCommandPermissions.Allows(accepted.State, new ChooseOptions(accepted.State.Id, "current-choice", [7]))
            && !GameCommandPermissions.Allows(accepted.State, new ChooseOptions(accepted.State.Id, "retired-choice", [7]))
            && !GameCommandPermissions.Allows(accepted.State, new ChooseOptions(accepted.State.Id, "current-choice", [999]))
            && !GameCommandPermissions.Allows(accepted.State, new SelectCard(accepted.State.Id, "current-choice", "source")),
            "A typed dependency grants no current card handle, invented option or retired decision authority");
    }

    private static ActionOperationGraph Graph()
    {
        EffectRecipients Source(int id) => new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
            [new($"op{id}:card:source", SemanticEntityKind.Card, "source", ControllerId: 1, OwnerId: 1)]);
        EffectRecipients Player(int id, int player, EffectRecipientBinding binding) => new(SemanticKnowledge.Known, binding, null,
            [new($"op{id}:player:{player}", SemanticEntityKind.Player, PlayerId: player, ControllerId: player)]);
        return new(SemanticKnowledge.Unknown, 17, [
            new(17, ActionEffectKind.Mill) { ActorPlayerId = 1, Sources = Source(17), Recipients = Player(17, 1, EffectRecipientBinding.Actor), Links = [new(29, OperationLinkKind.Sequence)] },
            new(29, ActionEffectKind.LifeLoss) { ActorPlayerId = 1, Sources = Source(29), Recipients = Player(29, 0, EffectRecipientBinding.Explicit), Links = [new(41, OperationLinkKind.Sequence)] },
            new(41, ActionEffectKind.Cleanup) { ActorPlayerId = 1, Knowledge = SemanticKnowledge.Known, Sources = Source(41), Recipients = Source(41) }
        ]);
    }
}
