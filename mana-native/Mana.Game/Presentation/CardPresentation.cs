using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

/// <summary>The current authority and interaction policy used to prepare cards.</summary>
internal sealed record CardPresentationContext(GameSnapshot? State, string Scope,
    bool OverlayOpen, bool GalleryOpen, bool Dragging, bool CompactDecision, bool AbilityChoice = false, bool SourceActions = false,
    bool KeepBattlefieldAnchor = false, Rectangle? BattlefieldReaderAvoid = null)
{
    public bool OpeningHandDecision => State?.Decision is { Kind: "input", Purpose: DecisionPurpose.OpeningHand };

    public bool CanBrowseOpeningHand(PresentedCard entry) => OpeningHandDecision && State?.Viewer is { } viewer
        && entry.Zone == "Hand" && entry.Request.Owner == viewer.Id
        && viewer.Zone("Hand").Cards.Any(card => !card.FaceDown && CardViews.Same(card, entry.Card));

    public Card? ChoiceFace(int slot) => State?.Decision is { } decision
        ? decision.LibraryCards.Length > 0 ? decision.LibraryCards.ElementAtOrDefault(slot)?.Card
            : decision.Choices.ElementAtOrDefault(slot) is { } choice ? CardViews.Choice(State, choice) : null
        : null;

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
    bool OpensZone = false, int ChoiceSlot = -1, Rectangle? ActivationBounds = null);

internal enum CardPresentationLayer { Table, Returning, Reader }

/// <summary>A prepared occurrence. Rest, painting and input share presentation-space geometry.</summary>
internal sealed record PresentedCard(CardPresentationRequest Request,
    Pose Display, Hit Hit, bool Focused, bool Emphasized, CardPresentationLayer Layer)
{
    public BoardView View { get; init; } = BoardView.Identity;
    public Pose? ReaderTarget { get; init; }
    public bool Hovered { get; init; }
    public string Id => Request.Id;
    public Card Card => Request.Card;
    public string Zone => Request.Zone;
    public Pose Rest => View.Apply(Request.Rest);
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
