using Mana.Contracts;
using Mana.Magic;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal sealed record ManaColorPaint(int Index, string Mana, string ProducerId, Rectangle Bounds, Rectangle ProducerBounds);
    private readonly List<ManaColorPaint> manaColorPaint = [];

    private bool DrawManaColorDecision(GameSnapshot state)
    {
        if (state.Decision is not { Payload: OptionsPayload { ManaPaymentContext: not null } options } decision
            || session.NeedsRecovery) return false;
        var (anchor, producer, card, _) = DecisionSourceAnchor(state, decision);
        // A support-card cell can be wider than its painted face, especially
        // after tapping. Bind this small gesture to the displayed occurrence.
        if (card != null && matchPresentation.Cards.Cards.LastOrDefault(entry => entry.Zone == "Battlefield"
            && CardViews.Same(entry.Card, card)) is { } painted) {
            producer = painted.Display; anchor = painted.Display.Bounds;
        }
        var choices = options.Selection.Choices;
        const int pitch = 68, side = 56;
        int width = Math.Max(170, choices.Length * pitch + 12), height = 104;
        var reservations = matchPresentation.Cards.Cards.Where(entry => producer == null || entry.Display.Bounds != anchor)
            .Select(entry => entry.Display.Bounds).ToList();
        if (manaJourneyPaint is { } bill) reservations.Add(bill.Bounds);
        Rectangle? aligned = null;
        if (TableStudyEnabled && producer != null && choices.Length > 0) {
            var preferred = new Rectangle(anchor.Center.X - width / 2, anchor.Top - height - 16, width, height);
            var title = canvas.TextSize("Choose mana", .78f, width - 14, 1, true);
            var pieces = new List<Rectangle> { new(preferred.X + 7, preferred.Y + 3, title.X, title.Y) };
            int first = preferred.Center.X - (choices.Length * pitch - (pitch - side)) / 2;
            for (int i = 0; i < choices.Length; i++)
                pieces.Add(new(first + i * pitch - 3, preferred.Y + 33, 62, 62));
            var footprint = pieces.Aggregate(Rectangle.Union);
            int centerOffset = preferred.Center.X - footprint.Left;
            int left = Math.Max(20, anchor.Left - centerOffset);
            int right = Math.Min(1580 - footprint.Width, anchor.Right - centerOffset);
            if (right >= left) {
                // This gesture belongs to the tapped producer. Translate its
                // complete title and largest orb faces within that source's
                // column, rather than moving it over a neighboring resource.
                var corridor = new Rectangle(left, 64 + footprint.Top - preferred.Top,
                    right - left + footprint.Width, 696 - height + footprint.Height);
                var occupied = StudyHudObstacles.Concat(reservations).Append(anchor).Distinct().Select(bounds => {
                    bounds.Inflate(4, 4); return bounds;
                }).ToArray();
                if (DecisionWorkspaceLayout.Translate(pieces, corridor, occupied) is { } placed) {
                    preferred.Offset(placed.X - footprint.X, placed.Y - footprint.Y);
                    aligned = preferred;
                }
            }
        }
        var area = aligned ?? Rectangle.Empty;
        if (aligned == null) {
            var room = DecisionActionArea(anchor, width, height,
                additionalReservations: reservations, minimumWidth: width, boundedWorkspace: true);
            area = new(room.Center.X - width / 2, room.Y, width, height);
        }
        decisionPanelBounds = area;
        canvas.Glow(area.Center.ToVector2(), new(width + 35, height + 30), new Color(8, 17, 23) * .95f);
        canvas.Text("Choose mana", area.X + 7, area.Y + 3, Ink, .78f, area.Width - 14, 1, true);
        int firstX = area.Center.X - (choices.Length * pitch - (pitch - side)) / 2;
        for (int i = 0; i < choices.Length; i++) {
            var option = choices[i];
            string id = "mana-color:" + option.Index;
            var bounds = new Rectangle(firstX + i * pitch, area.Y + 36, side, side);
            bool over = !busy && IsControlHovered(id);
            var center = bounds.Center.ToVector2();
            canvas.Glow(center, new(over ? 94 : 75), (over ? Blue : Gold) * (over ? .34f : .17f));
            canvas.Circle(center, over ? 31 : 29, over ? Blue : Gold * .65f);
            canvas.Circle(center, over ? 29 : 27, new(10, 20, 26));
            PaintCostSymbol(option.Mana, center, over ? 49 : 45);
            if (!busy) hits.Add(new(id, Scope, bounds, new MatchIntent.ChooseManaColor(option.Index)));
            manaColorPaint.Add(new(option.Index, option.Mana, card == null ? "" : CardViews.Identity(card), bounds, anchor));
        }
        if (producer is { } source) {
            canvas.CardFrame(source with { Size = source.Size + new Vector2(9) }, Gold, 2);
            var end = new Mana.Renderer.Pose(area.Center.ToVector2(), area.Size.ToVector2()).EdgeToward(source.Center, 5);
            canvas.Line(source.EdgeToward(end, 5), end, Gold * .85f, 2.2f);
        }
        return true;
    }
}
