using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

// Upright player state belongs to the fixed seat HUD. Only physical cards and
// cloth enter the table camera. These measured pieces own paint, hits and space.
internal enum SeatReadoutTextRole { LandTotal, LandState, Status, Incoming }
internal sealed record SeatReadoutText(string Text, Rectangle Bounds, float Size, bool Bold, SeatReadoutTextRole Role);
internal sealed record SeatReadoutState(string Id, string Text, string Kind, Rectangle Bounds, int MeasuredWidth, bool More);
internal abstract record SeatReadoutPlacement
{
    internal sealed record Placed(SeatReadoutPlan Plan) : SeatReadoutPlacement;
    internal sealed record NoFit(int PlayerId, int SeatOrdinal) : SeatReadoutPlacement;
}
internal delegate SeatReadoutPlacement SeatReadoutPlacer(IReadOnlyList<SeatReadoutPlan> arrangements, SeatReadoutPlan reserved,
    Rectangle viewport, IReadOnlyList<Rectangle> occupied);
internal sealed record SeatReadoutPlan(int PlayerId, int Seat, int SeatOrdinal, IReadOnlyList<SeatReadoutText> Text,
    IReadOnlyList<SeatReadoutState> States, int HiddenStates, Rectangle CounterBounds)
{
    internal IReadOnlyList<Rectangle> Pieces => Text.Select(item => item.Bounds).Concat(States.Select(item => item.Bounds)).ToArray();
    internal Rectangle Bounds => Pieces.Aggregate(Rectangle.Union);

    internal static SeatReadoutPlan Build(Player player, int seat, int seatOrdinal, string caption, bool incoming,
        Func<string, float, bool, int> width, Func<string, float, int, bool, Point> measure,
        int column = 224, bool? horizontal = null, bool countedOnly = false)
    {
        const int gap = 4;
        bool across = horizontal ?? seat == 1;
        int x = seat switch { 0 => 188, 2 => 1188, _ => 432 };
        int y = seat == 1 ? 172 : 110;
        var texts = new List<SeatReadoutText>();
        var states = new List<SeatReadoutState>();
        var source = StateClarity.ProminentPlayerStates(player);
        int stateX = across ? x + column + 12 : x, cursor = 0, row = 0;
        int rowHeight = Math.Max(24, source.Select(StateClarity.StateCaption).Where(text => width(text, .6f, true) <= column - 12)
            .Select(text => measure(text, .6f, column - 12, true).Y + 4).DefaultIfEmpty(24).Max());
        foreach (var state in source) {
            if (countedOnly) break;
            string text = StateClarity.StateCaption(state);
            int measured = width(text, .6f, true), size = measured + 12;
            if (size > column) break; // Keep the complete name in the counted detail reader.
            if (cursor > 0 && cursor + size > column) { row++; cursor = 0; }
            if (row >= 2) break;
            states.Add(new(state.Id, text, state.Kind, new(stateX + cursor, y + row * (rowHeight + 3), size, rowHeight), measured, false));
            cursor += size + 3;
        }
        int hidden = source.Length - states.Count;
        if (hidden > 0) {
            string More() => "+" + hidden + " states";
            while (states.Count > 0) {
                var last = states[^1];
                cursor = last.Bounds.Right - stateX + 3; row = (last.Bounds.Y - y) / (rowHeight + 3);
                if (cursor + width(More(), .53f, true) + 12 <= column || row == 0) break;
                states.RemoveAt(states.Count - 1); hidden++;
            }
            int moreWidth = width(More(), .53f, true) + 12;
            if (cursor + moreWidth > column) { row++; cursor = 0; }
            if (states.Count == 0) { row = 0; cursor = 0; }
            states.Add(new("", More(), "", new(stateX + cursor, y + row * (rowHeight + 3), moreWidth, rowHeight), moreWidth - 12, true));
        }
        var counters = states.Count == 0 ? Rectangle.Empty : states.Select(item => item.Bounds).Aggregate(Rectangle.Union);
        int landY = across || counters.IsEmpty ? y : counters.Bottom + gap;
        var lands = PublicResources.Lands(player);
        void Line(string text, float size, bool bold, SeatReadoutTextRole role) {
            var measured = measure(text, size, column, bold);
            texts.Add(new(text, new(x, landY, measured.X, measured.Y), size, bold, role));
            landY += measured.Y + gap;
        }
        string owner = countedOnly ? "Seat " + seatOrdinal : player.Name;
        Line(owner + " · " + lands.Total + (lands.Total == 1 ? " land" : " lands"), .75f, true, SeatReadoutTextRole.LandTotal);
        Line(lands.Untapped + " untapped · " + lands.Tapped + " tapped", .55f, false, SeatReadoutTextRole.LandState);
        if (caption.Length > 0) {
            var measured = measure(caption, .53f, column, true);
            int captionX = across ? stateX : x;
            int captionY = across ? counters.IsEmpty ? y : counters.Bottom + gap : landY;
            texts.Add(new(caption, new(captionX, captionY, measured.X, measured.Y), .53f, true,
                incoming ? SeatReadoutTextRole.Incoming : SeatReadoutTextRole.Status));
        }
        return new(player.Id, seat, seatOrdinal, texts.AsReadOnly(), states.AsReadOnly(), hidden, counters);
    }

    internal SeatReadoutPlan Translate(Point offset)
    {
        Rectangle Move(Rectangle rectangle) => rectangle.IsEmpty ? rectangle : new(rectangle.X + offset.X, rectangle.Y + offset.Y, rectangle.Width, rectangle.Height);
        return this with { Text = Text.Select(item => item with { Bounds = Move(item.Bounds) }).ToArray(),
            States = States.Select(item => item with { Bounds = Move(item.Bounds) }).ToArray(), CounterBounds = Move(CounterBounds) };
    }

    internal static SeatReadoutPlacement Place(IReadOnlyList<SeatReadoutPlan> arrangements, SeatReadoutPlan reserved,
        Rectangle viewport, IReadOnlyList<Rectangle> occupied)
    {
        foreach (var plan in arrangements) {
            if (DecisionWorkspaceLayout.TranslateComplete(plan.Pieces, viewport, occupied) is { } placed)
                return new SeatReadoutPlacement.Placed(plan.Translate(placed.Location - plan.Bounds.Location));
        }
        // Only the bounded public seat marker enters this reserved plan. Full
        // names remain in the attempted arrangements and the detail reader.
        if (reserved.Pieces.All(piece => viewport.Contains(piece) && occupied.All(obstacle => !obstacle.Intersects(piece))))
            return new SeatReadoutPlacement.Placed(reserved);
        if (DecisionWorkspaceLayout.TranslateComplete(reserved.Pieces, viewport, occupied) is { } placedReserved)
            return new SeatReadoutPlacement.Placed(reserved.Translate(placedReserved.Location - reserved.Bounds.Location));
        return new SeatReadoutPlacement.NoFit(reserved.PlayerId, reserved.SeatOrdinal);
    }
}
