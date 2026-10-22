using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Authored public checkpoints for reading a counter-triggered damage choice.</summary>
public static class CounterDamageScenario
{
    public const string Id = "counter-damage.v1";
    public const string Context = "lab-counter-damage-target";
    public const string SourceId = "counter-damage-source";
    public const string CounterCardId = "counter-damage-chandra";
    public const string TargetId = "counter-damage-target";

    public static IReadOnlyList<ExperienceStage> Create() => [
        new("Choose the 3-damage target", "Authored checkpoint: Chandra entered with three loyalty counters. Choose a highlighted opponent or creature.", Snapshot(0)),
        new("3 damage is pending", "The chosen target is recorded; this effect has not resolved. Pass priority to see the authored result.", Snapshot(1)),
        new("3 damage resolved", "Authored result only. Next loads a separate +1 loyalty checkpoint.", Snapshot(2)),
        new("Choose the 1-damage target", "Chandra now has four loyalty, but this trigger came from adding only one counter. Choose a highlighted target.", Snapshot(3)),
        new("1 damage is pending", "The same captured one-counter event stays attached to the pending effect.", Snapshot(4)),
        new("1 damage resolved", "The authored result applies one damage; Chandra retains four loyalty. Reset or Previous revisits the choice.", Snapshot(5))
    ];

