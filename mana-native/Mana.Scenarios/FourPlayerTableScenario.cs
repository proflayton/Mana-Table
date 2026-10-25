using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>
/// Authored four-seat public projections for layout and attention review.
/// Stage navigation does not cast cards, choose targets or simulate Magic rules.
/// </summary>
public static class FourPlayerTableScenario
{
    public const string Id = "four-player-table.v1";
    public const string SourceId = "four-player-1-ballista";
    public const string TargetId = "four-player-0-tracker";
    public const string EquipmentId = "four-player-0-sword";
    public const string PendingAbilityId = "four-player-ballista-damage";
    private const string DamageAbility = "Remove a +1/+1 counter from Walking Ballista: It deals 1 damage to any target.";
    private static readonly string[] Names = ["You", "Rowan", "Mira", "Jules"];

    public static IReadOnlyList<ExperienceStage> Create()
    {
        var ordinary = Ordinary();
        var additions = BusyAdditions();
        var crowded = ordinary with {
            Players = ordinary.Players.Select(player => player.Id != 2 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = [.. zone.Cards, .. additions], Count = zone.Cards.Length + additions.Length
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        // The cost has already been paid in this checkpoint. These explicit
        // characteristics are not derived again from the displayed counters.
        var pending = MapBattlefield(crowded, card => card.VisualId != SourceId ? card : card with {
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2), Power = 2, Toughness = 2
        });
        var source = pending.Players[1].Zone("Battlefield").Cards.Single(card => card.VisualId == SourceId);
        var target = pending.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == TargetId);
        var cause = new PublicEffectCause(SourceId, source.Name, DamageAbility, 1, "ability", PendingAbilityId);
        var ability = new StackItem(source.Name, "Walking Ballista deals 1 damage to Tireless Tracker.", true, source) {
            Id = PendingAbilityId, ControllerId = 1, Targets = [new("card", TargetId, target.Name, 0)]
        };
        pending = pending with {
            Stack = [ability],
            Activity = [
                new(1, 8, "MAIN1", "counters", 1, "Removed a +1/+1 counter.", SourceId, source.Name) {
                    Change = new("counters", "+1/+1", 3, 2), Cause = cause
                },
                new(2, 8, "MAIN1", "ability", 1, "Rowan activated Walking Ballista.", SourceId, source.Name) {
                    Detail = ability.Text, Cause = cause
                }
            ]
        };
        // Damage is separate from toughness. The Tracker remains an effective
        // 5/4 with one counter and its equipment; one damage is now marked on it.
        var result = MapBattlefield(pending, card => card.VisualId != TargetId ? card : card with { Damage = 1 }) with {
            Stack = [],
            Activity = [.. pending.Activity,
                new(3, 8, "MAIN1", "damage", 0, "Walking Ballista dealt 1 damage to Tireless Tracker.", TargetId, target.Name) {
                    Change = new("damage", "damage", 0, 1), Cause = cause
                },
                new(4, 8, "MAIN1", "resolved", 1, "Walking Ballista's ability resolved.", SourceId, source.Name) {
                    Outcome = "resolved", Detail = ability.Text, Cause = cause
                }
            ]
        };
        return Array.AsReadOnly(new ExperienceStage[] {
            new("Four ordinary battlefields", "Authored public snapshot: four distinct boards, your seven-card hand, public command and graveyard cards, counters and an attached Sword. No legal play actions are supplied.", ordinary),
            new("Mira's battlefield is crowded", "Only Mira gains additional public permanents in this authored comparison. The other three boards, hands and occurrence IDs stay unchanged; no arrivals are simulated.", crowded),
            new("Rowan targets your Tracker", "Authored pending ability: Rowan's Ballista is now 2/2 with two counters after its cost. It targets your equipped 5/4 Tireless Tracker across seats; you have priority. Next previews the result after the other response windows.", pending),
            new("Damage marked; current state retained", "Authored result: the ability is gone and your Tracker has one marked damage. It remains 5/4 with one +1/+1 counter and its attached Sword. Rowan's Ballista remains 2/2 with two counters. No rules were simulated.", result)
        });
    }

