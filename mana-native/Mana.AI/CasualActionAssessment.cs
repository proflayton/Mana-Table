using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

public enum CasualAssessmentStatus { Estimated, Incomplete, Unsupported, BudgetExceeded, EstimatedPartial, SearchLimited }
public sealed record CasualActionEstimate(CasualAssessmentStatus Status, double Value, string Reason);

/// <summary>
/// Bounded public-effect estimates. Mechanical graph knowledge is a prerequisite,
/// not a claim that these heuristic values predict replacements or responses.
/// Targets and their controllers share one assignment throughout an operation.
/// </summary>
public static class CasualActionAssessment
{
    // A low-confidence exploration rule, not a token-body valuation. Missing
    // token characteristics do not make an actor-only resource creation harmful.
    // Every linked operation must have a recognizable favorable direction and
    // exact actor ownership; mixed, conditional and paid sub-effects are excluded.
    public static bool IsConservativeOwnBenefit(GameSnapshot state, ActionOperationGraph graph, AiWorkBudget work)
    {
        if (graph.Knowledge == SemanticKnowledge.Unsupported || graph.Operations.IsDefaultOrEmpty || graph.Operations.Length > 64) return false;
        bool benefit = false;
        foreach (var node in graph.Operations) {
            if (!work.TrySpend()) return false;
            if (node.Conditional || node.Targets != null || node.Id != graph.RootId && !node.Costs.IsEmpty
                || node.Links.Any(link => link.Kind != OperationLinkKind.Sequence)) return false;
            if (node.Effect == ActionEffectKind.Cleanup) continue;
            if (node.Recipients.Knowledge != SemanticKnowledge.Known
                || node.Recipients.Binding is not (EffectRecipientBinding.Actor or EffectRecipientBinding.Source or EffectRecipientBinding.Explicit)
                || node.Recipients.Entities.IsDefaultOrEmpty || node.Recipients.Entities.Any(entity =>
                    (entity.Kind == SemanticEntityKind.Player ? entity.PlayerId : entity.ControllerId) != state.ViewerId)) return false;
            if (node.Effect == ActionEffectKind.Untap) {
                benefit |= node.Recipients.Entities.Any(entity => CardViews.FindZoneOccurrence(state, entity.CardId)?.Card.Tapped == true);
                continue;
            }
            if (node.Effect is not (ActionEffectKind.Token or ActionEffectKind.Draw or ActionEffectKind.Scry or ActionEffectKind.LifeGain or ActionEffectKind.AddCounters)
                || node.AmountKnowledge != SemanticKnowledge.Known || node.Amount is null or < 0
                || node.Effect == ActionEffectKind.AddCounters && node.CounterType is not ("P1P1" or "+1/+1")) return false;
            if (node.Effect == ActionEffectKind.Draw && state.Viewer?.Zones.FirstOrDefault(zone => zone.Name == "Library") is { } library && node.Amount > library.Count)
                return false;
            benefit |= node.Amount > 0;
        }
        return benefit;
    }

    public static CasualActionEstimate Evaluate(GameSnapshot state, ActionOperationGraph graph, int workUnits, Card? source = null)
    {
        if (workUnits <= 0) throw new ArgumentOutOfRangeException(nameof(workUnits));
        return Evaluate(state, graph, new AiWorkBudget(workUnits), source, null, [], false);
    }

    public static CasualActionEstimate Evaluate(GameSnapshot state, ActionOperationGraph graph, AiWorkBudget work,
        Card? source = null) => Evaluate(state, graph, work.CreateSlice(256, 4), source, null, [], true);

    public static CasualActionEstimate Target(GameSnapshot state, ActionOperationGraph graph, int operationId,
        SemanticEntity candidate, AiWorkBudget work, Card? source = null)
    {
        // Marginal value excludes unrelated actor effects from the recipient
        // decision. The incomplete baseline is an evaluation device, never a
        // proposed target set or a relaxation of engine selection constraints.
        var allowance = work.CreateSlice(128, 4);
        var baseline = Evaluate(state, graph, allowance, source, operationId, [], true);
        if (baseline.Status == CasualAssessmentStatus.EstimatedPartial) return new(CasualAssessmentStatus.SearchLimited, 0, "target-comparison-limit");
        if (baseline.Status != CasualAssessmentStatus.Estimated) return baseline;
        var added = Evaluate(state, graph, allowance, source, operationId, [candidate], true);
        if (added.Status == CasualAssessmentStatus.EstimatedPartial) return new(CasualAssessmentStatus.SearchLimited, 0, "target-comparison-limit");
        return added.Status == CasualAssessmentStatus.Estimated ? added with { Value = added.Value - baseline.Value } : added;
    }

