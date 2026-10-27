using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

// Validation grants no action authority. Semantic references must describe only
// the current entitled projection before a policy may inspect or traverse them.
internal static class AiSemanticValidation
{
    internal static void Validate(GameSnapshot state, DecisionActionDomain domain, DecisionSemantics semantics)
    {
        var context = new Context(state);
        if (semantics.Numeric is { } numeric) context.Numeric(state, numeric);
        context.Graph(semantics.OperationGraph, semantics.OperationId);
        foreach (var option in semantics.Options) context.Graph(option.OperationGraph);
        foreach (var action in domain.Actions) {
            if (!Enum.IsDefined(action.ManaCostKnowledge)) throw Invalid();
            if (action.Costs.Any(cost => cost.CounterType == null || cost.ManaSymbols.Any(string.IsNullOrWhiteSpace))) throw Invalid();
            context.Graph(action.OperationGraph);
            Production(action.Production);
        }
    }

    private sealed class Context
    {
        private readonly HashSet<int> players;
        private readonly HashSet<string> occurrences;
        private readonly HashSet<string> stackOccurrences;
        private readonly Dictionary<string, int> controllers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> spellControllers = new(StringComparer.Ordinal);
        internal Context(GameSnapshot state)
        {
            players = state.Players.Select(player => player.Id).ToHashSet();
            var cards = CardViews.Visible(state)
                .Concat(state.ActionSources.Select(source => source.Card))
                .Concat(state.Stack.Select(item => item.Card).OfType<Card>()).ToArray();
            occurrences = cards.SelectMany(card => new[] { card.VisualId, card.CombatId }).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            stackOccurrences = state.Stack.Where(item => item.Card != null).SelectMany(item => new[] { item.Card!.VisualId, item.Card!.CombatId })
                .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            foreach (var occurrence in CardViews.InZones(state)) {
                foreach (string id in new[] { occurrence.Card.VisualId, occurrence.Card.CombatId }.Where(id => !string.IsNullOrEmpty(id)))
                    controllers[id] = occurrence.PlayerId;
            }
            foreach (var occurrence in state.ActionSources)
                foreach (string id in new[] { occurrence.Card.VisualId, occurrence.Card.CombatId }.Where(id => !string.IsNullOrEmpty(id)))
                    controllers[id] = occurrence.PlayerId;
            foreach (var item in state.Stack.Where(item => item.Card != null && item.ControllerId != null))
                foreach (string id in new[] { item.Card!.VisualId, item.Card!.CombatId }.Where(id => !string.IsNullOrEmpty(id)))
                    spellControllers[id] = item.ControllerId!.Value;
        }

        internal void Numeric(GameSnapshot state, NumericDecision numeric)
        {
            if (state.Decision?.Payload is not NumberPayload || !Enum.IsDefined(numeric.Purpose)
                || string.IsNullOrWhiteSpace(numeric.SourceCardId) || !occurrences.Contains(numeric.SourceCardId)
                || !Enum.IsDefined(numeric.ManaCostKnowledge)) throw Invalid();
            if (state.Decision.Context.Source is not { } owner || owner.CardId != numeric.SourceCardId
                && (owner.Card == null || CardViews.Identity(owner.Card) != numeric.SourceCardId)) throw Invalid();
            if (owner.Card is { } portrait && (portrait.FaceDown || !string.IsNullOrEmpty(portrait.Key)
                || portrait.Selectable || portrait.Highlighted)) throw Invalid();
            Costs(numeric.Costs);
            var funding = numeric.Funding;
            if (funding == null || !Enum.IsDefined(funding.Knowledge) || funding.Pool == null
                || funding.Pool.Count > 6 || funding.Pool.Any(entry => entry.Value < 0 || entry.Value > 1024
                    || entry.Key is not ("W" or "U" or "B" or "R" or "G" or "C"))
                || funding.Sources.IsDefault || funding.Sources.Length > 128
                || funding.Sources.Any(source => source == null)
                || funding.Sources.Select(source => source.SourceCardId).Distinct(StringComparer.Ordinal).Count() != funding.Sources.Length) throw Invalid();
            // Pool entitlement is quantitative: an observation cannot invent
            // floating mana, even though spend applicability remains nominal.
            if (funding.Pool.Any(entry => entry.Value > state.Viewer!.Mana.GetValueOrDefault(entry.Key))) throw Invalid();
            var ownBattlefield = CardViews.InZones(state).Where(occurrence => occurrence.PlayerId == state.ViewerId
                && occurrence.Zone == "Battlefield").Select(occurrence => CardViews.Identity(occurrence.Card)).ToHashSet(StringComparer.Ordinal);
            foreach (var source in funding.Sources) {
                if (string.IsNullOrWhiteSpace(source.SourceCardId) || !ownBattlefield.Contains(source.SourceCardId)
                    || source.Routes.IsDefault || source.Routes.Length > 32) throw Invalid();
                foreach (var route in source.Routes) {
                    if (route == null || !Enum.IsDefined(route.Eligibility)) throw Invalid();
                    Costs(route.Costs); Production(route.Production);
                }
            }
        }

