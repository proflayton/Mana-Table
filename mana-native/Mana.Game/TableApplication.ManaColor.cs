using Mana.Contracts;
using Mana.Magic;
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
        var room = DecisionActionArea(anchor, width, height,
            additionalReservations: reservations, minimumWidth: width, boundedWorkspace: true);
        // Glyph choices reserve their actual footprint rather than the wider
        // minimum used by readable text workspaces.
        var area = new Rectangle(room.Center.X - width / 2, room.Y, width, height);
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
