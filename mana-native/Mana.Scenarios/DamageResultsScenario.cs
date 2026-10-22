using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Authored presentation checkpoints; real prevention is verified separately against Forge.</summary>
public static class DamageResultsScenario
{
    public const string Id = "damage-results.v1";
    public static IReadOnlyList<ExperienceStage> Create()
    {
        var initial = CounterDamageScenario.Snapshot(1) with { Id = Id };
        var first = CounterDamageScenario.Snapshot(2) with { Id = Id };
        var victim = first.Players[1].Zone("Battlefield").Cards.Single(c => c.VisualId == CounterDamageScenario.TargetId);
        var dead = first with { Players = first.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z =>
            z.Name == "Battlefield" ? z with { Count = z.Count - 1, Cards = z.Cards.Where(c => c.VisualId != victim.VisualId).ToArray() }
            : z.Name == "Graveyard" ? z with { Count = z.Count + 1, Cards = [victim with { Damage = 0 }] } : z).ToArray() }).ToArray(),
            Activity = [.. first.Activity,
                new(10, 8, "MAIN1", "damage", 1, "All Will Be One dealt 3 damage to Colossal Dreadmaw.", victim.VisualId, victim.Name) {
                    Impact = new("damage", 3, victim.VisualId, victim.Name, SourceCardId: CounterDamageScenario.SourceId, SourceName: "All Will Be One", DamageType: "normal") },
                new(11, 8, "MAIN1", "moved", 1, "Colossal Dreadmaw moved from battlefield to graveyard.", victim.VisualId, victim.Name)
            ] };
        var playerTarget = new StackTarget("player", "1", initial.Players[1].Name, 1);
        var playerPending = CounterDamageScenario.Snapshot(1, playerTarget) with { Id = Id, Players = dead.Players, Activity = dead.Activity };
        var armor = new Card { VisualId = "damage-results-armor", CombatId = "damage-results-armor", Name = "Urza's Armor", Type = "Artifact",
            Text = "If a source would deal damage to you, prevent 1 of that damage." };
        playerPending = playerPending with { Players = playerPending.Players.Select(p => p.Id != 1 ? p : p with {
            Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with { Count = z.Count + 1, Cards = [.. z.Cards, armor] }).ToArray() }).ToArray() };
        var prevented = playerPending with { Stack = [], Players = playerPending.Players.Select(p => p.Id == 1 ? p with { Life = 38 } : p).ToArray(),
            Activity = [.. playerPending.Activity,
                new(20, 8, "MAIN1", "damage", 1, "All Will Be One dealt 2 damage.", CounterDamageScenario.SourceId, "All Will Be One") {
                    Impact = new("damage", 2, PlayerId: 1, SourceCardId: CounterDamageScenario.SourceId, SourceName: "All Will Be One", Combat: false, Infect: false) },
                new(21, 8, "MAIN1", "life", 1, "Lost 2 life; 38 remaining.", null, null) { Impact = new("life", -2, PlayerId: 1, Before: 40, After: 38) },
                new(22, 8, "MAIN1", "resolved", 0, "All Will Be One's ability resolved.", CounterDamageScenario.SourceId, "All Will Be One") { Outcome = "resolved" }
            ] };
        var bears = Enumerable.Range(0, 3).Select(index => new Card { VisualId = "damage-results-bear-" + index,
            CombatId = "damage-results-bear-" + index, Name = "Grizzly Bears", Type = "Creature — Bear", Power = 2, Toughness = 2,
            Damage = index == 2 ? 1 : 0 }).ToArray();
        var groupBefore = prevented with { Players = prevented.Players.Select(p => p.Id != 1 ? p : p with {
            Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with { Count = bears.Length, Cards = bears }).ToArray()
        }).ToArray() };
        var groupAfter = groupBefore with { Players = groupBefore.Players.Select(p => p.Id != 1 ? p : p with {
            Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with { Cards = bears.Select(card => card with { Damage = 1 }).ToArray() }).ToArray()
        }).ToArray(), Activity = [.. groupBefore.Activity, .. bears.Take(2).Select((card, index) =>
            new ActivityEntry(30 + index, 8, "MAIN1", "damage", 1, "All Will Be One dealt 1 damage to Grizzly Bears.", card.VisualId, card.Name) {
                Impact = new("damage", 1, card.VisualId, card.Name, SourceCardId: CounterDamageScenario.SourceId, SourceName: "All Will Be One", DamageType: "normal")
            })] };
        return [
            new("3 base damage pending", "Authored presentation: the effect is still pending against a 6/6 creature.", initial),
            new("3 damage dealt · survives", "The actual result is attached to the damaged creature; its current 6/6 and 3 marked damage remain.", first),
            new("Another 3 · creature dies", "An additional authored damage event and public departure happen in one observation.", dead),
            new("3 base damage · prevention", "The next authored effect targets a player with Urza's Armor. Pending damage remains a base amount.", playerPending),
            new("2 damage dealt · 38 life", "This authored outcome reports actual damage and a separate life transition. Forge prevention has its own engine regression.", prevented),
            new("Matching creatures · before", "One of three identical Bears already has one marked damage.", groupBefore),
            new("1 damage each · 2 of 3", "Two Bears receive one damage each and now share the third Bear's current state. Their shared face must not imply one two-damage hit.", groupAfter)
        ];
    }
}
