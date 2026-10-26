using System.Collections.Immutable;

namespace Mana.Contracts;

// These describe the rules operation which owns a decision. They are not
// recommendations, inferred text classifiers or another source of permission.
public enum DecisionIntent
{
    Unknown, StartingPlayer, Benefit, Harm, Search, Discard, Sacrifice,
    AddCounters, RemoveCounters, Damage, Destroy, Exile, Bounce, Counter,
    Tap, Untap, CombatDamage, NameCard
}
public sealed record DecisionOptionSemantics(int Index, ActionEffectKind Effect, int? Amount = null)
{
    public ActionOperationGraph OperationGraph { get; init; } = ActionOperationGraph.Unknown;
}
public sealed record DecisionSemantics(DecisionIntent Intent = DecisionIntent.Unknown, int? Amount = null)
{
    // One underlying rules decision can replace its input object while keeping
    // the same causal operation (for example repairing a combat declaration).
    public string EpisodeId { get; init; } = "";
    public ImmutableArray<DecisionOptionSemantics> Options { get; init; } = [];
    // Finite, currently legal text choices. An unrestricted textbox without a
    // provider-supplied domain remains unsupported rather than inventing names.
    public ImmutableArray<string> LegalTextValues { get; init; } = [];
    // Remaining exact rules symbols for the current payment, including generic
    // and alternative shards. These are not the source card's printed cost.
    public ImmutableArray<string> UnpaidMana { get; init; } = [];
    // Player candidates can include a selected player to permit human undo.
    // A policy needs the actual staged selection to avoid toggling it forever.
    public ImmutableArray<int> SelectedPlayers { get; init; } = [];
    public string CounterType { get; init; } = "";
    public bool SelectionIsCost { get; init; }
    public ActionOperationGraph OperationGraph { get; init; } = ActionOperationGraph.Unknown;
    public int? OperationId { get; init; }
}
