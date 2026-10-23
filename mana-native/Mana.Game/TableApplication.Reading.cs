using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static readonly Color HoverAccent = new(229, 240, 246);
    private Hit? pointerSurface;
    private bool IsControlHovered(string id) => !dragging && pointerSurface?.Id == id && CurrentHit(pointerSurface);
    // These callbacks compose draw commands and are drained before publication.
    // The card owner and the final GPU frame contain no painting callbacks.
    private readonly List<(PresentedCard Card, Action<Pose, bool> Compose)> deferredCardComposition = [];
    private (string Scope, int Slot)? inspectedChoice;
    private bool ReadingGalleryOpen => overlay.Length == 0 && zoneName == null && DecisionModal
        && match?.Decision?.Kind is "choice" or "reveal";
    private bool ZoneReadingOpen => overlay.Length == 0
        && !updatesOpen && !ActivityOpen && !ExperienceOpen
        && zoneName != null && zoneName != "Battlefield";
    private bool CompactCardDecision => !AbilityActionPending && (TableChoice
        || match?.Decision?.Intent is DecisionIntent.Selection or DecisionIntent.Attack or DecisionIntent.Block);
    private CardPresentationContext CardContext => new(match, Scope, OverlayOpen && !BattlefieldFocused && !ZoneReadingOpen,
        ReadingGalleryOpen, dragging, CompactCardDecision, AbilityActionPending, SourceActionsSupported, TableStudyEnabled,
        TableStudyEnabled ? StudyActionArea : null);
    private string? readingId => matchPresentation.Cards.PinnedId;
    private Rectangle? hoverPreview => matchPresentation.Cards.ReaderBounds;
    internal bool InteractivePointer => !dragging && HitAt(pointer) is { } hit
        && hit.ShowPointer
        && (hit.Card is not { } card || !card.FaceDown || IsCardActionable(card));

    private void ExtendCardHit(Card card, Rectangle bounds) => hits.ExtendCanonical(
        "card:" + card.Key + ":" + card.VisualId, Scope, bounds);

    private Hit? TableHitAt(Point point)
    {
        if (ActivityOpen) return hits.LastOrDefault(hit => hit.Intent.ScopeKind == InteractionScopeKind.Activity && hit.Contains(point) && CurrentHit(hit));
        if (updatesOpen) return hits.LastOrDefault(hit => hit.Intent.ScopeKind == InteractionScopeKind.Updates && hit.Contains(point) && CurrentHit(hit));
        if (ExperienceOpen) return hits.LastOrDefault(hit => hit.Intent.ScopeKind is InteractionScopeKind.Experience or InteractionScopeKind.Updates && hit.Contains(point) && CurrentHit(hit));
        if (BattlefieldFocused && hits.LastOrDefault(hit => hit.Contains(point)) is { } focusHit
            && focusHit.Intent is MatchIntent.FocusSurface or MatchIntent.CloseBattlefieldFocus or MatchIntent.SetBattlefieldOwner
                or MatchIntent.ScrollBattlefieldTo or MatchIntent.ReadFocusedCard or MatchIntent.OpenStack) return focusHit;
        return matchPresentation.Cards.ResolveHit(point, hits, hits.LastOrDefault(hit => hit.Contains(point)), CardContext);
    }

    private void BeginCardReading(float frameElapsed)
    {
        // Resolve hover against the last published frame before starting the next
        // frame. Preparation then uses current snapshot visibility and policy.
        pointerSurface = HitAt(pointer);
        string? hover = pointerSurface?.Id;
        matchPresentation.Cards.BeginFrame(CardContext, hover, frameElapsed, preferences.ReducedMotion, boardView);
        deferredCardComposition.Clear();
        sourceReaderControls.Clear(); sourceReaderActionsBounds = Rectangle.Empty; sourceReaderScrollMax = 0;
        battlefieldReaderStateBounds = Rectangle.Empty;
    }
    private void EndCardReading() => matchPresentation.Cards.EndFrame();
    private bool ToggleCardReader(Hit hit) => matchPresentation.Cards.Toggle(hit, CardContext);
    private bool ReadingAbilitySource(Decision decision)
    {
        if (!AbilityActionPending || match == null || matchPresentation.Cards.Pinned is not { Zone: "Battlefield" } entry) return false;
        string id = decision.SourceCardId.Length > 0 ? decision.SourceCardId
            : decision.SourceCard is { VisualId.Length: > 0 } source ? source.VisualId : decision.SourceCard?.CombatId ?? "";
        return CardViews.FindZoneOccurrence(match, id) is { Zone: "Battlefield", Card: { FaceDown: false } card }
            && SameVisibleCard(card, entry.Card);
    }
    private Card? ChoiceFace(int slot) => CardContext.ChoiceFace(slot);
    private Card? RefreshInspectedCard()
    {
        if (inspectedChoice is { } choice) return choice.Scope == Scope && ChoiceFace(choice.Slot) is { FaceDown: false } face ? face : null;
        return inspected == null ? null : VisibleCards().FirstOrDefault(card => SameVisibleCard(card, inspected));
    }

    private Pose DrawTableCard(string id, Card card, Pose rest, string zone, int? owner,
        string hitId, UiIntent activate, Action<Pose, bool> paint, Vector2? entrance = null, float entranceDelay = 0,
        bool opensZone = false, int choiceSlot = -1, Rectangle? activationBounds = null)
    {
        var entry = matchPresentation.Cards.Prepare(new(id, card, rest, zone, owner, hitId, activate,
            entrance, entranceDelay, opensZone, choiceSlot, activationBounds));
        hits.Add(entry.Hit);
        if (entry.Layer == CardPresentationLayer.Table) ComposeCard(entry, paint);
        else deferredCardComposition.Add((entry, paint));
        return entry.Display;
    }

    private void ComposeCard(PresentedCard entry, Action<Pose, bool> compose)
    {
        bool liveReader = DrawSourceActionReader(entry);
        if (!liveReader) compose(entry.Display, entry.Focused);
        if (entry.Hovered && !liveReader) canvas.CardFocus(entry.Display, HoverAccent);
        if (Automated) matchPresentation.Cards.RecordComposition(entry);
    }
    private void DrawLiftedCards()
    {
        var pending = deferredCardComposition.OrderBy(item => item.Card.Layer).ToArray();
        deferredCardComposition.Clear();
        foreach (var (entry, compose) in pending) {
            ComposeCard(entry, compose);
            // Labels may extend the face while composing. Promote that same
            // canonical hit rather than replacing it with the original face.
            hits.PromoteCanonical(entry.Hit.Id, entry.Hit.Scope, entry.Hit);
        }
    }
    private void DrawReaderControls()
    {
        var entry = matchPresentation.Cards.Pinned;
        if (entry == null || CompactCardDecision || !CardContext.CanRead(entry)) return;
        var target = (entry.ReaderTarget ?? CardPresentationController.ReaderPose(entry.Rest, entry.Zone)).Bounds;
        if (Vector2.Distance(entry.Display.Size, target.Size.ToVector2()) > 2
            || Vector2.Distance(entry.Display.Center, target.Center.ToVector2()) > 2) return;
        if (CardContext.CanBrowseOpeningHand(entry)) return;
        if (SourceActionsSupported) { DrawSourceReaderControls(entry); return; }
        if (match?.Decision?.Intent is DecisionIntent.Priority or DecisionIntent.Mana && IsCardActionable(entry.Card)) {
            string label = entry.Zone == "Battlefield" ? "Abilities"
                : !entry.Card.Selectable && HandPlacements.Options(match, entry.Card).Length > 0 ? "Place"
                : entry.Card.Type.Contains("Land", StringComparison.Ordinal) ? "Play" : "Cast";
            Button("read-card-action", label, target.X + 8, Math.Min(840, target.Bottom - 2),
                Math.Max(90, canvas.TextWidth(label, .76f) + 24), new MatchIntent.UseReaderCard(entry.Card), !busy, height: 30);
        }
    }

    private void ClearCardComposition()
    {
        matchPresentation.Cards.ClearComposition(); deferredCardComposition.Clear();
    }
    private void ClearCardReading()
    {
        matchPresentation.Cards.Reset(); deferredCardComposition.Clear(); inspectedChoice = null;
    }
    private void CollapseCardReading() => matchPresentation.Cards.Collapse(HitAt(pointer)?.Id);
}
