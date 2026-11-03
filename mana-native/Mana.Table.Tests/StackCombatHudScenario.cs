using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

// Authored public presentation checkpoint, not a rules simulation. Consider was
// already pending when Mithril Coat was flashed in; its disclosed entry trigger
// now targets the local Geist while an opponent's two attackers remain unblocked.
internal static class StackCombatHudScenario
{
    internal const string Coat = "stack-hud-mithril-coat", Geist = "stack-hud-geist";
    internal const string Trigger = "stack-hud-mithril-trigger", OlderSpell = "stack-hud-consider";
    internal const string TriggerExecution = "stack-hud-mithril-execution", OlderExecution = "stack-hud-consider-execution";
    internal const string TriggerText = "When Mithril Coat enters, attach it to target legendary creature you control.";
    // WrappedAbility carries targeting plus important public stack objects in
    // this form. The numeric display references here are authored fixture text,
    // never command handles or evidence of an actual engine receipt.
    internal const string ProviderTriggerText = TriggerText
        + " (Targeting: [Geist of Saint Thalia (604)]) [Zone Changer: Mithril Coat (599)]";

    internal static GameSnapshot ProviderDescription(int pendingCount = 2)
    {
        var state = Snapshot(pendingCount);
        return state with { Stack = state.Stack.SetItem(0, state.Stack[0] with { Text = ProviderTriggerText }) };
    }

    internal static GameSnapshot Grouped()
    {
        var state = ProviderDescription(1);
        var entries = Enumerable.Range(0, 3).Select(index => state.Stack[0] with {
            Id = index == 0 ? Trigger : Trigger + "-copy-" + index,
            ExecutionId = index == 0 ? TriggerExecution : TriggerExecution + "-copy-" + index
        }).ToImmutableArray();
        var occurrences = entries.Select(item => new ExecutionOccurrence(item.ExecutionId, TriggerExecution,
            ExecutionKind.Trigger, ExecutionStep.Stack) {
                ParentId = item.Id == Trigger ? null : TriggerExecution, ActorPlayerId = 0,
                SourceCardId = Coat, SourceName = item.Name
            }).ToImmutableArray();
        return state with { Stack = entries, Executions = new(1, 3, occurrences,
            entries.Select((item, index) => new ExecutionTransition(index + 1, item.ExecutionId, ExecutionStep.Stack)).ToImmutableArray()) };
    }

    internal static GameSnapshot CompletedAnnouncement()
    {
        var state = Snapshot();
        var occurrence = state.Executions!.Occurrences[0] with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved };
        return state with { Stack = [], Executions = new(1, 1, [occurrence],
            [new(1, TriggerExecution, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
            Activity = [new(1, state.Turn, state.PhaseKey, "resolved", 0, "Mithril Coat's ability resolved.", Coat, "Mithril Coat") {
                ExecutionId = TriggerExecution, ExecutionStep = ExecutionStep.Complete, StackId = Trigger, Detail = TriggerText
            }] };
    }

    internal static GameSnapshot Snapshot(int pendingCount = 1)
    {
        if (pendingCount is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(pendingCount));
        Card Face(string id, string name, string type, string text = "", int? power = null, int? toughness = null) => new() {
            Key = id, VisualId = id, CombatId = id, Name = name, ArtName = name, Type = type, Text = text,
            Power = power, Toughness = toughness
        };
        var coat = Face(Coat, "Mithril Coat", "Legendary Artifact — Equipment",
            "Flash\nIndestructible\n" + TriggerText + "\nEquipped creature has indestructible.\nEquip {3}") with { ManaCost = "{3}" };
        var geist = Face(Geist, "Geist of Saint Thalia", "Legendary Creature — Spirit Cleric",
            "Flying\nNoncreature spells you cast cost {1} less to cast.", 1, 2) with { ManaCost = "{1}{U}" };
        var attackers = new[] { Face("stack-hud-attacker-a", "Grizzly Bears", "Creature — Bear", power: 2, toughness: 2) with { Tapped = true },
            Face("stack-hud-attacker-b", "Llanowar Elves", "Creature — Elf Druid", "{T}: Add {G}.", 1, 1) with { Tapped = true } };
        var consider = Face(OlderSpell, "Consider", "Instant", "Surveil 1.\nDraw a card.") with { Key = "", ManaCost = "{U}" };
        var players = Enumerable.Range(0, 4).Select(seat => {
            var lands = Enumerable.Range(0, seat == 0 ? 8 : 6 - seat).Select(index =>
                Face($"stack-hud-land-{seat}-{index}", index % 2 == 0 ? "Island" : "Forest", index % 2 == 0 ? "Basic Land — Island" : "Basic Land — Forest")
                    with { Tapped = index < 3 }).ToImmutableArray();
            var battlefield = lands.AddRange(seat == 0 ? [geist, coat] : seat == 1 ? attackers : []);
            return new Player(seat, new[] { "You", "Rowan", "Mira", "Jebb" }[seat], 40, seat == 0, false,
                ImmutableDictionary<string, int>.Empty, [new("Battlefield", battlefield.Length, battlefield, null),
                    new("Hand", seat == 0 ? 0 : 7, [], null), new("Library", 70, [], null), new("Command", 0, [], null),
                    new("Graveyard", 0, [], null), new("Exile", 0, [], null)]);
        }).ToImmutableArray();
        var trigger = new StackItem(coat.Name, TriggerText, StackActionKind.TriggeredAbility, coat) {
            Id = Trigger, ExecutionId = TriggerExecution, ControllerId = 0,
            Targets = [new("card", Geist, geist.Name, 0)]
        };
        ImmutableArray<StackItem> stack = pendingCount == 1 ? [trigger] : [trigger,
            new(consider.Name, consider.Text, StackActionKind.Spell, consider) { Id = OlderSpell, ExecutionId = OlderExecution, ControllerId = 1 }];
        var occurrences = stack.Select(item => new ExecutionOccurrence(item.ExecutionId, item.ExecutionId,
            item.Ability ? ExecutionKind.Trigger : ExecutionKind.Spell, ExecutionStep.Stack) {
                ActorPlayerId = item.ControllerId, SourceCardId = item.Card!.VisualId, SourceName = item.Name
            }).ToImmutableArray();
        return new("stack-combat-hud", "playing", null, null, 0, 12, "Declare blockers", "COMBAT_DECLARE_BLOCKERS", 1,
            players, stack, new(attackers.Select(card => new Attack(card.CombatId, 0, [], []) {
                Defender = new("player", "0", 0, "You") }).ToImmutableArray(), [], null),
            new("stack-hud-priority-" + pendingCount, new(), new PriorityPayload(new("Pass once", true), CanAutoPass: false))) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "priority",
                StackInterruptionVersion = 1, StackActionVersion = 1, ResolutionChoiceVersion = 1,
                Executions = new(1, 0, occurrences, []),
                Tempo = new(new(TempoPreset.Fast, TempoExpiry.Pass, 30, 60, "stack-hud-tempo"),
                    [new(0, "stack-hud-response-clock", TempoPhase.Thinking, 12_000, 30_000, 0)])
            };
    }
}
