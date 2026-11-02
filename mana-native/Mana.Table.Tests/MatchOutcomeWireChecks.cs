using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class MatchOutcomeWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"rules-outcome","status":"finished","viewerId":7,"result":"Defeat",
             "players":[{"id":7,"name":"Same","teamId":11,"life":40,"mana":{},"zones":[]},
                        {"id":13,"name":"Same","teamId":47,"life":0,"eliminated":true,"mana":{},"zones":[]},
                        {"id":21,"name":"Same","teamId":11,"life":0,"eliminated":true,"mana":{},"zones":[]}],
             "stack":[],"outcome":{"gameId":"rules-outcome","kind":"win","winningPlayerIds":[7],
                                    "winningTeamIds":[11],"ruleReason":"AllOpponentsLost"}}
            """)!;
        GameSnapshot Decode(JsonNode node, bool spectator = false) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node), spectator)!;
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        var state = Decode(wire);
        check(state.Outcome is { GameId: "rules-outcome", Kind: MatchOutcomeKind.Win, RuleReason: "AllOpponentsLost" }
                && state.Outcome.WinningPlayerIds.SequenceEqual([7]) && state.Outcome.WinningTeamIds.SequenceEqual([11])
                && PlayerRelations.ForViewer(state, 21) == PlayerRelation.Ally,
            "The provider decodes actual rule winners and game teams without assigning an eliminated ally an individual win");
        var otherViewer = wire.DeepClone(); otherViewer["viewerId"] = 21;
        var spectator = wire.DeepClone(); spectator["viewerId"] = -1;
        check(Decode(otherViewer).Outcome!.WinningPlayerIds.SequenceEqual(state.Outcome!.WinningPlayerIds)
                && Decode(spectator, true).Outcome!.WinningTeamIds.SequenceEqual(state.Outcome.WinningTeamIds),
            "Every authorized player and public spectator retains the same viewer-independent rules outcome");

        foreach (bool absent in new[] { true, false }) {
            var legacy = wire.DeepClone();
            if (absent) ((JsonObject)legacy).Remove("outcome"); else legacy["outcome"] = null;
            foreach (var player in legacy["players"]!.AsArray()) ((JsonObject)player!).Remove("teamId");
            check(Decode(legacy).Outcome == null && Decode(legacy).Players.All(player => player.TeamId == null),
                "Missing or null legacy outcome and missing team fields remain unknown even when Result says finished");
        }
        var inProgress = wire.DeepClone(); inProgress["status"] = "playing"; inProgress["outcome"] = null;
        var error = inProgress.DeepClone(); error["status"] = "error"; error["error"] = "Host interrupted";
        check(Decode(inProgress).Outcome == null && Decode(error).Outcome == null,
            "Ongoing and failed matches remain readable without inventing a rules outcome");
        var draw = wire.DeepClone(); draw["outcome"]!["kind"] = "draw";
        draw["outcome"]!["winningPlayerIds"] = new JsonArray(); draw["outcome"]!["winningTeamIds"] = new JsonArray();
        check(Decode(draw).Outcome is { Kind: MatchOutcomeKind.Draw, WinningPlayerIds.IsEmpty: true, WinningTeamIds.IsEmpty: true },
            "A rules draw explicitly contains no winning participants or teams");

        foreach (var mutate in new Action<JsonNode>[] {
            node => node["outcome"]!["gameId"] = "other-game",
            node => node["status"] = "playing",
            node => node["status"] = "resolving",
            node => node["status"] = "starting",
            node => node["status"] = "error",
            node => node["error"] = "Engine interrupted",
            node => node["prompt"] = JsonNode.Parse("""{"id":"pending","kind":"input","task":"confirmation","okEnabled":true}"""),
            node => node["outcome"]!["winningPlayerIds"] = new JsonArray(99),
            node => node["outcome"]!["winningPlayerIds"] = new JsonArray(7, 7),
            node => node["outcome"]!["winningTeamIds"] = new JsonArray(11, 11),
            node => node["outcome"]!["winningTeamIds"] = new JsonArray(47),
            node => node["outcome"]!["winningTeamIds"] = new JsonArray(11, 47),
            node => node["outcome"]!["winningPlayerIds"] = new JsonArray(),
            node => node["outcome"]!["winningTeamIds"] = new JsonArray(),
            node => node["outcome"]!["kind"] = "draw",
            node => node["outcome"]!["ruleReason"] = " ",
            node => node["players"]![0]!["teamId"] = null,
            node => ((JsonObject)node["players"]![0]!).Remove("teamId"),
            node => node["players"]![1]!["id"] = 7
        }) {
            var changed = wire.DeepClone(); mutate(changed);
            check(Reject(changed), "Foreign, unfinished or contradictory rules outcomes are rejected at the provider boundary");
        }
        foreach (string field in new[] { "gameId", "kind", "winningPlayerIds", "winningTeamIds", "ruleReason" }) {
            var changed = wire.DeepClone(); ((JsonObject)changed["outcome"]!).Remove(field);
            check(Reject(changed), "A rules outcome requires its explicit " + field);
        }
        foreach (string invalid in new[] { "1", "\"Win\"", "\"win, draw\"", "\"unknown\"", "null", "{}" }) {
            var changed = wire.DeepClone(); changed["outcome"]!["kind"] = JsonNode.Parse(invalid);
            check(Reject(changed), "Outcome kinds use a closed, canonical named wire vocabulary: " + invalid);
        }
        foreach (string invalid in new[] { "-1", "\"11\"", "11.5", "true", "{}" }) {
            var changed = wire.DeepClone(); changed["players"]![0]!["teamId"] = JsonNode.Parse(invalid);
            check(Reject(changed), "Rules teams must be actual nonnegative integers rather than guessed or coerced values: " + invalid);
        }
        foreach (string field in new[] { "gameId", "winningPlayerIds", "winningTeamIds", "ruleReason" }) {
            var changed = wire.DeepClone(); changed["outcome"]![field] = null;
            check(Reject(changed), "A present rules outcome cannot default a null " + field);
        }
        var missingParticipant = wire.DeepClone();
        ((JsonObject)missingParticipant["players"]![0]!).Remove("id");
        missingParticipant["viewerId"] = 0;
        missingParticipant["outcome"]!["winningPlayerIds"] = new JsonArray(0);
        check(Reject(missingParticipant), "An omitted participant ID cannot default to zero and manufacture a known rules winner");
        var typedParticipants = wire.DeepClone();
        foreach (var player in typedParticipants["players"]!.AsArray()) {
            int id = player!["id"]!.GetValue<int>();
            ((JsonObject)player).Remove("id"); player["Id"] = id;
        }
        check(Decode(typedParticipants).Players.Select(player => player.Id).SequenceEqual([7, 13, 21]),
            "Required participant IDs retain the decoder's case-insensitive compatibility with typed Player JSON");
        foreach (string alias in new[] { "Id", "ID", "iD" }) {
            var duplicate = wire.DeepClone(); duplicate["players"]![0]![alias] = 7;
            check(Reject(duplicate), "Duplicate case aliases cannot provide competing participant IDs: " + alias);
        }
    }
}
