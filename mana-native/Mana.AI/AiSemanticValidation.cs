using Mana.Contracts;
using Mana.Planning;

namespace Mana.AI;

// Validation grants no action authority. Semantic references must describe only
// the current entitled projection before a policy may inspect or traverse them.
internal static class AiSemanticValidation
{
    internal static void Validate(GameSnapshot state, DecisionActionDomain domain, DecisionSemantics semantics)
    {
        var context = new Context(state);
        foreach (var item in state.Stack) context.Committed(item);
        if (state.Decision?.Payload is EntitySelectionPayload { ManaReduction: not null } reduction
            && (!semantics.SelectionIsCost || semantics.Intent != DecisionIntent.Tap || reduction.CounterPayment != null
                || !reduction.Players.IsEmpty)) throw Invalid();
        if (semantics.Numeric is { } numeric) context.Numeric(state, numeric);
        if (semantics.Payment is { } payment) context.Payment(state, payment);
        context.Graph(semantics.OperationGraph, semantics.OperationId);
        foreach (var option in semantics.Options) {
            context.Graph(option.OperationGraph);
            if (option.Target is { } target) {
                var choices = state.Decision?.Payload switch {
                    OptionsPayload options => options.Selection.Choices,
                    AbilityChoicePayload abilities => abilities.Selection.Choices,
                    OrderingPayload ordering => ordering.Selection.Choices,
                    _ => []
                };
                if (!choices.Any(choice => choice.Index == option.Index)) throw Invalid();
                context.Entity(target);
            }
        }
        foreach (var action in domain.Actions) {
            if (!Enum.IsDefined(action.ManaCostKnowledge)) throw Invalid();
            if (action.Costs.Any(cost => cost.CounterType == null || cost.ManaSymbols.Any(string.IsNullOrWhiteSpace))) throw Invalid();
            context.Graph(action.OperationGraph);
            ManaFundingFacts.ValidateProduction(action.Production);
            if (action.ProspectiveFunding is { } funding) {
                if (!action.Costs.Any(cost => cost.Kind == ActionCostKind.Mana && (cost.Amount > 0 || !cost.ManaSymbols.IsEmpty))) throw Invalid();
                ManaFundingFacts.Validate(state, funding);
            }
        }
    }

    private sealed class Context
    {
        private readonly HashSet<int> players;
        private readonly HashSet<string> occurrences;
        private readonly ActionOperationValidation operations;
        internal Context(GameSnapshot state)
        {
            players = state.Players.Select(player => player.Id).ToHashSet();
            operations = new(state);
            var cards = CardViews.Visible(state)
                .Concat(state.ActionSources.Select(source => source.Card))
                .Concat(state.Stack.Select(item => item.Card).OfType<Card>()).ToArray();
            occurrences = cards.SelectMany(card => new[] { card.VisualId, card.CombatId }).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
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
            ManaFundingFacts.ValidateCosts(numeric.Costs);
            ManaFundingFacts.Validate(state, numeric.Funding);
        }

        internal void Payment(GameSnapshot state, PaymentDecision payment)
        {
            if (state.Decision?.Payload is not PaymentPayload payload || !Enum.IsDefined(payment.Purpose)
                || !players.Contains(payment.PayerId)
                || string.IsNullOrWhiteSpace(payment.SourceCardId) || !occurrences.Contains(payment.SourceCardId)
                || payment.Mandatory && payload.Cancel.Enabled) throw Invalid();
            if (state.Decision.Context.Source is not { } owner || owner.CardId != payment.SourceCardId
                && (owner.Card == null || CardViews.Identity(owner.Card) != payment.SourceCardId)) throw Invalid();
            if (owner.Card is { } portrait && (portrait.FaceDown || !string.IsNullOrEmpty(portrait.Key)
                || portrait.Selectable || portrait.Highlighted)) throw Invalid();
        }

        internal void Graph(ActionOperationGraph? graph, int? active = null) => operations.Validate(graph, active);
        internal void Committed(StackItem item) => operations.ValidateCommitted(item);
        internal void Entity(SemanticEntity entity) => operations.ValidateEntity(entity);
    }
    private static ArgumentException Invalid() => new("Operation semantics must be finite, typed and scoped to currently entitled occurrences.");
}