    private static GameSnapshot Ordinary()
    {
        Card[] hand = [
            Face("four-player-0-hand-forest", "Forest", "Basic Land — Forest"),
            Face("four-player-0-hand-island", "Island", "Basic Land — Island"),
            Face("four-player-0-hand-growth", "Giant Growth", "Instant", "{G}", "Target creature gets +3/+3 until end of turn."),
            Face("four-player-0-hand-counterspell", "Counterspell", "Instant", "{U}{U}", "Counter target spell."),
            Face("four-player-0-hand-cultivate", "Cultivate", "Sorcery", "{2}{G}"),
            Face("four-player-0-hand-sage", "Reclamation Sage", "Creature — Elf Shaman", "{2}{G}") with { Power = 2, Toughness = 1 },
            Face("four-player-0-hand-beast", "Beast Within", "Instant", "{2}{G}")
        ];
        Card[] commanders = [
            Face("four-player-0-commander", "Tatyova, Benthic Druid", "Legendary Creature — Merfolk Druid", "{3}{G}{U}") with { Power = 3, Toughness = 3, CommanderTax = 2 },
            Face("four-player-1-commander", "Shorikai, Genesis Engine", "Legendary Artifact — Vehicle", "{2}{W}{U}") with { Power = 8, Toughness = 8 },
            Face("four-player-2-commander", "Aesi, Tyrant of Gyre Strait", "Legendary Creature — Serpent", "{4}{G}{U}") with { Power = 5, Toughness = 5 },
            Face("four-player-3-commander", "Emmara, Soul of the Accord", "Legendary Creature — Elf Cleric", "{G}{W}") with { Power = 2, Toughness = 2 }
        ];
        Card[] graveyard = [
            Face("four-player-0-grave-think", "Think Twice", "Instant", "{1}{U}", "Draw a card.\nFlashback {2}{U} (You may cast this card from your graveyard for its flashback cost. Then exile it.)"),
            Face("four-player-1-grave-ponder", "Ponder", "Sorcery", "{U}"),
            Face("four-player-2-grave-growth", "Growth Spiral", "Instant", "{G}{U}"),
            Face("four-player-3-grave-cultivate", "Cultivate", "Sorcery", "{2}{G}")
        ];
        int[] life = [36, 38, 40, 33];
        var players = Enumerable.Range(0, 4).Select(seat => {
            var field = OrdinaryField(seat);
            return new Player(seat, Names[seat], life[seat], seat == 0, false,
                seat == 0 ? ImmutableDictionary<string, int>.Empty.Add("G", 1).Add("U", 2) : ImmutableDictionary<string, int>.Empty, [
                    new("Battlefield", field.Length, field.ToImmutableArray(), null),
                    new("Hand", seat == 0 ? hand.Length : 5, (seat == 0 ? hand : []).ToImmutableArray(), null),
                    new("Command", 1, [commanders[seat]], null),
                    new("Library", 72, [], null),
                    new("Graveyard", 1, [graveyard[seat]], graveyard[seat]),
                    new("Exile", 0, [], null)
                ]);
        }).ToArray();
        return new(Id, "playing", null, null, 0, 8, "Main phase", "MAIN1", 1, players.ToImmutableArray(), [], null, new() {
            Task = DecisionTask.Priority, OkEnabled = false, CanAutoPass = false,
            Message = "Authored snapshot. Inspect cards; use the stage controls to compare checkpoints."
        }) { PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority" };
    }

    private static Card[] OrdinaryField(int seat)
    {
        Card[] cards = seat switch {
            0 => [
                Face(TargetId, "Tireless Tracker", "Creature — Human Scout", "{2}{G}") with {
                    Power = 5, Toughness = 4, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1),
                    Attachments = [new(EquipmentId, "Sword of the Animist", "Legendary Artifact — Equipment")]
                },
                Face(EquipmentId, "Sword of the Animist", "Legendary Artifact — Equipment", "{2}", "Equipped creature gets +1/+1.\nEquip {2}") with {
                    AttachedTo = new(TargetId, "Tireless Tracker", "Creature — Human Scout")
                },
                Creature(0, "elf", "Llanowar Elves", "Elf Druid", "{G}", 1, 1),
                Creature(0, "witness", "Eternal Witness", "Human Shaman", "{1}{G}{G}", 2, 1),
                Support(0, "ring", "Sol Ring", "Artifact", "{1}"),
                Support(0, "signet", "Arcane Signet", "Artifact", "{2}"),
                Support(0, "study", "Rhystic Study", "Enchantment", "{2}{U}")
            ],
            1 => [
                Face(SourceId, "Walking Ballista", "Artifact Creature — Construct", "{X}{X}",
                    "Walking Ballista enters with X +1/+1 counters on it.\n{4}: Put a +1/+1 counter on Walking Ballista.\n" + DamageAbility) with {
                    Power = 3, Toughness = 3, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 3)
                },
                Creature(1, "angel", "Serra Angel", "Angel", "{3}{W}{W}", 4, 4) with { CombatKeywords = ["flying", "vigilance"] },
                Face("four-player-1-solemn", "Solemn Simulacrum", "Artifact Creature — Golem", "{4}") with { Power = 2, Toughness = 2 },
                Support(1, "ring", "Sol Ring", "Artifact", "{1}"),
                Support(1, "signet", "Azorius Signet", "Artifact", "{2}"),
                Support(1, "remora", "Mystic Remora", "Enchantment", "{U}")
            ],
            2 => [
                Creature(2, "oracle", "Coiling Oracle", "Snake Elf Druid", "{G}{U}", 1, 1),
                Face("four-player-2-courser", "Courser of Kruphix", "Enchantment Creature — Centaur", "{1}{G}{G}") with { Power = 2, Toughness = 4 },
                Creature(2, "scute", "Scute Swarm", "Insect", "{2}{G}", 1, 1),
                Creature(2, "krasis", "Hydroid Krasis", "Jellyfish Hydra Beast", "{X}{G}{U}", 4, 4) with {
                    Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 4), CombatKeywords = ["flying", "trample"]
                },
                Support(2, "ring", "Sol Ring", "Artifact", "{1}"),
                Support(2, "signet", "Simic Signet", "Artifact", "{2}"),
                Support(2, "exploration", "Exploration", "Enchantment", "{G}"),
                Support(2, "retreat", "Retreat to Coralhelm", "Enchantment", "{2}{U}")
            ],
            _ => [
                Creature(3, "aspirant", "Luminarch Aspirant", "Human Cleric", "{1}{W}", 3, 3) with { Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2) },
                Creature(3, "pridemage", "Qasali Pridemage", "Cat Wizard", "{G}{W}", 2, 2),
                Creature(3, "voice", "Voice of Resurgence", "Elemental", "{G}{W}", 2, 2),
                Support(3, "ring", "Sol Ring", "Artifact", "{1}"),
                Support(3, "signet", "Selesnya Signet", "Artifact", "{2}"),
                Support(3, "retreat", "Felidar Retreat", "Enchantment", "{3}{W}")
            ]
        };
        string[] lands = seat switch {
            1 => ["Plains", "Island", "Plains", "Island", "Command Tower"],
            3 => ["Forest", "Plains", "Forest", "Plains", "Command Tower"],
            _ => ["Forest", "Island", "Forest", "Island", "Command Tower"]
        };
        return [.. cards, .. lands.Select((name, index) => Land(seat, "land-" + index, name, index < 2))];
    }

    private static Card[] BusyAdditions() => [
        // Authored copies retain separate occurrence IDs and public state.
        // They compare family grouping; no token creation or damage is simulated.
        .. Enumerable.Range(0, 8).Select(index => Creature(2, "busy-scute-copy-" + index,
            "Scute Swarm", "Insect", "{2}{G}", index < 6 ? 1 : 2, index < 6 ? 1 : 2) with {
                Tapped = index is 4 or 5,
                Counters = index < 6 ? ImmutableDictionary<string, int>.Empty : ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1),
                Damage = index == 7 ? 1 : 0
            }),
        Creature(2, "busy-birds", "Birds of Paradise", "Bird", "{G}", 0, 1) with { CombatKeywords = ["flying"] },
        Creature(2, "busy-cobra", "Lotus Cobra", "Snake", "{1}{G}", 2, 1),
        Creature(2, "busy-provisioner", "Tireless Provisioner", "Elf Scout", "{2}{G}", 3, 2),
        Creature(2, "busy-ooze", "Scavenging Ooze", "Ooze", "{1}{G}", 4, 4) with { Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2) },
        Creature(2, "busy-witness", "Eternal Witness", "Human Shaman", "{1}{G}{G}", 2, 1),
        Creature(2, "busy-sage", "Reclamation Sage", "Elf Shaman", "{2}{G}", 2, 1),
        Face("four-player-2-busy-solemn", "Solemn Simulacrum", "Artifact Creature — Golem", "{4}") with { Power = 2, Toughness = 2 },
        Creature(2, "busy-wall", "Wall of Blossoms", "Plant Wall", "{1}{G}", 0, 4) with { CombatKeywords = ["defender"] },
        Creature(2, "busy-slime", "Acidic Slime", "Ooze", "{3}{G}{G}", 2, 2) with { CombatKeywords = ["deathtouch"] },
        Creature(2, "busy-avenger", "Avenger of Zendikar", "Elemental", "{5}{G}{G}", 5, 5),
        Creature(2, "busy-baloths", "Rampaging Baloths", "Beast", "{4}{G}{G}", 6, 6) with { CombatKeywords = ["trample"] },
        Creature(2, "busy-oracle", "Oracle of Mul Daya", "Elf Shaman", "{3}{G}", 2, 2),
        Creature(2, "busy-elder", "Sakura-Tribe Elder", "Snake Shaman", "{1}{G}", 1, 1),
        Creature(2, "busy-wood-elves", "Wood Elves", "Elf Scout", "{2}{G}", 1, 1),
        Creature(2, "busy-whisperer", "Beast Whisperer", "Elf Druid", "{2}{G}{G}", 2, 3),
        Face("four-player-2-busy-toski", "Toski, Bearer of Secrets", "Legendary Creature — Squirrel", "{3}{G}") with {
            Power = 1, Toughness = 1, CombatKeywords = ["indestructible"]
        },
        Support(2, "busy-henge", "The Great Henge", "Legendary Artifact", "{7}{G}{G}"),
        Support(2, "busy-project", "Guardian Project", "Enchantment", "{3}{G}"),
        Support(2, "busy-season", "Doubling Season", "Enchantment", "{4}{G}"),
        .. Enumerable.Range(0, 12).Select(index => Land(2, "busy-land-" + index, index % 2 == 0 ? "Forest" : "Island", index < 7))
    ];

    private static Card Creature(int seat, string id, string name, string subtypes, string mana, int power, int toughness) =>
        Face($"four-player-{seat}-{id}", name, "Creature — " + subtypes, mana) with { Power = power, Toughness = toughness };
    private static Card Support(int seat, string id, string name, string type, string mana) =>
        Face($"four-player-{seat}-{id}", name, type, mana);
    private static Card Land(int seat, string id, string name, bool tapped) =>
        Face($"four-player-{seat}-{id}", name, name == "Command Tower" ? "Land" : "Basic Land — " + name) with { Tapped = tapped };
    private static Card Face(string id, string name, string type, string mana = "", string text = "") => new() {
        VisualId = id, CombatId = id, Name = name, ArtName = name, Type = type, ManaCost = mana, Text = text
    };
    private static GameSnapshot MapBattlefield(GameSnapshot state, Func<Card, Card> map) => state with {
        Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = zone.Cards.Select(map).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray()
    };
}