    private static CasualActionEstimate Evaluate(GameSnapshot state, ActionOperationGraph graph, AiWorkBudget work,
        Card? source, int? fixedOperation, ImmutableArray<SemanticEntity> fixedTargets, bool localOptimization, int depth = 0)
    {
        if (depth > 8) return Incomplete("pending-effect-depth");
        if (graph.Knowledge == SemanticKnowledge.Unsupported) return Unsupported("operation-knowledge");
        if (graph.Knowledge != SemanticKnowledge.Known || graph.Operations.IsDefaultOrEmpty)
            return Incomplete("operation-knowledge");
        if (graph.Operations.Length > 64 || graph.Operations.Select(node => node.Id).Distinct().Count() != graph.Operations.Length)
            return Unsupported("operation-shape");
        var nodes = graph.Operations.ToDictionary(node => node.Id);
        if (!nodes.ContainsKey(graph.RootId) || state.Viewer == null) return Unsupported("operation-root");
        if (graph.Operations.Any(node => node.Knowledge != SemanticKnowledge.Known || node.Conditional
                || node.Id != graph.RootId && !node.Costs.IsEmpty
                || node.Links.Any(link => link.Kind is OperationLinkKind.IfTrue or OperationLinkKind.IfFalse or OperationLinkKind.Repeat or OperationLinkKind.Additional)))
            return Incomplete("conditional-or-coupled-operation");
        var targets = graph.Operations.Where(node => node.Targets != null).OrderBy(node => node.Id).ToArray();
        if (fixedOperation is { } selected && !targets.Any(node => node.Id == selected)) return Incomplete("current-target-operation");
        foreach (var node in targets) {
            var domain = node.Targets!;
            if (domain.Knowledge != SemanticKnowledge.Known || domain.Min is not { } minimum || domain.Max is not { } maximum
                || minimum < 0 || maximum < minimum || maximum > 64 || domain.Candidates.IsDefault)
                return Incomplete("target-domain");
            if (domain.Candidates.Select(Identity).Distinct().Count() != domain.Candidates.Length) return Unsupported("duplicate-target");
            if (node.Id == fixedOperation && fixedTargets.Any(entity => !domain.Candidates.Any(candidate => Identity(candidate) == Identity(entity))))
                return Incomplete("current-target-candidate");
        }
        var modes = graph.Operations.Where(node => node.Links.Any(link => link.Kind == OperationLinkKind.Choice)).OrderBy(node => node.Id).ToArray();
        foreach (var node in modes) {
            int count = node.Links.Count(link => link.Kind == OperationLinkKind.Choice);
            if (node.MinChoices is not { } minimum || node.MaxChoices is not { } maximum || minimum < 0 || maximum < minimum
                || maximum > 64 || !node.MayRepeatChoices && maximum > count) return Incomplete("mode-domain");
            if (node.MayRepeatChoices && maximum > 1) {
                var pending = new Stack<int>(node.Links.Where(link => link.Kind == OperationLinkKind.Choice).Select(link => link.OperationId));
                var visited = new HashSet<int>();
                while (pending.TryPop(out int id)) {
                    if (!visited.Add(id)) continue;
                    if (!work.TrySpend()) return new(localOptimization ? CasualAssessmentStatus.SearchLimited : CasualAssessmentStatus.BudgetExceeded, 0, "operation-budget");
                    if (!nodes.TryGetValue(id, out var child)) return Unsupported("operation-link");
                    // Repeating a recipe does not create separately addressable
                    // target instances. Do not force independent copies to share.
                    if (child.Targets != null) return Incomplete("repeated-target-instance");
                    foreach (var link in child.Links) pending.Push(link.OperationId);
                }
            }
        }
        var assignments = new Dictionary<int, ImmutableArray<SemanticEntity>>();
        var choices = new Dictionary<int, ImmutableArray<int>>();
        CasualActionEstimate? failure = null;
        double best = double.NegativeInfinity;
        AssignModes(0);
        if (localOptimization && failure?.Status is CasualAssessmentStatus.BudgetExceeded or CasualAssessmentStatus.SearchLimited)
            return double.IsNegativeInfinity(best) ? new(CasualAssessmentStatus.SearchLimited, 0, "optimization-limit")
                : new(CasualAssessmentStatus.EstimatedPartial, best, "best-completed-plan");
        return failure ?? (double.IsNegativeInfinity(best) ? Unsupported("no-finite-plan") : new(CasualAssessmentStatus.Estimated, best, "finite-public-effects"));

        bool Spend()
        {
            if (failure != null) return false;
            if (work.TrySpend()) return true;
            failure = new(CasualAssessmentStatus.BudgetExceeded, 0, "operation-budget"); return false;
        }
        void AssignTargets(ActionOperation[] active, int index)
        {
            if (!Spend()) return;
            if (index == active.Length) {
                var effects = new List<(ActionOperation Node, ImmutableArray<SemanticEntity> Entities)>();
                if (Collect(graph.RootId, new(), effects)) {
                    var value = Value(state, source, effects, work, depth);
                    if (value.Status != CasualAssessmentStatus.Estimated) failure = value;
                    else best = Math.Max(best, value.Value);
                }
                return;
            }
            var node = active[index];
            if (node.Id == fixedOperation) {
                assignments[node.Id] = fixedTargets.Select(entity => node.Targets!.Candidates.First(candidate => Identity(candidate) == Identity(entity))).ToImmutableArray();
                AssignTargets(active, index + 1); return;
            }
            var domain = node.Targets!;
            var candidates = domain.Candidates.OrderBy(Identity, StringComparer.Ordinal).ToArray();
            for (int count = domain.Min!.Value; count <= Math.Min(domain.Max!.Value, candidates.Length) && failure == null; count++)
                Subsets(candidates.Length, count, false, selected => {
                    assignments[node.Id] = selected.Select(item => candidates[item]).ToImmutableArray(); AssignTargets(active, index + 1);
                });
        }
        void AssignModes(int index)
        {
            if (!Spend()) return;
            if (index == modes.Length) {
                var reachable = new HashSet<int>();
                if (!Reach(graph.RootId, new(), reachable) || fixedOperation is { } current && !reachable.Contains(current)) return;
                assignments.Clear();
                AssignTargets(targets.Where(node => reachable.Contains(node.Id)).ToArray(), 0);
                return;
            }
            var node = modes[index];
            var links = node.Links.Where(link => link.Kind == OperationLinkKind.Choice).OrderBy(link => link.OperationId).ToArray();
            for (int count = node.MinChoices!.Value; count <= node.MaxChoices!.Value && failure == null; count++)
                Subsets(links.Length, count, node.MayRepeatChoices, selected => {
                    choices[node.Id] = selected.Select(item => links[item].OperationId).ToImmutableArray(); AssignModes(index + 1);
                });
        }
        bool Reach(int id, HashSet<int> path, HashSet<int> reachable)
        {
            if (!Spend()) return false;
            if (!nodes.TryGetValue(id, out var node) || path.Contains(id)) { failure = Unsupported("operation-cycle-or-link"); return false; }
            if (!reachable.Add(id)) return true;
            path.Add(id);
            foreach (var link in node.Links.Where(link => link.Kind == OperationLinkKind.Sequence))
                if (!Reach(link.OperationId, path, reachable)) return false;
            if (choices.TryGetValue(id, out var selected))
                foreach (int child in selected) if (!Reach(child, path, reachable)) return false;
            path.Remove(id); return true;
        }
        void Subsets(int count, int take, bool repeat, Action<ImmutableArray<int>> found)
        {
            var selected = new int[take];
            Choose(0, 0);
            void Choose(int offset, int start)
            {
                if (!Spend()) return;
                if (offset == take) { found(selected.ToImmutableArray()); return; }
                for (int item = start; item < count && failure == null; item++) {
                    selected[offset] = item; Choose(offset + 1, repeat ? item : item + 1);
                }
            }
        }
        bool Collect(int id, HashSet<int> path, List<(ActionOperation, ImmutableArray<SemanticEntity>)> effects)
        {
            if (!Spend()) return false;
            if (!nodes.TryGetValue(id, out var node) || !path.Add(id)) { failure = Unsupported("operation-cycle-or-link"); return false; }
            if (node.Effect is not (ActionEffectKind.Choose or ActionEffectKind.Cleanup)) {
                var recipients = Resolve(node);
                if (recipients == null) { failure = Incomplete("recipient-binding"); return false; }
                effects.Add((node, recipients.Value));
            }
            foreach (var link in node.Links.Where(link => link.Kind == OperationLinkKind.Sequence))
                if (!Collect(link.OperationId, path, effects)) return false;
            if (choices.TryGetValue(id, out var selected))
                foreach (int child in selected) if (!Collect(child, path, effects)) return false;
            path.Remove(id); return true;
        }
        ImmutableArray<SemanticEntity>? Resolve(ActionOperation node)
        {
            var recipients = node.Recipients;
            if (recipients.Knowledge != SemanticKnowledge.Known) return null;
            if (recipients.Binding is EffectRecipientBinding.Targets or EffectRecipientBinding.TargetControllers or EffectRecipientBinding.TargetOwners) {
                if (recipients.TargetOperationId is not { } target || !assignments.TryGetValue(target, out var selected)) return null;
                if (recipients.Binding == EffectRecipientBinding.Targets) return selected;
                var players = new List<SemanticEntity>();
                foreach (var entity in selected) {
                    int? player = recipients.Binding == EffectRecipientBinding.TargetOwners ? entity.OwnerId : entity.ControllerId;
                    if (player == null) return null;
                    players.Add(new("player:" + player, SemanticEntityKind.Player, PlayerId: player));
                }
                return players.DistinctBy(Identity).ToImmutableArray();
            }
            if (recipients.Binding is EffectRecipientBinding.Actor or EffectRecipientBinding.Source or EffectRecipientBinding.Explicit)
                return recipients.Entities;
            return null;
        }
    }

