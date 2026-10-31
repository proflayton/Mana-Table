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
        && decisions.Presentation?.RequiredSurface == DecisionRequiredSurface.Gallery;
    private bool ZoneReadingOpen => overlay.Length == 0
        && !updatesOpen && !ActivityOpen && !ExperienceOpen
        && zoneName != null && zoneName != "Battlefield";
    private bool CompactCardDecision => decisions.Presentation?.RequiredSurface is DecisionRequiredSurface.Table
        or DecisionRequiredSurface.Targets or DecisionRequiredSurface.Combat;
    private CardPresentationContext CardContext => new(match, Scope, OverlayOpen && !BattlefieldFocused && !ZoneReadingOpen && !CardCollectionOpen,
        ReadingGalleryOpen, dragging, CompactCardDecision, TableStudyEnabled,
        TableStudyEnabled ? StudyActionArea : null, decisions.Presentation,
        spreadCollection as CardCollectionTarget.PublicZone, RulesReaderSize(), CurrentManaReading, ReaderControlReservations);
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
        if (BattlefieldFocused && hits.LastOrDefault(hit => hit.Contains(point)) is { Zone: "BattlefieldFocus", Intent: MatchIntent.UseCard } candidate)
            return candidate;
        var top = hits.LastOrDefault(hit => hit.Contains(point));
        var resolved = matchPresentation.Cards.ResolveHit(point, hits, top, CardContext);
        if (CardCollectionOpen && resolved?.Card is { } spreadCard && SpreadContains(CardViews.Identity(spreadCard))
            && !resolved.Contains(point)) return top;
        return resolved?.Zone == "Battlefield" && !BattlefieldFocused && resolved.Id != matchPresentation.Cards.Pinned?.Hit.Id
            && !BoardViewport.Contains(point) ? null : resolved;
    }

    private void BeginCardReading(float frameElapsed)
    {
        // Resolve hover against the last published frame before starting the next
        // frame. Preparation then uses current snapshot visibility and policy.
        pointerSurface = HitAt(pointer);
        manaJourneyPaint = null;
        string? hover = pointerSurface?.Id;
        matchPresentation.Cards.BeginFrame(CardContext, hover, frameElapsed, preferences.ReducedMotion, boardView);
        if (rulesReaderId != readingId || rulesReaderGame != match?.Id) ClearRulesReader();
        deferredCardComposition.Clear();
        sourceReaderControls.Clear(); sourceReaderActionsBounds = Rectangle.Empty; sourceReaderScrollMax = 0;
        linkedSpellFaces.Clear();
        battlefieldReaderStateBounds = Rectangle.Empty;
    }
    private void EndCardReading() => matchPresentation.Cards.EndFrame();
    private bool ToggleCardReader(Hit hit) => matchPresentation.Cards.Toggle(hit, CardContext);
    private bool ReadingAbilitySource(Decision decision)
    {
        if (!AbilityActionPending || match == null || matchPresentation.Cards.Pinned is not { Zone: "Battlefield" } entry) return false;
        string id = decision.Context.Source?.CardId is { Length: > 0 } sourceId ? sourceId
            : decision.Context.Source?.Card is { VisualId.Length: > 0 } source ? source.VisualId : decision.Context.Source?.Card?.CombatId ?? "";
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
        bool opensZone = false, int choiceSlot = -1, Rectangle? activationBounds = null, Rectangle? readingAnchor = null, Rectangle? clip = null,
        bool directPlacement = false, bool registerHit = true)
    {
        if (choiceSlot < 0 && id == CardIdentity(card) && ReleasedPlayPose(card, zone) is { } submitted) {
            rest = submitted; directPlacement = true; clip = null;
        }
        var play = choiceSlot < 0 && id == CardIdentity(card) && zone is "Stack" or "Battlefield"
            ? matchPresentation.CardCues.LastOrDefault(cue => cue.Kind == CueKind.Play && cue.VisualId == id) : null;
        if (play?.PlayerId is { } actor)
            entrance = matchPresentation.Cards.TryGetPreviousPose(id, out var preceding) ? preceding.Center : SeatHand(actor);
        var entry = matchPresentation.Cards.Prepare(new(id, card, rest, zone, owner, hitId, activate,
            entrance, entranceDelay, opensZone, choiceSlot, activationBounds, play == null ? null : (float)(now - play.Started), play?.Reveal ?? true, readingAnchor, directPlacement));
        var hit = entry.Hit;
        if (clip is { } viewport && !entry.Focused)
            hit = hit with { Bounds = Rectangle.Intersect(hit.Bounds, viewport), Shape = point => viewport.Contains(point) && entry.Hit.Contains(point) };
        if (zone == "Battlefield" && choiceSlot < 0 && !entry.Focused && !BattlefieldFocused)
            hit = hit with { Bounds = Rectangle.Intersect(hit.Bounds, BoardViewport), Shape = point => BoardViewport.Contains(point) && entry.Hit.Contains(point) };
        if (registerHit) hits.Add(hit);
        if (entry.Layer == CardPresentationLayer.Table) {
            if (clip is { } paintingClip) canvas.BeginClip(paintingClip);
            ComposeCard(entry, paint);
            if (clip != null) canvas.EndClip();
        }
        else deferredCardComposition.Add((entry, paint));
        return entry.Display;
    }

    private void ComposeCard(PresentedCard entry, Action<Pose, bool> compose)
    {
        bool clipped = entry.Zone == "Battlefield" && entry.Layer == CardPresentationLayer.Returning && !BattlefieldFocused;
        if (clipped) canvas.BeginClip(BoardViewport);
        bool liveReader = DrawRulesReader(entry) || DrawSourceActionReader(entry);
        if (!liveReader) {
            if (entry.Playing) PaintInteractionCard(entry.Card, entry.Display, false, false);
            else compose(entry.Display, entry.Focused);
        }
        if (entry.Hovered && !liveReader) canvas.CardFocus(entry.Display, HoverAccent);
        if (clipped) canvas.EndClip();
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
        if (entry == null || CompactCardDecision && !CardContext.CanBrowseHandSelection(entry)
            && !CardContext.CanBrowsePublicCollection(entry) || !CardContext.CanRead(entry)) return;
        var target = (entry.ReaderTarget ?? CardPresentationController.ReaderPose(entry.Rest, entry.Zone)).Bounds;
        if (Vector2.Distance(entry.Display.Size, target.Size.ToVector2()) > 2
            || Vector2.Distance(entry.Display.Center, target.Center.ToVector2()) > 2) return;
        int firstAction = hits.Count();
        DrawReaderActions(entry, target);
        DrawRulesReaderControl(entry, target, hits.Skip(firstAction).Select(hit => ReaderControlPaintBounds(hit.Bounds)).ToArray());
    }

    private void DrawReaderActions(PresentedCard entry, Rectangle target)
    {
        if (CardContext.CanBrowseOpeningHand(entry)) return;
        if (CardContext.CanBrowseHandSelection(entry)
            || CardContext.CanBrowsePublicCollection(entry) && match?.Decision?.Payload is not (null or PriorityPayload or PaymentPayload)) {
            if (IsCardActionable(entry.Card)) {
                int? index = TableChoices.Index(match!, entry.Card);
                bool selected = index is { } choice ? decisions.Selections.Contains(choice) : entry.Card.Highlighted;
                string label = selected ? "Deselect" : "Choose card";
                if (decisions.Choice is { HandReduction: not null } draft)
                    label = draft.Keeping ? selected ? "Remove from kept cards" : "Keep card"
                        : selected ? "Keep this card" : "Discard card";
                Button("read-card-action", label, target.X + 8, Math.Min(840, target.Bottom - 2),
                    canvas.TextWidth(label, .76f) + 24, new MatchIntent.UseReaderCard(entry.Card), !busy, height: 30);
            }
            return;
        }
        if (SourceActionsSupported || session.OptionalPaymentPolicies.Any(policy => policy.SourceCardId == CardViews.Identity(entry.Card))) {
            DrawSourceReaderControls(entry); return;
        }
        if (match?.Decision?.Payload is PriorityPayload or PaymentPayload && IsCardActionable(entry.Card)) {
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
