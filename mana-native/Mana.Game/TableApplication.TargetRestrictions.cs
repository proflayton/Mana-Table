using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Only an occurrence and its current input scope are retained for crossing
    // into the explanation. Every displayed fact is looked up again each frame.
    private string targetRestrictionScope = "", targetRestrictionCard = "";
    private Rectangle targetRestrictionBounds;

    private static string TargetRestrictionCaption(TargetRestriction restriction) => restriction.Reason switch {
        TargetRestrictionReason.Shroud => "Can't target: Shroud",
        TargetRestrictionReason.Hexproof => "Can't target: Hexproof",
        TargetRestrictionReason.Protection => "Can't target: Protection",
        _ => "Can't target"
    };

    private static string TargetRestrictionDescription(GameSnapshot state, TargetRestriction restriction)
    {
        string text = restriction.Detail;
        if (restriction.SourceCardId != null && TargetRestrictionFacts.PublicCard(state, restriction.SourceCardId) is { } source)
            text += "\nFrom " + source.Name + ".";
        return text;
    }

    private void DrawTargetRestrictionFeedback()
    {
        void Clear()
        {
            targetRestrictionScope = targetRestrictionCard = "";
            targetRestrictionBounds = Rectangle.Empty;
        }
        if (!InputReady || busy || session.NeedsRecovery || AbilityActionPending || dragging || boardPanning || boardKeyboardPanning
            || spreadPanning || spreadKeyboardPanning || match is not { } state || OverlayOpen || StartupVisible
            || HistoryOpen || updatesOpen || ActivityOpen || ExperienceOpen || tableGuideOpen) { Clear(); return; }
        Card? card = null;
        TargetRestriction? restriction = null;
        var hit = HitAt(pointer);
        if (hit is { Card: { FaceDown: false } hoveredCard } && CurrentHit(hit)
            && hit.Zone == "Battlefield"
            && (restriction = TargetRestrictionFacts.For(state, hoveredCard)) != null) card = hoveredCard;
        else if (targetRestrictionScope == Scope && targetRestrictionBounds.Contains(pointer)
            && TargetRestrictionFacts.PublicCard(state, targetRestrictionCard) is { } retained
            && (restriction = TargetRestrictionFacts.For(state, retained)) != null) card = retained;
        if (card == null || restriction == null) { Clear(); return; }
        var entry = matchPresentation.Cards.Cards.LastOrDefault(item => item.ChoiceSlot < 0 && item.Zone == "Battlefield"
            && item.Card.VisualId == card.VisualId && CardContext.CanRead(item));
        if (entry == null) { Clear(); return; }
        targetRestrictionScope = Scope; targetRestrictionCard = card.VisualId;
        canvas.CardFrame(entry.Display with { Size = entry.Display.Size + new Vector2(7) }, Red, 2);
        if (restriction.SourceCardId != null) {
            var source = matchPresentation.Cards.Cards.LastOrDefault(item => item.ChoiceSlot < 0 && item.Zone == "Battlefield"
                && item.Card.VisualId == restriction.SourceCardId && CardContext.CanRead(item));
            if (source != null && source != entry)
                canvas.CardFrame(source.Display with { Size = source.Display.Size + new Vector2(5) }, Gold * .8f, 2);
        }
        // A pinned card already has the same explanation in its live-state
        // column. Keep the source cue while avoiding a second prose panel.
        if (IsStudyBattlefieldReader(entry)) { targetRestrictionBounds = Rectangle.Empty; return; }
        const float titleFont = .8f, detailFont = .72f;
        int width = 380;
        SourceActionTextLayout title, detail;
        int height;
        do {
            title = SourceActionTextLayout.Measure(canvas, TargetRestrictionCaption(restriction), titleFont, width - 32, true);
            detail = SourceActionTextLayout.Measure(canvas, TargetRestrictionDescription(state, restriction), detailFont, width - 32, false);
            height = 26 + title.Height + detail.Height + 8;
            if (height <= 560 || width >= 700) break;
            width += 80;
        } while (true);
        var reservations = new List<Rectangle> { decisionPanelBounds, sourceReaderActionsBounds, battlefieldReaderStateBounds, keywordHelpBounds };
        if (chatOpen) reservations.Add(ChatBounds);
        if (hoverPreview is { } reader) reservations.Add(reader);
        if (manaJourneyPaint is { } payment) { reservations.Add(payment.Bounds); reservations.Add(payment.SourceBounds); }
        var room = DecisionActionArea(entry.Display.Bounds, width, height, additionalReservations: reservations,
            minimumWidth: width, boundedWorkspace: true);
        var area = new Rectangle(room.X, room.Y, width, height);
        if (room.Width < width || room.Height < height || DecisionActionReservations(entry.Display.Bounds, false, reservations)
            .Any(bounds => !bounds.IsEmpty && bounds.Intersects(area))) { targetRestrictionBounds = Rectangle.Empty; return; }
        targetRestrictionBounds = area;
        canvas.Panel(area, new Color(24, 17, 21) * .98f, Red * .75f, 6);
        int y = area.Top + 12;
        for (int line = 0; line < title.Lines.Count; line++)
            PaintSourceActionLine(title, line, area.Left + 16, y + line * title.LineHeight, Red, titleFont, true);
        y += title.Height + 8;
        for (int line = 0; line < detail.Lines.Count; line++)
            PaintSourceActionLine(detail, line, area.Left + 16, y + line * detail.LineHeight, Ink, detailFont, false);
        // This blocks only the explanation itself. The actual blocked card
        // keeps its ordinary inspection hit and never acquires a SelectCard.
        hits.Add(new("target-restriction-help", Scope, area, UiIntent.None, ShowPointer: false));
    }
}
