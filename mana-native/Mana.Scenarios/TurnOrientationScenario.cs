using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>
/// Matching authored checkpoints for stationary/follow comparison. Next changes
/// public projections; no rules, turns, spells or combat are simulated here.
/// </summary>
public static class TurnOrientationScenario
{
    public const string AttackerToYouId = "crowd-1-creature-0";
    public const string AttackerToJulesId = "crowd-1-creature-1";
    public const string MiraTargetId = "crowd-2-creature-0";
    public const string JulesTargetId = "crowd-3-creature-1";
    private static readonly string[] Names = ["You", "Rowan", "Mira", "Jules"];

    public static IReadOnlyList<ExperienceStage> Ordinary() => Create(false);
    public static IReadOnlyList<ExperienceStage> Crowded() => Create(true);

    private static IReadOnlyList<ExperienceStage> Create(bool crowded)
    {
        var growth = Face("orientation-local-growth", "Giant Growth", "Instant", "{G}",
            "Target creature gets +3/+3 until end of turn.");
        Card[] hand = [growth, Face("orientation-hand-forest", "Forest", "Basic Land — Forest")];
        var source = ClutteredChangesScenario.Initial();
        var baseline = source with {
            Id = crowded ? "turn-orientation-crowded" : "turn-orientation",
            Players = source.Players.Select(player => player with {
                Name = Names[player.Id], Mana = ImmutableDictionary<string, int>.Empty, States = [],
                Zones = player.Zones.Select(zone => zone.Name switch {
                    "Battlefield" => zone with {
                        Count = crowded ? zone.Count : 5,
                        Cards = (crowded ? zone.Cards.Select(card => card with { Selectable = false, Highlighted = false }).ToArray()
                            : OrdinaryField(player.Id)).ToImmutableArray()
                    },
                    "Hand" => zone with { Count = player.Id == 0 ? hand.Length : 5, Cards = (player.Id == 0 ? hand : []).ToImmutableArray() },
                    _ => zone
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var yours = Priority(baseline, 8, 0, 0);
        var rowans = Priority(yours, 9, 1, 1);
        var cultivate = Face("orientation-cultivate-card", "Cultivate", "Sorcery", "{2}{G}");
        var counterspell = Face("orientation-counterspell-card", "Counterspell", "Instant", "{U}{U}", "Counter target spell.");
        var response = Priority(rowans, 9, 1, 2);
        response = response with {
            Players = response.Players.Select(player => player.Id is not (1 or 2) ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = 4 } : zone).ToImmutableArray()
            }).ToImmutableArray(),
            Stack = [
                new("Counterspell", counterspell.Text, false, counterspell) {
                    Id = "orientation-counterspell", ControllerId = 2,
                    Targets = [new("stack", "orientation-cultivate", "Cultivate")]
                },
                new("Cultivate", "", false, cultivate) { Id = "orientation-cultivate", ControllerId = 1 }
            ]
        };
        // These are explicit public combat assignments, not calculated legal
        // attacks or a resolution of the preceding spell checkpoint.
        var combat = Priority(response, 9, 1, 3) with {
            Phase = "Declare attackers", PhaseKey = "COMBAT_DECLARE_ATTACKERS",
            Combat = new([
                new(AttackerToYouId, 0, [], []) { Defender = new("player", "0", 0, Names[0]) },
                new(AttackerToJulesId, 3, [], []) { Defender = new("player", "3", 3, Names[3]) }
            ], [], null)
        };
        combat = MapBattlefield(combat, card => card.VisualId == AttackerToYouId ? card with {
            Tapped = true, Attacking = true, DefenderId = 0, Defender = Names[0]
        } : card.VisualId == AttackerToJulesId ? card with {
            Tapped = true, Attacking = true, DefenderId = 3, Defender = Names[3]
        } : card);
        var targetCards = combat.Players.SelectMany(player => player.Zone("Battlefield").Cards)
            .Where(card => card.VisualId is MiraTargetId or JulesTargetId).ToArray();
        var choosing = combat with {
            ActionClass = "selection", PriorityPlayerId = null, DecisionOwnerId = 0,
            Players = combat.Players.Select(player => player with { Priority = false }).ToImmutableArray(),
            Decision = new() {
                Task = DecisionTask.Options, Title = "Choose a creature", Message = "Choose one of the shown creatures in this scripted target preview.",
                Min = 1, Max = 1, SourceCard = growth, SourceCardId = growth.VisualId, SourceZone = "Hand", SourceControllerId = 0,
                Choices = targetCards.Select((card, index) => new Choice(index, card.Name, card) {
                    Id = "orientation-target-" + index, CardId = card.VisualId,
                    ControllerId = card.VisualId == MiraTargetId ? 2 : 3, OwnerId = card.VisualId == MiraTargetId ? 2 : 3
                }).ToImmutableArray()
            }
        };
        var quiet = Priority(MapBattlefield(combat, card => card with {
            Attacking = false, Blocking = false, DefenderId = null, Defender = null
        }), 9, 1, 0) with { Phase = "Second main phase", PhaseKey = "MAIN2" };
        var miras = Priority(quiet, 10, 2, 2);
        var extra = Priority(miras, 11, 2, 2);
        return Array.AsReadOnly(new ExperienceStage[] {
            new("Your turn · baseline", "Your turn and your priority. Inspect a card or use Next to preview the handoff.", yours),
            new("Rowan's turn", "A reported turn boundary changes the active player from You to Rowan.", rowans),
            new("Mira responds on Rowan's turn", "Mira's Counterspell is on top of Rowan's Cultivate. Rowan still owns the turn; Mira has priority.", response),
            new("Combat · two defenders", "An authored combat checkpoint shows Rowan attacking You and Jules. No spell or combat rules were simulated.", combat),
            new("Your cross-seat target choice", "You must choose between current public creatures controlled by Mira and Jules. Rowan still owns the turn.", choosing),
            new("Quiet priority on Rowan's turn", "The authored sequence moves to an empty-stack window. You have priority; Rowan remains the turn owner.", quiet),
            new("Mira's turn", "A new reported turn boundary moves turn ownership to Mira.", miras),
            new("Mira's extra turn", "The reported turn advances while Mira remains active. An already oriented view can stay still.", extra)
        });
    }

    private static GameSnapshot Priority(GameSnapshot basis, int turn, int active, int responder) => basis with {
        Turn = turn, Phase = "Main phase", PhaseKey = "MAIN1", ActivePlayerId = active,
        PriorityPlayerId = responder, DecisionOwnerId = responder, ActionClass = "priority", Stack = [], Combat = null,
        Players = basis.Players.Select(player => player with { Priority = player.Id == responder }).ToImmutableArray(),
        Decision = responder == basis.ViewerId ? new() {
            Task = DecisionTask.Priority, OkEnabled = true, CanAutoPass = false,
            Message = "You have priority. Use Next to continue this scripted sequence."
        } : null
    };
    private static GameSnapshot MapBattlefield(GameSnapshot state, Func<Card, Card> map) => state with {
        Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Cards = zone.Cards.Select(map).ToImmutableArray() } : zone).ToImmutableArray()
        }).ToImmutableArray()
    };
    private static Card[] OrdinaryField(int seat) => [
        Face($"crowd-{seat}-creature-0", "Llanowar Elves", "Creature — Elf Druid", "{G}", "{T}: Add {G}.") with { Power = 1, Toughness = 1 },
        Face($"crowd-{seat}-creature-1", "Elvish Mystic", "Creature — Elf Druid", "{G}", "{T}: Add {G}.") with { Power = 1, Toughness = 1 },
        Face($"crowd-{seat}-land-24", "Forest", "Basic Land — Forest"),
        Face($"crowd-{seat}-land-25", "Plains", "Basic Land — Plains"),
        Face($"crowd-{seat}-support-8", "Rhystic Study", "Enchantment", "{2}{U}")
    ];
    private static Card Face(string id, string name, string type, string mana = "", string text = "") => new() {
        VisualId = id, CombatId = id, Name = name, ArtName = name, Type = type, ManaCost = mana, Text = text
    };
}
