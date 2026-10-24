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
    internal const int StudyDetailsWidth = 280, StudyDetailsGap = 18;
    private readonly SceneMotion motion = new();
    private readonly List<PresentedCard> cards = [];
    private readonly HashSet<string> transitions = [];
    private readonly HashSet<string> pendingCollapses = [];
    private readonly Dictionary<string, int> compositionCounts = [];
    private readonly record struct PresentedPose(Pose Display, bool UsesBoardMotion);
    private Dictionary<string, PresentedPose> displayedPoses = [], previousDisplayedPoses = [];
    private CardPresentationContext context = new(null, "", false, false, false, false);
    private BoardView boardView = BoardView.Identity;
    private Pose? readerTarget;
    private string? pinnedId, pinnedZone;
    private string hoveredId = "";
    private string hoveredHit = "";

    public string? PinnedId => pinnedId;
    public bool HasPinnedCard => pinnedId != null;
    public bool HasReaderTransition => HasPinnedCard || transitions.Count > 0 || pendingCollapses.Count > 0;
    public Rectangle? ReaderBounds { get; private set; }
    public IReadOnlyList<PresentedCard> Cards => cards.AsReadOnly();
    public PresentedCard? Pinned => cards.LastOrDefault(card => card.Id == pinnedId && card.Zone == pinnedZone);

    public void BeginFrame(CardPresentationContext current, string? hoveredHitId,
        float elapsedSeconds, bool reducedMotion, BoardView view = default)
    {
        if (context.State?.Id != current.State?.Id) Reset();
        bool leavingOpeningHand = context.OpeningHandDecision && !current.OpeningHandDecision;
        context = current;
        var hovered = cards.LastOrDefault(card => card.Hit.Id == hoveredHitId && card.Hit.Scope == current.Scope && !card.Card.FaceDown);
        hoveredId = hovered?.Id ?? "";
        hoveredHit = hovered?.Hit.Id ?? "";
        // A reader survives pointer movement, but never lost visibility or a
        // required compact interaction. Reconciliation uses the current snapshot.
        if (pinnedId != null && (current.Dragging || leavingOpeningHand || Pinned is not { } pinned
            || current.CompactDecision && !current.CanBrowseHandSelection(pinned) || !current.CanRead(pinned))) Collapse(hoveredHitId);
        (displayedPoses, previousDisplayedPoses) = (previousDisplayedPoses, displayedPoses);
        displayedPoses.Clear();
        boardView = view;
        ClearComposition();
        compositionCounts.Clear(); ReaderBounds = null;
        motion.Begin(elapsedSeconds, reducedMotion);
    }

    public PresentedCard Prepare(CardPresentationRequest request)
    {
        bool readable = (request.ChoiceSlot >= 0 ? context.GalleryOpen : !context.OverlayOpen) && !context.Dragging;
        bool focused = !request.Card.FaceDown && pinnedId == request.Id && pinnedZone == request.Zone && readable;
        if (request.Card.FaceDown) transitions.Remove(request.Id);
        var view = request.ChoiceSlot < 0 && BoardView.AffectsZone(request.Zone) ? boardView : BoardView.Identity;
        var rest = view.Apply(request.Rest);
        if (focused) {
            readerTarget ??= context.KeepBattlefieldAnchor && request.Zone == "Battlefield"
                ? StudyReaderPose(rest, context.BattlefieldReaderAvoid)
                : ReaderPose(rest, request.Zone);
        }
        bool useBoardMotion = !focused && BoardView.AffectsZone(request.Zone) && request.ChoiceSlot < 0;
        bool collapse = pendingCollapses.Remove(request.Id);
        bool hadPrevious = previousDisplayedPoses.TryGetValue(request.Id, out var previous);
        bool wasBoardMotion = displayedPoses.TryGetValue(request.Id, out var current) ? current.UsesBoardMotion : previous.UsesBoardMotion;
        if (!collapse && hadPrevious && wasBoardMotion != useBoardMotion) {
            // Ordinary board movement is smoothed in canonical layout space;
            // readers and direct manipulation are fixed presentation space.
            // Rebase once when crossing that boundary, never once per frame.
            motion.Hold(request.Id, useBoardMotion ? view.Inverse(previous.Display) : previous.Display);
        }
        var target = focused ? readerTarget!.Value : request.Rest;
        Vector2? entrance = request.Entrance is { } start && useBoardMotion ? view.Inverse(start) : request.Entrance;
        var pose = collapse ? motion.Hold(request.Id, focused ? rest : request.Rest)
            : motion.Place(request.Id, target, entrance, request.EntranceDelay);
        if (useBoardMotion) pose = view.Apply(pose);
        displayedPoses[request.Id] = new(pose, useBoardMotion);
        if (!focused && Vector2.Distance(pose.Size, rest.Size) < 2
            && Vector2.Distance(pose.Center, rest.Center) < 2) transitions.Remove(request.Id);
        bool anchored = request.Zone == "Hand" && (focused || transitions.Contains(request.Id))
            || context.KeepBattlefieldAnchor && request.Zone == "Battlefield" && focused;
        var geometry = new CardHitGeometry(pose, anchored ? rest : null,
            request.ActivationBounds is { } activation ? view.Apply(activation) : null);
        var hit = new Hit(request.HitId, context.Scope, geometry.Bounds, request.Activate, request.Card,
            request.Zone, request.Owner, geometry.Contains);
        var layer = focused ? CardPresentationLayer.Reader
            : transitions.Contains(request.Id) ? CardPresentationLayer.Returning : CardPresentationLayer.Table;
        bool pointerHover = request.Id == hoveredId && request.HitId == hoveredHit && !request.Card.FaceDown && readable;
        var entry = new PresentedCard(request, pose, hit, focused,
            (focused || pointerHover) && !request.Card.FaceDown && readable, layer)
            { View = view, ReaderTarget = focused ? readerTarget : null, Hovered = pointerHover };
        cards.Add(entry);
        if (focused) ReaderBounds = pose.Bounds;
        return entry;
    }

    public void RecordComposition(PresentedCard entry) => compositionCounts[entry.Id]
        = compositionCounts.GetValueOrDefault(entry.Id) + 1;
    public int CompositionCount(string id) => compositionCounts.GetValueOrDefault(id);

    public bool Toggle(Hit hit, CardPresentationContext current)
    {
        var entry = cards.LastOrDefault(card => card.Hit.Id == hit.Id && card.Hit.Scope == hit.Scope);
        if (entry == null || entry.Card.FaceDown || !current.CanRead(entry)
            || current.Dragging || current.CompactDecision && !current.CanBrowseHandSelection(entry)) return false;
        if (entry.ChoiceSlot >= 0) {
            if (current.State?.Decision?.Kind != "reveal") return false;
        } else if (current.OverlayOpen
            || !current.AbilityChoice && current.State?.Decision is { Intent: not DecisionIntent.Priority }
                && current.State?.Decision?.Intent != DecisionIntent.Mana
                && !current.CanBrowseOpeningHand(entry) && !current.CanBrowseHandSelection(entry)) return false;
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
        pinnedId = pinnedZone = null; readerTarget = null; ReaderBounds = null; hoveredId = hoveredHit = "";
    }

    public void ClearComposition() => cards.Clear();
    public void EndFrame()
    {
        transitions.RemoveWhere(id => !cards.Any(card => card.Id == id));
        pendingCollapses.RemoveWhere(id => !displayedPoses.ContainsKey(id));
        previousDisplayedPoses.Clear();
        motion.End();
    }
    public Pose Hold(string id, Pose pose)
    {
        displayedPoses[id] = new(pose, false);
        return motion.Hold(id, pose);
    }
    public bool TryGetPose(string id, out Pose pose)
    {
        bool found = displayedPoses.TryGetValue(id, out var current); pose = current.Display; return found;
    }
    public void Reset()
    {
        ClearComposition(); transitions.Clear(); pendingCollapses.Clear(); compositionCounts.Clear();
        displayedPoses.Clear(); previousDisplayedPoses.Clear();
        pinnedId = pinnedZone = null; readerTarget = null; hoveredId = hoveredHit = ""; ReaderBounds = null;
        context = new(null, "", false, false, false, false);
        boardView = BoardView.Identity; motion.Clear();
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

    internal static Pose StudyReaderPose(Pose rest, Rectangle? avoid)
    {
        // Keep the complete card and its right-hand state column on screen.
        var right = new Pose(new(1600 - 20 - StudyDetailsWidth - StudyDetailsGap - 205, 385), new(410, 574), Elevation: 22);
        var left = right with { Center = new(510, 385) };
        var preferred = rest.Center.X < 800 ? right : left;
        var alternate = rest.Center.X < 800 ? left : right;
        int Overlap(Pose pose) {
            if (avoid == null) return 0;
            var bounds = pose.Bounds;
            bounds.Width += StudyDetailsGap + StudyDetailsWidth;
            var overlap = Rectangle.Intersect(bounds, avoid.Value);
            return overlap.Width * overlap.Height;
        }
        return Overlap(preferred) <= Overlap(alternate) ? preferred : alternate;
    }
}
