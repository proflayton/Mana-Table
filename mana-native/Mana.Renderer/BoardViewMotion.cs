using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Only public turn ownership and explicit application policy enter this request.</summary>
public sealed record BoardViewRequest(string GameId, int Turn, int? ActivePlayerId,
    int ViewerId, int PlayerCount, bool Enabled, bool InteractionBlocked,
    bool NeedsRecovery, bool ReducedMotion, Vector2 RequestedOffset);

/// <summary>
/// A deliberately small, opt-in turn-follow transition. Priority changes cannot
/// request movement. Interruptions consume their turn boundary instead of
/// queuing a movement for when the player finishes interacting.
/// </summary>
public sealed class BoardViewMotion
{
    public const float MaximumHorizontalOffset = 48;
    public const float MaximumVerticalOffset = 24;
    public const double TransitionSeconds = .55;

    private string? gameId;
    private int viewerId, turn;
    private int? activePlayerId;
    private bool eligible, moving;
    private BoardView origin, destination;
    private double startedAt, lastTime;

    public BoardView Current { get; private set; } = BoardView.Identity;
    public bool Paused { get; private set; }
    public bool LimitedByLayout { get; private set; }

    public void Pause() { Paused = true; moving = false; }
    public void Resume() { Paused = false; }
    public void Reset()
    {
        Current = origin = destination = BoardView.Identity;
        gameId = null; activePlayerId = null; viewerId = turn = 0;
        eligible = moving = Paused = LimitedByLayout = false; startedAt = lastTime = 0;
    }

    public BoardView Update(BoardViewRequest request, double totalSeconds,
        IReadOnlyList<Rectangle> boardFootprints, IReadOnlyList<Rectangle> fixedReservations,
        Rectangle viewport)
    {
        bool sameGame = gameId == request.GameId && viewerId == request.ViewerId;
        bool changedTurn = turn != request.Turn || activePlayerId != request.ActivePlayerId;
        bool mayFollow = request.Enabled && !request.NeedsRecovery && !request.ReducedMotion
            && request.PlayerCount > 2 && request.Turn > 0 && request.ActivePlayerId != null;
        bool validClock = double.IsFinite(totalSeconds);

        // Observe even when blocked: closing a reader or resuming cannot replay
        // a turn boundary that happened while the player was occupied.
        gameId = request.GameId; viewerId = request.ViewerId;
        turn = request.Turn; activePlayerId = request.ActivePlayerId;

        if (!mayFollow || !sameGame || !eligible || !validClock || totalSeconds < lastTime) {
            Current = origin = destination = BoardView.Identity; moving = LimitedByLayout = false;
            eligible = mayFollow && validClock;
            lastTime = validClock ? totalSeconds : 0;
            return Current;
        }
        lastTime = totalSeconds;
        // Layout and fixed surfaces can change after a transition has settled,
        // including while a required choice or manual inspection blocks motion.
        // Recover to canonical geometry immediately; the observed turn above is
        // consumed, so removing the obstruction cannot schedule a delayed pan.
        if (!Current.IsIdentity && !PathAllowed(Current, Current, boardFootprints, fixedReservations, viewport)) {
            Current = origin = destination = BoardView.Identity; moving = false;
            LimitedByLayout = true;
            return Current;
        }
        if (Paused || request.InteractionBlocked) { moving = false; return Current; }

        if (moving) {
            // Recheck the entire remaining path against this frame's layout;
            // a newly reserved surface must never be crossed on the way there.
            if (!PathAllowed(Current, destination, boardFootprints, fixedReservations, viewport)) {
                moving = false; LimitedByLayout = true; return Current;
            }
            float amount = (float)Math.Clamp((totalSeconds - startedAt) / TransitionSeconds, 0, 1);
            amount = amount * amount * (3 - 2 * amount);
            Current = new(Vector2.Lerp(origin.Offset, destination.Offset, amount));
            if (amount >= 1) moving = false;
        }

        if (!changedTurn) return Current;
        moving = false;
        var requested = request.RequestedOffset;
        if (!float.IsFinite(requested.X) || !float.IsFinite(requested.Y)) return Current;
        var target = new BoardView(new(Math.Clamp(requested.X, -MaximumHorizontalOffset, MaximumHorizontalOffset),
            Math.Clamp(requested.Y, -MaximumVerticalOffset, MaximumVerticalOffset)));
        if (target == Current) { LimitedByLayout = false; return Current; }
        if (!PathAllowed(Current, target, boardFootprints, fixedReservations, viewport)) {
            LimitedByLayout = true; return Current;
        }
        LimitedByLayout = false;
        origin = Current; destination = target; startedAt = totalSeconds; moving = true;
        return Current;
    }

    private static bool PathAllowed(BoardView from, BoardView to,
        IReadOnlyList<Rectangle> footprints, IReadOnlyList<Rectangle> reservations, Rectangle viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || footprints.Count == 0) return false;
        foreach (var footprint in footprints) {
            if (footprint.Width <= 0 || footprint.Height <= 0) continue;
            // Translation sweeps every point along one line. Its rectangular
            // envelope is conservative for both projected faces and labels.
            float left = footprint.Left + Math.Min(from.Offset.X, to.Offset.X);
            float top = footprint.Top + Math.Min(from.Offset.Y, to.Offset.Y);
            float right = footprint.Right + Math.Max(from.Offset.X, to.Offset.X);
            float bottom = footprint.Bottom + Math.Max(from.Offset.Y, to.Offset.Y);
            if (left < viewport.Left || top < viewport.Top || right > viewport.Right || bottom > viewport.Bottom) return false;
            foreach (var reservation in reservations) {
                if (reservation.Width > 0 && reservation.Height > 0
                    && left < reservation.Right && right > reservation.Left
                    && top < reservation.Bottom && bottom > reservation.Top) return false;
            }
        }
        return true;
    }
}
