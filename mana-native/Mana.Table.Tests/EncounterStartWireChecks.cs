using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class EncounterStartWireChecks
{
    internal static JsonNode PreparingWire() => JsonNode.Parse("""
            {"tableId":"table","revision":14,"mode":"hosting","status":"Preparing","canStart":false,"matchActive":false,
             "addresses":[],"slots":[
              {"index":0,"name":"Same","type":"LOCAL","ready":true,"local":true,"participantId":"a","teamId":30,"role":"party"},
              {"index":1,"name":"Same","type":"AI","ready":true,"local":false,"participantId":"b","teamId":80,"role":"opposition"}],
             "matchSetupCapabilities":{"rulesetIds":["commander.free-for-all.v1","commander.cooperative.v1"],"maximumSeats":6,"maximumPartySeats":4,"maximumOppositionSeats":2},
             "matchSetupConsent":"reviewed-setup","matchSetup":{"rulesetId":"commander.cooperative.v1","seats":[
              {"index":0,"teamId":30,"role":"party"},{"index":1,"teamId":80,"role":"opposition"}]},
             "partyProgress":{"partyId":"party","encounterId":null,"completedEncounters":0,"victories":0,"saveState":"ready","saveError":null,"retryAllowed":false,"lastReceipt":null},
             "encounterStartSupported":true,
             "startState":{"attemptId":"attempt","phase":"preparing","error":null,"retryAllowed":false}}
            """)!;

    internal static void Run(Action<bool, string> check)
    {
        var wire = PreparingWire();
        Lobby Decode(JsonNode value) => ForgeEngine.MapLobby(JsonSerializer.SerializeToElement(value));
        bool Reject(JsonNode value) { try { Decode(value); return false; } catch (IOException) { return true; } }
        bool ScopeReject(Action operation) { try { operation(); return false; } catch (IOException) { return true; } }
        var initial = Decode(wire);
        check(initial is { EncounterStartSupported: true, MatchActive: false, StartState.Phase: EncounterStartPhase.Preparing }
                && initial.StartState.AttemptId == "attempt" && initial.PartyProgress is { EncounterId: null, CompletedEncounters: 0 },
            "Admission publishes its own typed attempt without manufacturing a game frame or changing terminal progress");
        var request = JsonSerializer.SerializeToElement(ForgeEngine.EncodeEncounterStart("table", "reviewed-setup", "reviewed-tempo"));
        var retry = JsonSerializer.SerializeToElement(ForgeEngine.EncodeEncounterStartScope("table", "attempt"));
        check(request.GetProperty("tableId").GetString() == "table" && request.GetProperty("matchSetupConsent").GetString() == "reviewed-setup"
                && request.GetProperty("tempoRevision").GetString() == "reviewed-tempo"
                && retry.GetProperty("attemptId").GetString() == "attempt" && retry.EnumerateObject().Count() == 2,
            "Begin carries the displayed setup/timer consent while retry/cancel keep the exact table and immutable attempt scope");
        var legacy = wire.DeepClone(); ((JsonObject)legacy).Remove("encounterStartSupported"); ((JsonObject)legacy).Remove("startState");
        check(Decode(legacy) is { EncounterStartSupported: false, StartState: null },
            "An older co-op host remains unsupported for durable admission even if it supports teams and terminal receipts");
        var failed = wire.DeepClone(); failed["startState"]!["phase"] = "failed";
        failed["startState"]!["error"] = "The attempt could not be recorded."; failed["startState"]!["retryAllowed"] = true;
        check(Decode(failed).StartState is { Phase: EncounterStartPhase.Failed, RetryAllowed: true },
            "A failed initial journal write can offer host-only retry without becoming a saved result");
        var guest = failed.DeepClone(); guest["mode"] = "joined"; guest["startState"]!["retryAllowed"] = false;
        check(Decode(guest).StartState is { Phase: EncounterStartPhase.Failed, RetryAllowed: false }
                && ScopeReject(() => ForgeEngine.ValidateStartReply(Decode(guest), "table", "attempt")),
            "Guests can observe the same failed attempt without acquiring its host retry permission");
        var cancelled = wire.DeepClone(); cancelled["startState"]!["phase"] = "cancelled";
        check(Decode(cancelled).StartState is { Phase: EncounterStartPhase.Cancelled, Error: null, RetryAllowed: false },
            "Cancellation is an explicit lifecycle fact rather than a parsed error or a disappeared snapshot");
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["encounterStartSupported"] = false, node => node["encounterStartSupported"] = "true",
            node => node["encounterStartSupported"] = null, node => ((JsonObject)node).Remove("revision"),
            node => node["startState"]!["attemptId"] = " ", node => ((JsonObject)node["startState"]!).Remove("attemptId"),
            node => node["startState"]!["phase"] = "Recording", node => node["startState"]!["phase"] = 1,
            node => node["startState"]!["phase"] = "complete", node => ((JsonObject)node["startState"]!).Remove("error"),
            node => node["startState"]!["error"] = "Not a failure", node => node["startState"]!["retryAllowed"] = true,
            node => ((JsonObject)node["startState"]!).Remove("retryAllowed"), node => node["canStart"] = true,
            node => node["matchActive"] = true,
            node => { node["startState"]!["phase"] = "failed"; },
            node => { node["startState"]!["phase"] = "failed"; node["startState"]!["error"] = "Failure"; node["startState"]!["retryAllowed"] = true; node["mode"] = "joined"; }
        }) { var changed = wire.DeepClone(); mutate(changed); check(Reject(changed), "Malformed or contradictory admission facts fail closed at the native lobby boundary"); }
        var activeWire = wire.DeepClone(); activeWire["matchActive"] = true; activeWire["startState"]!["phase"] = "starting";
        activeWire["partyProgress"]!["encounterId"] = "attempt";
        var active = Decode(activeWire);
        var snapshot = ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(new {
            id = "actual-game", status = "starting", viewerId = 7, players = Array.Empty<object>(), stack = Array.Empty<object>(),
            partyId = "party", encounterId = "attempt"
        }))!;
        ForgeEngine.ValidateStartReply(active, "table", "attempt");
        ForgeEngine.ValidateStartedFrame(active, snapshot, "party");
        check(snapshot is { Status: "starting", Players.IsEmpty: true, Outcome: null }
                && ScopeReject(() => ForgeEngine.ValidateStartReply(active, "foreign-table", "attempt"))
                && ScopeReject(() => ForgeEngine.ValidateStartReply(active, "table", "other-attempt"))
                && ScopeReject(() => ForgeEngine.ValidateStartedFrame(active, snapshot with { Encounter = new("party", "previous-attempt") }, "party"))
                && ScopeReject(() => ForgeEngine.ValidateStartedFrame(active, snapshot with { Encounter = new("other-party", "attempt") }, "party"))
                && ScopeReject(() => ForgeEngine.ValidateStartedFrame(active, null, "party")),
            "Only the actual admitted party/attempt frame can complete startup; a prior game, foreign scope or absent frame cannot");
        var missing = activeWire.DeepClone(); ((JsonObject)missing).Remove("startState");
        check(Reject(missing), "An upgraded active co-op host cannot silently omit the admission that authorized its game");
    }
}
