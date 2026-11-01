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
    internal SeatReadoutText Header => Text.Single(item => item.Role == SeatReadoutTextRole.LandTotal);

    internal static SeatReadoutPlan Build(Player player, int seat, int seatOrdinal, string caption, bool incoming,
        Func<string, float, bool, int> width, Func<string, float, int, bool, Point> measure,
        int column = 224, bool? horizontal = null, bool countedOnly = false)
    {
        const int gap = 4;
        bool across = horizontal ?? seat == 1;
        const int x = 0, y = 0;
        var texts = new List<SeatReadoutText>();
        var states = new List<SeatReadoutState>();
        int landY = y;
        void Line(string text, float size, bool bold, SeatReadoutTextRole role) {
            var measured = measure(text, size, column, bold);
            texts.Add(new(text, new(x, landY, measured.X, measured.Y), size, bold, role));
            landY += measured.Y + gap;
        }
        var lands = PublicResources.Lands(player);
        string owner = countedOnly ? "Seat " + seatOrdinal : player.Name;
        Line(owner + " \u00b7 " + lands.Total + (lands.Total == 1 ? " land" : " lands"), .75f, true, SeatReadoutTextRole.LandTotal);
        Line(lands.Untapped + " untapped \u00b7 " + lands.Tapped + " tapped", .55f, false, SeatReadoutTextRole.LandState);
        var source = StateClarity.ProminentPlayerStates(player);
        int stateX = across ? x + column + 12 : x, cursor = 0, row = 0;
        int stateY = across ? y : landY;
        int rowHeight = Math.Max(24, source.Select(StateClarity.StateCaption).Where(text => width(text, .6f, true) <= column - 12)
            .Select(text => measure(text, .6f, column - 12, true).Y + 4).DefaultIfEmpty(24).Max());
        foreach (var state in source) {
            if (countedOnly) break;
            string text = StateClarity.StateCaption(state);
            int measured = width(text, .6f, true), size = measured + 12;
            if (size > column) break; // Keep the complete name in the counted detail reader.
            if (cursor > 0 && cursor + size > column) { row++; cursor = 0; }
            if (row >= 2) break;
            states.Add(new(state.Id, text, state.Kind, new(stateX + cursor, stateY + row * (rowHeight + 3), size, rowHeight), measured, false));
            cursor += size + 3;
        }
        int hidden = source.Length - states.Count;
        if (hidden > 0) {
            string More() => "+" + hidden + " states";
            while (states.Count > 0) {
                var last = states[^1];
                cursor = last.Bounds.Right - stateX + 3; row = (last.Bounds.Y - stateY) / (rowHeight + 3);
                if (cursor + width(More(), .53f, true) + 12 <= column || row == 0) break;
                states.RemoveAt(states.Count - 1); hidden++;
            }
            int moreWidth = width(More(), .53f, true) + 12;
            if (cursor + moreWidth > column) { row++; cursor = 0; }
            if (states.Count == 0) { row = 0; cursor = 0; }
            states.Add(new("", More(), "", new(stateX + cursor, stateY + row * (rowHeight + 3), moreWidth, rowHeight), moreWidth - 12, true));
        }
        var counters = states.Count == 0 ? Rectangle.Empty : states.Select(item => item.Bounds).Aggregate(Rectangle.Union);
        if (caption.Length > 0) {
            var measured = measure(caption, .53f, column, true);
            int captionX = across ? stateX : x;
            int captionY = across ? counters.IsEmpty ? y : counters.Bottom + gap : counters.IsEmpty ? landY : counters.Bottom + gap;
            texts.Add(new(caption, new(captionX, captionY, measured.X, measured.Y), .53f, true,
                incoming ? SeatReadoutTextRole.Incoming : SeatReadoutTextRole.Status));
        }
        return new(player.Id, seat, seatOrdinal, texts.AsReadOnly(), states.AsReadOnly(), hidden, counters);
    }

    internal SeatReadoutPlan AtPortrait(Vector2 portrait)
    {
        var bounds = Bounds;
        int x = Seat switch {
            0 => 12,
            2 => 1588 - bounds.Width,
            _ => Math.Clamp((int)portrait.X - bounds.Width / 2, 12, Math.Max(12, 1588 - bounds.Width))
        };
        return Translate(new(x - bounds.X, (int)portrait.Y + 58 - bounds.Y));
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
