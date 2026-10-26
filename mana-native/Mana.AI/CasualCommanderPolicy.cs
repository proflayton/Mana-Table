using System.Collections.Immutable;
using System.Globalization;
using Mana.Contracts;

namespace Mana.AI;

/// <summary>
/// A bounded, deterministic whole-seat baseline. Rules offers own legality;
/// public characteristics and typed effect/cost facts own its estimates. This
/// is a heuristic player, not a solver or proof of socially engaging play.
/// </summary>
public sealed class CasualCommanderPolicy : IAiPolicy
{
    public AiPolicyDescriptor Descriptor { get; } = new("casual-commander.v1", AiTasks.All);

    public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellation.ThrowIfCancellationRequested();
        if (!request.Work.TrySpend()) return Task.FromResult<AiProposal>(new AiProposal.Unavailable("work-budget", "Decision allowance exhausted."));
        var state = request.Frame.State;
        var decision = state.Decision!;
        AiProposal result = decision.Payload switch {
            PriorityPayload priority => Priority(request, priority),
            OpeningHandPayload opening => Opening(request, opening),
            PaymentPayload payment => Payment(request, payment),
            AttackPayload attack => Attackers(request, attack),
            BlockPayload block => Blockers(request, block),
            TargetSelectionPayload target => Select(request, target.Complete, target.Cancel, target.Range, target.Players),
            EntitySelectionPayload entity => Select(request, entity.Complete, entity.Cancel, entity.Range, entity.Players),
            ConfirmationPayload confirmation => Confirmation(request, confirmation),
            AbilityChoicePayload ability => Choices(request, ability.Selection, false),
            OptionsPayload options => options.CounterPayment is { } counter ? CounterChoice(request, counter) : Choices(request, options.Selection, false),
            OrderingPayload ordering => Choices(request, ordering.Selection, true),
            RevealPayload => Answer(request, new AcknowledgeReveal(state.Id, decision.Id), "acknowledge-reveal"),
            NumberPayload number => Number(request, number),
            TextPayload => Text(request),
            AllocationPayload allocation => Allocate(request, allocation),
            _ => Missing("decision-family")
        };
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    private static AiProposal Priority(AiDecisionRequest request, PriorityPayload priority)
    {
        var state = request.Frame.State;
        var offers = Offers(request).ToArray();
        var candidates = offers.Where(offer => !offer.Semantics.ManaAbility && offer.Semantics.Affordability != ActionAffordability.Unavailable
                && offer.Semantics.TargetsAvailable != false)
            .Select(offer => (Offer: offer, Score: ActionScore(request, offer, offers)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Offer.Semantics.StableId, StringComparer.Ordinal).ToArray();
        foreach (var candidate in candidates) {
            var offer = candidate.Offer;
            string reason = AttemptReason(state.Turn, offer.Semantics.StableId.Length > 0 ? offer.Semantics.StableId
                : offer.Source.VisualId + ":" + offer.Action.Kind + ":" + offer.Semantics.Effect);
            // A cast which reaches payment and is cancelled must not be tried
            // forever. Activated routes receive the same conservative guard;
            // repetitive combo planning needs an explicit bounded plan later.
            if (request.History.AttemptedRoutes.Contains(new(state.Turn, offer.Set.SourceCardId, offer.Semantics.StableId))) continue;
            return Answer(request, new UseOfferedAction(state.Id, state.Decision!.Id, offer.Set.SourceCardKey, offer.Action.Id), reason);
        }
        return priority.Pass.Enabled ? Answer(request, new PassPriority(state.Id, state.Decision!.Id), "priority-no-profitable-route")
            : Missing("required-priority-route");
    }

    private static double ActionScore(AiDecisionRequest request, Offered offer, Offered[] offers)
    {
        var semantic = offer.Semantics;
        if (semantic.Effect == ActionEffectKind.Land || offer.Action.Kind == "play") return 1000;
        if (semantic.Effect == ActionEffectKind.Unknown && offer.Action.Kind != "cast") return -1;
        var state = request.Frame.State;
        if (semantic.Affordability == ActionAffordability.Unknown && !PlausiblyFunded(request, semantic, offers)) return -1;
        double value = semantic.Effect switch {
            ActionEffectKind.Permanent => 8 + CasualTableAssessment.CardValue(offer.Source),
            ActionEffectKind.Draw => 8 + Math.Min(4, semantic.EffectAmount ?? 1) * 2,
            ActionEffectKind.Token => 9 + Math.Min(5, semantic.EffectAmount ?? 1),
            ActionEffectKind.AddCounters or ActionEffectKind.Untap or ActionEffectKind.Search => 8,
            ActionEffectKind.Damage or ActionEffectKind.Destroy or ActionEffectKind.Exile or ActionEffectKind.Bounce => 6,
            ActionEffectKind.CounterSpell => state.Stack.Any(item => item.ControllerId != state.ViewerId) ? 10 : -10,
            ActionEffectKind.LifeGain => state.Viewer!.Life < 15 ? 7 : 2,
            ActionEffectKind.LifeLoss or ActionEffectKind.Discard or ActionEffectKind.Mill or ActionEffectKind.Tap => 3,
            ActionEffectKind.RemoveCounters => 2,
            ActionEffectKind.ChangeControl => 9,
            _ => offer.Action.Kind == "cast" ? 2 : -1
        };
        if (offer.Action.Kind == "cast" && offer.Source.Power is not null) value += CasualTableAssessment.CardValue(offer.Source);
        foreach (var cost in semantic.Costs) value -= cost.Kind switch {
            ActionCostKind.Life => (cost.Amount ?? 4) * (state.Viewer!.Life < 12 ? 3 : .35),
            ActionCostKind.Sacrifice => cost.FromSource ? CasualTableAssessment.CardValue(offer.Source) : (cost.Amount ?? 1) * 4,
            ActionCostKind.Discard => (cost.Amount ?? 1) * 3,
            ActionCostKind.Exile => (cost.Amount ?? 1) * 2,
            ActionCostKind.Other => 8,
            _ => 0
        };
        return value + (semantic.Affordability == ActionAffordability.Payable ? .5 : 0)
            + CasualTableAssessment.Tie(request.Seed, offer.Semantics.StableId + offer.Source.VisualId) * .1;
    }

    private static bool PlausiblyFunded(AiDecisionRequest request, ActionSemantics semantic, Offered[] offers)
    {
        long needed = semantic.Costs.Where(cost => cost.Kind == ActionCostKind.Mana)
            .Sum(cost => (long)(cost.Amount ?? 0) + cost.ManaSymbols.Count(symbol => symbol != "{X}"));
        if (needed == 0) return true;
        long floating = request.Frame.State.Viewer!.Mana.Values.Sum(value => Math.Max(0L, value));
        // Choose one production route per physical source, so a dual land is
        // never counted twice. This is an estimate; payment remains authoritative.
        var producers = offers.Where(offer => offer.Semantics.ManaAbility && offer.Semantics.ManaAmount > 0
                && !offer.Source.Tapped && offer.Semantics.Affordability != ActionAffordability.Unavailable)
            .GroupBy(offer => offer.Set.SourceCardId).ToArray();
        long production = producers.Sum(group => (long)group.Max(offer => offer.Semantics.ManaAmount));
        if (floating + production < needed) return false;
        foreach (string color in new[] { "W", "U", "B", "R", "G", "C" }) {
            int exact = semantic.Costs.Where(cost => cost.Kind == ActionCostKind.Mana)
                .Sum(cost => cost.ManaSymbols.Count(symbol => symbol.Trim('{', '}') == color));
            if (exact == 0) continue;
            long available = request.Frame.State.Viewer.Mana.GetValueOrDefault(color)
                + producers.Sum(group => (long)group.Where(offer => offer.Semantics.ManaColors.Contains(color))
                    .Select(offer => offer.Semantics.ManaAmount).DefaultIfEmpty(0).Max());
            if (available < exact) return false;
        }
        // Flexible sources, hybrid/life substitutions and coupled costs can
        // still make this optimistic. The engine owns the actual payment plan.
        return true;
    }

    private static AiProposal Opening(AiDecisionRequest request, OpeningHandPayload opening)
    {
        var state = request.Frame.State;
        var hand = state.Viewer!.Zone("Hand").Cards;
        int lands = hand.Count(CasualTableAssessment.IsLand);
        bool keep = lands >= 2 && lands <= Math.Max(4, hand.Length - 2)
            || hand.Length <= 4 || opening.Consequence is { NextHandSize: < 5 };
        if (!keep && opening.Mulligan.Enabled)
            return Answer(request, new MulliganOpeningHand(state.Id, state.Decision!.Id), "opening-seek-resources");
        return opening.Keep.Enabled ? Answer(request, new KeepOpeningHand(state.Id, state.Decision!.Id), "opening-keep-playable-hand")
            : opening.Mulligan.Enabled ? Answer(request, new MulliganOpeningHand(state.Id, state.Decision!.Id), "opening-required-redraw") : Missing("opening-answer");
    }

    private static AiProposal Payment(AiDecisionRequest request, PaymentPayload payment)
    {
        var state = request.Frame.State;
        if (payment.Pay.Enabled) return Answer(request, new PayMana(state.Id, state.Decision!.Id), "payment-commit-funded-cost");
        if (!request.Frame.Domain.PoolManaColors.IsDefaultOrEmpty) {
            string color = request.Frame.Domain.PoolManaColors
                .OrderByDescending(color => ManaFit(request.Frame.Semantics.UnpaidMana, [color]))
                .ThenBy(color => CasualTableAssessment.Tie(request.Seed, color)).First();
            return Answer(request, new SpendPoolMana(state.Id, state.Decision!.Id, color), "payment-spend-current-pool");
        }
        var source = Offers(request).Where(offer => offer.Semantics.ManaAbility && offer.Semantics.ManaAmount > 0
                && offer.Semantics.Affordability != ActionAffordability.Unavailable
                && offer.Semantics.PaymentManaColors is not { IsEmpty: true })
            .OrderByDescending(offer => ManaFit(request.Frame.Semantics.UnpaidMana, offer.Semantics.PaymentManaColors ?? offer.Semantics.ManaColors))
            .ThenBy(offer => ManaCost(offer)).ThenByDescending(offer => offer.Semantics.ManaAmount)
            .ThenBy(offer => CasualTableAssessment.Tie(request.Seed, offer.Semantics.StableId + offer.Source.VisualId)).FirstOrDefault();
        if (source != null) return Answer(request, new UseOfferedAction(state.Id, state.Decision!.Id, source.Set.SourceCardKey, source.Action.Id), "payment-use-offered-resource");
        return payment.Cancel.Enabled ? Answer(request, new CancelPayment(state.Id, state.Decision!.Id), "payment-no-funding-plan") : Missing("payment-source");
    }
    private static double ManaCost(Offered offer) => offer.Semantics.Costs.Sum(cost => cost.Kind switch {
        ActionCostKind.Sacrifice => (cost.FromSource ? CasualTableAssessment.CardValue(offer.Source) : 5) + 10,
        ActionCostKind.Life => (cost.Amount ?? 4) * 2, ActionCostKind.Discard => 15,
        ActionCostKind.RemoveCounters => 3, ActionCostKind.Other => 30, ActionCostKind.Mana => 20, _ => 0 });
    private static int ManaFit(ImmutableArray<string> unpaid, ImmutableArray<string> colors)
    {
        int colored = 0;
        foreach (string shard in unpaid) {
            string[] parts = shard.Trim('{', '}').Split('/');
            if (parts.Any(part => part is "W" or "U" or "B" or "R" or "G" or "C"))
                colored += colors.Count(color => parts.Contains(color.Trim('{', '}'))) * 10;
        }
        return colored;
    }

    private static AiProposal Select(AiDecisionRequest request, DecisionReply complete, DecisionReply cancel, SelectionRange? range, ImmutableArray<int> players)
    {
        var state = request.Frame.State;
        var intent = EffectiveIntent(request.Frame.Semantics);
        bool cost = request.Frame.Semantics.SelectionIsCost || intent is DecisionIntent.Discard or DecisionIntent.Sacrifice;
        var cards = CardViews.InZones(state).Where(item => item.Card.Selectable && item.Card.Key.Length > 0 && !item.Card.Highlighted)
            .GroupBy(item => item.Card.Key).Select(group => group.First()).ToArray();
        // Complete a staged selection as soon as the engine permits it. This
        // avoids toggling the same targets or paying an optional cost repeatedly.
        if (complete.Enabled && (range?.Min > 0 || cards.Length == 0 && players.IsEmpty))
            return Answer(request, new CompleteSelection(state.Id, state.Decision!.Id), "selection-complete");
        var options = new List<(DecisionCommand Command, double Score)>();
        foreach (var occurrence in cards) {
            var card = occurrence.Card;
            double value = CasualTableAssessment.CardValue(card);
            bool own = occurrence.PlayerId == state.ViewerId;
            double score = cost ? (own ? 100 - value : -100)
                : IsBenefit(intent) ? (own ? 100 + value : -100)
                : IsHarm(intent) ? (own ? -100 : 20 + value + CasualTableAssessment.OpponentScore(request, occurrence.PlayerId))
                : 1 + CasualTableAssessment.Tie(request.Seed, card.VisualId + card.Key);
            options.Add((new SelectCard(state.Id, state.Decision!.Id, card.Key), score));
        }
        foreach (int player in players.Where(player => !request.Frame.Semantics.SelectedPlayers.Contains(player))) {
            double score = intent == DecisionIntent.StartingPlayer ? (player == state.ViewerId ? 100 : 0)
                : IsBenefit(intent) || intent == DecisionIntent.Search ? (player == state.ViewerId ? 100 : -100)
                : IsHarm(intent) ? CasualTableAssessment.OpponentScore(request, player)
                : CasualTableAssessment.Tie(request.Seed, player.ToString(CultureInfo.InvariantCulture));
            options.Add((new SelectPlayer(state.Id, state.Decision!.Id, player), score));
        }
        var best = options.OrderByDescending(option => option.Score).FirstOrDefault();
        if (best.Command != null && (best.Score >= 0 || !complete.Enabled)) return Answer(request, best.Command, cost ? "selection-pay-lowest-opportunity-cost" : "selection-public-effect-recipient");
        if (complete.Enabled) return Answer(request, new CompleteSelection(state.Id, state.Decision!.Id), "selection-decline-optional-target");
        return cancel.Enabled ? Answer(request, new CancelSelection(state.Id, state.Decision!.Id), "selection-no-current-candidate") : Missing("required-selection");
    }

    private static AiProposal Confirmation(AiDecisionRequest request, ConfirmationPayload confirmation)
    {
        var state = request.Frame.State;
        if (confirmation.Decline == null) return Answer(request, new AcknowledgeDecision(state.Id, state.Decision!.Id), "acknowledge-required-step");
        bool decline = request.Frame.Semantics.SelectionIsCost && request.Frame.Semantics.Intent == DecisionIntent.Unknown;
        if (!decline && confirmation.Accept.Enabled) return Answer(request, new AcceptConfirmation(state.Id, state.Decision!.Id), "accept-offered-effect");
        if (confirmation.Decline.Enabled) return Answer(request, new DeclineConfirmation(state.Id, state.Decision!.Id), "decline-optional-cost");
        return confirmation.Accept.Enabled ? Answer(request, new AcceptConfirmation(state.Id, state.Decision!.Id), "accept-required-effect") : Missing("confirmation-answer");
    }

    private static AiProposal Choices(AiDecisionRequest request, ChoiceSelection selection, bool ordering)
    {
        var state = request.Frame.State;
        bool lose = request.Frame.Semantics.SelectionIsCost || request.Frame.Semantics.Intent is DecisionIntent.Discard or DecisionIntent.Sacrifice;
        var ordered = selection.Choices.Select(choice => (Choice: choice, Score: ChoiceScore(request, choice, lose)))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Choice.Index).ToArray();
        int count = ordering ? selection.Range.Max : Math.Max(selection.Range.Min, selection.Range.Max > 0 ? 1 : 0);
        var indices = ordered.Take(count).Select(item => item.Choice.Index).ToImmutableArray();
        DecisionCommand command = ordering ? new OrderOptions(state.Id, state.Decision!.Id, indices) : new ChooseOptions(state.Id, state.Decision!.Id, indices);
        return Answer(request, command, ordering ? "order-public-card-values" : "choose-offered-effect");
    }
    private static double ChoiceScore(AiDecisionRequest request, Choice choice, bool lose)
    {
        var option = request.Frame.Semantics.Options.FirstOrDefault(option => option.Index == choice.Index);
        double effect = option?.Effect switch {
            ActionEffectKind.Draw => 6 + (option.Amount ?? 1), ActionEffectKind.Token => 5 + (option.Amount ?? 1),
            ActionEffectKind.Permanent or ActionEffectKind.AddCounters or ActionEffectKind.Search => 5,
            ActionEffectKind.LifeGain => request.Frame.State.Viewer!.Life < 15 ? 8 : 2,
            ActionEffectKind.LifeLoss or ActionEffectKind.Discard => -5,
            _ => 0
        };
        double card = choice.Card == null ? 0 : CasualTableAssessment.CardValue(CardViews.Choice(request.Frame.State, choice) ?? choice.Card);
        double mana = choice.Mana.Length == 0 ? 0 : ManaFit(request.Frame.Semantics.UnpaidMana, [choice.Mana]) * 10;
        return effect + mana + (lose ? -card : card) + CasualTableAssessment.Tie(request.Seed, choice.Index.ToString(CultureInfo.InvariantCulture)) * .1;
    }

