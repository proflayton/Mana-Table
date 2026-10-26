using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

/// <summary>The current authority and interaction policy used to prepare cards.</summary>
internal sealed record CardPresentationContext(GameSnapshot? State, string Scope,
    bool OverlayOpen, bool GalleryOpen, bool Dragging, bool CompactDecision,
    bool KeepBattlefieldAnchor = false, Rectangle? BattlefieldReaderAvoid = null, DecisionDockView? Decision = null)
{
    private DecisionReadingPolicy Reading => new(Decision ?? (State == null ? null : DecisionDockPresentation.From(State, new())));
    public bool OpeningHandDecision => Reading.Allows(DecisionReading.OpeningHandInspection);

    public bool CanBrowseOpeningHand(PresentedCard entry) => OpeningHandDecision && State?.Viewer is { } viewer
        && entry.Zone == "Hand" && entry.Request.Owner == viewer.Id
        && viewer.Zone("Hand").Cards.Any(card => !card.FaceDown && CardViews.Same(card, entry.Card));

    public bool CanBrowseDecisionSource(PresentedCard entry) => Reading.Allows(DecisionReading.SourceInspection)
        && Reading.View?.SourceCard is { FaceDown: false } source && CardViews.Same(source, entry.Card)
        && entry.ChoiceSlot < 0 && CurrentOccurrence(entry) is { FaceDown: false };

    public bool CanBrowseHandSelection(PresentedCard entry) => State is { } state
        && Reading.Allows(DecisionReading.HandSelectionInspection)
        && state.Viewer is { } viewer && entry.ChoiceSlot < 0
        && entry.Zone == "Hand" && entry.Request.Owner == viewer.Id
        && viewer.Zone("Hand").Cards.Any(card => !card.FaceDown && CardViews.Same(card, entry.Card));

    public Card? ChoiceFace(int slot) => Reading.ChoiceFace(State, slot);

    public bool InspectOnClick(PresentedCard entry)
    {
        if (!CanRead(entry) || Dragging || CompactDecision && !CanBrowseHandSelection(entry)) return false;
        if (entry.ChoiceSlot >= 0) return Reading.Allows(DecisionReading.RevealInspection);
        return !OverlayOpen && (Reading.Allows(DecisionReading.TableInspection) || CanBrowseOpeningHand(entry)
            || CanBrowseHandSelection(entry) || CanBrowseDecisionSource(entry));
    }

    public bool CanRead(PresentedCard entry)
    {
        if (entry.Request.ChoiceSlot < 0) {
            if (OverlayOpen || State == null) return false;
            return CurrentOccurrence(entry) is { FaceDown: false };
        }
        // Anonymous revealed faces belong to an original slot in one decision.
        // Neither their name nor a retained visual id can extend that authority.
        return GalleryOpen && entry.Hit.Scope == Scope
            && ChoiceFace(entry.Request.ChoiceSlot) is { FaceDown: false };
    }

    // A historical source/choice face is not a live card location. Real readers
    // reconcile against the current physical zone or stack, including a disclosed top.
    public Card? CurrentOccurrence(PresentedCard entry)
    {
        if (State == null || entry.ChoiceSlot >= 0) return null;
        return CurrentOccurrence(entry.Zone, entry.Card);
    }

    public Card? CurrentOccurrence(string zoneName, Card shown)
    {
        if (State == null) return null;
        IEnumerable<Card> candidates = zoneName == "Stack"
            ? State.Stack.Where(item => item.Card != null).Select(item => item.Card!)
            : CardViews.InZones(State).Where(occurrence => occurrence.Zone == zoneName).Select(occurrence => occurrence.Card);
        return candidates.FirstOrDefault(card => CardViews.Same(card, shown));
    }
}

/// <summary>Rest is canonical layout geometry; Entrance is an already presented source position.</summary>
internal sealed record CardPresentationRequest(string Id, Card Card, Pose Rest, string Zone,
    int? Owner, string HitId, UiIntent Activate, Vector2? Entrance = null, float EntranceDelay = 0,
    bool OpensZone = false, int ChoiceSlot = -1, Rectangle? ActivationBounds = null, float? PlayAge = null, bool RevealPlay = true,
    Rectangle? ReadingAnchor = null, bool DirectPlacement = false);

internal enum CardPresentationLayer { Table, Playing, Returning, Reader }

/// <summary>A prepared occurrence. Rest, painting and input share presentation-space geometry.</summary>
internal sealed record PresentedCard(CardPresentationRequest Request,
    Pose Display, Hit Hit, bool Focused, bool Emphasized, CardPresentationLayer Layer)
{
    public BoardView View { get; init; } = BoardView.Identity;
    public Pose? ReaderTarget { get; init; }
    public bool Hovered { get; init; }
    public bool Playing => Layer == CardPresentationLayer.Playing;
    public string Id => Request.Id;
    public Card Card => Request.Card;
    public string Zone => Request.Zone;
    public Pose Rest => View.Apply(Request.Rest);
    public bool AnchorContains(Point point) => Request.ReadingAnchor is { } anchor ? View.Apply(anchor).Contains(point) : Rest.Contains(point);
    public int ChoiceSlot => Request.ChoiceSlot;
}

/// <summary>A card's actual face, optional hand anchor, and explicit action area.</summary>
internal readonly record struct CardHitGeometry(Pose Display, Pose? Anchor, Rectangle? Activation)
{
    public Rectangle Bounds {
        get {
            var bounds = Display.Bounds;
            if (Anchor is { } anchor) bounds = Rectangle.Union(bounds, anchor.Bounds);
            if (Activation is { } activation) bounds = Rectangle.Union(bounds, activation);
            return bounds;
        }
    }
    public bool Contains(Point point) => Display.Contains(point)
        || Anchor?.Contains(point) == true || Activation?.Contains(point) == true;
}
