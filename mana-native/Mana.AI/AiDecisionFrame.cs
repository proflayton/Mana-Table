using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Planning;

namespace Mana.AI;

public enum AiActionCoverage { NotApplicable, Partial, Complete, Unsupported }
public readonly record struct AiDecisionStamp(string GameId, int SeatId, string DecisionId,
    long Revision, long BoardRevision, long OfferVersion);

/// <summary>
/// One authorized seat observation and its offered actions. Completeness is an
/// explicit provider claim, never inferred from Selectable or auto-pass hints.
/// No copied rules objects, reconstructed card rules or speculative action IDs.
/// </summary>
public sealed class AiDecisionFrame
{
    public GameSnapshot State { get; }
    public AiDecisionStamp Stamp { get; }
    public AiTask Task { get; }
    public AiActionCoverage ActionCoverage { get; }
    public ImmutableArray<SourceActionSet> SourceActions { get; }
    public DecisionActionDomain Domain { get; }
    public DecisionSemantics Semantics { get; }
    public string ExecutionBudgetRootId => ExecutionFrames.Find(State, State.Decision?.ExecutionId)?.BudgetRootId ?? "";
    public string EpisodeId => ExecutionBudgetRootId.Length > 0 ? "execution:" + ExecutionBudgetRootId
        : Semantics.EpisodeId.Length > 0 ? "causal:" + Semantics.EpisodeId : "input:" + Stamp.DecisionId;

