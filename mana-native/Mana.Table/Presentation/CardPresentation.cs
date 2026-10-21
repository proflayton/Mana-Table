using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

/// <summary>The current authority and interaction policy used to prepare cards.</summary>
internal sealed record CardPresentationContext(GameSnapshot? State, string Scope,
    bool OverlayOpen, bool GalleryOpen, bool Dragging, bool CompactDecision)
{
    public Card? ChoiceFace(int slot) => State?.Decision is { } decision
        ? decision.LibraryCards.Length > 0 ? decision.LibraryCards.ElementAtOrDefault(slot)?.Card
            : decision.Choices.ElementAtOrDefault(slot)?.Card
        : null;

    public bool CanRead(PresentedCard entry)
    {
        if (entry.Request.ChoiceSlot < 0)
            return !OverlayOpen && State != null && CardViews.Visible(State)
                .Any(card => CardViews.Same(card, entry.Card) && !card.FaceDown);
        // Anonymous revealed faces belong to an original slot in one decision.
        // Neither their name nor a retained visual id can extend that authority.
        return GalleryOpen && entry.Hit.Scope == Scope
            && ChoiceFace(entry.Request.ChoiceSlot) is { FaceDown: false };
    }
}

internal sealed record CardPresentationRequest(string Id, Card Card, Pose Rest, string Zone,
    int? Owner, string HitId, Action Activate, Vector2? Entrance = null, float EntranceDelay = 0,
    bool OpensZone = false, int ChoiceSlot = -1, Rectangle? ActivationBounds = null);

internal enum CardPresentationLayer { Table, Returning, Reader }

/// <summary>A prepared occurrence. Painting and input share its displayed pose.</summary>
internal sealed record PresentedCard(int Sequence, CardPresentationRequest Request,
    Pose Display, Hit Hit, bool Focused, bool Emphasized, CardPresentationLayer Layer)
{
    public string Id => Request.Id;
    public Card Card => Request.Card;
    public string Zone => Request.Zone;
    public Pose Rest => Request.Rest;
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
