using System.Collections.Immutable;
using Mana.Contracts;

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

    public AiDecisionFrame(GameSnapshot state, long offerVersion = 0,
        AiActionCoverage actionCoverage = AiActionCoverage.Partial, ImmutableArray<SourceActionSet> sourceActions = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.Id) || state.Decision is not { } decision || state.Status != "playing"
            || state.Viewer is not { Eliminated: false } || offerVersion < 0 || state.Revision < 0 || state.BoardRevision < 0
            || state.DecisionOwnerId is { } owner && owner != state.ViewerId
            || decision.Payload is PriorityPayload && state.PriorityPlayerId is { } responder && responder != state.ViewerId)
            throw new ArgumentException("An AI frame needs a live, current decision belonging to its viewing seat.", nameof(state));
        if (!Enum.IsDefined(actionCoverage)) throw new ArgumentOutOfRangeException(nameof(actionCoverage));
        State = state; Task = AiTasks.From(decision.Payload);
        if (decision.Payload is AttackPayload or BlockPayload && state.Combat == null)
            throw new ArgumentException("Combat decisions require their published combat constraints.", nameof(state));
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
            var source = CardViews.FindZoneActionSource(state, set.SourceCardKey)?.Card;
            if (set.GameId != state.Id || set.DecisionId != decision.Id || source == null || source.FaceDown
                || string.IsNullOrWhiteSpace(set.SourceCardId) || set.SourceCardId != source.VisualId
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
            return SourceActions.Any(set => set.SourceCardKey == offered.Card && set.Actions.Any(action => action.Id == offered.ActionId));
        if (command is PassPriority or AutoPassPriority or PassTurn or PassForNow)
            return ActionCoverage == AiActionCoverage.Complete;
        return true;
    }
}
