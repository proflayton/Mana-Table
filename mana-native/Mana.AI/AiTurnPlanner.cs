using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

// Bounded preferences over current authorized facts. The session owns their
// lifetime; this planner cannot submit commands, retain handles or undo costs.
internal static class AiTurnPlanner
{
    internal static AiResourceReservation? Interaction(AiDecisionRequest request)
    {
        if (request.Plan?.Reservations.FirstOrDefault() is { } retained) return retained;
        var metadata = request.Frame.Domain.Actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
        int inspected = 0;
        foreach (var set in request.Frame.SourceActions.OrderBy(set => set.SourceCardId, StringComparer.Ordinal)) {
            var source = CardViews.FindActionSource(request.Frame.State, set.SourceCardKey);
            if (source == null || !source.Value.Card.Type.Contains("Instant", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var route in set.Actions.OrderBy(route => metadata.GetValueOrDefault(route.Id)?.StableId, StringComparer.Ordinal)
                .ThenBy(route => route.Kind, StringComparer.Ordinal)) {
                if (request.Work.Remaining <= 12 || inspected++ >= 16) return null;
                if (!metadata.TryGetValue(route.Id, out var action) || action.StableId.Length == 0
                    || action.ManaAbility || action.Affordability == ActionAffordability.Unavailable
                    || request.History.AttemptedRoutes.Contains(new(request.Frame.State.Turn, set.SourceCardId, action.StableId))
                    || action.OperationGraph.Knowledge != SemanticKnowledge.Known
                    || !action.OperationGraph.Operations.Any(node => node.Effect is ActionEffectKind.CounterSpell
                        or ActionEffectKind.Destroy or ActionEffectKind.Exile or ActionEffectKind.Damage or ActionEffectKind.Bounce)
                    || action.Costs.Any(cost => cost.Kind != ActionCostKind.Mana && cost.Amount != 0)) continue;
                if (Reserve(request, set, route, action, AiReservationPurpose.Interaction) is { } reservation) return reservation;
            }
        }
        return null;
    }

    // Sequence two currently known plays without assuming either will resolve.
    // A follow-up shares the same session lifetime and payment witness as a
    // held response; only its strategic purpose differs.
    internal static AiResourceReservation? FollowUp(AiDecisionRequest request, SourceActionSet set,
        SourceAction route, ActionSemantics action, ActionSemantics first, int firstTax)
    {
        if (route.Kind != "cast") return null;
        var reservation = Reserve(request, set, route, action, AiReservationPurpose.FollowUp);
        return reservation != null && Preserves(request, first, reservation, firstTax) ? reservation : null;
    }

    private static AiResourceReservation? Reserve(AiDecisionRequest request, SourceActionSet set,
        SourceAction route, ActionSemantics action, AiReservationPurpose purpose)
    {
        var source = CardViews.FindActionSource(request.Frame.State, set.SourceCardKey);
        if (source == null || action.StableId.Length == 0
            || action.OperationGraph.Knowledge != SemanticKnowledge.Known
            || action.Costs.Any(cost => cost.Kind != ActionCostKind.Mana && cost.Amount != 0)) return null;
        var costs = action.Costs.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
        if (costs.Any(cost => cost.Amount == null) || costs.SelectMany(cost => cost.ManaSymbols).Any(symbol => symbol.Trim('{', '}') == "X")) return null;
        int tax = source.Value.Zone == "Command" && route.Kind == "cast" ? source.Value.Card.CommanderTax : 0;
        long generic = costs.Sum(cost => Math.Max(0L, cost.Amount ?? 0)) + tax;
        if (generic > 1024 || request.Work.Remaining <= 12 || request.PlanVersion == long.MaxValue) return null;
        var funding = AiManaPlanner.ForAction(request with { Work = request.Work.CreateSlice(192, 8) }, action, tax);
        if (funding.Status != AiManaPlanStatus.Funded) return null;
        var reservation = new AiResourceReservation(set.SourceCardId, action.StableId,
            costs.SelectMany(cost => cost.ManaSymbols).ToImmutableArray(), (int)generic) {
            Purpose = purpose, SourceZone = source.Value.Zone,
            ManaSourceIds = funding.Steps.OfType<AiManaStep.Source>().Select(step => step.SourceCardId).Distinct().ToImmutableArray(),
            PoolMana = funding.Steps.OfType<AiManaStep.Pool>().GroupBy(step => step.Color)
                .ToImmutableDictionary(group => group.Key, group => group.Sum(step => step.Amount))
        };
        // Play permission can include foreign or transient sources which the
        // first memory model cannot retain. Do not turn a legal current play
        // into an invalid answer merely by proposing unsupported memory for it.
        var state = request.Frame.State;
        var proposed = new AiTurnPlan(state.Id, state.ViewerId, state.Turn, request.PlanVersion + 1,
            AiPlanPurpose.Develop, [reservation], []) { CreatedFrom = request.Frame.Stamp };
        return AiPlanValidation.IsTransitionValid(new(request.PlanVersion, proposed), request.PlanVersion, request.Frame, null) ? reservation : null;
    }

    internal static bool Matches(AiResourceReservation reservation, string sourceId, ActionSemantics action)
        => reservation.SourceCardId == sourceId && reservation.StableRouteId == action.StableId;

    internal static bool Preserves(AiDecisionRequest request, ActionSemantics action, AiResourceReservation reservation, int tax)
    {
        if (AiManaPlanner.WithReservation(request with { Work = request.Work.CreateSlice(128, 8) }, action, reservation, tax).Status != AiManaPlanStatus.Funded) return false;
        // A joint bill can be paid by tapping a bundled producer that was pinned
        // for the response. Do not promise a preserved witness unless this action
        // can also be funded without consuming that witness.
        return AiManaPlanner.ForAction(request with { Work = request.Work.CreateSlice(128, 8) }, action, tax,
            preserve: reservation).Status == AiManaPlanStatus.Funded;
    }

    internal static double Opportunity(AiDecisionRequest request, AiResourceReservation reservation)
    {
        var set = request.Frame.SourceActions.FirstOrDefault(set => set.SourceCardId == reservation.SourceCardId);
        var ids = set?.Actions.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);
        var action = request.Frame.Domain.Actions.FirstOrDefault(action => action.StableId == reservation.StableRouteId && ids?.Contains(action.ActionId) == true);
        if (action == null || action.TargetsAvailable == false || request.Work.Remaining <= 12) return 4;
        var estimate = CasualActionAssessment.Evaluate(request.Frame.State, action.OperationGraph,
            request.Work.CreateSlice(128, 8), CardViews.FindZoneOccurrence(request.Frame.State, reservation.SourceCardId)?.Card);
        return estimate.Status == CasualAssessmentStatus.Estimated ? Math.Max(4, estimate.Value) : 4;
    }

