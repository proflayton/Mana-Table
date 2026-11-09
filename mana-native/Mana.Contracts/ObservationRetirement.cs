namespace Mana.Contracts;

// Closed, identity-free diagnostics for private failure evidence. These facts
// carry no provider payload, card, player, game, decision or command identifiers.
public enum ObservationRetirementReason { AudienceChanged, CommandAdmitted, RevisionBehind, PublicationChanged }

public sealed record ObservationRetirement(ObservationRetirementReason Reason,
    long ReadAudience, long CurrentAudience, long ReadAdmission, long CurrentAdmission,
    long? ReplyRevision, long? CachedRevision, bool? NullReply);
