using Mana.Table.Interaction;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class InteractionFrameChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var frame = new InteractionFrame();
        var background = new TableHit("background", "game:decision", new(0, 0, 100, 100), UiIntent.None);
        var card = new TableHit("card:one", "game:decision", new(10, 10, 40, 40), UiIntent.None,
            Shape: point => point.X >= 20 && point.Y >= 20);
        frame.Begin(); frame.Add(background); frame.Add(card);
        check(frame.FindTop(new Point(25, 25)) == card && frame.FindTop(new Point(11, 11)) == background,
            "Interaction picking follows paint order and the precise face shape rather than its bounding rectangle");

        var label = new Rectangle(20, 60, 30, 12);
        frame.ExtendCanonical(card.Id, card.Scope, label);
        var extended = frame.FindTop(hit => hit.Id == card.Id);
        check(extended != null && extended.Contains(new(25, 65)) && extended.Contains(new(25, 25))
            && !extended.Contains(new(11, 11)) && !extended.Contains(new(25, 55)),
            "A card label extends its canonical input region without filling gaps or losing the projected face shape");

        // A foreground control wins until the corresponding card is lifted.
        frame.Add(new("foreground", card.Scope, new(20, 60, 30, 12), UiIntent.None));
        frame.Add(card with { Scope = "retired:decision", Bounds = new(75, 75, 10, 10), Shape = null });
        frame.PromoteCanonical(card.Id, card.Scope);
        check(frame.Count(hit => hit.Id == card.Id && hit.Scope == card.Scope) == 1
            && frame.Count(hit => hit.Id == card.Id && hit.Scope == "retired:decision") == 1
            && ReferenceEquals(frame.FindTop(new Point(25, 65)), extended)
            && !frame.FindTop(hit => hit.Id == card.Id)!.Contains(new(11, 11)),
            "Lifting a card promotes its one canonical region with the accumulated face and label geometry intact");

        frame.Add(new("outside-chat", card.Scope, new(110, 110, 10, 10), UiIntent.None));
        frame.RemoveIntersections(new(0, 0, 100, 100));
        check(frame.Count == 1 && frame[0].Id == "outside-chat" && frame.FindTop(new Point(25, 25)) == null,
            "An opaque chat panel removes every intersecting underlying control while preserving controls outside it");

        frame.ClearForModal();
        var modal = new TableHit("modal-action", "modal:decision", new(0, 0, 50, 50), UiIntent.None);
        frame.Add(modal);
        check(frame.Count == 1 && frame.FindTop(new Point(25, 25)) == modal && frame.FindTop(new Point(115, 115)) == null,
            "A modal replaces the complete underlying interaction set instead of leaving invisible controls reachable");

        frame.Publish();
        var first = frame.Published;
        bool blocked = false;
        try { frame.Clear(); } catch (InvalidOperationException) { blocked = true; }
        frame.Begin(); frame.Add(background);
        bool separated = frame.FindTop(new Point(25, 25)) == background && first.Count == 1 && first[0] == modal;
        frame.Publish();
        check(blocked && separated && !frame.IsBuilding && first.Count == 1 && first[0] == modal
            && frame.Published.Count == 1 && frame.Published[0] == background,
            "Completed interaction frames reject mutation and remain stable while the next displayed frame is composed and published");
    }
}