    public static GameSnapshot Snapshot(int stage, StackTarget? chosen = null)
    {
        stage = Math.Clamp(stage, 0, 5);
        int added = stage < 3 ? 3 : 1, loyalty = stage < 3 ? 3 : 4;
        bool choosing = stage % 3 == 0, pending = stage % 3 == 1, resolved = stage % 3 == 2;
        var source = new Card {
            VisualId = SourceId, CombatId = SourceId, Name = "All Will Be One", Type = "Enchantment", ManaCost = "{3}{R}{R}",
            Text = "Whenever you put one or more counters on a permanent or player, All Will Be One deals that much damage to target opponent, creature an opponent controls, or planeswalker an opponent controls."
        };
        var chandra = new Card {
            VisualId = CounterCardId, CombatId = CounterCardId, Name = "Chandra, the Firebrand", Type = "Legendary Planeswalker — Chandra",
            ManaCost = "{3}{R}", Counters = new() { ["Loyalty"] = loyalty },
            Text = "[+1]: Chandra, the Firebrand deals 1 damage to any target.\n[-2]: When you cast your next instant or sorcery spell this turn, copy that spell. You may choose new targets for the copy.\n[-6]: Chandra, the Firebrand deals 6 damage to each of up to six targets."
        };
        var target = new Card {
            VisualId = TargetId, CombatId = TargetId, Name = "Colossal Dreadmaw", Type = "Creature — Dinosaur", ManaCost = "{4}{G}{G}",
            Power = 6, Toughness = 6, Selectable = choosing,
            Text = "Trample"
        };
        chosen ??= new("card", TargetId, target.Name, 1);
        var effect = new PendingEffect {
            Id = "counter-damage-effect:" + added, Kind = "damage", Amount = added, AmountBasis = "trigger",
            CounterChange = new() { Card = chandra, ActorPlayerId = 0, Counters = [new("LOYALTY", "Loyalty", added)] }
        };
        var state = FourPlayerTableScenario.Create()[1].Snapshot;
        var viewerCards = new[] { source, chandra,
            Face("counter-damage-ultron", "Ultron, Artificial Malevolence", "Legendary Creature — Robot", 2, 4),
            Face("counter-damage-student", "Earthbending Student", "Creature", 1, 3),
            Face("counter-damage-torque", "Liquimetal Torque", "Artifact"),
            Face("counter-damage-forest-a", "Forest", "Basic Land — Forest"),
            Face("counter-damage-forest-b", "Forest", "Basic Land — Forest") with { Tapped = true },
            Face("counter-damage-mountain", "Mountain", "Basic Land — Mountain")
        };
        var cause = new PublicEffectCause(CounterCardId, chandra.Name, "Loyalty counters added", 0, "ability", "counter-origin:" + added);
        var activity = new ActivityEntry(1, 8, "MAIN1", "counters", 0, "Loyalty counters added.", CounterCardId, chandra.Name) {
            Change = new("counters", "Loyalty", loyalty - added, loyalty), Cause = cause
        };
        return state with {
            Id = Id, Turn = 8, PhaseKey = "MAIN1", Phase = "Main phase", ActivePlayerId = 0,
            PriorityPlayerId = choosing ? null : 0, DecisionOwnerId = 0, ActionClass = choosing ? "selection" : "priority",
            Players = state.Players.Select(player => player with {
                Life = player.Id == 0 ? 19 : resolved && chosen.Kind == "player" && chosen.PlayerId == player.Id ? 40 - added : 40,
                Mana = [], Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = player.Id == 0 ? viewerCards : player.Id == 1
                        ? new[] { target with { Damage = resolved && chosen.Kind == "card" && chosen.Id == TargetId ? added : 0 } }
                            .Concat(zone.Cards.Take(6).Select(card => card with { Selectable = false })).ToArray()
                        : zone.Cards.Select(card => card with { Selectable = false }).ToArray(),
                    Count = player.Id == 0 ? viewerCards.Length : player.Id == 1 ? 1 + Math.Min(6, zone.Cards.Length) : zone.Count
                }).ToArray()
            }).ToArray(),
            Stack = pending ? [new(source.Name, $"All Will Be One deals {added} damage to {chosen.Name}.", true, source) {
                Id = effect.Id, ControllerId = 0, Targets = [chosen], Effect = effect
            }] : [],
            Activity = resolved ? [activity,
                new(2, 8, "MAIN1", "damage", chosen.PlayerId, $"All Will Be One dealt {added} damage to {chosen.Name}.", chosen.Kind == "card" ? chosen.Id : SourceId, chosen.Kind == "card" ? chosen.Name : source.Name) {
                    Impact = new("damage", added, CardId: chosen.Kind == "card" ? chosen.Id : null,
                        CardName: chosen.Kind == "card" ? chosen.Name : null, PlayerId: chosen.Kind == "player" ? chosen.PlayerId : null,
                        SourceCardId: SourceId, SourceName: source.Name, DamageType: chosen.Kind == "card" ? "normal" : null,
                        Combat: chosen.Kind == "player" ? false : null, Infect: chosen.Kind == "player" ? false : null)
                },
                .. chosen.Kind == "player" ? new ActivityEntry[] { new(3, 8, "MAIN1", "life", chosen.PlayerId, $"{chosen.Name} lost {added} life.", null, null) {
                    Impact = new("life", -added, PlayerId: chosen.PlayerId, Before: 40, After: 40 - added)
                } } : [],
                new(4, 8, "MAIN1", "resolved", 0, "All Will Be One's ability resolved.", SourceId, source.Name) {
                Outcome = "resolved", Count = added, Cause = new(SourceId, source.Name, $"Deal {added} damage.", 0, "ability", effect.Id)
            }] : [activity],
            Decision = choosing ? new() {
                Kind = "input", Purpose = DecisionPurpose.TargetSelection, Intent = DecisionIntent.Selection, Context = Context,
                SourceCard = source, SourceCardId = SourceId, SourceZone = "Battlefield", SourceControllerId = 0, Effect = effect,
                Min = 1, Max = 1, PlayerChoices = [1, 2, 3],
                Message = "All Will Be One (62) — Whenever you put one or more counters on a permanent or player, All Will Be One deals that much damage. [Added once: You; Chandra, the Firebrand (96)]"
            } : new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = pending }
        };
    }

    private static Card Face(string id, string name, string type, int? power = null, int? toughness = null) => new() {
        VisualId = id, CombatId = id, Name = name, Type = type, Power = power, Toughness = toughness
    };
}
