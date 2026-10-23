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

    // The provider's exact current cast/play offer authorizes a cross-zone play
    // gesture. Selectable alone can also mean activation, so never infer it here.
    private SourceAction[] OfferedPlayOptions(Card card) => ReaderActionsMatch(card)
        ? sourceActions.View.Actions.Where(action => action.Kind is "cast" or "play").ToArray() : [];

    private bool CanStartSourceDrag(Hit source) => match?.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block
        || source.Zone is not ("Battlefield" or "Stack")
            && (source.Zone is "Hand" or "Command" || source.Card is { } card && OfferedPlayOptions(card).Length > 0);

    private Card? CurrentDragCard(Hit source) => source.Scope == Scope && source.Zone is { } zone && source.Card is { } held
        && CardContext.CurrentOccurrence(zone, held) is { FaceDown: false } current && current.Key == held.Key
        ? current : null;

    private DecisionCommand? PlayDragCommand(Card card, string? zone)
    {
        if (match is not { Decision.Intent: DecisionIntent.Priority } state || zone is null or "Battlefield" or "Stack") return null;
        var options = OfferedPlayOptions(card);
        if (options.Length == 1 && ReaderActionContext(card) is { } context)
            return sourceActions.TryCreateCommand(context, options[0].Id);
        if (options.Length > 1) return null;
        var placements = zone == "Hand" ? HandPlacements.Options(state, card) : [];
        if (placements.Length > 0)
            return placements.Length == 1 ? HandPlacements.Start(state, card, placements[0].Id) : null;
        return !ReaderActionsMatch(card) && zone is ("Hand" or "Command")
            ? InteractionController.Play(state, card, zone) : null;
    }

    private bool OpenDraggedPlayChoices(Card card, string? zone)
    {
        if (zone is null or "Battlefield" or "Stack" || OfferedPlayOptions(card).Length < 2
            || matchPresentation.Cards.Cards.LastOrDefault(entry => entry.Zone == zone && SameVisibleCard(entry.Card, card)) is not { } entry) return false;
        return matchPresentation.Cards.Toggle(entry.Hit, CardContext with { Dragging = false });
    }

    private void BeginCardDrag(Hit? source)
    {
        ClearCardDrag();
        if (source == null || source.Zone is "Battlefield" or "Stack" || source.Scope != Scope
            || matchPresentation.Cards.Cards.LastOrDefault(candidate => candidate.Zone == source.Zone
                && source.Id == candidate.Hit.Id && source.Scope == candidate.Hit.Scope) is not { } entry) return;
        // The reader is already above the table. Require deliberate movement,
        // then allow its card to land without escaping an invisible old face.
        bool expanded = entry.Focused && entry.Display.Contains(pressPoint) && !entry.Rest.Contains(pressPoint);
        cardPlayDragOrigin = new(source.Scope, source.Id, entry.Rest, expanded);
    }

    private bool CardPlayDragMatches(CardPlayDragOrigin origin)
        => origin.Scope == Scope && dragSource?.Scope == origin.Scope && dragSource.Id == origin.HitId;

    private bool CanCommitCardDrag => dragSource != null && CurrentDragCard(dragSource) is { Key.Length: > 0 }
        && (cardPlayDragOrigin is not { } origin
            || CardPlayDragMatches(origin) && !origin.Anchor.Contains(pointer)
                && Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > CardDragThreshold);

    private void ClearCardDrag() => cardPlayDragOrigin = null;

    private void DrawHandPlayArea()
    {
        if (!dragging || busy || session.NeedsRecovery || OverlayOpen || match is not { Decision.Intent: DecisionIntent.Priority } state
            || dragSource is not { Card: { } source } || dragSource.Zone is "Battlefield" or "Stack" || dragSource.Scope != Scope) return;
        var card = CurrentDragCard(dragSource);
        if (card == null || PlayDragCommand(card, dragSource.Zone) == null && OfferedPlayOptions(card).Length < 2
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
