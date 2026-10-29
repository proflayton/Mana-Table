using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class FiniteStackYieldWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"finite-yield","status":"playing","viewerId":0,"turn":7,"phaseKey":"MAIN1","decisionOwnerId":0,"priorityPlayerId":0,
             "stackActionVersion":3,"resolutionChoiceVersion":2,
             "players":[{"id":0,"name":"Actor","life":40,"mana":{},"zones":[]}],
             "stack":[{"id":"stack-one","name":"Source","kind":"triggered","ability":true,"executionId":"one","controllerId":0}],
             "executions":{"firstSequence":1,"lastSequence":0,"transitions":[],"occurrences":[
                {"id":"one","budgetRootId":"one","kind":"trigger","actorPlayerId":0,"step":"stack"}]},
             "finiteStackYield":{"grantId":"","status":"Idle","executionIds":[],"remainingExecutionIds":[],"stackActionVersion":3,"resolutionChoiceVersion":2,"reasonCode":"","automaticPasses":0},
             "prompt":{"id":"priority","kind":"input","task":"priority","okEnabled":true,"cancelEnabled":false}}
            """)!;
        GameSnapshot Decode(JsonNode node) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node))!;
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var state = Decode(wire);
        var command = new GrantFiniteStackYield(state.Id, state.Decision!.Id, "grant", ["one"], 3, 2);
        var scope = new ForgeCommandScope(); scope.Observe(state); var encoded = scope.Encode(command);
        check(encoded["action"] as string == "grantFiniteStackYield" && encoded["grantId"] as string == "grant"
                && encoded["expectedTask"] as string == "priority" && encoded["stackActionVersion"] is long action && action == 3
                && encoded["executionIds"] is ImmutableArray<string> ids && ids.SequenceEqual(command.ExecutionIds),
            "Finite yield uses the existing typed, observed command lane with exact prompt, members and current guards");
        var legacy = wire.DeepClone(); legacy.AsObject().Remove("finiteStackYield");
        check(!GameCommandPermissions.Allows(Decode(legacy), command), "An older provider without explicit finite-yield capability keeps ordinary priority only");
        var activeWire = wire.DeepClone(); activeWire["finiteStackYield"] = JsonNode.Parse("""
            {"grantId":"grant","status":"Active","executionIds":["one"],"remainingExecutionIds":["one"],"stackActionVersion":3,"resolutionChoiceVersion":2,"automaticPasses":0}
            """);
        var active = Decode(activeWire);
        check(!GameCommandPermissions.Allows(active, command with { GrantId = "another" }),
            "An active provider grant must be stopped before a different consent can replace it");
        var revoke = new RevokeFiniteStackYield(active.Id, "grant");
        scope.Observe(active with { Decision = null, DecisionOwnerId = 1 }); var stop = scope.Encode(revoke);
        check(stop["action"] as string == "revokeFiniteStackYield" && stop["grantId"] as string == "grant" && !stop.ContainsKey("expectedTask"),
            "Exact grant revocation remains available while another player owns input without inventing rules-input authority");
        foreach (string status in new[] { "Completed", "Revoked" }) {
            var terminal = activeWire.DeepClone(); terminal["finiteStackYield"]!["status"] = status;
            terminal["finiteStackYield"]!["remainingExecutionIds"] = new JsonArray();
            var receipt = Decode(terminal);
            check(receipt.FiniteStackYield is { ExecutionIds.Length: 1 } && GameCommandPermissions.Allows(receipt, revoke),
                "Terminal finite consent keeps its original identity and exact captured members: " + status);
        }
        foreach (var pair in new (string Key, JsonNode? Value)[] {
            ("grantId", ""), ("status", "forever"), ("status", 1), ("executionIds", new JsonArray("one", "one")),
            ("remainingExecutionIds", new JsonArray("unowned")), ("remainingExecutionIds", new JsonArray()),
            ("stackActionVersion", -1), ("resolutionChoiceVersion", -1), ("automaticPasses", -1)
        }) {
            var malformed = activeWire.DeepClone(); malformed["finiteStackYield"]![pair.Key] = pair.Value;
            check(Reject(malformed), "Malformed finite consent fails provider decoding before reaching client intent: " + pair.Key);
        }
        var oversized = activeWire.DeepClone(); oversized["finiteStackYield"]!["executionIds"] = new JsonArray(
            Enumerable.Range(0, 257).Select(index => (JsonNode?)JsonValue.Create("execution-" + index)).ToArray());
        check(Reject(oversized), "A finite advertised grant cannot exceed the shared 256-member bound");
        foreach (string required in new[] { "grantId", "status", "executionIds", "remainingExecutionIds", "stackActionVersion", "resolutionChoiceVersion" }) {
            var missing = activeWire.DeepClone(); missing["finiteStackYield"]!.AsObject().Remove(required);
            check(Reject(missing), "A capability advertisement cannot invent an omitted required consent fact: " + required);
        }
    }
}