    internal static AiProposal Remember(AiDecisionRequest request, AiProposal proposal, AiPlanPurpose purpose,
        AiResourceReservation? reservation, ImmutableArray<string> blockers = default)
    {
        if (proposal is not AiProposal.Answer answer || request.PlanVersion == long.MaxValue) return proposal;
        var state = request.Frame.State;
        var next = new AiTurnPlan(state.Id, state.ViewerId, state.Turn, request.PlanVersion + 1,
            purpose, reservation == null ? [] : [reservation], blockers.IsDefault ? request.Plan?.RetainedBlockerIds ?? [] : blockers) {
            CreatedFrom = request.Frame.Stamp
        };
        return answer with { PlanTransition = new(request.PlanVersion, next) };
    }

    internal static AiProposal AbandonReservation(AiDecisionRequest request, AiProposal proposal)
        => request.Plan?.Reservations.IsEmpty != false ? proposal
            : Remember(request, proposal, AiPlanPurpose.Develop, null);

    // Public one-combat exposure estimate. We do not forecast hidden pumps,
    // turn order promises, goad or future removals. Engine offers still decide
    // actual legal attacks and the joint repair witness overrides preferences.
    internal static bool NeededBlocker(AiDecisionRequest request, Card proposed)
    {
        var state = request.Frame.State;
        if (state.Viewer == null || CasualTableAssessment.HasKeyword(proposed, "vigilance")) return false;
        var unavailable = state.Combat!.Attackers.Select(attack => attack.CardId).ToHashSet(StringComparer.Ordinal);
        unavailable.Add(CardViews.Identity(proposed));
        var blockers = state.Viewer.Zone("Battlefield").Cards.Where(card => !card.Tapped && !card.PhasedOut
            && CasualTableAssessment.IsCreature(card) && !unavailable.Contains(CardViews.Identity(card))).ToArray();
        var work = request.Work.CreateSlice(96, 8);
        foreach (var enemy in state.Players.Where(player => player.Id != state.ViewerId && !player.Eliminated)) {
            // A tapped enemy normally untaps before its next combat. This is
            // exposure over the next turn, not a current blocking domain.
            var incoming = enemy.Zone("Battlefield").Cards.Where(card => !card.PhasedOut && CasualTableAssessment.IsCreature(card))
                .OrderByDescending(CasualTableAssessment.UnblockedDamage).ToArray();
            if (incoming.Sum(card => (long)CasualTableAssessment.UnblockedDamage(card)) < state.Viewer.Life) continue;
            long? without = Damage(incoming, blockers);
            if (without == null) return request.Plan?.RetainedBlockerIds.Contains(CardViews.Identity(proposed)) == true;
            if (without < state.Viewer.Life) continue;
            long? with = Damage(incoming, blockers.Append(proposed).ToArray());
            if (with == null) return request.Plan?.RetainedBlockerIds.Contains(CardViews.Identity(proposed)) == true;
            if (with < without) return true;
        }
        return false;

        long? Damage(Card[] incoming, Card[] available)
        {
            long damage = 0;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attacker in incoming) {
                if (!work.TrySpend()) return null;
                long hit = CasualTableAssessment.UnblockedDamage(attacker);
                if (CasualTableAssessment.HasKeyword(attacker, "menace")) {
                    Card? first = null, second = null;
                    foreach (var blocker in available) {
                        if (!work.TrySpend()) return null;
                        if (used.Contains(CardViews.Identity(blocker)) || !CanBlock(attacker, blocker)) continue;
                        if (first == null) first = blocker;
                        else { second = blocker; break; }
                    }
                    if (first == null || second == null) { damage += hit; continue; }
                    // The ordinary exchange model is one blocker only. A
                    // menace pair stops nontrampling damage; assigning trample
                    // across that pair needs a separate, richer forecast.
                    if (CasualTableAssessment.HasKeyword(attacker, "trample")) return null;
                    used.Add(CardViews.Identity(first)); used.Add(CardViews.Identity(second));
                    continue;
                }
                Card? best = null;
                long prevented = 0;
                foreach (var blocker in available) {
                    if (!work.TrySpend()) return null;
                    if (used.Contains(CardViews.Identity(blocker)) || !CanBlock(attacker, blocker)) continue;
                    long stops = hit - CasualTableAssessment.Exchange(attacker, blocker).DamageToDefender;
                    if (stops > prevented) { best = blocker; prevented = stops; }
                }
                if (best != null) used.Add(CardViews.Identity(best));
                damage += Math.Max(0, hit - prevented);
            }
            return damage;
        }
    }

    private static bool CanBlock(Card attacker, Card blocker)
        => !CasualTableAssessment.HasKeyword(attacker, "unblockable")
            && (!CasualTableAssessment.HasKeyword(attacker, "flying") || CasualTableAssessment.HasKeyword(blocker, "flying") || CasualTableAssessment.HasKeyword(blocker, "reach"));
}
