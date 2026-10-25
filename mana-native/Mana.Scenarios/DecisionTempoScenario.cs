using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Authored clock observations, not a second timeout/rules engine.
public static class DecisionTempoScenario
{
    public const string Id = "decision-tempo.v1";
    public static IReadOnlyList<ExperienceStage> Create()
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        var settings = new TempoSettings(TempoPreset.Standard, TempoExpiry.Pass, 60, 60, "lab-tempo-policy");
        GameSnapshot State(int owner, TempoPhase phase, long remaining) => baseline with {
            Id = "decision-tempo-lab", DecisionOwnerId = owner, PriorityPlayerId = phase == TempoPhase.Grace ? null : owner,
            ActionClass = phase == TempoPhase.Grace ? "selection" : "priority",
            Decision = owner != baseline.ViewerId ? null : phase != TempoPhase.Grace ? baseline.Decision
                : new("lab-required-target", new(Message: "Choose a creature"),
                    new TargetSelectionPayload(new("Confirm", false), new("Cancel", false), new(1, 1), [])),
            Tempo = new(settings, [new(owner, "lab-clock-" + owner + "-" + phase, phase, remaining, 60_000, 0)])
        };
        return [new("Your decision", "The local portrait shows a quiet clock while you decide.", State(baseline.ViewerId, TempoPhase.Thinking, 55_000)),
            new("Running low", "The same portrait gains a visible countdown near expiry.", State(baseline.ViewerId, TempoPhase.Thinking, 9_000)),
            new("Off-turn response", "The response clock follows the deciding seat, not the active turn.", State(2, TempoPhase.Thinking, 12_000)),
            new("Required-choice grace", "Required choices remain unanswered during their extra grace time.", State(baseline.ViewerId, TempoPhase.Grace, 45_000)),
            new("Warning expired", "An authored warning-only expiry remains visible without a client rules action.", State(1, TempoPhase.Expired, 0) with {
                Tempo = new(settings with { Expiry = TempoExpiry.Warn }, [new(1, "lab-warning", TempoPhase.Expired, 0, 60_000, 0)])
            }),
            new("Seat timed out", "A confirmed timeout loss remains visible at the eliminated seat.", baseline with {
                Id = "decision-tempo-lab", Players = baseline.Players.Select(player => player.Id == 1 ? player with { Eliminated = true } : player).ToImmutableArray(),
                Tempo = new(settings, []) { TimedOutPlayers = [1] }
            }),
            new("Engine resolving", "No decision clock runs while the engine processes a choice.", baseline with {
                Id = "decision-tempo-lab", Tempo = new(settings, []), Decision = null, PriorityPlayerId = null, DecisionOwnerId = null, ActionClass = "resolving"
            })];
    }
}
