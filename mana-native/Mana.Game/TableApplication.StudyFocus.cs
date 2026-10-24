using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private int? studyFocusOwner;
    private string studyFocusGame = "";
    private int studyFocusViewer;
    private bool MultiplayerFocusAvailable => TableStudyEnabled && match?.Players.Length > 2;
    private bool StudyFocusActive => MultiplayerFocusAvailable && studyFocusOwner != null;
    private bool TwoFieldTable => TableStudyEnabled && match?.Players.Length == 2;
    private Rectangle StudyActionArea => TwoFieldTable || StudyFocusActive || !boardView.IsIdentity ? DuelTableStudy.ActionArea : SharedTableStudy.ActionArea;

    private Vector2 SeatHero(int player) => tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == player)?.Geometry.Hero
        ?? TableLayout.Hero(player == match?.ViewerId ? 3 : match == null ? 1 : TableLayout.SeatIndex(match, player));
    private Vector2 SeatHand(int player) => tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == player)?.Geometry.Hand
        ?? TableLayout.SeatHand(player == match?.ViewerId ? 3 : match == null ? 1 : TableLayout.SeatIndex(match, player));
    private int SeatDisplayIndex(int player) => tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == player)?.Index
            ?? (player == match?.ViewerId ? 3 : match == null ? 1 : TableLayout.SeatIndex(match, player));

    private void ValidateStudyFocus()
    {
        if (!TableStudyEnabled || match == null || studyFocusGame != match.Id || studyFocusViewer != match.ViewerId
            || !match.Players.Any(player => player.Id == studyFocusOwner)) {
            studyFocusOwner = null; studyFocusGame = "";
        }
    }

    private void SetStudyFocus(int? owner)
    {
        if (!MultiplayerFocusAvailable || match == null || dragging) return;
        if (owner != null && !match.Players.Any(player => player.Id == owner)) return;
        CollapseCardReading(); ClearReaderActions();
        pressed = null; dragSource = null; ClearCardDrag();
        zoneName = null; overlay = "";
        studyFocusOwner = owner;
        studyFocusGame = owner == null ? "" : match.Id;
        studyFocusViewer = match.ViewerId;
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
        hits.Add(new(id, Scope, bounds, new MatchIntent.SetStudyFocus(null)));
    }

}
