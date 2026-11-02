using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class MatchSetupWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var lobbyWire = JsonNode.Parse("""
            {"tableId":"setup-table","revision":14,"mode":"hosting","status":"Ready","canStart":false,"matchActive":false,
             "addresses":[],"slots":[
                {"index":0,"name":"Same","type":"LOCAL","ready":false,"local":true,"participantId":"a","teamId":30,"role":"party"},
                {"index":1,"name":"Same","type":"AI","ready":true,"local":false,"participantId":"b","teamId":80,"role":"opposition"}],
             "matchSetupCapabilities":{"rulesetIds":["commander.free-for-all.v1","commander.cooperative.v1"],"maximumSeats":6,"maximumPartySeats":4,"maximumOppositionSeats":2},
             "matchSetupConsent":"setup-consent-14",
             "matchSetup":{"rulesetId":"commander.cooperative.v1","seats":[{"index":0,"teamId":30,"role":"party"},{"index":1,"teamId":80,"role":"opposition"}]},
             "partyProgress":{"partyId":"party","encounterId":"encounter","completedEncounters":0,"victories":0,"saveState":"pending","saveError":null,"retryAllowed":false,"lastReceipt":null}}
            """)!;
        Lobby Decode(JsonNode value) => ForgeEngine.MapLobby(JsonSerializer.SerializeToElement(value));
        bool Reject(JsonNode value) { try { Decode(value); return false; } catch (IOException) { return true; } }
        var lobby = Decode(lobbyWire);
        check(lobby.MatchSetup?.RulesetId == MatchRulesets.CooperativeCommander && lobby.Seats[0] is { ParticipantId: "a", TeamId: 30, Role: MatchSeatRole.Party }
                && lobby.PartyProgress is { CompletedEncounters: 0, SaveState: EncounterSaveState.Pending },
            "The native lobby boundary keeps explicit ruleset, stable participant, actual team and unsaved progress facts separate");
        var request = JsonSerializer.SerializeToElement(ForgeEngine.EncodeMatchSetup(lobby.TableId, lobby.Revision, lobby.MatchSetup!));
        check(request.GetProperty("tableId").GetString() == lobby.TableId && request.GetProperty("expectedLobbyRevision").GetInt64() == 14
                && request.GetProperty("specification").GetProperty("seats")[0].GetProperty("role").GetString() == "party"
                && request.GetProperty("specification").GetProperty("rulesetId").GetString() == MatchRulesets.CooperativeCommander,
            "Setup commands preserve exact table/revision fencing and canonical engine-independent specification fields");
        var readyRequest = JsonSerializer.SerializeToElement(ForgeEngine.EncodeMatchReady(lobby.TableId, lobby.MatchSetupConsent!, true, "reviewed-tempo"));
        check(readyRequest.GetProperty("matchSetupConsent").GetString() == "setup-consent-14"
                && readyRequest.GetProperty("tempoRevision").GetString() == "reviewed-tempo" && readyRequest.GetProperty("ready").GetBoolean(),
            "A single Ready command carries both the displayed setup consent and the displayed tempo revision");
        var legacy = lobbyWire.DeepClone();
        foreach (string name in new[] { "matchSetupCapabilities", "matchSetup", "matchSetupConsent", "partyProgress" }) ((JsonObject)legacy).Remove(name);
        foreach (var seat in legacy["slots"]!.AsArray()) foreach (string name in new[] { "participantId", "teamId", "role" }) ((JsonObject)seat!).Remove(name);
        check(Decode(legacy) is { MatchSetupCapabilities: null, MatchSetup: null, PartyProgress: null }
                && Decode(legacy).Seats.All(seat => seat.ParticipantId == "" && seat.TeamId == null && seat.Role == null),
            "An older lobby stays unsupported for cooperative setup rather than inferring capability from AI seats or names");
        var saved = lobbyWire.DeepClone();
        saved["partyProgress"]!["saveState"] = "ready";
        saved["partyProgress"]!["completedEncounters"] = 1; saved["partyProgress"]!["victories"] = 1;
        saved["partyProgress"]!["lastReceipt"] = JsonNode.Parse("""
            {"receiptId":"receipt","partyId":"party","encounterId":"encounter","gameId":"finished-game",
             "rulesetId":"commander.cooperative.v1","partyTeamId":30,"completedUtc":"2026-11-02T00:00:00Z",
             "outcome":{"gameId":"finished-game","kind":"win","winningPlayerIds":[7],"winningTeamIds":[30],"ruleReason":"AllOpposingTeamsLost"},
             "participants":[{"participantId":"a","index":0,"playerId":7,"teamId":30,"role":"party","deckSelectionId":"selection-a"},
                             {"participantId":"b","index":1,"playerId":13,"teamId":80,"role":"opposition","deckSelectionId":"selection-b"}]}
            """);
        check(Decode(saved).PartyProgress?.LastReceipt is { GameId: "finished-game", Outcome.Kind: MatchOutcomeKind.Win, Participants.Length: 2 },
            "A durable public receipt decodes retained participant-to-player identities and the same typed rules outcome");
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["partyProgress"]!["lastReceipt"]!["partyId"] = "other-party",
            node => node["partyProgress"]!["lastReceipt"]!["outcome"]!["winningPlayerIds"] = new JsonArray(99),
            node => node["partyProgress"]!["lastReceipt"]!["outcome"]!["winningTeamIds"] = new JsonArray(80),
            node => node["partyProgress"]!["lastReceipt"]!["participants"]![1]!["participantId"] = "a",
            node => ((JsonObject)node["partyProgress"]!["lastReceipt"]!["participants"]![0]!).Remove("playerId"),
            node => ((JsonObject)node["partyProgress"]!["lastReceipt"]!["participants"]![0]!).Remove("deckSelectionId"),
            node => node["partyProgress"]!["lastReceipt"]!["participants"]![0]!["deckSelectionId"] = "",
            node => node["partyProgress"]!["lastReceipt"]!["completedUtc"] = "not-a-date"
        }) { var changed = saved.DeepClone(); mutate(changed); check(Reject(changed), "A committed receipt cannot smuggle foreign, incomplete or contradictory participant/outcome facts"); }
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["revision"] = -1,
            node => node["matchSetupConsent"] = null,
            node => ((JsonObject)node).Remove("revision"),
            node => node["matchSetup"]!["seats"]![0]!["teamId"] = 80,
            node => node["slots"]![1]!["participantId"] = "a",
            node => node["slots"]![1]!["teamId"] = 30,
            node => node["slots"]![1]!["type"] = "REMOTE",
            node => node["slots"]![1]!["role"] = "Party",
            node => ((JsonObject)node["matchSetup"]!["seats"]![0]!).Remove("index"),
            node => node["matchSetupCapabilities"]!["rulesetIds"] = new JsonArray(),
            node => node["matchSetupCapabilities"]!["maximumSeats"] = 1,
            node => node["partyProgress"]!["victories"] = 1,
            node => node["partyProgress"]!["saveState"] = "Pending",
            node => node["partyProgress"]!["retryAllowed"] = true,
            node => ((JsonObject)node["partyProgress"]!).Remove("completedEncounters")
        }) {
            var changed = lobbyWire.DeepClone(); mutate(changed);
            check(Reject(changed), "Malformed setup capability, identity, topology or checkpoint facts fail the native lobby boundary");
        }
        var snapshot = JsonNode.Parse("""
            {"id":"encounter-game","status":"playing","viewerId":7,"players":[
                {"id":7,"name":"Actor","teamId":30,"participantId":"a","life":40,"mana":{},"zones":[]},
                {"id":13,"name":"AI","teamId":80,"participantId":"b","life":40,"mana":{},"zones":[]}],"stack":[],
             "partyId":"party","encounterId":"encounter"}
            """)!;
        GameSnapshot Game(JsonNode value, bool spectator = false) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(value), spectator)!;
        bool RejectGame(JsonNode value) { try { Game(value); return false; } catch (IOException) { return true; } }
        check(Game(snapshot).Encounter is { PartyId: "party", EncounterId: "encounter" } && Game(snapshot).Viewer!.ParticipantId == "a",
            "Rules snapshots carry only encounter linkage and stable player identity, with no mutable progress counters");
        var starting = snapshot.DeepClone(); starting["status"] = "starting"; starting["players"] = new JsonArray();
        check(Game(starting).Encounter == Game(snapshot).Encounter && Game(starting).Players.IsEmpty,
            "An initial starting publication retains encounter linkage before any player roster or decision authority is available");
        foreach (string status in new[] { "playing", "finished" }) {
            var empty = starting.DeepClone(); empty["status"] = status;
            check(RejectGame(empty), "Only starting may omit encounter participants; a full " + status + " publication requires their identities");
        }
        var earlyFailure = starting.DeepClone(); earlyFailure["status"] = "error"; earlyFailure["error"] = "The engine failed before its opening roster.";
        var failedState = Game(earlyFailure);
        check(failedState is { Players.IsEmpty: true, Outcome: null, Decision: null }
                && failedState.Error == "The engine failed before its opening roster." && failedState.Encounter == Game(snapshot).Encounter,
            "A real startup error retains its diagnostic and encounter linkage without inventing participants, decisions or a result");
        earlyFailure["outcome"] = JsonNode.Parse("""
            {"gameId":"encounter-game","kind":"win","winningPlayerIds":[7],"winningTeamIds":[30],"ruleReason":"AllOpposingTeamsLost"}
            """);
        check(RejectGame(earlyFailure), "An empty error frame cannot certify an outcome or a winning participant");
        var spectator = snapshot.DeepClone(); spectator["viewerId"] = -1;
        check(Game(spectator, true).Encounter == Game(snapshot).Encounter,
            "Public spectators receive the same encounter identity without inheriting a participant's private seat");
        foreach (var mutate in new Action<JsonNode>[] {
            node => ((JsonObject)node).Remove("partyId"),
            node => node["encounterId"] = "",
            node => node["players"]![1]!["participantId"] = "a",
            node => ((JsonObject)node["players"]![0]!).Remove("participantId"),
            node => node["players"]![0]!["teamId"] = null
        }) { var changed = snapshot.DeepClone(); mutate(changed); check(RejectGame(changed), "Incomplete or aliased encounter-player linkage is rejected"); }
    }
}