        internal void Graph(ActionOperationGraph? graph, int? active = null)
        {
            if (graph == null || !Enum.IsDefined(graph.Knowledge) || graph.Operations.IsDefault || graph.Operations.Length > 128
                || graph.RootId < 0 || graph.Operations.Any(node => node == null || node.Id < 0)
                || graph.Operations.Select(node => node.Id).Distinct().Count() != graph.Operations.Length) throw Invalid();
            if (graph.Operations.IsEmpty) {
                if (graph.Knowledge == SemanticKnowledge.Known || graph.RootId != 0 || active != null) throw Invalid();
                return;
            }
            var nodes = graph.Operations.ToDictionary(node => node.Id);
            if (!nodes.ContainsKey(graph.RootId) || active is { } current && !nodes.ContainsKey(current)) throw Invalid();
            int edges = 0;
            foreach (var node in graph.Operations) {
                if (!Enum.IsDefined(node.Effect) || !Enum.IsDefined(node.Knowledge) || !Enum.IsDefined(node.AmountKnowledge)
                    || node.AmountKnowledge == SemanticKnowledge.Known && node.Amount == null || node.CounterType == null
                    || !Zone(node.OriginZone) || !Zone(node.DestinationZone)
                    || node.ActorPlayerId is { } actor && !players.Contains(actor)
                    || node.Conditional && node.Knowledge == SemanticKnowledge.Known
                    || node.MinChoices < 0 || node.MaxChoices < 0 || node.MinChoices > node.MaxChoices
                    || node.Costs.IsDefault || node.Costs.Any(cost => cost == null || !Enum.IsDefined(cost.Kind)
                        || cost.Amount < 0 || cost.CounterType == null || cost.ManaSymbols.IsDefault || cost.ManaSymbols.Any(string.IsNullOrWhiteSpace))
                    || node.Links.IsDefault || node.Links.Any(link => link == null || !Enum.IsDefined(link.Kind) || !nodes.ContainsKey(link.OperationId))) throw Invalid();
                edges += node.Links.Length;
                Binding(node.Sources, nodes, node); Binding(node.Recipients, nodes, node);
                if (node.Targets is { } targets) {
                    if (!Enum.IsDefined(targets.Knowledge) || targets.Min < 0 || targets.Max < 0 || targets.Min > targets.Max
                        || targets.Knowledge == SemanticKnowledge.Known && (targets.Min == null || targets.Max == null)
                        || targets.Candidates.IsDefault || targets.Candidates.Select(entity => entity?.Id).Distinct(StringComparer.Ordinal).Count() != targets.Candidates.Length) throw Invalid();
                    foreach (var entity in targets.Candidates) Entity(entity);
                }
                if (graph.Knowledge == SemanticKnowledge.Known && (node.Knowledge != SemanticKnowledge.Known
                    || node.Sources.Knowledge != SemanticKnowledge.Known || node.Recipients.Knowledge != SemanticKnowledge.Known
                    || node.Targets is { Knowledge: not SemanticKnowledge.Known })) throw Invalid();
            }
            if (edges > 512) throw Invalid();
            // No recursive expansion of loops. Repeat is a typed edge to a
            // finite body; a cyclic graph needs a different explicit model.
            var colors = new Dictionary<int, int>();
            bool Visit(int id, int depth) {
                if (depth > 128) return false;
                if (colors.TryGetValue(id, out int prior)) return prior == 2;
                colors[id] = 1;
                foreach (var link in nodes[id].Links)
                    if (!Visit(link.OperationId, depth + 1)) return false;
                colors[id] = 2; return true;
            }
            if (!Visit(graph.RootId, 1) || colors.Count != nodes.Count) throw Invalid();
        }

