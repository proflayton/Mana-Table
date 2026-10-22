using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly BoardViewMotion boardViewMotion = new();
    private BoardView boardView = BoardView.Identity;
    private bool followTurnEnabled, boardPointerActivity, boardViewportChanged;
    private Point boardViewport;

    private void SetTurnFollow(bool enabled)
    {
        followTurnEnabled = enabled;
        if (enabled) boardViewMotion.Resume();
        else { boardViewMotion.Reset(); boardView = BoardView.Identity; }
    }

    private void UpdateBoardView()
    {
        if (match is not { } state || tableScene == null) {
            boardViewMotion.Reset(); boardView = BoardView.Identity; return;
        }
        bool dense = tableScene.Seats.Any(seat => seat.IsDense);
        bool enabled = followTurnEnabled && !TableStudyEnabled && !dense && state.Players.Length is 3 or 4;
        bool blocked = OverlayOpen || readingId != null || cardPresentation.HasReaderTransition
            || pressed != null || dragging || previousMouse.LeftButton == ButtonState.Pressed
            || boardViewportChanged || boardPointerActivity && pointer.Y is >= 54 and < 778
            || HitAt(pointer)?.Card != null || state.Stack.Length > 0 || state.Combat != null
            || state.Decision is { Intent: not DecisionIntent.Priority }
            || state.DecisionOwnerId != null && state.ActionClass is not ("priority" or "waiting");
        Vector2 target = state.ActivePlayerId == state.ViewerId ? Vector2.Zero
            : TableLayout.SeatIndex(state, state.ActivePlayerId.GetValueOrDefault(state.ViewerId)) switch {
                0 => new(28, 0), 1 => new(0, 8), 2 => new(-28, 0), _ => Vector2.Zero
            };
        var footprints = enabled ? TableViewGeometry.BoardFootprints(tableScene).ToList() : [];
        if (enabled) {
            // Existing displayed poses include transient elevation/flight. Guard
            // those too, without retaining faces in the view controller.
            foreach (var card in cardPresentation.Cards.Where(card => BoardView.AffectsZone(card.Zone) && !card.Focused))
                footprints.Add(boardView.Inverse(card.Display).Bounds);
        }
        boardView = boardViewMotion.Update(new(state.Id, state.Turn, state.ActivePlayerId, state.ViewerId,
            state.Players.Length, enabled, blocked, session.NeedsRecovery, preferences.ReducedMotion, target),
            now, footprints, enabled ? TableViewGeometry.FixedReservations(state, tableScene) : [], new(0, 54, 1600, 724));
    }
}