    private static CasualActionEstimate Value(GameSnapshot state, Card? source,
        List<(ActionOperation Node, ImmutableArray<SemanticEntity> Entities)> effects, AiWorkBudget work, int depth)
    {
        double value = 0;
        var lifeLoss = new Dictionary<int, long>();
        var lifeGain = new Dictionary<int, long>();
        var draws = new Dictionary<int, long>();
        var cardDamage = new Dictionary<string, long>();
        foreach (var (node, entities) in effects) {
            bool quantified = node.Effect is ActionEffectKind.Draw or ActionEffectKind.Damage or ActionEffectKind.AddCounters
                or ActionEffectKind.RemoveCounters or ActionEffectKind.LifeGain or ActionEffectKind.LifeLoss or ActionEffectKind.Scry or ActionEffectKind.Token;
            if (quantified && (node.AmountKnowledge != SemanticKnowledge.Known || node.Amount is null or < 0)) return Incomplete("effect-amount");
            long amount = node.Amount ?? 1;
            foreach (var entity in entities) {
                if (!work.TrySpend()) return new(CasualAssessmentStatus.BudgetExceeded, 0, "operation-budget");
                int? controller = entity.Kind == SemanticEntityKind.Player ? entity.PlayerId : entity.ControllerId;
                if (controller == null || !state.Players.Any(player => player.Id == controller)) return Incomplete("recipient-controller");
                bool own = controller == state.ViewerId;
                double sign = own ? 1 : -.65;
                var card = Card(state, entity, source);
                switch (node.Effect) {
                    case ActionEffectKind.Draw when entity.Kind == SemanticEntityKind.Player:
                        draws[controller.Value] = draws.GetValueOrDefault(controller.Value) + amount; break;
                    case ActionEffectKind.LifeLoss when entity.Kind == SemanticEntityKind.Player:
                    case ActionEffectKind.Damage when entity.Kind == SemanticEntityKind.Player:
                        lifeLoss[controller.Value] = lifeLoss.GetValueOrDefault(controller.Value) + amount; break;
                    case ActionEffectKind.Damage when card != null:
                        cardDamage[entity.CardId] = cardDamage.GetValueOrDefault(entity.CardId) + amount; break;
                    case ActionEffectKind.Destroy when card != null:
                        if (!CasualTableAssessment.HasKeyword(card, "indestructible")) value -= sign * Worth(card); break;
                    case ActionEffectKind.Exile when card != null:
                        value -= sign * Worth(card); break;
                    case ActionEffectKind.CounterSpell:
                        var prevented = CounterTarget(state, entity, work, depth);
                        if (prevented.Status != CasualAssessmentStatus.Estimated) return prevented;
                        value += prevented.Value; break;
                    case ActionEffectKind.Bounce when card != null: value -= sign * Worth(card) * .6; break;
                    case ActionEffectKind.Permanent when card != null: value += sign * CasualTableAssessment.CardValue(card); break;
                    case ActionEffectKind.Land: value += sign * .5; break;
                    case ActionEffectKind.LifeGain:
                        lifeGain[controller.Value] = lifeGain.GetValueOrDefault(controller.Value) + amount; break;
                    case ActionEffectKind.Scry: value += sign * amount * .6; break;
                    case ActionEffectKind.Search: value += sign * 2; break;
                    case ActionEffectKind.Discard: value -= sign * amount * 3; break;
                    case ActionEffectKind.Tap when card != null: if (!card.Tapped) value -= sign * (.5 + Worth(card) * .2); break;
                    case ActionEffectKind.Untap when card != null: if (card.Tapped) value += sign * (.5 + Worth(card) * .2); break;
                    case ActionEffectKind.AddCounters when card != null:
                    case ActionEffectKind.RemoveCounters when card != null:
                        double polarity = node.CounterType is "P1P1" or "+1/+1" ? 1 : node.CounterType is "M1M1" or "-1/-1" ? -1 : 0;
                        if (polarity == 0) return Incomplete("counter-purpose");
                        value += sign * polarity * amount * 1.35 * (node.Effect == ActionEffectKind.RemoveCounters ? -1 : 1); break;
                    case ActionEffectKind.Mana: value += sign * amount * .2; break;
                    default: return Incomplete("effect-model");
                }
                double Worth(Card face) => own ? CasualTableAssessment.OpportunityCost(state, face,
                    CardViews.FindZoneOccurrence(state, entity.CardId)?.Zone ?? "Battlefield") : CasualTableAssessment.CardValue(face);
            }
        }
        foreach (int playerId in lifeLoss.Keys.Union(lifeGain.Keys)) {
            var player = state.Players.First(player => player.Id == playerId);
            bool own = playerId == state.ViewerId;
            long change = lifeGain.GetValueOrDefault(playerId) - lifeLoss.GetValueOrDefault(playerId);
            if (change < 0) value += (long)player.Life + change <= 0 ? own ? -1000 : 40 : -change * (own ? -.4 : .26);
            else value += change * (own ? 1 : -.65) * (player.Life < 15 ? .6 : .2);
        }
        foreach (var (playerId, amount) in draws) {
            var player = state.Players.First(player => player.Id == playerId);
            bool own = playerId == state.ViewerId;
            var library = player.Zones.FirstOrDefault(zone => zone.Name == "Library");
            value += library != null && amount > library.Count ? own ? -1000 : 40 : amount * (own ? 3 : -1.95);
        }
        foreach (var (id, amount) in cardDamage) {
            var occurrence = CardViews.FindZoneOccurrence(state, id);
            if (occurrence == null) return Incomplete("damage-recipient");
            var card = occurrence.Value.Card;
            double harm = !CasualTableAssessment.HasKeyword(card, "indestructible") && amount >= (long)(card.Toughness ?? int.MaxValue) - card.Damage
                ? CasualTableAssessment.CardValue(card) : amount * .1;
            value += harm * (occurrence.Value.PlayerId == state.ViewerId ? -1 : .65);
        }
        return new(CasualAssessmentStatus.Estimated, value, "public-effect-values");
    }

