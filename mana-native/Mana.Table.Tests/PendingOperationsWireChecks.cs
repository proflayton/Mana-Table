using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class PendingOperationsWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var graphJson = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
        var wire = JsonNode.Parse("""
            {"id":"pending-operations","status":"playing","viewerId":0,"turn":3,"phaseKey":"MAIN1",
             "players":[{"id":0,"name":"Actor","life":3,"mana":{},"zones":[]},
                        {"id":1,"name":"Opponent","life":40,"mana":{},"zones":[]}],
             "stack":[{"id":"spell-1","name":"Pending spell","text":"","ability":false,"kind":"spell","controllerId":1,
                       "card":{"name":"Pending spell","visualId":"spell-face","combatId":"spell-face"}}]}
            """)!;
        GameSnapshot Decode(JsonNode node) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node))!;
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var legacy = Decode(wire);
        check(legacy.Stack[0].OperationGraph == ActionOperationGraph.Unknown,
            "An older provider's absent pending graph stays explicitly unknown rather than deriving effects from spell wording");

        var source = new SemanticEntity("source", SemanticEntityKind.Card, "spell-face", ControllerId: 1, OwnerId: 1);
        var player = new SemanticEntity("recipient", SemanticEntityKind.Player, PlayerId: 0, ControllerId: 0);
        var node = new ActionOperation(0, ActionEffectKind.Damage) {
            Knowledge = SemanticKnowledge.Known, Amount = 3, AmountKnowledge = SemanticKnowledge.Known, ActorPlayerId = 1,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null, [source]),
            Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, [player]),
            Targets = new(SemanticKnowledge.Known, 1, 1, [player])
        };
        var graph = new ActionOperationGraph(SemanticKnowledge.Known, 0, [node]);
        JsonNode WithGraph(ActionOperationGraph value) {
            var changed = wire.DeepClone(); changed["stack"]![0]!["operationGraph"] = JsonSerializer.SerializeToNode(value, graphJson);
            return changed;
        }
        var state = Decode(WithGraph(graph));
        check(state.Stack[0].OperationGraph is { Knowledge: SemanticKnowledge.Known, Operations.Length: 1 }
                && state.Stack[0].OperationGraph.Operations[0].Targets!.Candidates[0].PlayerId == 0
                && state.Decision == null && !GameCommandPermissions.Allows(state, new SelectPlayer(state.Id, "invented", 0)),
            "The shipping adapter retains actual pending damage and its chosen recipient without granting a target or response command");
        foreach (var invalid in new[] {
            graph with { RootId = 77 }, graph with { Operations = [node, node] },
            graph with { Operations = [node with { Links = [new(0, OperationLinkKind.Sequence)] }] },
            graph with { Operations = [node with { Links = [new(77, OperationLinkKind.Sequence)] }] },
            graph with { Operations = [node with { Conditional = true }] },
            graph with { Operations = [node with { Costs = [new(ActionCostKind.Mana, 2, false, "")] }] },
            graph with { Operations = [node with { Targets = new(SemanticKnowledge.Known, 0, 1, [player]) }] },
            graph with { Operations = [node with { Recipients = node.Recipients with { Entities = [player with { PlayerId = 77 }] } }] },
            graph with { Operations = [node with { Sources = node.Sources with { Entities = [source with { CardId = "hidden-library-face" }] } }] }
        }) check(Reject(WithGraph(invalid)), "Pending wire rejects malformed, hypothetical, cyclic or undisclosed operation semantics before publication");
        var absentOperations = WithGraph(graph); absentOperations["stack"]![0]!["operationGraph"]!["Operations"] = null;
        check(Reject(absentOperations), "A pending graph cannot substitute a null operation collection for its explicit finite contents");
        var concealed = WithGraph(graph); concealed["stack"]![0]!["card"]!["faceDown"] = true;
        check(Reject(concealed), "A face-down pending spell cannot disclose the operation graph of its concealed printing");

        var counter = new SemanticEntity("target-spell", SemanticEntityKind.Spell, "spell-face", ControllerId: 1, OwnerId: 1) { StackItemId = "spell-1" };
        var counterNode = new ActionOperation(0, ActionEffectKind.CounterSpell) {
            Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
                [new("counter-source", SemanticEntityKind.Card, "counter-face", ControllerId: 0, OwnerId: 0)]),
            Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, [counter]),
            Targets = new(SemanticKnowledge.Known, 1, 1, [counter])
        };
        var counterWire = WithGraph(graph);
        var stack = (JsonArray)counterWire["stack"]!;
        var copy = stack[0]!.DeepClone(); copy["id"] = "spell-2"; stack.Add(copy);
        var response = JsonNode.Parse("""
            {"id":"counter","name":"Counter","text":"","ability":false,"kind":"spell","controllerId":0,
             "card":{"name":"Counter","visualId":"counter-face","combatId":"counter-face"}}
            """)!;
        response["operationGraph"] = JsonSerializer.SerializeToNode(new ActionOperationGraph(SemanticKnowledge.Known, 0, [counterNode]), graphJson);
        stack.Add(response);
        var exact = Decode(counterWire);
        check(exact.Stack[2].OperationGraph.Operations[0].Targets!.Candidates[0].StackItemId == "spell-1",
            "Counter semantics distinguish actual stack occurrences even when multiple actions share the same source portrait");
        foreach (string invalidId in new[] { "missing-stack-item", "" }) {
            var invalid = counterWire.DeepClone();
            invalid["stack"]![2]!["operationGraph"]!["Operations"]![0]!["Targets"]!["Candidates"]![0]!["StackItemId"] = invalidId;
            check(Reject(invalid), "A counter target cannot bind a missing stack occurrence or ambiguously infer one from a shared source card");
        }
    }
}
