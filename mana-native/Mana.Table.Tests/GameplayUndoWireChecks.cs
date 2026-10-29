using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class GameplayUndoWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"undo-wire","status":"playing","viewerId":0,"turn":4,"phaseKey":"MAIN1",
             "revision":5,"boardRevision":5,"decisionOwnerId":0,"priorityPlayerId":0,
             "players":[{"id":0,"name":"Actor","life":40,"mana":{"C":2},"zones":[
               {"name":"Battlefield","count":1,"cards":[{"visualId":"source","combatId":"source",
                "key":"current:source","name":"Sol Ring","type":"Artifact","tapped":true}]}]}],
             "stack":[],"prompt":{"id":"priority","kind":"input","task":"priority","okEnabled":true},
             "executions":{"firstSequence":1,"lastSequence":1,"transitions":[
                {"sequence":1,"executionId":"activation","step":"complete","outcome":"resolved"}],"occurrences":[
                {"id":"activation","budgetRootId":"activation","kind":"mana","step":"complete","outcome":"resolved",
                 "actorPlayerId":0,"sourceCardId":"source","sourceName":"Sol Ring"}]},
             "undo":{"barrierVersion":7,"restorationVersion":0,"lastRestoration":null,"offer":{
                "id":"opaque-offer","actionId":"action","executionId":"activation","decisionId":"priority",
                "playerId":0,"sourceCardId":"source","sourceName":"Sol Ring","barrierVersion":7}}}
            """)!;
        GameSnapshot Decode(JsonNode node, bool supported = true) =>
            ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node), certifiedUndo: supported)!;
        bool Reject(JsonNode node, bool supported = true) {
            try { Decode(node, supported); return false; } catch (IOException) { return true; }
        }
        var state = Decode(wire);
        var command = UndoFacts.Command(state)!;
        check(command is { OfferId: "opaque-offer", ActionId: "action", BarrierVersion: 7 }
                && state.Undo?.Offer?.SourceName == "Sol Ring" && Reject(wire, false),
            "Only a negotiated certified-undo provider admits the exact viewer's current mana reversal capability");
        var scope = new ForgeCommandScope(); scope.Observe(state);
        var encoded = scope.Encode(command);
        check(encoded["action"] as string == "undoCertifiedAction" && encoded["offerId"] as string == "opaque-offer"
                && encoded["actionId"] as string == "action" && encoded["undoBarrierVersion"] is long version && version == 7
                && encoded["expectedTask"] as string == "priority" && encoded["promptId"] as string == "priority"
                && !encoded.ContainsKey("key"),
            "Certified reversal encodes only the exact observed offer/action/barrier and decision, never a source key or legacy Cancel alias");
        bool Denied(GameCommand rejected) {
            try { scope.Encode(rejected); return false; } catch (EngineCommandRejectedException) { return true; }
        }
        check(Denied(command with { OfferId = "different" }) && Denied(command with { ActionId = "other" })
                && Denied(command with { BarrierVersion = 8 }) && Denied(command with { DecisionId = "old" })
                && Denied(new UndoLastAction(state.Id, state.Decision!.Id)),
            "Foreign, stale or altered certified reversals and the unsafe legacy Undo remain non-executable at the native boundary");
        foreach (string field in new[] { "id", "actionId", "executionId", "decisionId", "playerId", "sourceCardId", "sourceName", "barrierVersion" }) {
            var invalid = wire.DeepClone(); invalid["undo"]!["offer"]!.AsObject().Remove(field);
            check(Reject(invalid), "An offered reversal must explicitly supply its " + field + " rather than acquiring a default capability");
        }
        foreach (string field in new[] { "barrierVersion", "restorationVersion", "offer", "lastRestoration" }) {
            var invalid = wire.DeepClone(); invalid["undo"]!.AsObject().Remove(field);
            check(Reject(invalid), "The certified Undo state requires explicit " + field + " presence");
        }
        foreach (var change in new (string Field, object Value)[] {
            ("id", ""), ("decisionId", "other-input"), ("executionId", "missing-execution"), ("playerId", 1),
            ("sourceCardId", "different-occurrence"), ("sourceName", "Wrong face"), ("barrierVersion", 6) }) {
            var invalid = wire.DeepClone(); invalid["undo"]!["offer"]![change.Field] = JsonSerializer.SerializeToNode(change.Value);
            check(Reject(invalid), "The wire cannot fabricate reversal ownership or authority through " + change.Field);
        }
        var hidden = wire.DeepClone(); hidden["players"]![0]!["zones"]![0]!["cards"]![0]!["faceDown"] = true;
        check(Reject(hidden), "An Undo source cannot disclose or authorize a hidden battlefield face");
        var legacy = wire.DeepClone(); legacy.AsObject().Remove("undo");
        check(Decode(legacy, false).Undo == null, "Older providers with no certified Undo field retain ordinary play without an invented offer");
        var consumed = wire.DeepClone(); consumed["revision"] = 6; consumed["boardRevision"] = 6;
        consumed["undo"] = JsonNode.Parse("""
            {"barrierVersion":8,"restorationVersion":1,"offer":null,"lastRestoration":{
             "version":1,"actionId":"action","executionId":"activation","playerId":0}}
            """);
        var restored = Decode(consumed); scope.Observe(restored);
        check(UndoFacts.RestoredSince(state, restored) && !UndoFacts.CanUndo(restored) && Denied(command),
            "A newer retained restoration consumes the one-step offer and prevents the native encoder replaying it");
        foreach (string field in new[] { "version", "actionId", "executionId", "playerId" }) {
            var invalid = consumed.DeepClone(); invalid["undo"]!["lastRestoration"]!.AsObject().Remove(field);
            check(Reject(invalid), "Retained restoration provenance requires explicit " + field + " across skipped event windows");
        }
    }
}
