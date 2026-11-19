using System.Collections.Immutable;
using System.Globalization;
using Mana.Contracts;

namespace Mana.AI;

// The provider certifies the current acquisition stage, not a future result.
// Readable faces rank only their existing option indices: no library occurrence,
// identity, mana-production prediction or rules permission is manufactured.
internal static class CasualLibraryAcquisition
{
    internal static bool TryChoose(AiDecisionRequest request, OptionsPayload payload, out ImmutableArray<int> indices)
    {
        indices = [];
        var state = request.Frame.State;
        var context = payload.LibraryAcquisition;
        if (context == null || state.Viewer == null || state.DecisionOwnerId != state.ViewerId
            || context.ActorPlayerId != state.ViewerId || context.LibraryOwnerId != state.ViewerId
            || context.RecipientPlayerId != state.ViewerId || !context.AdditionalCostKnownAbsent
            || payload.CounterPayment != null || payload.ManaPaymentContext != null || payload.HandReduction != null
            || request.Frame.Semantics.SelectionIsCost
            || request.Frame.Semantics.Intent is not (DecisionIntent.Unknown or DecisionIntent.Benefit or DecisionIntent.Search)
            || !SupportedStage(context) || payload.Selection.Choices.Length > 512)
            return false;

        // Preserve the ordinary required-answer reserve. A completed local
        // comparison remains useful when later optional comparisons are cut off;
        // an unmet minimum returns to the unchanged mandatory-choice path.
        var work = request.Work.CreateSlice(512, 4);
        var ranked = new List<(int Index, double Score)>();
        foreach (var choice in payload.Selection.Choices.OrderBy(choice => choice.Index)) {
            if (!work.TrySpend()) break;
            if (choice.Card is not { FaceDown: false } card || !BasicLand(card)) continue;
            double score = CasualTableAssessment.CardValue(card)
                + CasualTableAssessment.Tie(request.Seed, choice.Index.ToString(CultureInfo.InvariantCulture)) * .1;
            ranked.Add((choice.Index, score));
        }
        int count = Math.Min(payload.Selection.Range.Max, ranked.Count);
        if (count < payload.Selection.Range.Min) return false;
        indices = ranked.OrderByDescending(choice => choice.Score).ThenBy(choice => choice.Index)
            .Take(count).Select(choice => choice.Index).ToImmutableArray();
        return true;
    }

    private static bool SupportedStage(LibraryAcquisitionContext context)
    {
        var plan = context.PlannedDestinations;
        bool single = plan.Length == 1 && plan[0].Destination == context.CurrentDestination
            && context.CurrentDestination is "Hand" or "Battlefield";
        return context.Stage switch {
            LibraryAcquisitionStage.Direct => single,
            LibraryAcquisitionStage.SelectBundle => context.CurrentDestination == "Library" && plan.Length == 2
                && plan.All(destination => destination.MaximumCount == 1)
                && plan.Any(destination => destination.Destination == "Hand")
                && plan.Any(destination => destination.Destination == "Battlefield"),
            LibraryAcquisitionStage.DistributeBundle => single && plan[0].MaximumCount == 1,
            _ => false
        };
    }

    private static bool BasicLand(Card card)
    {
        // Only the disclosed type-line header supplies these characteristics.
        // A creature subtype, card name, label or reminder text cannot grant them.
        string header = card.Type.Split(['—', '-'], 2)[0];
        var types = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return types.Contains("Basic", StringComparer.Ordinal) && types.Contains("Land", StringComparer.Ordinal);
    }
}
