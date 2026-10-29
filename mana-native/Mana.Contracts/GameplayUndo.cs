using System.Text.Json.Serialization;

namespace Mana.Contracts;

// A provider-certified capability, never an inverse inferred from a card or a snapshot.
public sealed record UndoOffer([property: JsonRequired] string Id, [property: JsonRequired] string ActionId,
    [property: JsonRequired] string ExecutionId, [property: JsonRequired] string DecisionId,
    [property: JsonRequired] int PlayerId, [property: JsonRequired] string SourceCardId,
    [property: JsonRequired] string SourceName, [property: JsonRequired] long BarrierVersion);

// Retained beyond the bounded activity window. Historical references grant no action authority.
public sealed record ActionRestoration([property: JsonRequired] long Version, [property: JsonRequired] string ActionId,
    [property: JsonRequired] string ExecutionId, [property: JsonRequired] int PlayerId);
public sealed record UndoState([property: JsonRequired] long BarrierVersion, [property: JsonRequired] long RestorationVersion,
    [property: JsonRequired] UndoOffer? Offer, [property: JsonRequired] ActionRestoration? LastRestoration);

public sealed record UndoAction(string GameId, string DecisionId, string OfferId, string ActionId, long BarrierVersion)
    : DecisionCommand(GameId, DecisionId);

public static class UndoFacts
{
    public static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

    public static bool CanUndo(GameSnapshot state)
    {
        try { Validate(state); return state.Undo?.Offer != null; }
        catch (ArgumentException) { return false; }
    }

    public static UndoAction? Command(GameSnapshot state) => CanUndo(state) && state.Undo?.Offer is { } offer
        ? new(state.Id, offer.DecisionId, offer.Id, offer.ActionId, offer.BarrierVersion) : null;

    public static void Validate(GameSnapshot state)
    {
        if (state.Undo is not { } undo) return;
        if (undo.BarrierVersion < 0 || undo.RestorationVersion < 0
            || undo.RestorationVersion == 0 && undo.LastRestoration != null
            || undo.RestorationVersion > 0 && (undo.LastRestoration is not { } restored
                || restored.Version != undo.RestorationVersion || !Identifier(restored.ActionId) || !Identifier(restored.ExecutionId)
                || !state.Players.Any(player => player.Id == restored.PlayerId))) throw Invalid();
        if (undo.Offer is not { } offer) return;
        if (!Identifier(offer.Id) || !Identifier(offer.ActionId) || !Identifier(offer.ExecutionId)
            || !Identifier(offer.DecisionId) || !Identifier(offer.SourceCardId)) throw Invalid();
        var source = CardViews.FindZoneOccurrence(state, offer.SourceCardId);
        var execution = ExecutionFrames.Find(state, offer.ExecutionId);
        if (string.IsNullOrWhiteSpace(offer.SourceName) || offer.SourceName.Length > 512
            || offer.SourceName.Any(char.IsControl) || offer.BarrierVersion != undo.BarrierVersion
            || state.Status != "playing" || state.Viewer is not { Eliminated: false }
            || state.Decision is not { Payload: PriorityPayload } decision || offer.DecisionId != decision.Id
            || offer.PlayerId != state.ViewerId || state.DecisionOwnerId is { } owner && owner != state.ViewerId
            || state.PriorityPlayerId is { } priority && priority != state.ViewerId
            || source is not { Zone: "Battlefield", Card.FaceDown: false }
            || source.Value.PlayerId != state.ViewerId || source.Value.Card.VisualId != offer.SourceCardId || source.Value.Card.Name != offer.SourceName
            || execution is not { Kind: ExecutionKind.Mana, Step: ExecutionStep.Complete, Outcome: ExecutionOutcome.Resolved }
            || execution.ActorPlayerId != offer.PlayerId || execution.SourceCardId != offer.SourceCardId) throw Invalid();
    }

    // Publication counters still increase when rules state is restored. Missing
    // capability metadata cannot silently erase a previously observed restore.
    public static void ValidateProgress(GameSnapshot? previous, GameSnapshot? current)
    {
        if (current != null) Validate(current);
        if (previous == null || current == null || current.Id != previous.Id || current.ViewerId != previous.ViewerId) return;
        if (RestoredSince(previous, current) && (current.Revision <= previous.Revision || current.BoardRevision <= previous.BoardRevision)) throw Invalid();
        if (previous.Undo is not { } before) return;
        if (current.Undo is not { } after || after.BarrierVersion < before.BarrierVersion
            || after.RestorationVersion < before.RestorationVersion
            || after.RestorationVersion == before.RestorationVersion && after.LastRestoration != before.LastRestoration
            || after.RestorationVersion > before.RestorationVersion && (after.BarrierVersion <= before.BarrierVersion
                || current.Revision <= previous.Revision || current.BoardRevision <= previous.BoardRevision)
            || after.RestorationVersion > before.RestorationVersion && previous.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } oldGrant
                && current.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } grant && oldGrant.GrantId == grant.GrantId
            || before.Offer is { } oldOffer && after.Offer is { } offer && oldOffer.Id == offer.Id && oldOffer != offer) throw Invalid();
    }

    public static bool RestoredSince(GameSnapshot? previous, GameSnapshot current) => current.Undo is { RestorationVersion: > 0 } after
        && (previous == null || previous.Id != current.Id || previous.ViewerId != current.ViewerId
            || after.RestorationVersion > (previous.Undo?.RestorationVersion ?? 0));

    private static ArgumentException Invalid() => new("Gameplay Undo requires exact current certified offers and monotonic retained restoration facts.");
}
