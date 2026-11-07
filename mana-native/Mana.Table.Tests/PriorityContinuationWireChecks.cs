using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class PriorityContinuationWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"standing-wire","status":"playing","viewerId":29,"turn":7,"phaseKey":"MAIN1","activePlayerId":47,
             "revision":10,"boardRevision":3,"decisionOwnerId":29,"priorityPlayerId":29,
             "humanPlayerIds":[29,47],
             "players":[{"id":29,"name":"You","life":40,"mana":{},"zones":[]},
                        {"id":47,"name":"Friend","life":40,"mana":{},"zones":[]}],"stack":[],
             "priorityContinuation":{"sequence":0,"grantId":"","mode":null,"status":"Idle","reasonCode":"","automaticPasses":0,"phaseStops":[]},
             "priorityObservation":{"boardRevision":3,"publicActivitySequence":16,"acknowledgedBoardRevision":0,
                "acknowledgedPublicActivitySequence":0,"registered":false,"overflowed":false},
             "prompt":{"id":"priority","kind":"input","task":"priority","okEnabled":true,"canAutoPass":false,
                "responseAvailability":"Available","quietYieldToken":"original"}}
            """)!;
        GameSnapshot Decode(JsonNode node) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node))!;
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var state = Decode(wire);
        check(state.HumanPlayerIds is { } ids && ids.SequenceEqual([29, 47])
            && state.Decision?.Payload is PriorityPayload { ResponseAvailability: PriorityResponseAvailability.Available },
            "The provider maps actual human player IDs and completed response availability into closed native facts");
        var command = new GrantPriorityContinuation(state.Id, state.Decision!.Id, 1, "grant", PriorityContinuationMode.NoResponse, ["MAIN2"]);
        check(!GameCommandPermissions.Allows(state, command), "A decoded but unregistered audience cannot install standing consent");
        state = state with { PriorityObservation = state.PriorityObservation! with { Registered = true,
            AcknowledgedBoardRevision = state.BoardRevision, AcknowledgedPublicActivitySequence = 16 } };
        var scope = new ForgeCommandScope(); scope.Observe(state);
        var encoded = scope.Encode(command);
        check(encoded["action"] as string == "grantPriorityContinuation" && encoded["sequence"] is 1L
            && encoded["mode"] as string == "NoResponse" && encoded["phaseStops"] is ImmutableArray<string> stops && stops.SequenceEqual(["MAIN2"])
            && encoded["expectedTask"] as string == "priority", "Standing consent encodes one typed grant with exact input and own-turn stop semantics");
        scope.Observe(state with { Decision = null, DecisionOwnerId = 47 });
        var revoke = scope.Encode(new RevokePriorityContinuation(state.Id, 2));
        var ack = scope.Encode(new AcknowledgePriorityObservation(state.Id, 3, 16));
        check(revoke["action"] as string == "revokePriorityContinuation" && revoke["throughSequence"] is 2L
            && !revoke.ContainsKey("expectedTask") && ack["action"] as string == "acknowledgePriorityObservation"
            && ack["boardRevision"] is 3L && ack["publicActivitySequence"] is 16L && !ack.ContainsKey("expectedTask"),
            "Withdrawal and accepted-frame observation credit require no invented priority prompt");
        var legacy = wire.DeepClone(); legacy.AsObject().Remove("priorityContinuation"); legacy.AsObject().Remove("priorityObservation");
        legacy.AsObject().Remove("humanPlayerIds"); legacy["prompt"]!.AsObject().Remove("responseAvailability");
        var older = Decode(legacy);
        check(older.PriorityContinuation == null && older.PriorityObservation == null && older.HumanPlayerIds == null
            && !GameCommandPermissions.Allows(older, command) && HumanParticipationFacts.HasOtherActiveHuman(older),
            "Missing host capabilities preserve old native Auto and optional presence behavior");
        foreach (var pair in new (string Key, JsonNode? Value)[] {
            ("sequence", -1), ("status", 1), ("status", "active"), ("mode", "Forever"), ("automaticPasses", -1),
            ("phaseStops", new JsonArray("MAIN1", "MAIN1")), ("phaseStops", new JsonArray("OPPONENT_MAIN1"))
        }) {
            var bad = wire.DeepClone(); bad["priorityContinuation"]![pair.Key] = pair.Value;
            check(Reject(bad), "Malformed continuation metadata is rejected before native ownership: " + pair.Key);
        }
        foreach (string required in new[] { "sequence", "grantId", "mode", "status", "reasonCode", "automaticPasses", "phaseStops" }) {
            var bad = wire.DeepClone(); bad["priorityContinuation"]!.AsObject().Remove(required);
            check(Reject(bad), "A partial standing advertisement cannot invent an omitted authority fact: " + required);
        }
        foreach (JsonNode value in new JsonNode[] { JsonValue.Create("Unguarded")!, JsonValue.Create(0)! }) {
            var bad = wire.DeepClone(); bad["prompt"]!["responseAvailability"] = value;
            check(Reject(bad), "Response availability rejects unknown and numeric values");
        }
        var contradiction = wire.DeepClone(); contradiction["prompt"]!["canAutoPass"] = true;
        check(Reject(contradiction), "A completed available-response scan cannot be advertised as safe ordinary Auto");
        foreach (string authored in new[] {
            """{"id":"payment","kind":"input","task":"mana","okEnabled":true,"cancelEnabled":true}""",
            """{"id":"opening","kind":"input","task":"openingHand","purpose":"openingHand","okEnabled":true,"cancelEnabled":true}""",
            """{"id":"choice","kind":"choice","task":"choice","min":1,"max":1,"choices":[{"index":0,"label":"Choose"}]}""",
            """{"id":"attack","kind":"input","task":"attack","okEnabled":true}""",
            """{"id":"selection","kind":"input","task":"selection","okEnabled":true,"cancelEnabled":true}"""
        }) {
            var prompt = JsonNode.Parse(authored)!;
            var baseline = ForgeDecisionDecoder.Decode(JsonSerializer.SerializeToElement(prompt));
            check(baseline.Payload is not PriorityPayload, "The nonpriority fixture is independently valid before adding priority evidence: " + prompt["task"]);
            prompt["responseAvailability"] = "Unknown";
            bool rejected = false;
            try { ForgeDecisionDecoder.Decode(JsonSerializer.SerializeToElement(prompt)); }
            catch (IOException) { rejected = true; }
            check(rejected, "Response availability cannot be smeared onto another otherwise valid task: " + prompt["task"]);
        }
        foreach (var roster in new[] { new JsonArray(0, 1), new JsonArray(29, 29), new JsonArray(-1), new JsonArray(29, 999) }) {
            var bad = wire.DeepClone(); bad["humanPlayerIds"] = roster;
            check(Reject(bad), "Human rosters reject seat ordinals, duplicate IDs and unregistered participants");
        }
        var future = wire.DeepClone(); future["priorityObservation"]!["acknowledgedPublicActivitySequence"] = 17;
        check(Reject(future), "Observer acknowledgement cannot exceed the actual published public activity cursor");
        var wrongBoard = wire.DeepClone(); wrongBoard["priorityObservation"]!["boardRevision"] = 4;
        check(Reject(wrongBoard), "Observation credit remains bound to the frame that actually reached the audience");
    }
}
