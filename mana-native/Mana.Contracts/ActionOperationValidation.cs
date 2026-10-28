namespace Mana.Contracts;

// One structural and visibility boundary for prospective and committed graphs.
// This grants no action authority and evaluates no rules or strategic utility.
public sealed class ActionOperationValidation
{
    private readonly HashSet<int> players;
    private readonly HashSet<string> occurrences;
    private readonly Dictionary<string, int> controllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StackItem> stackItems;
    private readonly Dictionary<string, StackItem[]> stackByCard;

    public ActionOperationValidation(GameSnapshot state)
    {
        players = state.Players.Select(player => player.Id).ToHashSet();
        var cards = CardViews.InZones(state).Select(occurrence => occurrence.Card).Concat(state.ActionSources.Select(source => source.Card))
            .Concat(state.Stack.Where(item => item.Kind == StackActionKind.Spell).Select(item => item.Card).OfType<Card>());
        occurrences = cards.SelectMany(Identities).ToHashSet(StringComparer.Ordinal);
        foreach (var occurrence in CardViews.InZones(state))
            foreach (string id in Identities(occurrence.Card)) controllers[id] = occurrence.PlayerId;
        foreach (var occurrence in state.ActionSources)
            foreach (string id in Identities(occurrence.Card)) controllers[id] = occurrence.PlayerId;
        foreach (var item in state.Stack.Where(item => item.Kind == StackActionKind.Spell && item.Card != null && item.ControllerId != null))
            foreach (string id in Identities(item.Card!)) controllers.TryAdd(id, item.ControllerId!.Value);
        // Ambiguous provider IDs cannot identify an occurrence. Do not silently
        // pick the last item when copies/abilities share a source card.
        stackItems = state.Stack.Where(item => !string.IsNullOrEmpty(item.Id)).GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        stackByCard = state.Stack.Where(item => item.Card != null)
            .SelectMany(item => Identities(item.Card!).Distinct(StringComparer.Ordinal).Select(id => (id, item)))
            .GroupBy(pair => pair.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.item).ToArray(), StringComparer.Ordinal);
    }

    public void ValidateCommitted(StackItem item)
    {
        Validate(item.OperationGraph);
        var graph = item.OperationGraph;
        if ((item.Card == null || item.Card.FaceDown) && !graph.Operations.IsEmpty) throw Invalid();
        foreach (var node in graph.Operations) {
            if (!node.Costs.IsEmpty || node.Links.Any(link => link.Kind == OperationLinkKind.Choice)) throw Invalid();
            if (node.Targets is { Knowledge: SemanticKnowledge.Known } targets
                && (targets.Min != targets.Candidates.Length || targets.Max != targets.Candidates.Length)) throw Invalid();
            if (node.Id == graph.RootId && node.ActorPlayerId is { } actor && item.ControllerId is { } controller && actor != controller)
                throw Invalid();
        }
    }

    public void ValidateEntity(SemanticEntity entity) => Entity(entity);

    public void Validate(ActionOperationGraph? graph, int? active = null)
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
            || entity.CardId == null || entity.StackItemId == null || entity.StackItemId.Length > 256
            || entity.PlayerId is { } player && !players.Contains(player)
            || entity.ControllerId is { } controller && !players.Contains(controller)
            || entity.OwnerId is { } owner && !players.Contains(owner)) throw Invalid();
        if (entity.Kind == SemanticEntityKind.Player) {
            if (entity.PlayerId == null || entity.CardId.Length != 0 || entity.StackItemId.Length != 0 || entity.OwnerId != null
                || entity.ControllerId != null && entity.ControllerId != entity.PlayerId) throw Invalid();
            return;
        }
        if (entity.PlayerId != null) throw Invalid();
        if (entity.Kind == SemanticEntityKind.Spell) {
            StackItem? target = null;
            if (entity.StackItemId.Length > 0) {
                if (!stackItems.TryGetValue(entity.StackItemId, out target)
                    || entity.CardId.Length > 0 && (target.Card == null || !Identities(target.Card).Contains(entity.CardId, StringComparer.Ordinal))) throw Invalid();
            } else {
                // Compatibility for older projections; a shared source is not
                // enough to choose among multiple pending abilities or copies.
                if (!stackByCard.TryGetValue(entity.CardId, out var matches) || matches.Length != 1) throw Invalid();
                target = matches[0];
            }
            if (entity.ControllerId is { } actual && target.ControllerId is { } expected && actual != expected) throw Invalid();
        } else if (entity.CardId.Length == 0 || !occurrences.Contains(entity.CardId) || entity.StackItemId.Length != 0
            || entity.ControllerId is { } actual && controllers.TryGetValue(entity.CardId, out int expected) && actual != expected) throw Invalid();
    }

    private static IEnumerable<string> Identities(Card card) => new[] { card.VisualId, card.CombatId }.Where(id => !string.IsNullOrEmpty(id));
    private static ArgumentException Invalid() => new("Operation semantics must be finite, typed and scoped to currently entitled occurrences.");
    private static bool Zone(string? zone) => zone is "" or "Battlefield" or "Hand" or "Library" or "Graveyard" or "Exile" or "Command" or "Stack" or "Sideboard";
}
