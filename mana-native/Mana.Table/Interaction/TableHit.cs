using Mana.Contracts;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Interaction;

// Input geometry belongs to the displayed occurrence. The table host still
// owns actions and checks their current game/decision scope before dispatch.
internal sealed record TableHit(string Id, string Scope, Rectangle Bounds, Action Action,
    Card? Card = null, string? Zone = null, int? PlayerId = null, Func<Point, bool>? Shape = null)
{
    public bool Contains(Point point) => Bounds.Contains(point) && (Shape?.Invoke(point) ?? true);
}
