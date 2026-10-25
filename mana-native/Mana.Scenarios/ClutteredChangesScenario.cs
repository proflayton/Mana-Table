using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Controlled public projections for presentation acceptance, not a rules engine.
// Forge's separate Toph/Felidar tests establish the underlying Magic behavior.
public static class ClutteredChangesScenario
{
    public const string TophName = "Toph, the First Metalbender";
    public const string RetreatName = "Felidar Retreat";
    public const string AnimatedLandId = "crowd-0-land-12";
    public const string ActionCardId = "crowd-0-creature-0";
    public static string TophId(int seat) => $"crowd-{seat}-toph";
    public static string RetreatId(int seat) => $"crowd-{seat}-retreat";
    public static readonly string[] CreatureNames = ["Llanowar Elves", "Elvish Mystic", "Birds of Paradise", "Lotus Cobra", "Tireless Tracker", "Scavenging Ooze",
        "Eternal Witness", "Reclamation Sage", "Solemn Simulacrum", "Wall of Blossoms", "Acidic Slime", "Avenger of Zendikar",
        "Rampaging Baloths", "Oracle of Mul Daya", "Sakura-Tribe Elder", "Wood Elves", "Beast Whisperer", "Toski, Bearer of Secrets"];
    private static readonly string[] SupportNames = ["Sol Ring", "Arcane Signet", "Selesnya Signet", "Swiftfoot Boots", "Lightning Greaves",
        "Skullclamp", "The Great Henge", "Doubling Season", "Rhystic Study", "Exploration"];

    public static GameSnapshot Initial()
    {
        Card New(string id, string name, string type, int? power = null, int? toughness = null) => new() {
            VisualId = id, CombatId = id, Name = name, Type = type, Power = power, Toughness = toughness
        };
        Card[] Battlefield(int seat) => [
            .. Enumerable.Range(0, 50).Select(index => New($"crowd-{seat}-cat-{index}", "Cat Beast Token", "Token Creature — Cat Beast", 2, 2)),
            .. Enumerable.Range(0, 40).Select(index => {
                bool animated = index < 12;
                int counters = animated ? 1 + index % 4 : 0;
                return New($"crowd-{seat}-land-{index}", index % 3 == 0 ? "Forest" : index % 3 == 1 ? "Plains" : "Island",
                    animated ? "Basic Land Creature — Elemental" : "Basic Land", animated ? counters : null, animated ? counters : null) with {
                        Counters = animated ? ImmutableDictionary<string, int>.Empty.Add("+1/+1", counters) : ImmutableDictionary<string, int>.Empty,
                        CombatKeywords = animated ? ["haste"] : [], Tapped = index is >= 12 and < 24
                    };
            }),
            .. CreatureNames.Select((name, index) => New($"crowd-{seat}-creature-{index}", name, "Creature", 2 + index % 5, 3 + index % 4) with {
                Selectable = seat == 0 && index == 0, Text = index == 0 ? "{T}: Add {G}." : "",
                Counters = index % 3 == 0 ? ImmutableDictionary<string, int>.Empty.Add("+1/+1", index + 1) : ImmutableDictionary<string, int>.Empty
            }),
            New(TophId(seat), TophName, "Legendary Creature — Human Warrior Ally", 3, 3),
            New(RetreatId(seat), RetreatName, "Enchantment"),
            .. SupportNames.Select((name, index) => New($"crowd-{seat}-support-{index}", name, index < 7 ? "Artifact Land" : "Enchantment"))
        ];
        var players = Enumerable.Range(0, 4).Select(seat => new Player(seat, seat == 0 ? "You" : "Opponent " + seat, 40, seat == 0, false, ImmutableDictionary<string, int>.Empty, [
            new("Battlefield", 120, Battlefield(seat).ToImmutableArray(), null), new("Hand", seat == 0 ? 0 : 7, [], null),
            new("Command", 0, [], null), new("Library", 60, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null)
        ])).ToArray();
        return new("cluttered-causal-changes", "playing", null, null, 0, 8, "Main phase", "MAIN1", 0, players.ToImmutableArray(), [], null,
            new() { Task = DecisionTask.Priority, OkEnabled = true, CanAutoPass = false,
                Message = "You have priority. Inspect any permanent, or activate Llanowar Elves to add mana." });
    }

    public static GameSnapshot Earthbend(GameSnapshot before)
    {
        var land = before.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == AnimatedLandId);
        int previous = land.Counters.GetValueOrDefault("+1/+1");
        var cause = new PublicEffectCause(TophId(0), TophName,
            "At the beginning of your end step, earthbend 2. Target land you control becomes a 0/0 creature with haste that's still a land. Put two +1/+1 counters on it.", 0, "trigger", "toph-earthbend-8");
        return Change(before, card => card.VisualId != AnimatedLandId ? card : card with {
            Type = "Basic Land Creature — Elemental", Tapped = false, CombatKeywords = ["haste"],
            Counters = (new Dictionary<string, int>(card.Counters) { ["+1/+1"] = previous + 2 }).ToImmutableDictionary(), Power = previous + 2, Toughness = previous + 2
        }, [new(1, 8, "END_OF_TURN", "counters", 0, "Added two +1/+1 counters.", land.VisualId, land.Name) {
            Change = new("counters", "+1/+1", previous, previous + 2), Cause = cause
        }]);
    }

    public static GameSnapshot Retreat(GameSnapshot before)
    {
        var cause = new PublicEffectCause(RetreatId(0), RetreatName,
            "Landfall — Put a +1/+1 counter on each creature you control. Those creatures gain vigilance until end of turn.", 0, "trigger", "felidar-landfall-8");
        var creatures = before.Viewer!.Zone("Battlefield").Cards.Where(IsCreature).ToArray();
        long first = before.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max() + 1;
        var entries = creatures.Select((card, index) => new ActivityEntry(first + index, 8, "MAIN1", "counters", 0,
            "Added a +1/+1 counter.", card.VisualId, card.Name) {
                Change = new("counters", "+1/+1", card.Counters.GetValueOrDefault("+1/+1"), card.Counters.GetValueOrDefault("+1/+1") + 1), Cause = cause
            }).ToArray();
        return Change(before, card => !IsCreature(card) ? card : card with {
            Counters = (new Dictionary<string, int>(card.Counters) { ["+1/+1"] = card.Counters.GetValueOrDefault("+1/+1") + 1 }).ToImmutableDictionary(),
            Power = card.Power + 1, Toughness = card.Toughness + 1,
            CombatKeywords = card.CombatKeywords.Append("vigilance").Distinct().ToImmutableArray()
        }, entries);
    }
    private static bool IsCreature(Card card) => card.Type.Contains("Creature", StringComparison.Ordinal);
    private static GameSnapshot Change(GameSnapshot before, Func<Card, Card> change, ActivityEntry[] entries) => before with {
        Players = before.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = zone.Cards.Select(change).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray(), Activity = [.. before.Activity, .. entries]
    };
}
