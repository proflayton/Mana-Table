using Mana.Contracts;

namespace Mana.AI;

// A separately versioned strategic preference, not a damage forecast or a
// safety proof. Four life per nominal exposed card is a frozen experimental
// stress assumption; it is neither expected mana value nor an upper bound.
internal static class CasualUncertainLifeRisk
{
    private const long StressLifePerCard = 4;

    internal static bool TryChoose(AiDecisionRequest request, OptionsPayload payload, out int drawIndex)
    {
        drawIndex = -1;
        var semantics = request.Frame.Semantics;
        var options = semantics.Options;
        // Constant-size routing preserves old-wire fallback/work exactly.
        if (options.Length != 2 || payload.Selection.Choices.Length != 2
            || options[0].RememberedMillLifeLoss == null && options[1].RememberedMillLifeLoss == null)
            return false;
        if (!request.Work.TrySpend()) return false;
        var state = request.Frame.State;
        if (state.Status != "playing" || state.Viewer is not { Eliminated: false, Life: > 0 } viewer
            || state.DecisionOwnerId != state.ViewerId || payload.Selection.Range is not { Min: 1, Max: 1 }
            || payload.CounterPayment != null || payload.ManaPaymentContext != null || payload.HandReduction != null
            || payload.LibraryAcquisition != null || !payload.Selection.Disclosures.IsEmpty
            || semantics.SelectionIsCost || semantics.Intent is DecisionIntent.Discard or DecisionIntent.Sacrifice)
            return false;
        foreach (var choice in payload.Selection.Choices) {
            if (!request.Work.TrySpend()) return false;
            if (choice.Card != null || choice.CardId.Length != 0 || choice.Mana.Length != 0
                || !options.Any(option => option.Index == choice.Index)
                || !request.Frame.Allows(new ChooseOptions(state.Id, state.Decision!.Id, [choice.Index]))) return false;
        }

        int riskSlot = options[0].RememberedMillLifeLoss != null ? 0 : 1;
        var risk = options[riskSlot];
        var draw = options[1 - riskSlot];
        if (draw.RememberedMillLifeLoss != null || draw.Target != null || risk.Target != null
            || risk.RememberedMillLifeLoss is not { Version: 1, ObservedCount: > 0 } certificate
            || draw.Effect != ActionEffectKind.Draw || risk.Effect != ActionEffectKind.Mill) return false;
        var drawGraph = draw.OperationGraph;
        if (drawGraph.Knowledge != SemanticKnowledge.Known || drawGraph.Operations.Length != 1) return false;
        var drawNode = drawGraph.Operations[0];
        if (!request.Work.TrySpend() || drawGraph.RootId != drawNode.Id || !Ordinary(drawNode)
            || drawNode.Effect != ActionEffectKind.Draw || drawNode.Knowledge != SemanticKnowledge.Known
            || drawNode.AmountKnowledge != SemanticKnowledge.Known || drawNode.Amount != 1 || !drawNode.Links.IsEmpty
            || PlayerRecipient(drawNode) is not { } opponentId || opponentId == state.ViewerId
            || drawNode.ActorPlayerId != opponentId) return false;

        var graph = risk.OperationGraph;
        if (graph.Knowledge != SemanticKnowledge.Unknown || graph.Operations.Length != 3) return false;
        ActionOperation? mill = null, loss = null, cleanup = null;
        foreach (var node in graph.Operations) {
            if (!request.Work.TrySpend() || !Ordinary(node) || node.ActorPlayerId != opponentId) return false;
            if (node.Id == graph.RootId) mill = node;
            else if (node.Id == certificate.LifeLossOperationId) loss = node;
            else cleanup = node;
        }
        if (mill is not { Effect: ActionEffectKind.Mill, Knowledge: SemanticKnowledge.Unknown,
                Amount: null, AmountKnowledge: SemanticKnowledge.Unknown }
            || loss is not { Effect: ActionEffectKind.LifeLoss, Knowledge: SemanticKnowledge.Unknown,
                Amount: null, AmountKnowledge: SemanticKnowledge.Unknown }
            || cleanup is not { Effect: ActionEffectKind.Cleanup, Knowledge: SemanticKnowledge.Known }
            || PlayerRecipient(mill) != opponentId || PlayerRecipient(loss) != state.ViewerId
            || loss.Recipients.Binding != EffectRecipientBinding.Explicit
            || !Sequence(mill, loss.Id) || !Sequence(loss, cleanup.Id) || !cleanup.Links.IsEmpty) return false;

        // Do not use the baseline's legacy unknown-team opponent assumption.
        long relationshipWork = (long)state.Players.Length * state.Players.Length;
        if (relationshipWork > int.MaxValue || !request.Work.TrySpend((int)relationshipWork)
            || PlayerRelations.ForViewer(state, opponentId) != PlayerRelation.Opponent) return false;
        var opponent = state.Players.SingleOrDefault(player => player.Id == opponentId);
        if (opponent is not { Eliminated: false }) return false;
        Zone? library = null;
        foreach (var zone in opponent.Zones) {
            if (!request.Work.TrySpend()) return false;
            if (zone.Name != "Library") continue;
            if (library != null) return false;
            library = zone;
        }
        // Empty/last-card terminal behavior stays exclusively with v6.
        if (library is not { Count: >= 2 }) return false;
        long exposure = Math.Min(certificate.ObservedCount, library.Count);
        if (viewer.Life > StressLifePerCard * exposure) return false;
        drawIndex = draw.Index;
        return true;
    }

    private static bool Ordinary(ActionOperation node) => !node.Conditional && !node.MayRepeatChoices
        && node.MinChoices == null && node.MaxChoices == null && node.Targets == null && node.Costs.IsEmpty;

    private static bool Sequence(ActionOperation node, int next) => node.Links is [{ Kind: OperationLinkKind.Sequence } link]
        && link.OperationId == next;

    private static int? PlayerRecipient(ActionOperation node) => node.Recipients is {
        Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Actor or EffectRecipientBinding.Explicit,
        TargetOperationId: null, Entities: [{ Kind: SemanticEntityKind.Player, PlayerId: { } player }]
    } ? player : null;
}
