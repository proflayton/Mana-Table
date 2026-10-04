using Mana.Contracts;
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

    private Hit? TableHitAt(Point point)
    {
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
        if (focused != null && (focused.Rest.Contains(point) || focused.Display.Contains(point))) return hits.LastOrDefault(h => h.Id == focused.Hit.Id);
        return top;
    }
    private void BeginCardReading()
    {
        var hit = HitAt(pointer);
        var candidate = readingCards.LastOrDefault(c => c.Hit.Id == hit?.Id && !c.Card.FaceDown);
        if (dragging || selectedCombat != null || candidate != null && !CanReadCard(candidate)) candidate = null;
        string identity = candidate?.Id ?? "";
        if (lastHover != identity) { lastHover = identity; hoverSince = now; }
        if (candidate == null) { readingId = readingZone = null; }
        else if (now - hoverSince >= .18) { readingId = candidate.Id; readingZone = candidate.Zone; readingTransitions.Add(candidate.Id); }
        readingCards.Clear(); liftedCards.Clear(); cardPaintCounts.Clear(); hoverPreview = null;
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
        var pose = motion.Place(id, focused ? ReadingPose(rest, zone) : rest, entrance, entranceDelay);
        if (!focused && Vector2.Distance(pose.Size, rest.Size) < 2 && Vector2.Distance(pose.Center, rest.Center) < 2) readingTransitions.Remove(id);
        // A pile face opens a browser; it is not a cast/play action.
        var hit = new Hit(hitId, Scope, activationBounds is { } bounds ? Rectangle.Union(pose.Bounds, bounds) : pose.Bounds,
            activate, card, opensZone ? null : zone, owner, point => pose.Contains(point) || activationBounds?.Contains(point) == true);
        var entry = new ReadingCard(id, card, zone, rest, pose, hit, choiceSlot);
        readingCards.Add(entry); hits.Add(hit);
        void Paint()
        {
            paint(pose, id == lastHover && !card.FaceDown && readable);
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
            hits.Remove(lifted.Card.Hit); hits.Add(lifted.Card.Hit);
        }
        liftedCards.Clear();
    }
    private void ClearCardReading()
    {
        readingCards.Clear(); liftedCards.Clear(); readingTransitions.Clear(); cardPaintCounts.Clear();
        readingId = readingZone = null; hoverPreview = null; lastHover = ""; hoverSince = 0;
    }
}
