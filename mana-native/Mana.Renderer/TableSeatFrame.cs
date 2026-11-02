using Mana.Contracts;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>A physical place at the table. Rules player IDs and teams do not
/// determine this presentation transform; the registered turn order does.</summary>
public readonly record struct TableSeatFrame(int Index, bool IsLocal, Point Center,
    int QuarterTurns, Vector2 Hero, Vector2 Hand, bool UprightFaces = false, bool LocalApron = false)
{
    public Vector2 BattlefieldEntrance { get; init; } = Hero;
    public bool IsSide => QuarterTurns is 1 or 3;
    public bool IsFar => QuarterTurns == 2;
    public float Facing => UprightFaces ? 0 : QuarterTurns switch {
        1 => MathF.PI / 2, 2 => MathF.PI, 3 => -MathF.PI / 2, _ => 0
    };
    public Vector2 Outward => QuarterTurns switch {
        1 => -Vector2.UnitX, 2 => -Vector2.UnitY, 3 => Vector2.UnitX, _ => Vector2.UnitY
    };

    public Vector2 Project(Vector2 local)
    {
        var delta = local - PlayerPlaymat.CanonicalBounds.Center.ToVector2();
        return Center.ToVector2() + (QuarterTurns switch {
            1 => new Vector2(-delta.Y, delta.X), 2 => -delta,
            3 => new Vector2(delta.Y, -delta.X), _ => delta
        });
    }

    public Rectangle Project(Rectangle local)
    {
        var origin = PlayerPlaymat.CanonicalBounds.Center;
        int left = local.Left - origin.X, right = local.Right - origin.X;
        int top = local.Top - origin.Y, bottom = local.Bottom - origin.Y;
        return QuarterTurns switch {
            1 => new(Center.X - bottom, Center.Y + left, local.Height, local.Width),
            2 => new(Center.X - right, Center.Y - bottom, local.Width, local.Height),
            3 => new(Center.X + top, Center.Y - right, local.Height, local.Width),
            _ => new(Center.X + left, Center.Y + top, local.Width, local.Height)
        };
    }

    public static TableSeatFrame For(int index, int players = 4, bool study = true, bool duel = false)
    {
        if (players is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(players));
        if (players <= 4) {
            if (index is < 0 or > 3 || duel && index is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(index));
            var center = index switch { 0 => new Point(368, 448), 1 => new Point(800, 304),
                2 => new Point(1232, 448), _ => new Point(800, 660) };
            var hero = TableLayout.Hero(index);
            var hand = TableLayout.SeatHand(index);
            if (study && index is 0 or 2) {
                hero -= new Vector2(0, 40);
                hand = new(index == 0 ? 84 : 1516, hand.Y - 80);
            }
            return new(index, index == 3, center, index switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 },
                hero, hand, duel, index == 3) { BattlefieldEntrance = TableLayout.Hero(index) };
        }
        // Three places along each long edge of a larger rectangular table.
        // Five players leave the far center open; furniture never follows card
        // counts, active players, elimination, camera focus or team changes.
        var (place, turns) = (players, index) switch {
            (_, 3) => (new Point(800, 660), 0),
            (_, 0) => (new Point(224, 660), 0),
            (5, 1) => (new Point(512, 304), 2),
            (5, 2) => (new Point(1088, 304), 2),
            (5, 4) => (new Point(1376, 660), 0),
            (6, 1) => (new Point(224, 304), 2),
            (6, 2) => (new Point(800, 304), 2),
            (6, 4) => (new Point(1376, 304), 2),
            (6, 5) => (new Point(1376, 660), 0),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        // Fixed identity controls sit at their physical mat's default camera
        // edge. Focusing/panning moves the shared table beneath those anchors;
        // it never rebuilds the player's furniture or moves the local portrait.
        var home = TableViewGeometry.Overview(players);
        var edge = home.Apply(TableSurfaceProjection.Project(
            new Vector2(place.X, place.Y + (turns == 2 ? -144 : 144))));
        var physical = new TableSeatFrame(index, index == 3, place, turns, Vector2.Zero, Vector2.Zero);
        // Pile thickness is visually capped. Reserve that complete prepared
        // envelope once, so a changing library count never moves an identity.
        int pileTop = SeatZoneLayout.PreparePiles(physical)
            .Min(pile => home.Apply(PileGeometry.Create(pile, int.MaxValue).Reservation).Top);
        var portrait = index == 3 ? TableLayout.Hero(3) : turns == 2
            ? new Vector2(edge.X, pileTop - 120) : edge + new Vector2(0, 66);
        var held = index == 3 ? TableLayout.LocalHand
            // The complete far mana halo ends 40px below the portrait. Keep
            // all seven held backs in the next band, clear of any mana width.
            : turns == 2 ? portrait + new Vector2(-180, 74)
            : portrait + new Vector2(index == 0 ? -160 : 160, 0);
        return new(index, index == 3, place, turns, portrait, held);
    }

    public static int DisplayIndex(GameSnapshot state, int player)
    {
        if (player == state.ViewerId) return 3;
        if (state.Viewer == null) {
            int registered = state.Players.IndexOf(state.Players.First(candidate => candidate.Id == player));
            return registered == 0 ? 3 : state.Players.Length == 2 ? 1 : registered <= 3 ? registered - 1 : registered;
        }
        if (state.Players.Length == 2) return 1;
        int ordinal = TableLayout.SeatIndex(state, player);
        return state.Players.Length > 4 && ordinal >= 3 ? ordinal + 1 : ordinal;
    }

    public static Rectangle Tabletop(int players) => players <= 4
        ? PlayerPlaymat.TabletopPlacement : new(-64, 136, 1728, 708);
}
