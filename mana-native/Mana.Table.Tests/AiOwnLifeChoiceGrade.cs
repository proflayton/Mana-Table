using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

// The experiment author declares a preference before running either arm. This
// independent judge reads the full original typed opportunity/current indices;
// it does not call the candidate ranker, calculate 4*exposure, inspect hidden
// damage, or derive its preference from scores, explanations or policy IDs.
internal static class AiOwnLifeChoiceGrade
{
    internal static int? PreferredIndex(AiDecisionFrame frame, string kind)
    {
        var state = frame.State;
        if (state.Viewer is not { Eliminated: false, Life: > 0 } || state.DecisionOwnerId != state.ViewerId
            || frame.Semantics.SelectionIsCost || frame.Semantics.Intent is DecisionIntent.Discard or DecisionIntent.Sacrifice
            || state.Decision?.Payload is not OptionsPayload { CounterPayment: null, ManaPaymentContext: null,
                HandReduction: null, LibraryAcquisition: null, Selection.Range: { Min: 1, Max: 1 } } payload
            || payload.Selection.Choices.Length != 2 || !payload.Selection.Disclosures.IsEmpty || frame.Semantics.Options.Length != 2
            || payload.Selection.Choices.Any(choice => choice.Card != null || choice.CardId.Length != 0 || choice.Mana.Length != 0)) return null;
        DecisionOptionSemantics? draw = null, other = null;
        int? opponentId = null;
        foreach (var option in frame.Semantics.Options) {
            if (!payload.Selection.Choices.Any(choice => choice.Index == option.Index)
                || !frame.Allows(new ChooseOptions(state.Id, state.Decision.Id, [option.Index]))) return null;
            if (option.OperationGraph is { Knowledge: SemanticKnowledge.Known, Operations: [var node] } graph
                && graph.RootId == node.Id && node.Effect == ActionEffectKind.Draw && node.Amount == 1
                && node.AmountKnowledge == SemanticKnowledge.Known && Ordinary(node)
                && SingletonPlayer(node) is { } player && node.ActorPlayerId == player) {
                if (draw != null) return null;
                draw = option; opponentId = player;
            } else {
                if (other != null) return null;
                other = option;
            }
        }
        if (draw == null || other == null || opponentId == null
            || PlayerRelations.ForViewer(state, opponentId.Value) != PlayerRelation.Opponent) return null;
        var opponent = state.Players.Single(player => player.Id == opponentId);
        var libraries = opponent.Zones.Where(zone => zone.Name == "Library").ToArray();
        if (opponent.Eliminated || libraries.Length != 1 || libraries[0].Count < 2) return null;
        if (kind == "prefer-opponent-draw") {
            if (other.RememberedMillLifeLoss is not { ObservedCount: > 0 } certificate
                || other.Effect != ActionEffectKind.Mill || other.OperationGraph.Knowledge != SemanticKnowledge.Unknown
                || other.OperationGraph.Operations.Length != 3) return null;
            var mill = other.OperationGraph.Operations.Single(node => node.Id == other.OperationGraph.RootId);
            var loss = other.OperationGraph.Operations.Single(node => node.Id == certificate.LifeLossOperationId);
            // Admission already checks the exact dependency/sequence/public
            // counter. The judge independently checks whose tradeoff is graded.
            return SingletonPlayer(mill) == opponentId && SingletonPlayer(loss) == state.ViewerId
                && loss.Amount == null && loss.AmountKnowledge == SemanticKnowledge.Unknown ? draw.Index : null;
        }
        if (kind == "prefer-fixed-self-loss" && other.RememberedMillLifeLoss == null
            && other.OperationGraph is { Knowledge: SemanticKnowledge.Known, Operations: [var fixedLoss] } known
            && known.RootId == fixedLoss.Id && fixedLoss.Effect == ActionEffectKind.LifeLoss
            && fixedLoss.Amount is 0 or 1 && fixedLoss.AmountKnowledge == SemanticKnowledge.Known
            && Ordinary(fixedLoss) && SingletonPlayer(fixedLoss) == state.ViewerId) return other.Index;
        return null;
    }

    private static bool Ordinary(ActionOperation node) => node.Knowledge == SemanticKnowledge.Known && !node.Conditional
        && node.Targets == null && node.Costs.IsEmpty && node.Links.IsEmpty && !node.MayRepeatChoices
        && node.MinChoices == null && node.MaxChoices == null;
    private static int? SingletonPlayer(ActionOperation node) => node.Recipients is {
        Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Actor or EffectRecipientBinding.Explicit,
        TargetOperationId: null, Entities: [{ Kind: SemanticEntityKind.Player, PlayerId: { } player }]
    } ? player : null;
}