    private static AiProposal CounterChoice(AiDecisionRequest request, CounterPaymentChoice payment)
    {
        var option = payment.Options.OrderByDescending(option => option.Available - payment.Amount)
            .ThenBy(option => CasualTableAssessment.Tie(request.Seed, option.Type)).First();
        return Answer(request, new ChooseOptions(request.Frame.State.Id, request.Frame.State.Decision!.Id, [option.Index]), "payment-use-offered-counter-surplus");
    }

    private static AiProposal Number(AiDecisionRequest request, NumberPayload number)
    {
        int initial = int.TryParse(number.Initial, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : number.Range?.Min ?? 0;
        int value = number.Range is { } range ? Math.Clamp(initial, range.Min, range.Max) : initial;
        return Answer(request, new ChooseNumber(request.Frame.State.Id, request.Frame.State.Decision!.Id, value), "choose-published-number-bound");
    }
    private static AiProposal Text(AiDecisionRequest request)
    {
        var candidates = request.Frame.Semantics.LegalTextValues;
        if (candidates.IsDefaultOrEmpty) return Missing("legal-text-domain");
        string value = candidates.OrderBy(value => CasualTableAssessment.Tie(request.Seed, value)).First();
        return Answer(request, new EnterText(request.Frame.State.Id, request.Frame.State.Decision!.Id, value), "choose-published-text-domain");
    }

    private static AiProposal Allocate(AiDecisionRequest request, AllocationPayload allocation)
    {
        var state = request.Frame.State;
        int[] amounts = allocation.Recipients.Select(recipient => recipient.Minimum).ToArray();
        long remaining = allocation.Amount - amounts.Sum(amount => (long)amount);
        if (remaining < 0) return Missing("satisfiable-allocation");
        var ordered = allocation.Recipients.Select((recipient, index) => (Recipient: recipient, Index: index))
            .OrderBy(item => allocation.Kind == AllocationKind.Combat && item.Recipient.IsDefender ? 1 : 0)
            .ThenByDescending(item => item.Recipient.PlayerId is { } player ? CasualTableAssessment.OpponentScore(request, player)
                : item.Recipient.Card is { } card ? CasualTableAssessment.CardValue(card) : 0).ToArray();
        if (allocation.Kind == AllocationKind.Combat) {
            foreach (var item in ordered.Where(item => !item.Recipient.IsDefender && item.Recipient.Lethal.HasValue)) {
                int add = (int)Math.Min(remaining, Math.Max(0, Math.Min(item.Recipient.Maximum, item.Recipient.Lethal!.Value) - amounts[item.Index]));
                amounts[item.Index] += add; remaining -= add;
            }
        }
        foreach (var item in ordered.OrderBy(item => allocation.Kind == AllocationKind.Combat && item.Recipient.IsDefender ? 0 : 1)) {
            if (item.Recipient.IsDefender && allocation.Kind == AllocationKind.Combat
                && allocation.Recipients.Where((recipient, index) => !recipient.IsDefender && recipient.Lethal is { } lethal && amounts[index] < lethal).Any()) continue;
            int add = (int)Math.Min(remaining, item.Recipient.Maximum - amounts[item.Index]);
            amounts[item.Index] += add; remaining -= add;
        }
        if (remaining != 0 || AllocationConstraints.Check(allocation, amounts) != null)
            return allocation.MaySkip ? Answer(request, new SkipAllocation(state.Id, state.Decision!.Id), "allocation-no-legal-distribution") : Missing("satisfiable-allocation");
        return Answer(request, new AllocateAmounts(state.Id, state.Decision!.Id, amounts.ToImmutableArray()), "allocate-with-authoritative-bounds");
    }

    private static AiProposal Attackers(AiDecisionRequest request, AttackPayload payload)
    {
        var state = request.Frame.State;
        var combat = state.Combat!;
        if (!combat.AttackValid) return RepairAttackers(request);
        if (request.History.Decisions.Any(record => record.EpisodeId == request.Frame.EpisodeId
                && record.Outcome == AiDecisionOutcome.Accepted && record.ReasonCode is "combat-repair-remove" or "combat-repair-add"))
            return payload.Complete.Enabled ? Answer(request, new CompleteAttackers(state.Id, state.Decision!.Id), "combat-complete-repaired-plan") : Missing("combat-repair-completion");
        var assigned = combat.Attackers.Select(attack => attack.CardId).ToHashSet();
        var options = new List<(DecisionCommand Command, double Score)>();
        foreach (var attack in combat.AttackOptions.Where(attack => !assigned.Contains(attack.CardId))) {
            var attacker = CardViews.FindZoneOccurrence(state, attack.CardId)?.Card;
            if (attacker == null || attacker.Key.Length == 0) continue;
            foreach (var defender in attack.Defenders) {
                var enemy = state.Players.FirstOrDefault(player => player.Id == defender.PlayerId);
                if (enemy == null) continue;
                var blockers = enemy.Zone("Battlefield").Cards.Where(card => !card.Tapped && !card.PhasedOut && CasualTableAssessment.IsCreature(card)
                    && (!CasualTableAssessment.HasKeyword(attacker, "flying") || CasualTableAssessment.HasKeyword(card, "flying") || CasualTableAssessment.HasKeyword(card, "reach"))).ToArray();
                bool safelyBlocked = blockers.Any(blocker => (blocker.Power ?? 0) >= (attacker.Toughness ?? 1) - attacker.Damage
                    && (blocker.Toughness ?? 1) - blocker.Damage > (attacker.Power ?? 0));
                double score = safelyBlocked ? -10 : Math.Max(0, attacker.Power ?? 0) + CasualTableAssessment.OpponentScore(request, defender.PlayerId) * .1;
                if (enemy.Life <= Math.Max(0, attacker.Power ?? 0) && blockers.Length == 0) score += 10;
                if (defender.Kind == "player") options.Add((new ToggleAttackPlayer(state.Id, state.Decision!.Id, attacker.Key, defender.PlayerId), score));
                else if (CardViews.FindZoneOccurrence(state, defender.Id)?.Card is { Key.Length: > 0 } permanent)
                    options.Add((new ToggleAttackPermanent(state.Id, state.Decision!.Id, attacker.Key, permanent.Key), score + 1));
            }
        }
        var best = options.OrderByDescending(item => item.Score).FirstOrDefault();
        if (best.Command != null && (best.Score > 0 || !payload.Complete.Enabled)) return Answer(request, best.Command, "combat-public-attack-opportunity");
        return payload.Complete.Enabled ? Answer(request, new CompleteAttackers(state.Id, state.Decision!.Id), "combat-complete-attackers") : Missing("required-attacker");
    }

    private static AiProposal Blockers(AiDecisionRequest request, BlockPayload payload)
    {
        var state = request.Frame.State;
        var combat = state.Combat!;
        var assigned = combat.Attackers.SelectMany(attack => attack.BlockerIds).ToHashSet();
        long incoming = combat.Attackers.Where(attack => attack.DefendingPlayerId == state.ViewerId && !attack.Blocked)
            .Sum(attack => Math.Max(0L, CardViews.FindZoneOccurrence(state, attack.CardId)?.Card.Power ?? 0));
        var options = new List<(DecisionCommand Command, double Score)>();
        foreach (var attack in combat.Attackers.Where(attack => attack.DefendingPlayerId == state.ViewerId)) {
            if (attack.BlockerIds.Length > 0 && string.IsNullOrEmpty(combat.BlockProblem)) continue;
            var attacker = CardViews.FindZoneOccurrence(state, attack.CardId)?.Card;
            if (attacker == null || attacker.Key.Length == 0) continue;
            foreach (string id in attack.EligibleBlockerIds.Where(id => !assigned.Contains(id))) {
                var blocker = CardViews.FindZoneOccurrence(state, id)?.Card;
                if (blocker == null || blocker.Key.Length == 0) continue;
                bool survives = (blocker.Toughness ?? 1) - blocker.Damage > (attacker.Power ?? 0);
                bool kills = (blocker.Power ?? 0) >= (attacker.Toughness ?? 1) - attacker.Damage || CasualTableAssessment.HasKeyword(blocker, "deathtouch");
                double score = (survives ? 4 : -CasualTableAssessment.CardValue(blocker)) + (kills ? CasualTableAssessment.CardValue(attacker) : 0);
                if (incoming >= state.Viewer!.Life) score += 30;
                if (attack.BlockerIds.Length > 0) score -= 10;
                if (!string.IsNullOrEmpty(combat.BlockProblem)) score += attack.BlockerIds.Length > 0 ? 20 : 1;
                options.Add((new ToggleBlock(state.Id, state.Decision!.Id, attacker.Key, blocker.Key), score));
            }
        }
        var best = options.OrderByDescending(item => item.Score).ThenBy(item => CasualTableAssessment.Tie(request.Seed, item.Command.ToString())).FirstOrDefault();
        if (best.Command != null && (best.Score > 0 || !payload.Complete.Enabled || !string.IsNullOrEmpty(combat.BlockProblem)))
            return Answer(request, best.Command, "combat-public-defensive-trade");
        return payload.Complete.Enabled && string.IsNullOrEmpty(combat.BlockProblem)
            ? Answer(request, new CompleteBlockers(state.Id, state.Decision!.Id), "combat-complete-blockers") : Missing("required-block-assignment");
    }

    private static AiProposal RepairAttackers(AiDecisionRequest request)
    {
        var state = request.Frame.State;
        var combat = state.Combat!;
        foreach (var attack in combat.Attackers) {
            var desired = combat.AttackRepair.FirstOrDefault(assignment => assignment.CardId == attack.CardId);
            var current = attack.Defender ?? new Defender("player", attack.DefendingPlayerId.ToString(CultureInfo.InvariantCulture), attack.DefendingPlayerId, "");
            if (desired != null && SameDefender(current, desired.Defender)) continue;
            return AttackAssignment(request, attack.CardId, current, "combat-repair-remove");
        }
        foreach (var desired in combat.AttackRepair) {
            var current = combat.Attackers.FirstOrDefault(attack => attack.CardId == desired.CardId);
            if (current != null && SameDefender(current.Defender ?? new("player", current.DefendingPlayerId.ToString(CultureInfo.InvariantCulture), current.DefendingPlayerId, ""), desired.Defender)) continue;
            return AttackAssignment(request, desired.CardId, desired.Defender, "combat-repair-add");
        }
        return Missing("satisfiable-attack-repair");
    }
    private static bool SameDefender(Defender first, Defender second) => first.Kind == second.Kind && first.PlayerId == second.PlayerId
        && (first.Kind == "player" || first.Id == second.Id);
    private static AiProposal AttackAssignment(AiDecisionRequest request, string attackerId, Defender defender, string reason)
    {
        var state = request.Frame.State;
        var attacker = CardViews.FindZoneOccurrence(state, attackerId)?.Card;
        if (attacker is not { Key.Length: > 0 }) return Missing("attack-repair-occurrence");
        if (defender.Kind == "player")
            return Answer(request, new ToggleAttackPlayer(state.Id, state.Decision!.Id, attacker.Key, defender.PlayerId), reason);
        var permanent = CardViews.FindZoneOccurrence(state, defender.Id)?.Card;
        return permanent is { Key.Length: > 0 }
            ? Answer(request, new ToggleAttackPermanent(state.Id, state.Decision!.Id, attacker.Key, permanent.Key), reason) : Missing("attack-repair-defender");
    }

    private sealed record Offered(SourceActionSet Set, SourceAction Action, Card Source, ActionSemantics Semantics);
    private static IEnumerable<Offered> Offers(AiDecisionRequest request)
    {
        var metadata = request.Frame.Domain.Actions.ToDictionary(semantic => semantic.ActionId, StringComparer.Ordinal);
        foreach (var set in request.Frame.SourceActions) {
            var source = CardViews.FindActionSource(request.Frame.State, set.SourceCardKey)?.Card;
            if (source == null) continue;
            foreach (var action in set.Actions) {
                if (metadata.TryGetValue(action.Id, out var semantics)) yield return new(set, action, action.Card ?? source, semantics);
            }
        }
    }
    private static DecisionIntent EffectiveIntent(DecisionSemantics semantic)
    {
        bool positive = semantic.CounterType is "P1P1" or "+1/+1";
        bool negative = semantic.CounterType is "M1M1" or "-1/-1" or "POISON" or "Poison";
        return semantic.Intent switch {
            DecisionIntent.AddCounters when positive => DecisionIntent.Benefit,
            DecisionIntent.AddCounters when negative => DecisionIntent.Harm,
            DecisionIntent.RemoveCounters when positive => DecisionIntent.Harm,
            DecisionIntent.RemoveCounters when negative => DecisionIntent.Benefit,
            _ => semantic.Intent
        };
    }
    private static bool IsBenefit(DecisionIntent intent) => intent is DecisionIntent.Benefit or DecisionIntent.Untap;
    private static bool IsHarm(DecisionIntent intent) => intent is DecisionIntent.Harm or DecisionIntent.Damage or DecisionIntent.Destroy
        or DecisionIntent.Exile or DecisionIntent.Bounce or DecisionIntent.Counter or DecisionIntent.Tap;
    private static string AttemptReason(int turn, string stable) => "route-t" + turn.ToString(CultureInfo.InvariantCulture) + "-" + CasualTableAssessment.IdentityCode(stable);
    private static AiProposal Answer(AiDecisionRequest request, DecisionCommand command, string reason)
        => request.Frame.Allows(command) ? new AiProposal.Answer(command, reason) : Missing("current-command-authority");
    private static AiProposal Missing(string capability) => new AiProposal.Unavailable(capability, "The current rules domain does not offer a supported continuation.");
}
