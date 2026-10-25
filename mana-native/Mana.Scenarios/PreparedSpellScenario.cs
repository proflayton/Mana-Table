using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Authored public checkpoints for the prepared-spell journey. These do not
// implement prepare, payment, targeting or drawing rules; Forge tests do that.
public static class PreparedSpellScenario
{
    public const string Id = "prepared-spell.v1";
    public const string SourceId = "prepared-emeritus";
    public const string SpellId = "prepared-recall";
    internal const string CastAction = "prepared-recall-cast";

    public static IReadOnlyList<ExperienceStage> Create() => [
        new("Prepared", "Read Emeritus, then its linked Ancestral Recall. The spell also remains visible in exile.", Snapshot(0)),
        new("Choose a player", "The authored target choice belongs to Ancestral Recall. Choose a player or cancel.", Snapshot(1)),
        new("Spell pending", "The authored cast consumes the prepared designation and one blue mana. Emeritus remains on the battlefield.", Snapshot(2)),
        new("Another responder", "Opponent 1 has priority. No private response is supplied for that player.", Snapshot(3)),
        new("Draw resolved", "The chosen player's hand grows by three. Emeritus stays on the battlefield, unprepared.", Snapshot(4)),
        new("Prepared again", "A later authored checkpoint restores the prepared designation with a new spell occurrence.", Snapshot(5))
    ];

    internal static GameSnapshot Snapshot(int stage, int recipient = 0, bool preparedAgain = false)
    {
        bool prepared = stage is 0 or 1 or 5;
        bool targeting = stage == 1;
        bool pending = stage is 2 or 3;
        var spell = Spell(stage == 5 || preparedAgain ? SpellId + "-again" : SpellId) with { Selectable = stage is 0 or 5 };
        var source = Source(SourceId, prepared) with { Selectable = stage is 0 or 5 };
        var players = Enumerable.Range(0, 4).Select(seat => {
            bool local = seat == 0;
            var field = local ? new[] { source, Land(0), Land(1), Land(2) }
                : seat == 1 ? new[] { Source("opponent-prepared-emeritus", true), Land(3), Land(4) }
                : new[] { new Card { VisualId = "prepared-bear-" + seat, CombatId = "prepared-bear-" + seat,
                    Name = "Grizzly Bears", Type = "Creature — Bear", Power = 2, Toughness = 2, States = [] } };
            int hand = 1 + (stage == 4 && recipient == seat ? 3 : 0);
            var handCards = local ? Enumerable.Range(0, hand).Select(index => new Card {
                VisualId = "prepared-hand-" + index, Name = "Forest", Type = "Basic Land — Forest", Text = "{T}: Add {G}.", States = []
            }).ToImmutableArray() : [];
            var exile = local && prepared ? new[] { spell } : seat == 1 ? new[] { Spell("opponent-prepared-recall") } : [];
            return new Player(seat, local ? "You" : "Opponent " + seat, 40, !targeting && seat == (stage == 3 ? 1 : 0), false,
                local && (stage is 0 or 1 or 5) ? ImmutableDictionary<string, int>.Empty.Add("U", 1) : ImmutableDictionary<string, int>.Empty, [
                    new("Battlefield", field.Length, field.ToImmutableArray(), null), new("Hand", hand, handCards, null),
                    new("Command", 0, [], null), new("Library", 80 - (stage == 4 && recipient == seat ? 3 : 0), [], null),
                    new("Graveyard", 0, [], null), new("Exile", exile.Length, exile.ToImmutableArray(), null)
                ]);
        }).ToImmutableArray();
        var decision = stage == 3 ? null : targeting ? new Decision {
            Task = DecisionTask.TargetSelection, Title = "Choose a player", Message = "Target player draws three cards.", Min = 1, Max = 1,
            CancelEnabled = true, PlayerChoices = [0, 1, 2, 3], SourceCard = spell,
            SourceCardId = spell.VisualId, SourceControllerId = 0, SourceZone = "Exile"
        } : new Decision { Task = DecisionTask.Priority, OkEnabled = pending,
            Message = pending ? "Ancestral Recall is waiting on the stack." : "You have priority." };
        return new(Id, "playing", null, null, 0, stage == 5 || preparedAgain ? 12 : 8, "Main phase", "MAIN1", 0, players,
            pending ? [new StackItem(spell.Name, "Target player draws three cards.", false, spell) {
                Id = "prepared-recall-on-stack", ControllerId = 0,
                Targets = [new("player", recipient.ToString(System.Globalization.CultureInfo.InvariantCulture), players[recipient].Name, recipient)]
            }] : [], null, decision) {
                DecisionOwnerId = stage == 3 ? 1 : 0, PriorityPlayerId = targeting ? null : stage == 3 ? 1 : 0,
                ActionClass = targeting ? "selection" : "priority"
            };
    }

    private static Card Source(string id, bool prepared) => new() {
        VisualId = id, CombatId = id, Name = "Emeritus of Ideation", ManaCost = "{3}{U}{U}",
        Type = "Creature — Human Wizard", Power = 5, Toughness = 5,
        Text = "Flying, ward {2}\nThis creature enters prepared.\nWhenever this creature attacks, you may exile eight cards from your graveyard. If you do, this creature becomes prepared.",
        States = prepared ? [new("keyword:flying", "Flying", "ability", null, null, "Flying", "flying"),
            new("keyword:ward:2", "Ward {2}", "ability", null, null, "Ward {2}", "ward"),
            new("prepared", "Prepared", "status", null, null, "Ancestral Recall {U}", "prepared")]
            : [new("keyword:flying", "Flying", "ability", null, null, "Flying", "flying"),
                new("keyword:ward:2", "Ward {2}", "ability", null, null, "Ward {2}", "ward")]
    };

    private static Card Spell(string id) => new() {
        VisualId = id, Name = "Ancestral Recall", ArtName = "Ancestral Recall", Type = "Instant", ManaCost = "{U}",
        Text = "Target player draws three cards.", GamePieceType = "copied_spell", States = []
    };

    private static Card Land(int index) => new() {
        VisualId = "prepared-island-" + index, Name = "Island", Type = "Basic Land — Island",
        Text = "{T}: Add {U}.", Tapped = true, States = []
    };
}
