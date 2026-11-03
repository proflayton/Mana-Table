using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class ConfiguredSimulationWireChecks
{
    internal static ConfiguredSimulationSetup Setup() => new(new(MatchRulesets.CooperativeCommander,
        [new(0, 17, MatchSeatRole.Party), new(1, 42, MatchSeatRole.Opposition),
         new(2, 17, MatchSeatRole.Party), new(3, 42, MatchSeatRole.Opposition)]), ["a", "b", "c", "d"]);

    internal static JsonNode Receipt(bool starting = false)
    {
        var setup = JsonSerializer.SerializeToNode(ForgeEngine.EncodeConfiguredSimulation(Setup()))!;
        return new JsonObject { ["setup"] = setup, ["participants"] = JsonNode.Parse("""
            [{"index":2,"playerId":83},{"index":0,"playerId":29},{"index":3,"playerId":11},{"index":1,"playerId":47}]
            """), ["state"] = State(starting) };
    }

    internal static JsonNode State(bool starting = false)
    {
        var state = JsonNode.Parse("""
            {"id":"configured-game","status":"playing","viewerId":-1,"turn":0,"activePlayerId":29,"stack":[],"players":[
            {"id":47,"name":"Same","teamId":42,"life":40,"mana":{},"zones":[{"name":"Hand","count":7,"cards":[]}]},
            {"id":83,"name":"Same","teamId":17,"life":40,"mana":{},"zones":[{"name":"Hand","count":7,"cards":[]}]},
            {"id":11,"name":"Same","teamId":42,"life":40,"mana":{},"zones":[{"name":"Hand","count":7,"cards":[]}]},
            {"id":29,"name":"Same","teamId":17,"life":40,"mana":{},"zones":[{"name":"Hand","count":7,"cards":[]}]}]}
            """)!;
        if (starting) { state["status"] = "starting"; state["players"] = new JsonArray(); }
        return state;
    }

    internal static void Run(Action<bool, string> check)
    {
        ConfiguredSimulationStart Decode(JsonNode node) => ForgeEngine.DecodeConfiguredSimulation(JsonSerializer.SerializeToElement(node), Setup());
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var wire = Receipt();
        var start = Decode(wire);
        check(start.Participants.First(person => person.Index == 0).PlayerId == 29
                && start.State.Players[0].Id == 47 && start.State.Players[0].TeamId == 42,
            "Configured receipt decoding binds explicit slot/player identities independently of serialized roster order");
        check(!ConfiguredSimulationFacts.IsReady(Decode(Receipt(true)), Decode(Receipt(true)).State),
            "The native configured receipt preserves a real empty starting frame without inventing ready players");
        var fullStarting = wire.DeepClone(); fullStarting["state"]!["status"] = "starting";
        var fullReceipt = Decode(fullStarting);
        check(!ConfiguredSimulationFacts.IsReady(fullReceipt, fullReceipt.State),
            "An actual full constructor spectator roster cannot authorize policy execution before committed play");
        fullStarting["state"]!["players"]![0]!["teamId"] = 17;
        check(Reject(fullStarting), "A full starting publication must already have the exact actual mapped teams");
        var request = JsonSerializer.SerializeToElement(ForgeEngine.EncodeConfiguredSimulation(Setup()));
        check(request.GetProperty("decks")[2].GetString() == "c"
                && request.GetProperty("specification").GetProperty("seats")[1].GetProperty("role").GetString() == "opposition",
            "The distinct configured command preserves ordered deck handles and canonical shared topology fields");
        foreach (var change in new Action<JsonNode>[] {
            node => ((JsonObject)node).Remove("setup"),
            node => ((JsonObject)node["setup"]!["specification"]!["seats"]![0]!).Remove("index"),
            node => node["setup"]!["specification"]!["seats"]![0]!["teamId"] = "17",
            node => node["setup"]!["specification"]!["rulesetId"] = "unknown.rules.v1",
            node => node["setup"]!["decks"]![1] = "changed-input",
            node => node["participants"] = new JsonArray(),
            node => ((JsonObject)node["participants"]![0]!).Remove("index"),
            node => ((JsonObject)node["participants"]![0]!).Remove("playerId"),
            node => node["participants"]![0]!["playerId"] = "83",
            node => node["participants"]![0]!["playerId"] = 29,
            node => node["participants"]![0]!["index"] = 0,
            node => node["participants"]![0]!["Index"] = 2,
            node => node["participants"]![0]!["PlayerId"] = 83,
            node => node["state"]!["players"]![0]!["teamId"] = 17,
            node => ((JsonObject)node["state"]!["players"]![0]!).Remove("teamId"),
            node => node["state"]!["viewerId"] = 29,
            node => node["state"]!["players"]![0]!["zones"]![0]!["cards"] = JsonNode.Parse("""[{"name":"Secret hand card"}]"""),
            node => { node["state"]!["partyId"] = "party"; node["state"]!["encounterId"] = "encounter"; }
        }) { var changed = wire.DeepClone(); change(changed); check(Reject(changed),
            "Malformed configured wire cannot borrow accepted setup, participant IDs, team facts, spectator privacy or live encounter authority"); }
        var aliases = wire.DeepClone();
        var participant = (JsonObject)aliases["participants"]![0]!;
        participant.Remove("index"); participant.Remove("playerId"); participant["Index"] = 2; participant["PlayerId"] = 83;
        check(Decode(aliases).Participants[0] == new ConfiguredSimulationParticipant(2, 83),
            "Case-insensitive configured identity decoding accepts one explicit alias while rejecting duplicate aliases");
        var noRoster = Receipt(true); noRoster["state"]!["status"] = "playing";
        check(Reject(noRoster), "A playing configured receipt cannot use the initial empty-roster exception");
    }
}