        private void Binding(EffectRecipients? binding, Dictionary<int, ActionOperation> nodes, ActionOperation operation)
        {
            if (binding == null || !Enum.IsDefined(binding.Knowledge) || !Enum.IsDefined(binding.Binding) || binding.Entities.IsDefault
                || binding.Entities.Select(entity => entity?.Id).Distinct(StringComparer.Ordinal).Count() != binding.Entities.Length
                || binding.Knowledge == SemanticKnowledge.Known && binding.Binding == EffectRecipientBinding.Unknown) throw Invalid();
            bool targets = binding.Binding is EffectRecipientBinding.Targets or EffectRecipientBinding.TargetControllers or EffectRecipientBinding.TargetOwners;
            if (binding.TargetOperationId is { } target) {
                if (!targets || !nodes.TryGetValue(target, out var node) || node.Targets == null) throw Invalid();
            } else if (targets && binding.Knowledge == SemanticKnowledge.Known) throw Invalid();
            foreach (var entity in binding.Entities) Entity(entity);
            if (binding.Knowledge == SemanticKnowledge.Known && binding.Binding == EffectRecipientBinding.Actor
                && (operation.ActorPlayerId == null || binding.Entities.Length != 1 || binding.Entities[0].Kind != SemanticEntityKind.Player
                    || binding.Entities[0].PlayerId != operation.ActorPlayerId)) throw Invalid();
        }

        private void Entity(SemanticEntity? entity)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.Id) || entity.Id.Length > 256 || !Enum.IsDefined(entity.Kind)
                || entity.CardId == null || entity.PlayerId is { } player && !players.Contains(player)
                || entity.ControllerId is { } controller && !players.Contains(controller)
                || entity.OwnerId is { } owner && !players.Contains(owner)) throw Invalid();
            if (entity.Kind == SemanticEntityKind.Player) {
                if (entity.PlayerId == null || entity.CardId.Length != 0 || entity.OwnerId != null
                    || entity.ControllerId != null && entity.ControllerId != entity.PlayerId) throw Invalid();
            } else if (entity.PlayerId != null || entity.CardId.Length == 0 || !occurrences.Contains(entity.CardId)
                || entity.Kind == SemanticEntityKind.Spell && !stackOccurrences.Contains(entity.CardId)) throw Invalid();
            else if (entity.ControllerId is { } actual && (entity.Kind == SemanticEntityKind.Spell
                ? spellControllers.TryGetValue(entity.CardId, out int expectedSpell) && actual != expectedSpell
                : controllers.TryGetValue(entity.CardId, out int expectedCard) && actual != expectedCard)) throw Invalid();
        }
    }

    private static void Costs(ImmutableArray<ActionCost> costs)
    {
        if (costs.IsDefault || costs.Length > 128 || costs.Any(cost => cost == null || !Enum.IsDefined(cost.Kind)
            || cost.Amount < 0 || cost.CounterType == null || cost.ManaSymbols.IsDefault || cost.ManaSymbols.Length > 128
            || cost.ManaSymbols.Any(string.IsNullOrWhiteSpace))) throw Invalid();
    }

    private static void Production(ManaProduction? production)
    {
        if (production == null || !Enum.IsDefined(production.Knowledge) || production.Alternatives.IsDefault
            || production.Alternatives.Length > 32 || production.Knowledge == SemanticKnowledge.Known && production.Alternatives.IsEmpty) throw Invalid();
        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alternative in production.Alternatives) {
            if (alternative?.Colors == null || alternative.Colors.Count > 6 || alternative.Colors.Any(entry => entry.Value < 0
                || entry.Key is not ("W" or "U" or "B" or "R" or "G" or "C"))
                || alternative.Colors.Values.Sum(value => (long)value) > int.MaxValue) throw Invalid();
            string canonical = string.Join(";", alternative.Colors.Where(entry => entry.Value != 0).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Key + ":" + entry.Value));
            if (!alternatives.Add(canonical)) throw Invalid();
        }
    }

    private static ArgumentException Invalid() => new("Operation semantics must be finite, typed and scoped to currently entitled occurrences.");
    private static bool Zone(string? zone) => zone is "" or "Battlefield" or "Hand" or "Library" or "Graveyard" or "Exile" or "Command" or "Stack" or "Sideboard";
}
