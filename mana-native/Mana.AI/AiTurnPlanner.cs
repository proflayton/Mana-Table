using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

// Bounded preferences over current authorized facts. The session owns their
// lifetime; this planner cannot submit commands, retain handles or undo costs.
internal static class AiTurnPlanner
{
    // Preserve the next draw/land opportunity before committing an ordinary
    // hand-based development instant or ordinary owned activated effect. This
    // is a current preference, not a
    // promise that a later land or spell will be legal. Responses, floating
    // mana, transient play access and phase-sensitive/unknown effects retain
    // their existing assessment. Even Scry is excluded: it can matter before
    // the normal draw. Unknown quantities alone do not hide a typed Token/Draw.
    internal static bool WaitForOwnDevelopment(AiDecisionRequest request, SourceActionSet set,
        SourceAction route, ActionSemantics action)
    {
        var state = request.Frame.State;
        if (state.Decision?.Payload is not PriorityPayload { Pass.Enabled: true }
            || state.ActivePlayerId != state.ViewerId || state.PhaseKey is not ("UPKEEP" or "DRAW")
            || !state.Stack.IsEmpty || state.Combat != null || state.Viewer is not { } actor
            || actor.Mana.Values.Any(amount => amount > 0) || actor.LandPlaysUsed is not >= 0
            || actor.LandPlayLimit is { } limit && actor.LandPlaysUsed >= limit
            || !actor.Zone("Hand").Cards.Any(card => !card.FaceDown && CasualTableAssessment.IsLand(card))
            || action.Targeted || action.ManaAbility || action.PhaseRestricted != false
            || CardViews.FindActionSource(state, set.SourceCardKey) is not { } source
            || source.PlayerId != state.ViewerId || source.Card.FaceDown
            || source.Card.PhasedOut) return false;
        bool ordinaryActivation = route.Kind == "activate" && source.Zone == "Battlefield" && action.PhaseRestricted == false;
        bool handInstant = route.Kind == "cast" && source.Zone == "Hand"
            && source.Card.Type.Contains("Instant", StringComparison.OrdinalIgnoreCase);
        if (!ordinaryActivation && !handInstant || !OrdinaryCosts(action.Costs)) return false;
        var graph = action.OperationGraph;
        if (graph.Knowledge == SemanticKnowledge.Unsupported || graph.Operations.Length != 1) return false;
        var operation = graph.Operations[0];
        return request.Work.TrySpend() && operation.Id == graph.RootId
            && operation.Knowledge != SemanticKnowledge.Unsupported && !operation.Conditional
            && operation.ActorPlayerId == state.ViewerId && operation.Targets == null && operation.Links.IsEmpty
            && OrdinaryCosts(operation.Costs)
            && operation.Effect is ActionEffectKind.Token or ActionEffectKind.Draw
            && operation.Effect == action.Effect && action.Effects.All(effect => effect == operation.Effect)
            && operation.Sources is { Knowledge: SemanticKnowledge.Known } sources
            && !sources.Entities.IsDefaultOrEmpty
            && sources.Entities.All(entity => entity.Kind == SemanticEntityKind.Card && entity.CardId == set.SourceCardId)
            && operation.Recipients is { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Actor } recipients
            && !recipients.Entities.IsDefaultOrEmpty
            && recipients.Entities.All(entity => entity.Kind == SemanticEntityKind.Player && entity.PlayerId == state.ViewerId);

        bool OrdinaryCosts(ImmutableArray<ActionCost> costs)
            => costs.Count(cost => cost.Kind == ActionCostKind.Tap) <= 1
                && costs.All(cost => cost.Kind == ActionCostKind.Mana
                    || ordinaryActivation && cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount == 1);
    }

    // The selected part is distinct from the other unpaid commitments. A
    // resource may pay a tap and then a sacrifice, but cannot pay two taps
    // without an intervening untap. Unknown sequences remain engine-owned.
    internal static bool ConflictsWithPendingSourceCosts(AiDecisionRequest request, string candidateId)
    {
        if (!request.Frame.Semantics.SelectionIsCost || request.Frame.Semantics.CostPayment is not { } payment
            || payment.CurrentCostIndex is not { } current || candidateId != payment.SourceCardId) return false;
        var selecting = payment.RemainingCosts[current];
        if (selecting.Kind is not (ActionCostKind.Tap or ActionCostKind.Sacrifice or ActionCostKind.Exile or ActionCostKind.Discard)
            || selecting.Amount == 0) return false;
        var others = payment.RemainingCosts.Where((_, index) => index != current).ToArray();
        if (others.Any(cost => cost.Kind == ActionCostKind.Other
                || cost.Kind == ActionCostKind.Untap && cost.FromSource && cost.Amount != 0)) return false;
        return others.Any(cost => cost.Kind == ActionCostKind.Tap && cost.FromSource && cost.Amount > 0);
    }

