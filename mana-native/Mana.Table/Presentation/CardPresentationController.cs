using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

/// <summary>
/// Reconciles card reading and motion before GPU painting. It owns presentation
/// state, never an engine, graphics device, canvas, or application host.
/// </summary>
internal sealed class CardPresentationController
{
    private readonly SceneMotion motion = new();
    private readonly List<PresentedCard> cards = [];
    private readonly List<PresentedCard> deferred = [];
    private readonly HashSet<string> transitions = [];
    private readonly HashSet<string> pendingCollapses = [];
    private readonly Dictionary<string, int> compositionCounts = [];
    private CardPresentationContext context = new(null, "", false, false, false, false);
    private string? pinnedId, pinnedZone;
    private string hoveredId = "";

    public string? PinnedId => pinnedId;
    public bool HasPinnedCard => pinnedId != null;
    public Rectangle? ReaderBounds { get; private set; }
    public IReadOnlyList<PresentedCard> Cards => cards.AsReadOnly();
    public PresentedCard? Pinned => cards.LastOrDefault(card => card.Id == pinnedId && card.Zone == pinnedZone);

    public void BeginFrame(CardPresentationContext current, string? hoveredHitId,
        float elapsedSeconds, bool reducedMotion)
    {
        if (context.State?.Id != current.State?.Id) Reset();
        context = current;
        hoveredId = cards.LastOrDefault(card => card.Hit.Id == hoveredHitId && !card.Card.FaceDown)?.Id ?? "";
        // A reader survives pointer movement, but never lost visibility or a
        // required compact interaction. Reconciliation uses the current snapshot.
        if (pinnedId != null && (current.Dragging || current.CompactDecision
            || Pinned is not { } pinned || !current.CanRead(pinned))) Collapse(hoveredHitId);
        ClearComposition();
        compositionCounts.Clear(); ReaderBounds = null;
        motion.Begin(elapsedSeconds, reducedMotion);
    }

    public PresentedCard Prepare(CardPresentationRequest request)
    {
        bool readable = (request.ChoiceSlot >= 0 ? context.GalleryOpen : !context.OverlayOpen) && !context.Dragging;
        bool focused = !request.Card.FaceDown && pinnedId == request.Id && pinnedZone == request.Zone && readable;
        if (request.Card.FaceDown) transitions.Remove(request.Id);
        var pose = pendingCollapses.Remove(request.Id) ? motion.Hold(request.Id, request.Rest)
            : motion.Place(request.Id, focused ? ReaderPose(request.Rest, request.Zone) : request.Rest,
                request.Entrance, request.EntranceDelay);
        if (!focused && Vector2.Distance(pose.Size, request.Rest.Size) < 2
            && Vector2.Distance(pose.Center, request.Rest.Center) < 2) transitions.Remove(request.Id);
        bool anchored = request.Zone == "Hand" && (focused || transitions.Contains(request.Id));
        var geometry = new CardHitGeometry(pose, anchored ? request.Rest : null, request.ActivationBounds);
        var hit = new Hit(request.HitId, context.Scope, geometry.Bounds, request.Activate, request.Card,
            request.OpensZone ? null : request.Zone, request.Owner, geometry.Contains);
        var layer = focused ? CardPresentationLayer.Reader
            : transitions.Contains(request.Id) ? CardPresentationLayer.Returning : CardPresentationLayer.Table;
        var entry = new PresentedCard(cards.Count, request, pose, hit, focused,
            (focused || request.Id == hoveredId) && !request.Card.FaceDown && readable, layer);
        cards.Add(entry);
        if (layer != CardPresentationLayer.Table) deferred.Add(entry);
        if (focused) ReaderBounds = pose.Bounds;
        return entry;
    }

    public IReadOnlyList<PresentedCard> TakeDeferred()
    {
        var result = deferred.OrderBy(card => card.Layer).ToArray();
        deferred.Clear();
        return Array.AsReadOnly(result);
    }

    public void RecordComposition(PresentedCard entry) => compositionCounts[entry.Id]
        = compositionCounts.GetValueOrDefault(entry.Id) + 1;
    public int CompositionCount(string id) => compositionCounts.GetValueOrDefault(id);

