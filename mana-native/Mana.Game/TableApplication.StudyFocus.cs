using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private int? studyFocusOwner, studyFocusOpponent;
    private string studyFocusGame = "";
    private bool StudyFocusActive => TableStudyEnabled && studyFocusOwner != null && studyFocusOpponent != null;
    private bool StudyRail(int player) => StudyFocusActive && player != match?.ViewerId && player != studyFocusOpponent;
    private Rectangle StudyActionArea => StudyFocusActive ? DuelTableStudy.ActionArea : SharedTableStudy.ActionArea;

    private Vector2 SeatHero(int player) => tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == player)?.Geometry.Hero
        ?? TableLayout.Hero(player == match?.ViewerId ? 3 : match == null ? 1 : TableLayout.SeatIndex(match, player));
    private Vector2 SeatHand(int player) => tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == player)?.Geometry.Hand
        ?? TableLayout.SeatHand(player == match?.ViewerId ? 3 : match == null ? 1 : TableLayout.SeatIndex(match, player));
    // Both condensed seats face inward from the left rail. Their actual player
    // IDs and scene seats remain distinct; this selects their HUD orientation.
    private int SeatDisplayIndex(int player) => StudyRail(player) ? 0
        : tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == player)?.Index
            ?? (player == match?.ViewerId ? 3 : match == null ? 1 : TableLayout.SeatIndex(match, player));

    private void ValidateStudyFocus()
    {
        if (!TableStudyEnabled || match == null || studyFocusGame != match.Id
            || !match.Players.Any(player => player.Id == studyFocusOwner)
            || !match.Players.Any(player => player.Id == studyFocusOpponent)) {
            studyFocusOwner = studyFocusOpponent = null; studyFocusGame = "";
        }
    }

    private void SetStudyFocus(int? owner)
    {
        if (!TableStudyEnabled || match == null || dragging) return;
        if (owner != null && !match.Players.Any(player => player.Id == owner)) return;
        ClearCardReading(); ClearReaderActions();
        pressed = null; dragSource = null; ClearCardDrag();
        zoneName = null; overlay = "";
        studyFocusOwner = owner;
        studyFocusOpponent = owner == null ? null : owner != match.ViewerId ? owner
            : match.Players.FirstOrDefault(player => player.Id != match.ViewerId && player.Id == match.ActivePlayerId)?.Id
                ?? TableLayout.Opponents(match).ElementAtOrDefault(1)?.Id ?? TableLayout.Opponents(match).FirstOrDefault()?.Id;
        studyFocusGame = owner == null ? "" : match.Id;
        boardViewMotion.Reset(); boardView = BoardView.Identity;
    }

    private void DrawStudyFocusNavigation()
    {
        if (!StudyFocusActive || OverlayOpen) return;
        var bounds = new Rectangle(276, 72, 152, 34);
        string id = "study-focus-return";
        bool hover = IsControlHovered(id);
        if (hover) canvas.Rounded(bounds, new Color(51, 77, 84) * .65f, 5);
        var center = new Vector2(bounds.X + 15, bounds.Center.Y);
        var color = hover ? Blue : Ink;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2) {
                var corner = center + new Vector2(x * 7, y * 7);
                canvas.Line(corner, corner - new Vector2(x * 5, 0), color, 1.5f);
                canvas.Line(corner, corner - new Vector2(0, y * 5), color, 1.5f);
            }
        canvas.Text("Whole table", bounds.X + 32, bounds.Y + 6, color, .65f, bounds.Width - 34, 80, true);
        hits.Add(new(id, Scope, bounds, () => SetStudyFocus(null)));
    }

    private void DrawStudyRailCount(Player player)
    {
        if (!StudyRail(player.Id)) return;
        var center = SeatHero(player.Id);
        var card = new Rectangle((int)center.X - 21, (int)center.Y + 83, 16, 21);
        canvas.Border(new(card.X - 3, card.Y - 3, card.Width, card.Height), Muted * .5f);
        canvas.Border(card, Muted);
        bool choices = player.Zone("Battlefield").Cards.Any(IsCardActionable);
        canvas.Text(player.Zone("Battlefield").Count.ToString(), card.Right + 7, card.Y + 1,
            choices ? ActionAccent : Ink, .65f, 90, 80, true);
        if (choices) canvas.Ring(center, 44, ActionAccent, 2);
        for (int index = 0; index < 3; index++) {
            var back = new Pose(new(center.X - 16 + index * 5, center.Y + 122), new(12, 17), (index - 1) * .15f);
            canvas.Surface(null, back, new(28, 47, 56));
            canvas.CardFrame(back, Muted * .8f);
        }
        canvas.Text(player.Zone("Hand").Count.ToString(), (int)center.X + 2, (int)center.Y + 111,
            Ink, .65f, 90, 80, true);
    }
}
