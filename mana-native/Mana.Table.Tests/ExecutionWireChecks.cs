using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// The shipping decoder admits historical execution facts, never new action or face permissions.
internal static class ExecutionWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"execution-wire","status":"playing","viewerId":0,"turn":3,"phaseKey":"MAIN1",
             "players":[{"id":0,"name":"Actor","life":40,"mana":{},"zones":[]}],
             "stack":[{"id":"stack-1","name":"Source ability","text":"Draw a card.","ability":true,"kind":"activated","executionId":"activation-1"}],
             "prompt":{"id":"current-payment","kind":"input","task":"mana","okEnabled":true,"cancelEnabled":true,
               "executionId":"activation-1","executionStep":"cost"},
             "activity":[{"id":1,"turn":3,"phaseKey":"MAIN1","kind":"counters","message":"A counter was paid.",
               "executionId":"activation-1","executionStep":"cost"}],
             "executions":{"firstSequence":1,"lastSequence":9,"occurrences":[
               {"id":"activation-1","budgetRootId":"activation-1","kind":"activation","step":"cost","actorPlayerId":0,
                "sourceCardId":"historical-source","sourceName":"Formerly visible source"},
               {"id":"mana-1","budgetRootId":"activation-1","parentId":"activation-1","kind":"mana","step":"complete","outcome":"resolved","actorPlayerId":0}],
               "transitions":[{"sequence":1,"executionId":"activation-1","step":"preparation"},
                 {"sequence":3,"executionId":"mana-1","step":"cost"},
                 {"sequence":4,"executionId":"mana-1","step":"complete","outcome":"resolved"},
                 {"sequence":8,"executionId":"activation-1","step":"cost"}]}}
            """)!;
        GameSnapshot Decode(JsonNode node, bool spectator = false) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node), spectator)!;
        bool Reject(JsonNode node, bool spectator = false) { try { Decode(node, spectator); return false; } catch (IOException) { return true; } }
        var state = Decode(wire);
        check(state.Executions is { FirstSequence: 1, LastSequence: 9, Occurrences.Length: 2, Transitions.Length: 4 }
                && state.Decision is { ExecutionId: "activation-1", ExecutionStep: ExecutionStep.Cost }
                && state.Stack[0].ExecutionId == "activation-1" && state.Activity[0].ExecutionId == "activation-1"
                && ExecutionFrames.Find(state, "mana-1") is { ParentId: "activation-1", BudgetRootId: "activation-1", Outcome: ExecutionOutcome.Resolved },
            "The provider wire retains one activation across its current payment, stack and committed changes, with an independently identified nested mana execution");
        check(state.Executions!.Transitions.Select(item => item.Sequence).SequenceEqual([1L, 3L, 4L, 8L]),
            "Sparse authorized execution sequences remain ordered facts within the provider's global retained bounds");
        check(CardViews.FindZoneOccurrence(state, "historical-source") == null
                && !CardViews.Visible(state).Any(card => CardViews.Identity(card) == "historical-source")
                && !GameCommandPermissions.Allows(state, new UseManaSource(state.Id, state.Decision!.Id, "historical-source"))
                && !GameCommandPermissions.Allows(state, new UseOfferedAction(state.Id, state.Decision!.Id, "historical-source", "activation-1"))
                && !GameCommandPermissions.Allows(state, new PayMana(state.Id, "activation-1"))
                && GameCommandPermissions.Allows(state, new PayMana(state.Id, state.Decision!.Id)),
            "A historical source and execution identity reveal no inspectable card or executable offer; only the independently owned current payment can be answered");

        foreach (var (collection, field) in new[] { ("occurrences", "kind"), ("occurrences", "step"), ("transitions", "step") })
            foreach (string invalid in new[] { "17", "\"17\"", "\"Unknown\"", "\"Activation\"", "\"cost, choice\"", "false", "null" }) {
                var changed = wire.DeepClone(); changed["executions"]![collection]![0]![field] = JsonNode.Parse(invalid);
                check(Reject(changed), "Execution wire rejects noncanonical " + collection + "/" + field + " value " + invalid);
            }
        foreach (string invalid in new[] { "0", "\"0\"", "\"Resolved\"", "\"resolved, cancelled\"", "\"unknown\"", "true", "null" }) {
            var changed = wire.DeepClone(); changed["executions"]!["occurrences"]![1]!["outcome"] = JsonNode.Parse(invalid);
            check(Reject(changed), "A completed execution requires an exact closed named outcome: " + invalid);
            changed = wire.DeepClone(); changed["executions"]!["transitions"]![2]!["outcome"] = JsonNode.Parse(invalid);
            check(Reject(changed), "A terminal transition rejects noncanonical or absent outcomes: " + invalid);
        }
        foreach (string invalid in new[] { "1", "\"Cost\"", "\"cost, choice\"", "\"unknown\"", "null" }) {
            var changed = wire.DeepClone(); changed["prompt"]!["executionStep"] = JsonNode.Parse(invalid);
            check(Reject(changed), "A current decision's execution step uses the same strict protocol vocabulary: " + invalid);
            changed = wire.DeepClone(); changed["activity"]![0]!["executionStep"] = JsonNode.Parse(invalid);
            check(Reject(changed), "Historical activity cannot inject a malformed execution step: " + invalid);
        }

        foreach (var mutate in new Action<JsonNode>[] {
            node => node["executions"]!["firstSequence"] = 0,
            node => node["executions"]!["firstSequence"] = 11,
            node => node["executions"]!["lastSequence"] = -1,
            node => node["executions"]!["transitions"]![1]!["sequence"] = 1,
            node => node["executions"]!["transitions"]![1]!["sequence"] = 0,
            node => node["executions"]!["transitions"]![3]!["sequence"] = 10,
            node => node["executions"]!["transitions"]![1]!["executionId"] = "missing",
            node => node["executions"]!["occurrences"]![1]!["budgetRootId"] = "missing",
            node => node["executions"]!["occurrences"]![1]!["parentId"] = "missing",
            node => node["executions"]!["occurrences"]![0]!["parentId"] = "mana-1",
            node => node["executions"]!["occurrences"]![1]!["id"] = "activation-1",
            node => node["executions"]!["occurrences"]![0]!["actorPlayerId"] = 99,
            node => node["executions"]!["occurrences"]![0]!["id"] = new string('x', 257),
            node => node["executions"]!["occurrences"]![0]!["outcome"] = "resolved",
            node => node["executions"]!["transitions"]![0]!["outcome"] = "resolved",
            node => { node["executions"]!["transitions"]![3]!["executionId"] = "mana-1"; },
            node => node["prompt"]!["executionId"] = "missing",
            node => node["prompt"]!["executionStep"] = "choice",
            node => node["stack"]![0]!["executionId"] = "missing",
            node => node["activity"]![0]!["executionId"] = "missing",
            node => node["activity"]![0]!["executionId"] = "",
            node => { node["executions"]!["occurrences"]![0]!["step"] = "complete"; node["executions"]!["occurrences"]![0]!["outcome"] = "cancelled"; }
        }) {
            var changed = wire.DeepClone(); mutate(changed);
            check(Reject(changed), "Malformed execution bounds, ancestry, terminal reuse or current/historical references fail at the provider boundary");
        }
        foreach (string field in new[] { "firstSequence", "lastSequence", "occurrences", "transitions" }) {
            var changed = wire.DeepClone(); ((JsonObject)changed["executions"]!).Remove(field);
            check(Reject(changed), "A provider execution frame requires its declared " + field);
        }
        foreach (string field in new[] { "id", "budgetRootId", "kind", "step" }) {
            var changed = wire.DeepClone(); ((JsonObject)changed["executions"]!["occurrences"]![0]!).Remove(field);
            check(Reject(changed), "An execution occurrence cannot acquire a default for its missing " + field);
        }
        foreach (string field in new[] { "sequence", "executionId", "step" }) {
            var changed = wire.DeepClone(); ((JsonObject)changed["executions"]!["transitions"]![0]!).Remove(field);
            check(Reject(changed), "An execution transition requires its explicit " + field);
        }
        foreach (string retained in new[] { "prompt", "stack", "activity" }) {
            var changed = wire.DeepClone(); ((JsonObject)changed).Remove("executions");
            if (retained != "prompt") ((JsonObject)changed).Remove("prompt");
            if (retained != "stack") changed["stack"] = new JsonArray();
            if (retained != "activity") changed["activity"] = new JsonArray();
            check(Reject(changed), "A migrated " + retained + " reference cannot survive without its declared execution frame");
        }
        var legacy = wire.DeepClone(); ((JsonObject)legacy).Remove("executions");
        ((JsonObject)legacy).Remove("prompt"); legacy["stack"] = new JsonArray(); legacy["activity"] = new JsonArray();
        check(Decode(legacy).Executions == null, "An unmigrated provider supplies no execution history rather than fabricated preparation or outcomes");

        var spectator = wire.DeepClone(); spectator["viewerId"] = -1; ((JsonObject)spectator).Remove("prompt"); spectator["activity"] = new JsonArray();
        spectator["executions"]!["occurrences"] = new JsonArray(wire["executions"]!["occurrences"]![0]!.DeepClone());
        spectator["executions"]!["transitions"] = new JsonArray(wire["executions"]!["transitions"]![0]!.DeepClone(), wire["executions"]!["transitions"]![3]!.DeepClone());
        var publicState = Decode(spectator, spectator: true);
        check(publicState.Executions is { FirstSequence: 1, LastSequence: 9, Occurrences.Length: 1, Transitions.Length: 2 }
                && publicState.Decision == null && publicState.ActionSources.IsEmpty && publicState.PlayAccesses.IsEmpty
                && !GameCommandPermissions.Allows(publicState, new PayMana(publicState.Id, "activation-1"))
                && !CardViews.Visible(publicState).Any(),
            "A viewer-filtered spectator execution stream preserves sparse public correlation without private decisions, hidden card faces or command authority");
        var illegalSpectator = spectator.DeepClone(); illegalSpectator["prompt"] = wire["prompt"]!.DeepClone();
        check(Reject(illegalSpectator, spectator: true), "Execution history cannot authorize a spectator's otherwise forbidden decision");
    }
}