    public bool Toggle(Hit hit, CardPresentationContext current)
    {
        var entry = cards.LastOrDefault(card => card.Hit.Id == hit.Id && card.Hit.Scope == hit.Scope);
        if (entry == null || entry.Card.FaceDown || !current.CanRead(entry)
            || current.Dragging || current.CompactDecision) return false;
        if (entry.ChoiceSlot >= 0) {
            if (current.State?.Decision?.Kind != "reveal") return false;
        } else if (current.OverlayOpen || hit.Zone is not ("Hand" or "Battlefield" or "Command")
            || current.State?.Decision is { Intent: not DecisionIntent.Priority }) return false;
        bool close = pinnedId == entry.Id && pinnedZone == entry.Zone;
        Collapse(hit.Id);
        if (!close) {
            pinnedId = entry.Id; pinnedZone = entry.Zone; transitions.Add(entry.Id);
        }
        return true;
    }

    public PresentedCard? RevealedEntry(Card card, CardPresentationContext current) => current.GalleryOpen
        ? cards.LastOrDefault(entry => entry.ChoiceSlot >= 0 && ReferenceEquals(entry.Card, card) && current.CanRead(entry))
        : null;

    public Hit? ResolveHit(Point point, IReadOnlyList<Hit> hits, Hit? top, CardPresentationContext current)
    {
        if (current.OverlayOpen && !current.GalleryOpen || current.Dragging || top is { Card: null }) return top;
        Hit? Registered(PresentedCard card) => hits.LastOrDefault(hit => hit.Id == card.Hit.Id && hit.Scope == card.Hit.Scope);
        if (current.GalleryOpen && point.Y is >= 605 and <= 633) {
            var tile = cards.LastOrDefault(card => card.ChoiceSlot >= 0
                && point.X >= card.Rest.Bounds.Left && point.X < card.Rest.Bounds.Right);
            if (tile != null) return Registered(tile);
        }
        if (!current.OverlayOpen && point.Y >= 745 && pinnedId != null) {
            var hand = cards.LastOrDefault(card => card.Zone == "Hand" && card.Rest.Contains(point));
            if (hand != null) return Registered(hand);
        }
        var focused = Pinned;
        if (focused?.Zone == "Battlefield") {
            var strip = cards.LastOrDefault(card => card.Zone == "Battlefield" && card.Id != focused.Id
                && (card.Rest with { Center = card.Rest.Point(.5f, .08f), Size = new(card.Rest.Size.X, card.Rest.Size.Y * .16f) }).Contains(point));
            if (strip != null) return Registered(strip);
        }
        return focused != null && (focused.Rest.Contains(point) || focused.Display.Contains(point))
            ? Registered(focused) : top;
    }

    public void Collapse(string? clickedHitId = null)
    {
        string clicked = cards.LastOrDefault(card => card.Hit.Id == clickedHitId)?.Id ?? pinnedId ?? hoveredId;
        pendingCollapses.UnionWith(cards.Where(card => transitions.Contains(card.Id)).Select(card => card.Id));
        if (clicked.Length > 0) pendingCollapses.Add(clicked);
        if (pinnedId != null) pendingCollapses.Add(pinnedId);
        pinnedId = pinnedZone = null; ReaderBounds = null; hoveredId = "";
    }

    public void ClearComposition() { cards.Clear(); deferred.Clear(); }
    public void EndFrame()
    {
        transitions.RemoveWhere(id => !cards.Any(card => card.Id == id));
        motion.End();
    }
    public Pose Hold(string id, Pose pose) => motion.Hold(id, pose);
    public bool TryGetPose(string id, out Pose pose) => motion.TryGet(id, out pose);
    public void Reset()
    {
        ClearComposition(); transitions.Clear(); pendingCollapses.Clear(); compositionCounts.Clear();
        pinnedId = pinnedZone = null; hoveredId = ""; ReaderBounds = null; motion.Clear();
    }

    public static Pose ReaderPose(Pose rest, string zone)
    {
        if (zone == "Choice") return new(new(Math.Clamp(rest.Center.X, 390, 1210), 444), new(410, 574), Elevation: 22);
        float x = Math.Clamp(rest.Center.X, 225, zone == "Hand" ? 1045 : 1375);
        float y = zone == "Hand" ? 383 : Math.Clamp(rest.Center.Y, 365, x > 1100 ? 425 : 548);
        var seat = TableLayout.LocalSeatBounds;
        if (x + 205 > seat.Left && x - 205 < seat.Right) y = Math.Min(y, seat.Top - 12 - 287);
        return new(new(x, y), new(410, 574), Elevation: 22);
    }
}
