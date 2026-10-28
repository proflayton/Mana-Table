using System.Collections.Immutable;

namespace Mana.Contracts;

// Knowledge describes mechanical projection, never predicted resolution or value.
public enum SemanticKnowledge { Unknown, Known, Unsupported }
public enum SemanticEntityKind { Player, Card, Spell }
public enum EffectRecipientBinding { Unknown, Actor, Source, Explicit, Targets, TargetControllers, TargetOwners }
public enum OperationLinkKind { Sequence, Choice, IfTrue, IfFalse, Repeat, Additional }

// Id belongs to this semantic graph. CardId refers only to an entitled source
// occurrence; a Spell may omit it when its permanent source has departed.
// Neither identity can be used in place of a decision's action handles.
public sealed record SemanticEntity(string Id, SemanticEntityKind Kind, string CardId = "",
    int? PlayerId = null, int? ControllerId = null, int? OwnerId = null)
{
    // An actual stack occurrence, independent of the source card. Copies and
    // abilities from the same permanent must remain distinct pending actions.
    public string StackItemId { get; init; } = "";
}
public sealed record EffectRecipients(SemanticKnowledge Knowledge, EffectRecipientBinding Binding,
    int? TargetOperationId, ImmutableArray<SemanticEntity> Entities)
{
    public static EffectRecipients Unknown { get; } = new(SemanticKnowledge.Unknown, EffectRecipientBinding.Unknown, null, []);
}
public sealed record EffectTargetDomain(SemanticKnowledge Knowledge, int? Min, int? Max,
    ImmutableArray<SemanticEntity> Candidates);
public sealed record OperationLink(int OperationId, OperationLinkKind Kind);
public sealed record ActionOperation(int Id, ActionEffectKind Effect)
{
    public SemanticKnowledge Knowledge { get; init; }
    public int? ActorPlayerId { get; init; }
    public int? Amount { get; init; }
    public SemanticKnowledge AmountKnowledge { get; init; }
    public string CounterType { get; init; } = "";
    // Canonical single zones only. Empty means absent, mixed or unresolved.
    public string OriginZone { get; init; } = "";
    public string DestinationZone { get; init; } = "";
    // Conditional means the rules may skip this operation; it is not evidence
    // that an unmet future/hidden-state condition has already been evaluated.
    public bool Conditional { get; init; }
    public int? MinChoices { get; init; }
    public int? MaxChoices { get; init; }
    public bool MayRepeatChoices { get; init; }
    public EffectRecipients Sources { get; init; } = EffectRecipients.Unknown;
    public EffectRecipients Recipients { get; init; } = EffectRecipients.Unknown;
    public EffectTargetDomain? Targets { get; init; }
    public ImmutableArray<ActionCost> Costs { get; init; } = [];
    public ImmutableArray<OperationLink> Links { get; init; } = [];
}
public sealed record ActionOperationGraph(SemanticKnowledge Knowledge, int RootId,
    ImmutableArray<ActionOperation> Operations)
{
    public static ActionOperationGraph Unknown { get; } = new(SemanticKnowledge.Unknown, 0, []);
}

// Alternatives preserve simultaneous bundles versus a choice of colors. Cost
// coupling, spending restrictions and replacements remain separate rules facts.
public sealed record ManaProductionAlternative(ImmutableDictionary<string, int> Colors);
public sealed record ManaProduction(SemanticKnowledge Knowledge, ImmutableArray<ManaProductionAlternative> Alternatives)
{
    public static ManaProduction Unknown { get; } = new(SemanticKnowledge.Unknown, []);
}
