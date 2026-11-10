using System.Collections.Immutable;

namespace Mana.Contracts;

public enum TargetRestrictionReason { Unknown, Shroud, Hexproof, Protection, Restriction }

/// <summary>Read-only explanation from the current target input, never a command or a target permission.</summary>
public sealed record TargetRestriction(string CardId, TargetRestrictionReason Reason, string Detail, string? SourceCardId = null);

public static class TargetRestrictionFacts
{
    public static ImmutableArray<TargetRestriction> ValidateRows(ImmutableArray<TargetRestriction> rows)
    {
        if (rows.IsDefault) return [];
        if (rows.Length > 256 || rows.Any(row => row == null || !PublicChoiceFacts.ValidId(row.CardId)
                || !Enum.IsDefined(row.Reason) || row.Detail == null || row.Detail.Length > 1024
                || row.Detail.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                || row.Reason != TargetRestrictionReason.Unknown && string.IsNullOrWhiteSpace(row.Detail)
                || row.SourceCardId != null && !PublicChoiceFacts.ValidId(row.SourceCardId))
            || rows.Select(row => row.CardId).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw Invalid();
        return rows;
    }

    public static void Validate(GameSnapshot state)
    {
        if (state.Decision?.Payload is not TargetSelectionPayload selection || selection.TargetRestrictions.IsEmpty) return;
        ValidateRows(selection.TargetRestrictions);
        if (!Current(state, selection)) throw Invalid();
        // Validate one publication in linear time even on token-heavy tables.
        // Duplicate identities remain ambiguous rather than choosing a face.
        var publicCards = new Dictionary<string, Card?>(StringComparer.Ordinal);
        foreach (var card in state.Players.SelectMany(player => player.Zone("Battlefield").Cards).Where(card => !card.FaceDown))
            if (!publicCards.TryAdd(card.VisualId, card)) publicCards[card.VisualId] = null;
        foreach (var row in selection.TargetRestrictions) {
            if (!publicCards.TryGetValue(row.CardId, out var target) || target == null || target.Selectable
                || row.SourceCardId != null && (!publicCards.TryGetValue(row.SourceCardId, out var source) || source == null))
                throw Invalid();
        }
    }

    // Look up only current public occurrences. Retained readers and old hit
    // faces cannot extend a prior decision's reason or granting-source identity.
    public static TargetRestriction? For(GameSnapshot state, Card card)
    {
        if (state.Decision?.Payload is not TargetSelectionPayload selection || !Current(state, selection)
            || card.FaceDown || card.Selectable || string.IsNullOrEmpty(card.VisualId)) return null;
        var row = selection.TargetRestrictions.FirstOrDefault(row => row.CardId == card.VisualId);
        if (row == null || row.Reason == TargetRestrictionReason.Unknown || PublicCard(state, row.CardId) is not { Selectable: false }
            || row.SourceCardId != null && PublicCard(state, row.SourceCardId) == null) return null;
        return row;
    }

    public static Card? PublicCard(GameSnapshot state, string cardId)
    {
        var matches = state.Players.SelectMany(player => player.Zone("Battlefield").Cards)
            .Where(card => card.VisualId == cardId && !card.FaceDown).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool Current(GameSnapshot state, TargetSelectionPayload selection) => state.Status == "playing"
        && state.ViewerId >= 0 && state.Viewer?.Eliminated == false && selection.Search == null
        && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId);
    private static InvalidOperationException Invalid() => new("Invalid current target restriction facts.");
}
