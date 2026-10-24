using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

// Header controls share reserved slots, including features composed later by
// the social layer. Conditional content never takes another control's space.
internal static class MatchHeaderLayout
{
    internal static readonly Rectangle Turn = new(220, 9, 280, 21);
    internal static readonly Rectangle Phase = new(220, 32, 280, 18);
    internal static readonly Rectangle Battlefields = new(510, 12, 178, 35);
    internal static readonly Rectangle TableStates = new(700, 12, 154, 35);
    internal static readonly Rectangle Changes = new(866, 12, 176, 35);
    internal static readonly Rectangle Chat = new(1052, 12, 126, 35);
    internal static readonly Rectangle History = new(1190, 12, 110, 35);
    internal static readonly Rectangle Settings = new(1310, 12, 112, 35);
    internal static readonly Rectangle Departure = new(1432, 12, 143, 35);
}