    private static Card? Card(GameSnapshot state, SemanticEntity entity, Card? source)
        => CardViews.FindZoneOccurrence(state, entity.CardId)?.Card
            ?? state.Stack.FirstOrDefault(item => item.Id == entity.Id || item.Card?.VisualId == entity.CardId)?.Card
            ?? (source != null && CardViews.Identity(source) == entity.CardId ? source : null);
    // A stack occurrence is not a physical card: copies can share CardId while
    // having different chosen targets. Never recover identity from display IDs.
    public static CasualActionEstimate CounterTarget(GameSnapshot state, SemanticEntity entity, AiWorkBudget work)
        => CounterTarget(state, entity, work.CreateSlice(256, 4), 0);

    private static CasualActionEstimate CounterTarget(GameSnapshot state, SemanticEntity entity, AiWorkBudget work, int depth)
    {
        var matches = state.Stack.Where(item => entity.StackItemId.Length > 0 ? item.Id == entity.StackItemId
            : item.Id == entity.Id || entity.CardId.Length > 0 && item.Card?.VisualId == entity.CardId).Take(2).ToArray();
        if (entity.Kind != SemanticEntityKind.Spell || matches.Length != 1) return Incomplete("pending-occurrence");
        var pending = matches[0];
        var estimate = Evaluate(state, pending.OperationGraph, work, pending.Card, null, [], false, depth + 1);
        return estimate.Status == CasualAssessmentStatus.Estimated ? estimate with { Value = -estimate.Value, Reason = "avoided-pending-effects" } : estimate;
    }

    private static string Identity(SemanticEntity entity) => entity.Kind == SemanticEntityKind.Player ? "player:" + entity.PlayerId
        : entity.Kind == SemanticEntityKind.Spell && entity.StackItemId.Length > 0 ? "stack:" + entity.StackItemId
        : entity.Kind + ":" + (entity.CardId.Length > 0 ? entity.CardId : entity.Id);
    private static CasualActionEstimate Incomplete(string reason) => new(CasualAssessmentStatus.Incomplete, 0, reason);
    private static CasualActionEstimate Unsupported(string reason) => new(CasualAssessmentStatus.Unsupported, 0, reason);
}
