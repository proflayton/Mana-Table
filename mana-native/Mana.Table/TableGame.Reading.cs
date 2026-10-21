using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private static readonly Color HoverAccent = new(255, 177, 64);
    private sealed record ReadingCard(string Id, Card Card, string Zone, Pose Rest, Pose Display, Hit Hit, int ChoiceSlot);
    private bool ReadingGalleryOpen => overlay.Length == 0 && zoneName == null && DecisionModal && match?.Decision?.Kind is "choice" or "reveal";
    private readonly List<ReadingCard> readingCards = [];
    private readonly List<(ReadingCard Card, Action Paint)> liftedCards = [];
    private readonly HashSet<string> readingTransitions = [];
    private readonly Dictionary<string, int> cardPaintCounts = [];
    private string? readingId, readingZone;
    private Rectangle? hoverPreview;
    private readonly HashSet<string> pendingReadingCollapses = [];
    private (string Scope, int Slot)? inspectedChoice;

    private bool CompactCardDecision => TableChoice || match?.Decision?.Intent is DecisionIntent.Selection or DecisionIntent.Mana or DecisionIntent.Attack or DecisionIntent.Block;

    private void ExtendCardHit(Card card, Rectangle bounds)
    {
        string hitId = "card:" + card.Key + ":" + card.VisualId;
        int index = hits.FindLastIndex(hit => hit.Id == hitId && hit.Scope == Scope);
        if (index < 0) return;
        var face = hits[index];
        hits[index] = face with { Bounds = Rectangle.Union(face.Bounds, bounds), Shape = point => face.Contains(point) || bounds.Contains(point) };
    }

    private Hit? TableHitAt(Point point)
    {
        if (ActivityOpen) return hits.LastOrDefault(h => h.Id.StartsWith("activity-") && h.Contains(point) && CurrentHit(h));
        if (updatesOpen) return hits.LastOrDefault(h => h.Id.StartsWith("updates-") && h.Contains(point) && CurrentHit(h));
        if (ExperienceOpen) return hits.LastOrDefault(h => (h.Id.StartsWith("experience-") || h.Id.StartsWith("updates-") || h.Id == "guidance-reset") && h.Contains(point) && CurrentHit(h));
        var top = hits.LastOrDefault(h => h.Contains(point));
        if (OverlayOpen && !ReadingGalleryOpen || dragging || top is { Card: null }) return top;
        // Keep the gallery's original label strip available to browse adjacent
        // choices, just as the hand keeps its original slots available.
        if (ReadingGalleryOpen && point.Y is >= 605 and <= 633) {
            var tile = readingCards.LastOrDefault(c => c.ChoiceSlot >= 0 && point.X >= c.Rest.Bounds.Left && point.X < c.Rest.Bounds.Right);
            if (tile != null) return hits.LastOrDefault(h => h.Id == tile.Hit.Id);
        }
        // Keep the fan's original slots available while browsing. Enlarging one
        // card must not trap the pointer over its neighbors in the bottom strip.
        if (!OverlayOpen && !dragging && point.Y >= 745 && readingId != null) {
            var hand = readingCards.LastOrDefault(c => c.Zone == "Hand" && c.Rest.Contains(point));
            if (hand != null) return hits.LastOrDefault(h => h.Id == hand.Hit.Id);
        }
        var focused = readingCards.LastOrDefault(c => c.Id == readingId && c.Zone == readingZone);
        if (focused?.Zone == "Battlefield") {
            // A fan's exposed header strip remains a browsing target even while
            // another occurrence is enlarged over it. Use the original geometry.
            var strip = readingCards.LastOrDefault(c => c.Zone == "Battlefield" && c.Id != focused.Id &&
                (c.Rest with { Center = c.Rest.Point(.5f, .08f), Size = new(c.Rest.Size.X, c.Rest.Size.Y * .16f) }).Contains(point));
            if (strip != null) return hits.LastOrDefault(h => h.Id == strip.Hit.Id);
        }
        if (focused != null && (focused.Rest.Contains(point) || focused.Display.Contains(point))) return hits.LastOrDefault(h => h.Id == focused.Hit.Id);
        return top;
    }
    private void BeginCardReading()
    {
        var hit = HitAt(pointer);
        var candidate = readingCards.LastOrDefault(c => c.Hit.Id == hit?.Id && !c.Card.FaceDown);
        string identity = candidate?.Id ?? "";
        lastHover = identity;
        // Hover only highlights. A reader belongs to an explicit click and must
        // not disappear because the enlarged face moved away from the pointer.
        var pinned = readingCards.LastOrDefault(c => c.Id == readingId && c.Zone == readingZone);
        if (readingId != null && (dragging || CompactCardDecision || pinned == null || !CanReadCard(pinned))) CollapseCardReading();
        readingCards.Clear(); liftedCards.Clear(); cardPaintCounts.Clear(); hoverPreview = null;
    }
    private bool ToggleCardReader(Hit hit)
    {
        var entry = readingCards.LastOrDefault(c => c.Hit.Id == hit.Id);
        if (entry == null || entry.Card.FaceDown || !CanReadCard(entry) || dragging || CompactCardDecision) return false;
        if (entry.ChoiceSlot >= 0) {
            if (match?.Decision?.Kind != "reveal") return false;
        } else if (OverlayOpen || hit.Zone is not ("Hand" or "Battlefield" or "Command")
            || match?.Decision is { Intent: not DecisionIntent.Priority }) return false;
        if (readingId == entry.Id && readingZone == entry.Zone) CollapseCardReading();
        else {
            CollapseCardReading();
            readingId = entry.Id; readingZone = entry.Zone; readingTransitions.Add(entry.Id);
        }
        return true;
    }
    private Card? ChoiceFace(int slot) => match?.Decision is { } decision
        ? decision.LibraryCards.Length > 0 ? decision.LibraryCards.ElementAtOrDefault(slot)?.Card : decision.Choices.ElementAtOrDefault(slot)?.Card
        : null;
    private Card? RefreshInspectedCard()
    {
        if (inspectedChoice is { } choice) return choice.Scope == Scope && ChoiceFace(choice.Slot) is { FaceDown: false } face ? face : null;
        return inspected == null ? null : VisibleCards().FirstOrDefault(card => SameVisibleCard(card, inspected));
    }
    private bool CanReadCard(ReadingCard entry)
    {
        if (entry.ChoiceSlot < 0) return !OverlayOpen && VisibleCards().Any(c => SameVisibleCard(c, entry.Card) && !c.FaceDown);
        if (!ReadingGalleryOpen || entry.Hit.Scope != Scope) return false;
        // Revealed faces intentionally have no persistent card identity. Their
        // occurrence belongs only to this decision and its original item slot.
        var decision = match!.Decision!;
        var card = decision.LibraryCards.Length > 0 ? decision.LibraryCards.ElementAtOrDefault(entry.ChoiceSlot)?.Card
            : decision.Choices.ElementAtOrDefault(entry.ChoiceSlot)?.Card;
        return card is { FaceDown: false };
    }
    private static Pose ReadingPose(Pose rest, string zone)
    {
        // Grow around the card's own position. Only shift enough to keep the
        // complete face and the main action control on screen.
        if (zone == "Choice") return new(new(Math.Clamp(rest.Center.X, 390, 1210), 444), new(410, 574), Elevation: 22);
        float x = Math.Clamp(rest.Center.X, 225, zone == "Hand" ? 1045 : 1375);
        float y = zone == "Hand" ? 383 : Math.Clamp(rest.Center.Y, 365, x > 1100 ? 425 : 548);
        // Keep the local life total and target hit area clear as a card grows.
        var seat = TableLayout.LocalSeatBounds;
        if (x + 205 > seat.Left && x - 205 < seat.Right) y = Math.Min(y, seat.Top - 12 - 287);
        return new(new(x, y), new(410, 574), Elevation: 22);
    }
    private Pose DrawTableCard(string id, Card card, Pose rest, string zone, int? owner,
        string hitId, Action activate, Action<Pose, bool> paint, Vector2? entrance = null, float entranceDelay = 0, bool opensZone = false,
        int choiceSlot = -1, Rectangle? activationBounds = null)
    {
        bool readable = (choiceSlot >= 0 ? ReadingGalleryOpen : !OverlayOpen) && !dragging;
        bool focused = !card.FaceDown && readingId == id && readingZone == zone && readable;
        if (card.FaceDown) readingTransitions.Remove(id);
        // A completed selection returns immediately to its normal footprint so
        // the old enlarged face cannot intercept the next adjacent click.
        var pose = pendingReadingCollapses.Remove(id) ? motion.Hold(id, rest)
            : motion.Place(id, focused ? ReadingPose(rest, zone) : rest, entrance, entranceDelay);
        if (!focused && Vector2.Distance(pose.Size, rest.Size) < 2 && Vector2.Distance(pose.Center, rest.Center) < 2) readingTransitions.Remove(id);
        // A pile face opens a browser; it is not a cast/play action.
        bool handAnchor = zone == "Hand" && (focused || readingTransitions.Contains(id));
        var hitBounds = activationBounds is { } bounds ? Rectangle.Union(pose.Bounds, bounds) : pose.Bounds;
        if (handAnchor) hitBounds = Rectangle.Union(hitBounds, rest.Bounds);
        var hit = new Hit(hitId, Scope, hitBounds,
            activate, card, opensZone ? null : zone, owner, point => pose.Contains(point) || handAnchor && rest.Contains(point) || activationBounds?.Contains(point) == true);
        var entry = new ReadingCard(id, card, zone, rest, pose, hit, choiceSlot);
        readingCards.Add(entry); hits.Add(hit);
        void Paint()
        {
            paint(pose, (focused || id == lastHover) && !card.FaceDown && readable);
            if (Automation != null) cardPaintCounts[id] = cardPaintCounts.GetValueOrDefault(id) + 1;
        }
        if (focused || readingTransitions.Contains(id)) liftedCards.Add((entry, Paint));
        else Paint();
        if (focused) hoverPreview = pose.Bounds;
        return pose;
    }
    private void DrawLiftedCards()
    {
        // Each occurrence is painted once. Returning cards stay above their
        // rank until they settle; the card being read is always foremost.
        foreach (var lifted in liftedCards.OrderBy(c => c.Card.Id == readingId ? 1 : 0)) {
            lifted.Paint();
            var currentHit = hits.LastOrDefault(hit => hit.Id == lifted.Card.Hit.Id && hit.Scope == lifted.Card.Hit.Scope) ?? lifted.Card.Hit;
            hits.RemoveAll(hit => hit.Id == currentHit.Id && hit.Scope == currentHit.Scope);
            hits.Add(currentHit);
        }
        liftedCards.Clear();
    }
    private void DrawReaderControls()
    {
        var entry = readingCards.LastOrDefault(c => c.Id == readingId && c.Zone == readingZone);
        if (entry == null || CompactCardDecision || !CanReadCard(entry)) return;
        // Controls do not chase the face while its opening animation settles.
        var target = ReadingPose(entry.Rest, entry.Zone).Bounds;
        int x = target.Right + 12;
        if (x + 220 > 1580) x = target.Left - 232;
        x = Math.Clamp(x, 20, 1360);
        int y = Math.Clamp(target.Top + 20, 70, 690);
        if (match?.Decision?.Intent == DecisionIntent.Priority && IsCardActionable(entry.Card)) {
            string label = entry.Zone == "Battlefield" ? "Activate ability" : !entry.Card.Selectable && HandPlacements.Options(match, entry.Card).Length > 0 ? "Place card" : "Play card";
            Button("read-card-action", label, x, y, 220, () => {
                var current = VisibleCards().FirstOrDefault(card => SameVisibleCard(card, entry.Card));
                if (current == null || !IsCardActionable(current)) return;
                CollapseCardReading(); CardClick(current);
            }, !busy, true, 40);
            y += 48;
        }
        Button("read-card-close", "Close card", x, y, 220, CollapseCardReading, height: 36);
    }
    private void ClearCardReading()
    {
        readingCards.Clear(); liftedCards.Clear(); readingTransitions.Clear(); cardPaintCounts.Clear();
        readingId = readingZone = null; hoverPreview = null; lastHover = "";
        inspectedChoice = null;
        pendingReadingCollapses.Clear();
    }
    private void CollapseCardReading()
    {
        var hit = HitAt(pointer);
        string clickedId = readingCards.LastOrDefault(c => c.Hit.Id == hit?.Id)?.Id ?? readingId ?? lastHover;
        // Moving to a neighbor can start its reader while an older one is still
        // returning. Selection restores every rendered reader to its new rest.
        pendingReadingCollapses.UnionWith(readingCards.Where(c => readingTransitions.Contains(c.Id)).Select(c => c.Id));
        pendingReadingCollapses.Add(clickedId);
        if (readingId != null) pendingReadingCollapses.Add(readingId);
        readingId = readingZone = null; hoverPreview = null; lastHover = "";
    }
}
