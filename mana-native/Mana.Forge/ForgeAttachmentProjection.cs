using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Forge;

// Attach remains Unknown on the legacy wire. Only this version-aware native
// boundary recognizes the additive descriptor; old enum readers remain usable.
internal static class ForgeAttachmentProjection
{
    internal static ActionOperationGraph Normalize(ActionOperationGraph graph, GameSnapshot? state = null, int? active = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Operations.IsDefaultOrEmpty) return graph;
        bool promoted = false;
        var nodes = graph.Operations.Select(node => {
            if (node?.Attachment is not { } attachment) return node!;
            if (!attachment.IsValid || node.Effect is not (ActionEffectKind.Unknown or ActionEffectKind.Attach))
                throw new ArgumentException("Invalid attachment modifier projection.");
            var sources = node.Sources ?? throw new ArgumentException("Attachment source binding is missing.");
            if (sources.Binding == EffectRecipientBinding.DecisionSource)
                throw new ArgumentException("Decision-source binding is internal to the owned observation boundary.");
            if (!string.IsNullOrEmpty(node.AttachmentSourceCardId)) {
                if (state == null || active != node.Id
                    || !AttachmentModifier.IsCurrentDecisionSource(state, node.AttachmentSourceCardId, node.ActorPlayerId)
                    || sources == null || sources.Entities.IsDefault || !sources.Entities.IsEmpty
                    || sources.Knowledge != SemanticKnowledge.Unknown || sources.Binding != EffectRecipientBinding.Unknown)
                    throw new ArgumentException("Attachment decision-source provenance is not current.");
                sources = new(SemanticKnowledge.Known, EffectRecipientBinding.DecisionSource, null, []);
            }
            bool known = attachment.Knowledge == SemanticKnowledge.Known && !node.Conditional
                && (sources is { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Source }
                    && !sources.Entities.IsDefault && sources.Entities.Length == 1 && sources.Entities[0] is { Kind: SemanticEntityKind.Card }
                    || sources is { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.DecisionSource })
                && node.Recipients is { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Targets }
                && node.Recipients.TargetOperationId == node.Id
                && node.Targets is { Knowledge: SemanticKnowledge.Known, Min: 1, Max: 1 }
                && !node.Targets.Candidates.IsDefault && node.Targets.Candidates.All(entity => entity is { Kind: SemanticEntityKind.Card });
            var knowledge = node.Knowledge;
            if (knowledge == SemanticKnowledge.Unknown && known) { knowledge = SemanticKnowledge.Known; promoted = true; }
            return node with { Effect = ActionEffectKind.Attach, Knowledge = knowledge, Sources = sources };
        }).ToImmutableArray();
        bool complete = promoted && graph.Knowledge == SemanticKnowledge.Unknown
            && nodes.All(node => node != null && node.Knowledge == SemanticKnowledge.Known
                && node.Sources is { Knowledge: SemanticKnowledge.Known }
                && node.Recipients is { Knowledge: SemanticKnowledge.Known }
                && node.Targets is not { Knowledge: not SemanticKnowledge.Known }
                && !node.Links.IsDefault && node.Links.All(link => link != null
                    && link.Kind is not (OperationLinkKind.Additional or OperationLinkKind.Scheduled)));
        return graph with { Operations = nodes, Knowledge = complete ? SemanticKnowledge.Known : graph.Knowledge };
    }

    internal static DecisionActionDomain Normalize(DecisionActionDomain domain)
        => domain with { Actions = domain.Actions.Select(action => {
            var graph = Normalize(action.OperationGraph);
            var root = graph.Operations.IsDefaultOrEmpty ? null : graph.Operations.FirstOrDefault(node => node.Id == graph.RootId);
            bool attachment = root?.Effect == ActionEffectKind.Attach;
            var effects = action.Effects;
            if (attachment && !effects.IsDefault) effects = graph.Knowledge == SemanticKnowledge.Known
                ? effects.Select(effect => effect == ActionEffectKind.Unknown ? ActionEffectKind.Attach : effect).Distinct().ToImmutableArray()
                : effects.Add(ActionEffectKind.Attach).Distinct().ToImmutableArray();
            return action with { OperationGraph = graph, Effect = attachment ? ActionEffectKind.Attach : action.Effect, Effects = effects };
        }).ToImmutableArray() };

    internal static DecisionSemantics Normalize(DecisionSemantics semantics, GameSnapshot state)
        => semantics with { OperationGraph = Normalize(semantics.OperationGraph, state, semantics.OperationId), Options = semantics.Options.Select(option
            => option with { OperationGraph = Normalize(option.OperationGraph) }).ToImmutableArray() };
}
