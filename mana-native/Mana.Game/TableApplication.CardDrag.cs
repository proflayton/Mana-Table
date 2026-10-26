using Mana.Renderer;
using Mana.Magic;
using Mana.Contracts;
using Mana.Client;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record CardPlayDragOrigin(string Scope, string HitId, Pose Anchor, bool ExpandedFace, bool FromCollection);
    private sealed record ReleasedCardPlay(string Scope, SourceActionContext Context, string Zone, Pose Pose, bool Submitted);
    private CardPlayDragOrigin? cardPlayDragOrigin;
    private ReleasedCardPlay? releasedCardPlay;
    private bool AwaitingReleasedPlay => releasedCardPlay is { Submitted: false };
    private int CardDragThreshold => cardPlayDragOrigin is { ExpandedFace: true } ? 32 : 9;

    // The provider's exact current cast/play offer authorizes a cross-zone play
    // gesture. Selectable alone can also mean activation, so never infer it here.
    private SourceAction[] OfferedPlayOptions(Card card) => ReaderActionsMatch(card)
        ? sourceActions.View.Actions.Where(action => action.Kind is "cast" or "play").ToArray() : [];

    private bool CanStartSourceDrag(Hit source) => match?.Decision?.Payload is AttackPayload or BlockPayload
        || match?.Decision?.Payload is PriorityPayload && source.Zone is not ("Battlefield" or "Stack")
            && (source.Zone is "Hand" or "Command" || source.Card is { } card
                && (OfferedPlayOptions(card).Length > 0 || CurrentPlayAccess(card) is { } access && CanStartPlayAccess(access)));

    private Card? CurrentDragCard(Hit source) => source.Scope == Scope && source.Zone is { } zone && source.Card is { } held
        && CardContext.CurrentOccurrence(zone, held) is { FaceDown: false } current && current.Key == held.Key
        ? current : null;

    private DecisionCommand? PlayDragCommand(Card card, string? zone)
    {
        if (match is not { Decision.Payload: PriorityPayload } state || zone is null or "Battlefield" or "Stack") return null;
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
        cardPlayDragOrigin = new(source.Scope, source.Id, CardCollectionOpen ? entry.Display : entry.Rest, expanded, CardCollectionOpen);
    }

    private bool LeftCollectionDragOrigin => cardPlayDragOrigin is not { FromCollection: true } origin || !origin.Anchor.Contains(pointer);

    private bool CardPlayDragMatches(CardPlayDragOrigin origin)
        => origin.Scope == Scope && dragSource?.Scope == origin.Scope && dragSource.Id == origin.HitId;

    private bool CanCommitCardDrag => dragSource != null && CurrentDragCard(dragSource) is { Key.Length: > 0 }
        && (cardPlayDragOrigin is not { } origin
            || CardPlayDragMatches(origin) && !origin.Anchor.Contains(pointer)
                && Vector2.Distance(pressPoint.ToVector2(), pointer.ToVector2()) > CardDragThreshold);

    private void ClearCardDrag() => cardPlayDragOrigin = null;

    private void CancelReleasedPlayIntent()
    {
        if (AwaitingReleasedPlay) releasedCardPlay = null;
    }

    private Card? ReleasedPlaySource()
    {
        if (releasedCardPlay is not { Submitted: false } intent) return null;
        var state = match;
        var location = state == null ? null : CardViews.FindZoneActionSource(state, intent.Context.SourceCardKey);
        var card = location?.Card;
        if (!SourceActionsSupported || busy || session.NeedsRecovery || commandFailure || intent.Scope != Scope
            || state?.Decision?.Payload is not PriorityPayload || location?.Zone != intent.Zone
            || card is not { FaceDown: false } || ReaderActionContext(card) != intent.Context
            || !state.PlayAccesses.Any(access => access.PlayerId == location.Value.PlayerId && access.Zone == intent.Zone
                && access.CardId == intent.Context.SourceCardId && access.Routes.Any(route => route.CanStart))) {
            releasedCardPlay = null;
            return null;
        }
        return card;
    }

    private Pose? ReleasedPlayPose(Card card, string zone)
    {
        if (releasedCardPlay is not { } intent) return null;
        if (intent.Context.SourceCardId != CardViews.Identity(card) || intent.Zone != zone) return null;
        return intent.Submitted ? SubmittedPlayCurrent(intent) ? intent.Pose : null : ReleasedPlaySource() != null ? intent.Pose : null;
    }

    private bool SubmittedPlayCurrent(ReleasedCardPlay intent)
    {
        var state = match;
        var current = state == null ? null : CardViews.FindZoneOccurrence(state, intent.Context.SourceCardId);
        bool resolving = state is { Decision: null } && (state.Status == "resolving" || state.ActionClass == "resolving");
        bool valid = state is { Status: "playing" or "resolving" } && state.Id == intent.Context.GameId
            && state.ViewerId == intent.Context.ViewerId && !session.NeedsRecovery && !commandFailure && (busy || resolving)
            && current is { Zone: var zone, Card: { FaceDown: false } card } && zone == intent.Zone
            && (card.Key == intent.Context.SourceCardKey || resolving && card.Key.Length == 0);
        if (!valid) releasedCardPlay = null;
        return valid;
    }

    private void ReconcileReleasedPlay()
    {
        if (releasedCardPlay is not { } intent) return;
        if (intent.Submitted) SubmittedPlayCurrent(intent);
        else ReleasedPlaySource();
    }

    private bool RetainReleasedPlay()
    {
        if (!SourceActionsSupported || dragSource is not { Zone: not ("Hand" or "Command" or "Battlefield" or "Stack") } source
            || CurrentDragCard(source) is not { } card || CurrentPlayAccess(card) is not { } access || !CanStartPlayAccess(access)
            || ReaderActionContext(card) is not { } context) return false;
        releasedCardPlay = new(Scope, context, source.Zone!,
            new(pointer.ToVector2() + new Vector2(0, -72), new(132, 185), -.025f, Elevation: 18), false);
        sourceActions.Observe(context);
        return true;
    }

    // Release records the player's intent, not a cast. The one source query
    // remains alive until its exact current offer can be revalidated.
    private void ContinueReleasedPlay()
    {
        ReconcileReleasedPlay();
        if (!AwaitingReleasedPlay || ReleasedPlaySource() is not { } card || releasedCardPlay is not { } intent) return;
        if (sourceActions.View.Context != intent.Context || sourceActions.View.Status is SourceActionStatus.Idle or SourceActionStatus.Loading) return;
        if (!ReaderActionsMatch(card)) {
            releasedCardPlay = null;
            if (sourceActions.View.Status == SourceActionStatus.Failed) error = sourceActions.View.Error;
            return;
        }
        var options = OfferedPlayOptions(card);
        if (options.Length > 1) {
            releasedCardPlay = null;
            OpenDraggedPlayChoices(card, intent.Zone);
            return;
        }
        releasedCardPlay = null;
        if (options.Length == 1 && sourceActions.TryCreateCommand(intent.Context, options[0].Id) is { } reply && Send(reply))
            releasedCardPlay = intent with { Scope = Scope, Submitted = true };
    }

    private void DrawHandPlayArea()
    {
        if (!dragging || busy || session.NeedsRecovery || OverlayOpen || match is not { Decision.Payload: PriorityPayload } state
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
