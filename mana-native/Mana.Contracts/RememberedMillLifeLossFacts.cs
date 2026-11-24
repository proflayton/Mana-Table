namespace Mana.Contracts;

// The producer certifies its empty-memory prerequisite without exposing those
// objects. This boundary validates only entitled public facts and graph shape;
// it cannot independently inspect Forge memory or establish command ownership.
public static class RememberedMillLifeLossFacts
{
    public static void Validate(GameSnapshot state, DecisionSemantics semantics)
    {
        if (semantics.Options.IsDefault || semantics.Options.Any(option => option == null)) throw Invalid();
        foreach (var option in semantics.Options) {
            if (option.RememberedMillLifeLoss is not { } fact) continue;
            var selection = state.Decision?.Payload switch {
                OptionsPayload { CounterPayment: null, ManaPaymentContext: null, HandReduction: null, LibraryAcquisition: null } options => options.Selection,
                AbilityChoicePayload abilities => abilities.Selection,
                _ => null
            };
            if (state.DecisionOwnerId != state.ViewerId || selection == null || selection.Range.Min != 1 || selection.Range.Max != 1
                || !selection.Choices.Any(choice => choice.Index == option.Index) || semantics.SelectionIsCost
                || semantics.CostPayment != null || semantics.Payment != null || semantics.Numeric != null
                || semantics.OrderingPurpose != DecisionOrderingPurpose.Unknown
                || fact.Version != 1 || string.IsNullOrWhiteSpace(fact.SourceCardId) || fact.SourceCardId.Length > 256
                || string.IsNullOrWhiteSpace(fact.CounterType) || fact.CounterType.Length > 128 || fact.ObservedCount < 0
                || fact.LifeLossOperationId < 0 || option.Effect != ActionEffectKind.Mill || option.Amount != null) throw Invalid();

            var sources = state.Players.SelectMany(player => player.Zones.Where(zone => zone.Name == "Battlefield")
                .SelectMany(zone => zone.Cards.Select(card => (player.Id, Card: card))))
                .Where(item => Identities(item.Card).Contains(fact.SourceCardId, StringComparer.Ordinal)).ToArray();
            if (sources.Length != 1 || sources[0].Card.FaceDown || sources[0].Card.PhasedOut
                || sources[0].Card.Counters == null
                || !sources[0].Card.Counters.TryGetValue(fact.CounterType, out int count) || count != fact.ObservedCount) throw Invalid();

            var graph = option.OperationGraph;
            new ActionOperationValidation(state).Validate(graph);
            if (graph.Knowledge != SemanticKnowledge.Unknown || graph.Operations.Length != 3) throw Invalid();
            var mill = graph.Operations.Single(node => node.Id == graph.RootId);
            var loss = graph.Operations.SingleOrDefault(node => node.Id == fact.LifeLossOperationId);
            if (mill.Effect != ActionEffectKind.Mill || loss == null || loss.Effect != ActionEffectKind.LifeLoss
                || !Unpriced(mill) || !Unpriced(loss) || !Sequence(mill, loss)
                || loss.Links.Length != 1 || loss.Links[0].Kind != OperationLinkKind.Sequence) throw Invalid();
            var cleanup = graph.Operations.Single(node => node.Id == loss.Links[0].OperationId);
            if (cleanup.Effect != ActionEffectKind.Cleanup || cleanup.Id == mill.Id || cleanup.Id == loss.Id
                || cleanup.Knowledge != SemanticKnowledge.Known || !cleanup.Links.IsEmpty) throw Invalid();
            foreach (var node in graph.Operations) {
                if (node.ActorPlayerId != sources[0].Id || node.Conditional || node.Targets != null || !node.Costs.IsEmpty
                    || node.Attachment != null || node.AttachmentSourceCardId.Length != 0 || node.CounterType.Length != 0
                    || node.OriginZone.Length != 0 || node.DestinationZone.Length != 0
                    || node.MinChoices != null || node.MaxChoices != null || node.MayRepeatChoices
                    || !Source(node.Sources, fact.SourceCardId)) throw Invalid();
            }
            if (mill.Recipients is not { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Actor, TargetOperationId: null }
                || mill.Recipients.Entities.Length != 1 || mill.Recipients.Entities[0].Kind != SemanticEntityKind.Player
                || mill.Recipients.Entities[0].PlayerId != mill.ActorPlayerId
                || loss.Recipients is not { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Explicit, TargetOperationId: null }
                || loss.Recipients.Entities.Length != 1 || loss.Recipients.Entities[0].Kind != SemanticEntityKind.Player
                || !Source(cleanup.Recipients, fact.SourceCardId) || cleanup.Amount != null
                || cleanup.AmountKnowledge != SemanticKnowledge.Unknown) throw Invalid();
        }
    }

    private static bool Unpriced(ActionOperation node) => node.Knowledge == SemanticKnowledge.Unknown
        && node.Amount == null && node.AmountKnowledge == SemanticKnowledge.Unknown;
    private static bool Sequence(ActionOperation from, ActionOperation to) => from.Links.Length == 1
        && from.Links[0].Kind == OperationLinkKind.Sequence && from.Links[0].OperationId == to.Id;
    private static bool Source(EffectRecipients binding, string source) => binding is {
        Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Source, TargetOperationId: null }
        && binding.Entities.Length == 1 && binding.Entities[0].Kind == SemanticEntityKind.Card && binding.Entities[0].CardId == source;
    private static IEnumerable<string> Identities(Card card) => new[] { card.VisualId, card.CombatId }.Where(id => !string.IsNullOrEmpty(id));
    private static ArgumentException Invalid() => new("Remembered mill/life-loss evidence must match the current public source and exact unpriced branch.");
}