    // A required cost is part of the initiating action's resource use. Compare
    // the current parent's remaining nominal bill after each candidate removal;
    // never reuse a remembered offer or assume an unknown resource is absent.
    internal static bool PreservesCurrentCostFunding(AiDecisionRequest request, string candidateId)
    {
        if (!request.Frame.Semantics.SelectionIsCost || request.Frame.Semantics.CostPayment is not { } payment
            || request.Frame.Semantics.Intent is not (DecisionIntent.Tap or DecisionIntent.Sacrifice
                or DecisionIntent.Exile or DecisionIntent.Discard)) return false;
        var unavailable = CardViews.InZones(request.Frame.State).Where(item => item.Card.Selectable && item.Card.Highlighted)
            .Select(item => CardViews.Identity(item.Card)).ToHashSet(StringComparer.Ordinal);
        unavailable.Add(candidateId);
        if (payment.RemainingCosts.Any(cost => cost.FromSource && cost.Amount != 0
                && cost.Kind is ActionCostKind.Tap or ActionCostKind.Sacrifice or ActionCostKind.Exile or ActionCostKind.Discard))
            unavailable.Add(payment.SourceCardId);
        return AiManaPlanner.ForCostSelection(request, payment, unavailable).Status == AiManaPlanStatus.Funded;
    }

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
                    || request.History.HasAttemptedRoute(request.Frame.State, set.SourceCardId, action)
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
        var held = (blockers.IsDefault ? request.Plan?.RetainedBlockerIds ?? [] : blockers)
            .Where(id => AiPlanValidation.CanRetainBlocker(state, id)).ToImmutableArray();
        var next = new AiTurnPlan(state.Id, state.ViewerId, state.Turn, request.PlanVersion + 1,
            purpose, reservation == null ? [] : [reservation], held) {
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
        foreach (var enemy in state.Players.Where(player => AiPlayerRelations.Opponent(state, player.Id) && !player.Eliminated)) {
            // A tapped enemy normally untaps before its next combat. This is
            // exposure over the next turn, not a current blocking domain.
            var incoming = enemy.Zone("Battlefield").Cards.Where(card => !card.PhasedOut && CasualTableAssessment.IsCreature(card))
                .OrderByDescending(CasualTableAssessment.UnblockedDamage).ToArray();
            // The unblocked upper exposure is cheap and preserves optional
            // comparison work when neither ordinary nor commander damage can
            // establish a modeled defeat even without any own defenders.
            if (new AiCombatSurvival(state, state.Viewer,
                incoming.Select(card => new AiCombatDamage(card, CasualTableAssessment.UnblockedDamage(card))).ToArray()).Risk.KnownDefeats == 0) continue;
            var without = Damage(incoming, blockers);
            if (without == null) return request.Plan?.RetainedBlockerIds.Contains(CardViews.Identity(proposed)) == true;
            var risk = new AiCombatSurvival(state, state.Viewer, without).Risk;
            if (risk.KnownDefeats == 0) continue;
            var with = Damage(incoming, blockers.Append(proposed).ToArray());
            if (with == null) return request.Plan?.RetainedBlockerIds.Contains(CardViews.Identity(proposed)) == true;
            if (AiCombatSurvival.Improvement(risk, new AiCombatSurvival(state, state.Viewer, with).Risk) != AiCombatSurvivalRank.None) return true;
        }
        return false;

        List<AiCombatDamage>? Damage(Card[] incoming, Card[] available)
        {
            var damage = new List<AiCombatDamage>();
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
                    if (first == null || second == null) { damage.Add(new(attacker, hit)); continue; }
                    // The ordinary exchange model is one blocker only. A
                    // menace pair stops nontrampling damage; assigning trample
                    // across that pair needs a separate, richer forecast.
                    if (CasualTableAssessment.HasKeyword(attacker, "trample")) return null;
                    used.Add(CardViews.Identity(first)); used.Add(CardViews.Identity(second));
                    damage.Add(new(attacker, 0));
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
                damage.Add(new(attacker, Math.Max(0, hit - prevented)));
            }
            return damage;
        }
    }

    private static bool CanBlock(Card attacker, Card blocker)
        => !CasualTableAssessment.HasKeyword(attacker, "unblockable")
            && (!CasualTableAssessment.HasKeyword(attacker, "flying") || CasualTableAssessment.HasKeyword(blocker, "flying") || CasualTableAssessment.HasKeyword(blocker, "reach"));
}
