using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class BlockRepairWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"block-repair","status":"playing","viewerId":0,"turn":7,"phaseKey":"COMBAT_DECLARE_BLOCKERS",
             "players":[{"id":0,"name":"Actor","life":40,"mana":{},"zones":[
               {"name":"Battlefield","count":2,"cards":[
                 {"key":"first-blocker-key","visualId":"first-blocker","combatId":"first-blocker","type":"Creature"},
                 {"key":"second-blocker-key","visualId":"second-blocker","combatId":"second-blocker","type":"Creature"}]}]},
               {"id":1,"name":"Opponent","life":40,"mana":{},"zones":[
                 {"name":"Battlefield","count":3,"cards":[
                   {"key":"first-attacker-key","visualId":"first-attacker","combatId":"first-attacker","type":"Creature"},
                   {"key":"second-attacker-key","visualId":"second-attacker","combatId":"second-attacker","type":"Creature"},
                   {"key":"foreign-blocker-key","visualId":"foreign-blocker","combatId":"foreign-blocker","type":"Creature"}]}]}],
             "stack":[],"prompt":{"id":"block-input","kind":"input","task":"block","okEnabled":true},
             "combat":{"attackers":[
               {"cardId":"first-attacker","defendingPlayerId":0,"blockerIds":["first-blocker"],"eligibleBlockerIds":["first-blocker"]},
               {"cardId":"second-attacker","defendingPlayerId":0,"blockerIds":["second-blocker"],"eligibleBlockerIds":["second-blocker"]}],
               "attackOptions":[],"blockProblem":"A joint block restriction is unsatisfied"}}
            """)!;
        GameSnapshot Decode(JsonNode node) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node))!;
        JsonNode With(string? repair)
        {
            var changed = wire.DeepClone();
            changed["combat"]!["blockRepair"] = repair == null ? null : JsonNode.Parse(repair);
            return changed;
        }
        bool Reject(JsonNode node) { try { Decode(node); return false; } catch (IOException) { return true; } }
        check(Decode(wire).Combat!.BlockRepair == null && Decode(With(null)).Combat!.BlockRepair == null,
            "Legacy absent and explicit-null block repair remain unknown rather than authorizing a clear declaration");
        var clear = Decode(With("[]"));
        check(clear.Combat!.BlockRepair is { IsEmpty: true }
                && !GameCommandPermissions.Allows(clear, new CompleteBlockers(clear.Id, clear.Decision!.Id))
                && GameCommandPermissions.Allows(clear, new ToggleBlock(clear.Id, clear.Decision!.Id, "first-attacker-key", "first-blocker-key")),
            "A certified empty repair retains current removal authority and cannot complete an invalid declaration");
        var subset = Decode(With("""[{"attackerId":"second-attacker","blockerId":"second-blocker"}]"""));
        check(subset.Combat!.BlockRepair!.Value.SequenceEqual([new BlockAssignment("second-attacker", "second-blocker")]),
            "The wire preserves a complete desired own subset without replacing its current pair identities");
        foreach (string malformed in new[] {
            "{}", "[null]", "[{}]", "[{\"attackerId\":\"\",\"blockerId\":\"second-blocker\"}]",
            "[{\"attackerId\":\"missing\",\"blockerId\":\"second-blocker\"}]",
            "[{\"attackerId\":\"first-attacker\",\"blockerId\":\"second-blocker\"}]",
            "[{\"attackerId\":\"second-attacker\",\"blockerId\":\"foreign-blocker\"}]",
            "[{\"attackerId\":\"second-attacker\",\"blockerId\":\"second-blocker\"},{\"attackerId\":\"second-attacker\",\"blockerId\":\"second-blocker\"}]",
            "[{\"attackerId\":\"first-attacker\",\"blockerId\":\"first-blocker\"},{\"attackerId\":\"second-attacker\",\"blockerId\":\"second-blocker\"}]"
        }) check(Reject(With(malformed)), "Malformed, duplicate, foreign, unassigned or unchanged block witnesses fail at the provider boundary");
        var noRemoval = With("[]");
        noRemoval["combat"]!["attackers"]![0]!["eligibleBlockerIds"] = new JsonArray();
        check(Reject(noRemoval), "A repair witness cannot invent toggle authority for an unoffered removal");
        var foreign = With("[]");
        foreign["combat"]!["attackers"]![0]!["blockerIds"] = JsonNode.Parse("""["foreign-blocker"]""");
        foreign["combat"]!["attackers"]![0]!["eligibleBlockerIds"] = JsonNode.Parse("""["foreign-blocker"]""");
        var accepted = Decode(foreign);
        check(accepted.Combat!.BlockRepair is { IsEmpty: true }
                && accepted.Combat.Attackers[0].BlockerIds.SequenceEqual(["foreign-blocker"]),
            "An empty own repair leaves other seats' assigned pairs present instead of claiming authority to clear them");
        var valid = With("[]"); valid["combat"]!["blockProblem"] = null;
        check(Reject(valid), "An already valid declaration cannot carry a removal witness");
        var outside = With("[]"); outside["prompt"]!["task"] = "priority";
        check(Reject(outside), "A block witness cannot be borrowed by another kind of current decision");
        var missing = With("[]"); missing["prompt"] = null;
        check(Reject(missing), "A read-only audience without the current blocking input receives no executable repair witness");
    }
}
