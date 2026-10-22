using Mana.Renderer;
using Mana.Magic;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record CardPlayDragOrigin(string Scope, string HitId, Pose Anchor, bool ExpandedFace);
    private CardPlayDragOrigin? cardPlayDragOrigin;
    private int CardDragThreshold => cardPlayDragOrigin is { ExpandedFace: true } ? 32 : 9;

    private void BeginCardDrag(Hit? source)
    {
        ClearCardDrag();
        if (source is not { Zone: "Hand" or "Command" } || source.Scope != Scope
            || cardPresentation.Cards.LastOrDefault(candidate => candidate.Zone == source.Zone
                && source.Id == candidate.Hit.Id && source.Scope == candidate.Hit.Scope) is not { } entry) return;
        // The reader is already above the table. Require deliberate movement,
        // then allow its card to land without escaping an invisible old face.
        bool expanded = entry.Focused && entry.Display.Contains(pressPoint) && !entry.Rest.Contains(pressPoint);
        cardPlayDragOrigin = new(source.Scope, source.Id, entry.Rest, expanded);
    }

    private bool CardPlayDragMatches(CardPlayDragOrigin origin)
        => origin.Scope == Scope && dragSource?.Scope == origin.Scope && dragSource.Id == origin.HitId;

    private bool CanCommitCardDrag => dragSource?.Card is { Key.Length: > 0 } source
        && VisibleCards().Any(card => card.Key == source.Key && SameVisibleCard(card, source))
        && (cardPlayDragOrigin is not { } origin
            || CardPlayDragMatches(origin) && !origin.Anchor.Contains(pointer)
                && Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > CardDragThreshold);

    private void ClearCardDrag() => cardPlayDragOrigin = null;

    private void DrawHandPlayArea()
    {
        if (!dragging || busy || session.NeedsRecovery || OverlayOpen || match is not { Decision.Intent: DecisionIntent.Priority } state
            || dragSource is not { Zone: "Hand" or "Command", Card: { } source } || dragSource.Scope != Scope) return;
        var card = state.Viewer?.Zone(dragSource.Zone).Cards.FirstOrDefault(card => CardViews.Same(card, source));
        if (card == null || InteractionController.Play(state, card, dragSource.Zone) == null
            && (dragSource.Zone != "Hand" || HandPlacements.Options(state, card).Length < 2)) return;
        var area = TableWorld.HandPlayArea;
        var color = ActionAccent * .35f;
        const int length = 28;
        foreach (var (x, y, dx, dy) in new[] {
            (area.Left, area.Top, 1, 1), (area.Right, area.Top, -1, 1),
            (area.Left, area.Bottom, 1, -1), (area.Right, area.Bottom, -1, -1) }) {
            canvas.Line(new(x, y), new(x + dx * length, y), color, 2);
            canvas.Line(new(x, y), new(x, y + dy * length), color, 2);
        }
    }
}