    public AiDecisionFrame(GameSnapshot state, long offerVersion = 0,
        AiActionCoverage actionCoverage = AiActionCoverage.Partial, ImmutableArray<SourceActionSet> sourceActions = default,
        DecisionActionDomain? domain = null, DecisionSemantics? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.Id) || state.Decision is not { } decision || state.Status != "playing"
            || state.Viewer is not { Eliminated: false } || offerVersion < 0 || state.Revision < 0 || state.BoardRevision < 0
            || state.DecisionOwnerId is { } owner && owner != state.ViewerId
            || decision.Payload is PriorityPayload && state.PriorityPlayerId is { } responder && responder != state.ViewerId)
            throw new ArgumentException("An AI frame needs a live, current decision belonging to its viewing seat.", nameof(state));
        if (!Enum.IsDefined(actionCoverage)) throw new ArgumentOutOfRangeException(nameof(actionCoverage));
        State = state; Task = AiTasks.From(decision.Payload);
        ExecutionFrames.Validate(state);
        Domain = domain ?? DecisionActionDomain.None;
        Semantics = semantics ?? new();
        if (Semantics.EpisodeId == null || Semantics.EpisodeId.Length > 256 || !Enum.IsDefined(Semantics.Intent)
            || Semantics.Options.IsDefault || Semantics.LegalTextValues.IsDefault || Semantics.UnpaidMana.IsDefault
            || Semantics.SelectedPlayers.IsDefault || Semantics.CounterType == null
            || Semantics.Options.Any(option => option == null || option.Index < 0 || !Enum.IsDefined(option.Effect))
            || Semantics.Options.Select(option => option.Index).Distinct().Count() != Semantics.Options.Length
            || Semantics.LegalTextValues.Any(value => value == null) || Semantics.UnpaidMana.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Decision semantics must be explicit immutable provider facts.", nameof(semantics));
        if (state.ActionSources.IsDefault || state.ActionSources.Any(source => !CardViews.IsCurrentActionSource(state, source))
            || state.ActionSources.Select(source => source.Card.Key).Distinct(StringComparer.Ordinal).Count() != state.ActionSources.Length)
            throw new ArgumentException("Additional action sources require unique current physical occurrences and the exact owned decision.", nameof(state));
        if (domain != null) {
            offerVersion = domain.Version;
            sourceActions = domain.Sources;
            actionCoverage = domain.Coverage switch {
                ActionDomainCoverage.Complete => AiActionCoverage.Complete,
                ActionDomainCoverage.Unsupported => AiActionCoverage.Unsupported,
                ActionDomainCoverage.NotApplicable => AiActionCoverage.NotApplicable,
                _ => throw new ArgumentException("Unknown action domain coverage.", nameof(domain))
            };
            if (domain.Version < 0 || domain.Sources.IsDefault || domain.Actions.IsDefault
                || domain.Sources.Any(source => source == null || source.Actions.IsDefault)
                || domain.PoolManaColors.IsDefault || domain.PoolManaColors.Distinct().Count() != domain.PoolManaColors.Length
                || domain.PoolManaColors.Any(color => color is not ("W" or "U" or "B" or "R" or "G" or "C"))
                || !domain.PoolManaColors.IsEmpty && decision.Payload is not PaymentPayload
                || domain.Actions.Any(action => action == null || !Enum.IsDefined(action.Effect) || !Enum.IsDefined(action.Affordability)
                    || action.Effects.IsDefault || action.Effects.Any(effect => !Enum.IsDefined(effect)) || action.Costs.IsDefault
                    || action.Costs.Any(cost => cost == null || !Enum.IsDefined(cost.Kind) || cost.Amount < 0 || cost.ManaSymbols.IsDefault)
                    || action.ManaColors.IsDefault || action.ManaColors.Any(color => color is not ("W" or "U" or "B" or "R" or "G" or "C"))
                    || action.PaymentManaColors is { } applicable && (applicable.IsDefault
                        || applicable.Any(color => color is not ("W" or "U" or "B" or "R" or "G" or "C"))) || action.ManaAmount < 0 || action.ManaValue < 0)
                || domain.Actions.Select(action => action.ActionId).Distinct().Count() != domain.Actions.Length
                || !domain.Actions.Select(action => action.ActionId).Order().SequenceEqual(
                    domain.Sources.SelectMany(source => source.Actions).Select(action => action.Id).Order()))
                throw new ArgumentException("Domain semantics must describe each exact offered action once.", nameof(domain));
        }
        AiSemanticValidation.Validate(state, Domain, Semantics);
        if (decision.Payload is AttackPayload or BlockPayload && state.Combat == null)
            throw new ArgumentException("Combat decisions require their published combat constraints.", nameof(state));
        if (state.Combat?.Attackers.Any(attack => !attack.HasDefender && attack.Defender != null) == true)
            throw new ArgumentException("An attack without a defender cannot name a current target.", nameof(state));
        if (decision.Payload is AttackPayload && state.Combat is { } combat
            && (combat.AttackRepair.IsDefault || combat.AttackValid && !combat.AttackRepair.IsEmpty
                || combat.AttackRepair.Any(assignment => assignment == null || string.IsNullOrWhiteSpace(assignment.CardId)
                    || assignment.Defender == null || !combat.AttackOptions.Any(option => option.CardId == assignment.CardId
                        && option.Defenders.Any(defender => defender.Kind == assignment.Defender.Kind
                            && defender.Id == assignment.Defender.Id && defender.PlayerId == assignment.Defender.PlayerId)))
                || combat.AttackRepair.Select(assignment => assignment.CardId).Distinct().Count() != combat.AttackRepair.Length))
            throw new ArgumentException("Attack repairs must be complete, current legal candidate assignments.", nameof(state));
        Stamp = new(state.Id, state.ViewerId, decision.Id, state.Revision, state.BoardRevision, offerVersion);
        bool usesSources = decision.Payload is PriorityPayload or PaymentPayload;
        SourceActions = sourceActions.IsDefault ? [] : sourceActions;
        if (!usesSources && (!SourceActions.IsEmpty || actionCoverage is AiActionCoverage.Complete or AiActionCoverage.Unsupported)
            || usesSources && actionCoverage == AiActionCoverage.NotApplicable
            || actionCoverage == AiActionCoverage.Unsupported && !SourceActions.IsEmpty)
            throw new ArgumentException("Source-action coverage must describe the current kind of decision.");
        ActionCoverage = usesSources ? actionCoverage : AiActionCoverage.NotApplicable;
        if (SourceActions.Any(set => set == null) || SourceActions.Select(set => set.SourceCardKey).Distinct().Count() != SourceActions.Length)
            throw new ArgumentException("Source catalogs must be present and unique.", nameof(sourceActions));
        foreach (var set in SourceActions) {
            if (string.IsNullOrWhiteSpace(set.SourceCardKey)) throw new ArgumentException("A source catalog needs its current action key.", nameof(sourceActions));
            var source = CardViews.FindActionSource(state, set.SourceCardKey)?.Card;
            if (set.GameId != state.Id || set.DecisionId != decision.Id || source == null || source.FaceDown && domain == null
                || string.IsNullOrWhiteSpace(set.SourceCardId) || set.SourceCardId != CardViews.Identity(source)
                || set.Actions.IsDefault || set.Actions.Any(action => action == null || string.IsNullOrWhiteSpace(action.Id)
                    || string.IsNullOrWhiteSpace(action.Kind) || string.IsNullOrWhiteSpace(action.Text)
                    || action.Card is { } linked && (linked.FaceDown || linked.Key.Length > 0 || linked.Selectable || linked.Highlighted
                        || string.IsNullOrWhiteSpace(linked.Name) || string.IsNullOrWhiteSpace(action.Zone)))
                || set.Actions.Select(action => action.Id).Distinct().Count() != set.Actions.Length)
                throw new ArgumentException("Source catalogs must name this observation's exact disclosed source and offers.", nameof(sourceActions));
        }
    }

    public bool Allows(DecisionCommand? command)
    {
        if (command == null || !GameCommandPermissions.Allows(State, command)) return false;
        // A legacy click can open an unspecified ability chooser. A policy must
        // choose the offered route explicitly, including while paying mana.
        if (command is PlayCard or ActivateCard or UseManaSource) return false;
        if (command is UseOfferedAction offered)
            return SourceActions.Any(set => set.SourceCardKey == offered.Card && set.Actions.Any(action => action.Id == offered.ActionId))
                && (offered.ManaOutput == null || Domain.Actions.Any(action => action.ActionId == offered.ActionId && action.ManaAbility
                    && ManaFundingFacts.MatchesOutput(action.Production, offered.ManaOutput)));
        if (command is SpendPoolMana pool) return Domain.PoolManaColors.Contains(pool.Color);
        if (command is PassPriority or AutoPassPriority or PassTurn or PassForNow)
            return ActionCoverage == AiActionCoverage.Complete;
        return true;
    }
}
