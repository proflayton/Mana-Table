using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static readonly Rectangle BoardViewport = new(0, 54, 1600, 724);
    private readonly BoardViewMotion boardViewMotion = new();
    private BoardView boardView = BoardView.Identity;
    private bool followTurnEnabled, boardPointerActivity, boardViewportChanged;
    private Point boardViewport;
    private BoardView? manualBoardView;
    private bool boardPanning;
    private Point boardPanOrigin;
    private BoardView boardPanView;

    private void UpdateBoardCamera(MouseState mouse, KeyboardState keys)
    {
        if (!TableStudyEnabled || CardCollectionOpen || OverlayOpen || textFocus || chatFocus || dragging || readingId != null) {
            boardPanning = false; return;
        }
        if (keys.IsKeyDown(Keys.Home) && previousKeys.IsKeyUp(Keys.Home)) {
            manualBoardView = null; boardPanning = false; SetStudyFocus(null); return;
        }
        // Retain pointer capture until release, including outside the board.
        if (!BoardViewport.Contains(pointer) && !boardPanning) return;
        int wheel = mouse.ScrollWheelValue - previousMouse.ScrollWheelValue;
        if (wheel != 0 && pressed == null) {
            var start = manualBoardView ?? boardView;
            float scale = Math.Clamp(start.Scale * MathF.Pow(1.18f, wheel / 120f), .45f, 8);
            manualBoardView = new(pointer.ToVector2() - start.Inverse(pointer.ToVector2()) * scale, scale);
            session.CancelAdvance();
        }
        if (mouse.MiddleButton == ButtonState.Pressed && previousMouse.MiddleButton == ButtonState.Released) {
            boardPanning = true; boardPanOrigin = pointer; boardPanView = manualBoardView ?? boardView;
            session.CancelAdvance();
        }
        if (boardPanning && mouse.MiddleButton == ButtonState.Pressed)
            manualBoardView = boardPanView with { Offset = boardPanView.Offset + (pointer - boardPanOrigin).ToVector2() };
        if (mouse.MiddleButton == ButtonState.Released) boardPanning = false;
    }

    private void SetTurnFollow(bool enabled)
    {
        followTurnEnabled = enabled;
        if (enabled) boardViewMotion.Resume();
        else { manualBoardView = null; boardPanning = false; boardViewMotion.Reset(); boardView = BoardView.Identity; }
    }

    private void UpdateBoardView()
    {
        if (match is not { } state || tableScene == null) {
            boardViewMotion.Reset(); boardView = BoardView.Identity; return;
        }
        if (TableStudyEnabled) {
            var targetView = manualBoardView ?? (StudyFocusActive
                ? BoardView.Frame(SharedTableStudy.Territory(tableScene.Seat(studyFocusOwner!.Value).Index), new(195, 170, 995, 530))
                : BoardView.Identity);
            boardView = boardViewMotion.Navigate(state.Id, state.ViewerId, targetView, now, preferences.ReducedMotion,
                dragging || pressed?.Card != null || readingId != null || OverlayOpen);
            return;
        }
        bool enabled = followTurnEnabled && !TableStudyEnabled && state.Players.Length is 3 or 4;
        bool blocked = OverlayOpen || readingId != null || matchPresentation.Cards.HasReaderTransition
            || pressed != null || dragging || previousMouse.LeftButton == ButtonState.Pressed
            || boardViewportChanged || boardPointerActivity && pointer.Y is >= 54 and < 778
            || HitAt(pointer)?.Card != null || state.Stack.Length > 0 || state.Combat != null
            || state.Decision is { Payload: not PriorityPayload }
            || state.DecisionOwnerId != null && state.ActionClass is not ("priority" or "waiting");
        Vector2 target = state.ActivePlayerId == state.ViewerId ? Vector2.Zero
            : TableLayout.SeatIndex(state, state.ActivePlayerId.GetValueOrDefault(state.ViewerId)) switch {
                0 => new(28, 0), 1 => new(0, 8), 2 => new(-28, 0), _ => Vector2.Zero
            };
        var footprints = enabled ? TableViewGeometry.BoardFootprints(tableScene).ToList() : [];
        if (enabled) {
            // Existing displayed poses include transient elevation/flight. Guard
            // those too, without retaining faces in the view controller.
            foreach (var card in matchPresentation.Cards.Cards.Where(card => BoardView.AffectsZone(card.Zone) && !card.Focused))
                footprints.Add(boardView.Inverse(card.Display).Bounds);
        }
        boardView = boardViewMotion.Update(new(state.Id, state.Turn, state.ActivePlayerId, state.ViewerId,
            state.Players.Length, enabled, blocked, session.NeedsRecovery, preferences.ReducedMotion, target),
            now, footprints, enabled ? TableViewGeometry.FixedReservations(state, tableScene) : [], new(0, 54, 1600, 724));
    }
}
