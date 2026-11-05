namespace Mana.Contracts;

// Closed, non-identifying reasons. A violation describes a refused public
// inspection context, never the context token or a private decision's contents.
public enum PublicInspectionViolation
{
    None, InvalidContextId, BoardRevisionMismatch, UnknownPlayer, DecisionOwnerMismatch,
    PriorityPlayerMismatch, NonPriorityAction, NotPlaying, MissingLocalPriority
}

public static class PublicInspectionFacts
{
    // Keep this predicate independent of Forge and identical for every provider
    // and consumer. Missing metadata is supported by older hosts, not a grant.
    public static PublicInspectionViolation Violation(GameSnapshot state)
    {
        if (state.PublicInspection is not { } context) return PublicInspectionViolation.None;
        if (string.IsNullOrWhiteSpace(context.Id) || context.Id.Length > 128 || context.Id.Any(char.IsControl))
            return PublicInspectionViolation.InvalidContextId;
        if (context.BoardRevision != state.BoardRevision) return PublicInspectionViolation.BoardRevisionMismatch;
        if (!state.Players.Any(player => player.Id == context.PlayerId)) return PublicInspectionViolation.UnknownPlayer;
        if (state.DecisionOwnerId != context.PlayerId) return PublicInspectionViolation.DecisionOwnerMismatch;
        if (state.PriorityPlayerId != context.PlayerId) return PublicInspectionViolation.PriorityPlayerMismatch;
        if (state.ActionClass != "priority") return PublicInspectionViolation.NonPriorityAction;
        if (state.Status != "playing") return PublicInspectionViolation.NotPlaying;
        if (context.PlayerId == state.ViewerId && state.Decision?.Payload is not PriorityPayload)
            return PublicInspectionViolation.MissingLocalPriority;
        return PublicInspectionViolation.None;
    }

    public static bool IsCurrent(GameSnapshot state)
        => state.PublicInspection != null && Violation(state) == PublicInspectionViolation.None;
}
