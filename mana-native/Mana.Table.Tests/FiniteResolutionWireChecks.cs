using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class FiniteResolutionWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"finite-resolution","status":"playing","viewerId":0,"turn":7,"phaseKey":"MAIN1",
             "stackInterruptionVersion":9,"stackActionVersion":3,"resolutionChoiceVersion":2,"decisionOwnerId":0,
             "players":[{"id":0,"name":"Actor","life":40,"mana":{},"zones":[{"name":"Battlefield","count":1,"cards":[
                {"name":"Public source","visualId":"source","combatId":"source","type":"Creature","faceDown":false}]}]},
                {"id":1,"name":"Opponent","life":40,"mana":{},"zones":[]}],
             "stack":[{"id":"stack-sibling","name":"Public source","text":"A rendered description","kind":"triggered","ability":true,
                "executionId":"sibling","controllerId":0,"card":{"name":"Public source","visualId":"source","combatId":"source"},
                "repetition":{"groupKey":"same-rule","sourceCardId":"source"}}],
             "executions":{"firstSequence":1,"lastSequence":0,"transitions":[],"occurrences":[
                {"id":"current","budgetRootId":"current","kind":"trigger","actorPlayerId":0,"sourceCardId":"source","step":"cost"},
                {"id":"sibling","budgetRootId":"sibling","kind":"trigger","actorPlayerId":0,"sourceCardId":"source","step":"stack"}]},
             "prompt":{"id":"payment","kind":"input","task":"mana","payerId":0,"okEnabled":false,"cancelEnabled":true,
                "executionId":"current","executionStep":"cost","unpaidManaParts":[{"symbol":"{1}","count":1}],
                "optionalDecline":{"groupKey":"same-rule","paymentKey":"same-terms","producingTriggerExecutionId":"current",
                  "sourceCardId":"source","purpose":"TriggeredAbilityCost","payerId":0,"mandatory":false,"offerId":"offer-2","previousOfferId":"offer-1"}}}
            """)!;
        GameSnapshot Decode(JsonNode node) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node))!;
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        JsonNode Change(string key, JsonNode? value)
        {
            var changed = wire.DeepClone(); changed["prompt"]!["optionalDecline"]![key] = value; return changed;
        }
        var actual = Decode(wire);
        check(actual.Decision!.Payload is PaymentPayload { OptionalDecline: { ProducingTriggerExecutionId: "current", OfferId: "offer-2", PreviousOfferId: "offer-1" } }
                && actual.Stack[0].Repetition == new StackRepetition("same-rule", "source")
                && actual.StackActionVersion == 3 && actual.ResolutionChoiceVersion == 2,
            "Human payment facts retain current producing ownership, distinct terms, actual-input lineage and split monotonic guards");
        check(actual.Stack.All(item => item.ExecutionId != "current")
                && GameCommandPermissions.Allows(actual, new CancelPayment(actual.Id, actual.Decision.Id)),
            "An in-resolution trigger may have left the stack while its exact owned Cancel action remains current");
        var legacy = wire.DeepClone(); legacy["prompt"]!["optionalDecline"] = null;
        legacy["stack"]![0]!["repetition"] = null; legacy.AsObject().Remove("stackActionVersion"); legacy.AsObject().Remove("resolutionChoiceVersion");
        var unknown = Decode(legacy);
        check(((PaymentPayload)unknown.Decision!.Payload).OptionalDecline == null && unknown.Stack[0].Repetition == null
                && unknown.StackActionVersion == null && unknown.ResolutionChoiceVersion == null,
            "Older providers remain explicitly unknown and cannot silently authorize a finite repeated answer");
        foreach (string key in new[] { "groupKey", "paymentKey", "producingTriggerExecutionId", "sourceCardId", "offerId" })
            check(Reject(Change(key, "")), "Required finite-answer identity cannot be missing or empty: " + key);
        foreach (var changed in new[] {
            Change("mandatory", true), Change("payerId", 1), Change("purpose", "SpellCost"), Change("purpose", 3),
            Change("previousOfferId", "offer-2"), Change("producingTriggerExecutionId", "sibling"), Change("sourceCardId", "missing")
        }) check(Reject(changed), "Mandatory, foreign, stale, unlinked and untyped optional-answer claims fail before presentation");
        var noCancel = wire.DeepClone(); noCancel["prompt"]!["cancelEnabled"] = false;
        check(Reject(noCancel), "Optional repetition never invents the currently unavailable negative command");
        foreach (string fence in new[] { "stackActionVersion", "resolutionChoiceVersion" }) {
            var missing = wire.DeepClone(); missing.AsObject().Remove(fence);
            var negative = wire.DeepClone(); negative[fence] = -1;
            check(Reject(missing) && Reject(negative), "A finite optional offer cannot bypass a missing or invalid guard: " + fence);
        }
        var concealed = wire.DeepClone(); concealed["players"]![0]!["zones"]![0]!["cards"]![0]!["faceDown"] = true;
        check(Reject(concealed), "A concealed source cannot carry public repeated-payment identity");
        var wrongTask = wire.DeepClone(); wrongTask["prompt"]!["task"] = "priority";
        check(Reject(wrongTask), "Another decision family cannot retain an optional mana-payment claim");
        var child = wire.DeepClone(); child["prompt"]!["executionId"] = "nested";
        child["executions"]!["occurrences"]!.AsArray().Add(JsonNode.Parse("""
            {"id":"nested","parentId":"current","budgetRootId":"current","kind":"rules","actorPlayerId":0,"sourceCardId":"source","step":"cost","ruleKind":"replacement"}
            """));
        check(Decode(child).Decision!.ExecutionId == "nested", "An explicitly disclosed parent chain retains the original producing trigger for a nested owned input");
        child["executions"]!["occurrences"]![2]!.AsObject().Remove("parentId");
        check(Reject(child), "A shared budget root cannot replace an absent producing-parent link");
        var spell = wire.DeepClone(); spell["stack"]![0]!["kind"] = "spell"; spell["stack"]![0]!["ability"] = false;
        check(Reject(spell), "A semantic trigger group cannot be attached to a spell through matching prose");
        var wrongController = wire.DeepClone(); wrongController["stack"]![0]!["controllerId"] = 1;
        check(Reject(wrongController), "A group retains the actual execution controller instead of borrowing another seat's stack portrait");
        var wrongSource = wire.DeepClone(); wrongSource["executions"]!["occurrences"]![1]!["sourceCardId"] = "another-source";
        check(Reject(wrongSource), "A matching portrait cannot certify a trigger produced by another source occurrence");
    }
}
