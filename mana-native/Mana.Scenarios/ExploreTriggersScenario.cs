using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Authored public presentation checkpoints based on the reported Explore burst.
// No private game capture, trigger creation or rules-resolution claim.
public static class ExploreTriggersScenario
{
    public const string Id = "explore-triggers.v1";
    public const string Source = "explore-nicanzil";
    public const string LandEffect = "Whenever a creature you control explores a land card, you may put a land card from your hand onto the battlefield tapped. [Explorer: Merfolk Skydiver (216), Explored: Mosswort Bridge (300)]";
    public const string CounterEffect = "Whenever a creature you control explores a nonland card, put a +1/+1 counter on Nicanzil, Current Conductor. [Explorer: Hakbal of the Surging Soul (302), Explored: Bygone Marvels (303)]";

    public static IReadOnlyList<ExperienceStage> Create() => [
        new("Before Explore", "Authored four-player public table; sources retain their physical occurrences.", Snapshot(0)),
        new("Mixed Explore triggers", "Read the actual next effect once. Source counts retain interleaved land and nonland triggers as distinct work.", Snapshot(1)),
        new("Next effect changed", "Removing one authored occurrence reveals the other Nicanzil effect without merging it by source name.", Snapshot(2)),
        new("A new response", "A distinct opposing spell becomes the actual top of the stack. Reading remains noncommitting.", Snapshot(3)),
        new("Required choice", "The current engine-style required choice keeps its answer controls while the remaining stack stays inspectable.", Snapshot(4))
    ];

    public static GameSnapshot Snapshot(int stage)
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        Card Face(string id, string name, string text, int power, int toughness) => new() {
            VisualId = id, CombatId = id, Name = name, ArtName = name, Type = "Legendary Creature — Merfolk Scout",
            Text = text, Power = power, Toughness = toughness
        };
        var nicanzil = Face(Source, "Nicanzil, Current Conductor", LandEffect + "\n" + CounterEffect, 5, 6);
        var hakbal = Face("explore-hakbal", "Hakbal of the Surging Soul",
            "At the beginning of combat on your turn, each Merfolk you control explores.", 4, 4);
        var skydiver = Face("explore-skydiver", "Merfolk Skydiver", "Flying", 2, 2);
        var players = baseline.Players.Select(player => player.Id != 2 ? player : player with {
            Name = "Explorers of the Deep · AI 2",
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = [.. zone.Cards, nicanzil, hakbal, skydiver], Count = zone.Count + 3
            }).ToImmutableArray()
        }).ToImmutableArray();
        StackItem Trigger(string id, Card source, string text) => new(source.Name, text, StackActionKind.TriggeredAbility, source) {
            Id = id, ExecutionId = "execution:" + id, ControllerId = 2
        };
        ImmutableArray<StackItem> stack = [Trigger("explore-land-0", nicanzil, LandEffect),
            Trigger("explore-counter-1", nicanzil, CounterEffect), Trigger("explore-land-2", nicanzil, LandEffect),
            Trigger("explore-hakbal-3", hakbal, hakbal.Text)];
        var announced = stack;
        if (stage == 0) stack = [];
        else if (stage >= 2) stack = stack.RemoveAt(0);
        if (stage >= 3) {
            var response = new Card { VisualId = "explore-stifle", Name = "Stifle", ArtName = "Stifle",
                Type = "Instant", ManaCost = "{U}", Text = "Counter target activated or triggered ability." };
            stack = stack.Insert(0, new(response.Name, response.Text, StackActionKind.Spell, response) {
                Id = "explore-response", ExecutionId = "execution:explore-response", ControllerId = 1,
                Targets = [new("stack", "explore-counter-1", nicanzil.Name, 2)]
            });
        }
        var occurrences = announced.Select(item => new ExecutionOccurrence(item.ExecutionId, item.ExecutionId,
            item.Ability ? ExecutionKind.Trigger : ExecutionKind.Spell, ExecutionStep.Stack) {
                ActorPlayerId = item.ControllerId, SourceCardId = item.Card!.VisualId, SourceName = item.Name
            }).ToImmutableArray();
        var transitions = occurrences.Select((item, index) => new ExecutionTransition(index + 1, item.Id, ExecutionStep.Stack)).ToImmutableArray();
        if (stage >= 2) {
            occurrences = occurrences.SetItem(0, occurrences[0] with { Step = ExecutionStep.Complete, Outcome = ExecutionOutcome.Resolved });
            transitions = transitions.Add(new(5, announced[0].ExecutionId, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved });
        }
        if (stage >= 3) {
            var response = stack[0];
            occurrences = occurrences.Add(new(response.ExecutionId, response.ExecutionId, ExecutionKind.Spell, ExecutionStep.Stack) {
                ActorPlayerId = 1, SourceCardId = response.Card!.VisualId, SourceName = response.Name
            });
            transitions = transitions.Add(new(6, response.ExecutionId, ExecutionStep.Stack));
        }
        if (stage == 0) { occurrences = []; transitions = []; }
        Decision decision = stage == 4
            ? new("explore-required", new("A required choice interrupts the pending triggers.", Source: new(Source, nicanzil, 2, "Battlefield")),
                new ConfirmationPayload(new("Continue", true)))
            : new("explore-priority-" + stage, new(), new PriorityPayload(new("Pass once", true), CanAutoPass: false));
        return baseline with { Id = "authored-explore-triggers", Players = players, Stack = stack, Decision = decision,
            Turn = 23, Phase = "Beginning of combat", PhaseKey = "COMBAT_BEGIN", ActivePlayerId = 2,
            DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = stage == 4 ? "confirmation" : "priority",
            Revision = stage + 1, BoardRevision = stage + 1, Combat = null, Activity = [],
            StackInterruptionVersion = stage >= 3 ? 2 : 1,
            Executions = new(1, transitions.Length, occurrences, transitions) };
    }
}
