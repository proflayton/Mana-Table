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
    private static readonly Color HoverAccent = new(255, 177, 64);
    private readonly CardPresentationController cardPresentation = new();
    // These callbacks compose draw commands and are drained before publication.
    // The card owner and the final GPU frame contain no painting callbacks.
    private readonly Dictionary<int, Action<Pose, bool>> deferredCardComposition = [];
    private (string Scope, int Slot)? inspectedChoice;
    private bool ReadingGalleryOpen => overlay.Length == 0 && zoneName == null && DecisionModal
        && match?.Decision?.Kind is "choice" or "reveal";
    private bool CompactCardDecision => TableChoice
        || match?.Decision?.Intent is DecisionIntent.Selection or DecisionIntent.Mana or DecisionIntent.Attack or DecisionIntent.Block;
    private CardPresentationContext CardContext => new(match, Scope, OverlayOpen && !BattlefieldFocused,
        ReadingGalleryOpen, dragging, CompactCardDecision);
    private string? readingId => cardPresentation.PinnedId;
    private Rectangle? hoverPreview => cardPresentation.ReaderBounds;

    private void ExtendCardHit(Card card, Rectangle bounds) => hits.ExtendCanonical(
        "card:" + card.Key + ":" + card.VisualId, Scope, bounds);

    private Hit? TableHitAt(Point point)
    {
        if (ActivityOpen) return hits.LastOrDefault(hit => hit.Id.StartsWith("activity-") && hit.Contains(point) && CurrentHit(hit));
        if (updatesOpen) return hits.LastOrDefault(hit => hit.Id.StartsWith("updates-") && hit.Contains(point) && CurrentHit(hit));
        if (ExperienceOpen) return hits.LastOrDefault(hit => (hit.Id.StartsWith("experience-") || hit.Id.StartsWith("updates-") || hit.Id == "guidance-reset") && hit.Contains(point) && CurrentHit(hit));
        if (BattlefieldFocused && hits.LastOrDefault(hit => hit.Contains(point)) is { } focusHit
            && focusHit.Id.StartsWith("battlefield-focus-", StringComparison.Ordinal)) return focusHit;
        return cardPresentation.ResolveHit(point, hits, hits.LastOrDefault(hit => hit.Contains(point)), CardContext);
    }

    private void BeginCardReading(float frameElapsed)
    {
        // Resolve hover against the last published frame before starting the next
        // frame. Preparation then uses current snapshot visibility and policy.
        string? hover = HitAt(pointer)?.Id;
        cardPresentation.BeginFrame(CardContext, hover, frameElapsed, preferences.ReducedMotion, boardView);
        deferredCardComposition.Clear();
    }
    private void EndCardReading() => cardPresentation.EndFrame();
    private bool ToggleCardReader(Hit hit) => cardPresentation.Toggle(hit, CardContext);
    private Card? ChoiceFace(int slot) => CardContext.ChoiceFace(slot);
    private Card? RefreshInspectedCard()
    {
        if (inspectedChoice is { } choice) return choice.Scope == Scope && ChoiceFace(choice.Slot) is { FaceDown: false } face ? face : null;
        return inspected == null ? null : VisibleCards().FirstOrDefault(card => SameVisibleCard(card, inspected));
    }

    private Pose DrawTableCard(string id, Card card, Pose rest, string zone, int? owner,
        string hitId, Action activate, Action<Pose, bool> paint, Vector2? entrance = null, float entranceDelay = 0,
        bool opensZone = false, int choiceSlot = -1, Rectangle? activationBounds = null)
    {
        var entry = cardPresentation.Prepare(new(id, card, rest, zone, owner, hitId, activate,
            entrance, entranceDelay, opensZone, choiceSlot, activationBounds));
        hits.Add(entry.Hit);
        if (entry.Layer == CardPresentationLayer.Table) ComposeCard(entry, paint);
        else deferredCardComposition.Add(entry.Sequence, paint);
        return entry.Display;
    }

    private void ComposeCard(PresentedCard entry, Action<Pose, bool> compose)
    {
        compose(entry.Display, entry.Emphasized);
        if (Automated) cardPresentation.RecordComposition(entry);
    }
    private void DrawLiftedCards()
    {
        foreach (var entry in cardPresentation.TakeDeferred()) {
            if (!deferredCardComposition.Remove(entry.Sequence, out var compose))
                throw new InvalidOperationException("A prepared card has no composition.");
            ComposeCard(entry, compose);
            // Labels may extend the face while composing. Promote that same
            // canonical hit rather than replacing it with the original face.
            hits.PromoteCanonical(entry.Hit.Id, entry.Hit.Scope, entry.Hit);
        }
    }
    private void DrawReaderControls()
    {
        var entry = cardPresentation.Pinned;
        if (entry == null || CompactCardDecision || !CardContext.CanRead(entry)) return;
        var target = (entry.ReaderTarget ?? CardPresentationController.ReaderPose(entry.Rest, entry.Zone)).Bounds;
        int x = target.Right + 12;
        if (x + 220 > 1580) x = target.Left - 232;
        x = Math.Clamp(x, 20, 1360);
        int y = Math.Clamp(target.Top + 20, 70, 690);
        if (match?.Decision?.Intent == DecisionIntent.Priority && IsCardActionable(entry.Card)) {
            string label = entry.Zone == "Battlefield" ? "Activate ability"
                : !entry.Card.Selectable && HandPlacements.Options(match, entry.Card).Length > 0 ? "Place card" : "Play card";
            Button("read-card-action", label, x, y, 220, () => {
                var current = VisibleCards().FirstOrDefault(card => SameVisibleCard(card, entry.Card));
                if (current == null || !IsCardActionable(current)) return;
                CollapseCardReading(); if (BattlefieldFocused) zoneName = null; CardClick(current);
            }, !busy, true, 40);
            y += 48;
        }
        Button("read-card-close", "Close card", x, y, 220, CollapseCardReading, height: 36);
    }

    private void ClearCardComposition()
    {
        cardPresentation.ClearComposition(); deferredCardComposition.Clear();
    }
    private void ClearCardReading()
    {
        cardPresentation.Reset(); deferredCardComposition.Clear(); inspectedChoice = null;
    }
    private void CollapseCardReading() => cardPresentation.Collapse(HitAt(pointer)?.Id);
}
