using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void Button(string id, string label, int x, int y, int width, Action action, bool enabled = true, bool accent = false, int height = 40)
    {
        var bounds = new Rectangle(x, y, width, height);
        bool over = enabled && IsControlHovered(id);
        bool down = over && pressed?.Id == id && CurrentHit(pressed);
        bool decisionAction = match != null && id is "confirm" or "open-decision";
        bool primary = accent || decisionAction;
        // Importance and pointer state are separate. A primary action keeps
        // its quiet gold edge at rest, then responds like every other button.
        Color fill = !enabled ? new(26, 31, 38) : down ? new(17, 29, 39)
            : over ? primary ? new(65, 108, 131) : new(52, 71, 87)
            : primary ? new(36, 67, 83) : new(32, 40, 51);
        Color trim = !enabled ? new(45, 51, 59) : over ? Blue : primary ? Gold * .75f : new(72, 85, 98);
        if (decisionAction && enabled && !down)
            canvas.Glow(bounds.Center.ToVector2(), new(width + 45, height * 2.7f), (over ? Blue : Gold) * (over ? .20f : .09f));
        canvas.Panel(bounds, fill, trim, decisionAction ? 4 : 6);
        if (down) {
            var inset = bounds; inset.Inflate(-1, -1);
            canvas.Rounded(inset, trim, decisionAction ? 3 : 5);
            inset.Inflate(-1, -1);
            canvas.Rounded(inset, fill, decisionAction ? 2 : 4);
            canvas.Line(new(x + 5, y + 4), new(x + width - 5, y + 4), Color.Black * .65f, 2);
        } else if (over)
            canvas.Line(new(x + 7, y + height - 3), new(x + width - 7, y + height - 3), Blue, 2);
        canvas.CenterText(label, new(x + 9, y + 1, width - 18, height - 2), enabled ? Ink : Muted * .6f, .76f, primary);
        if (enabled) hits.Add(new(id, ControlScope(id), bounds, action));
    }

    private void PaintControlSurface(string id, Rectangle bounds, Color restingFill, Color restingTrim, bool enabled = true, float opacity = 1)
    {
        bool over = enabled && IsControlHovered(id);
        bool down = over && pressed?.Id == id && CurrentHit(pressed);
        Color fill = !enabled ? new(26, 31, 38) : down ? new(17, 29, 39) : over ? new(55, 91, 111) : restingFill;
        Color trim = !enabled ? new(45, 51, 59) : over ? Blue : restingTrim;
        fill *= opacity; trim *= opacity;
        canvas.Panel(bounds, fill, trim, 4);
        if (down) {
            var inset = bounds; inset.Inflate(-1, -1);
            canvas.Rounded(inset, trim, 3);
            inset.Inflate(-1, -1);
            canvas.Rounded(inset, fill, 2);
            canvas.Line(new(bounds.X + 5, bounds.Y + 4), new(bounds.Right - 5, bounds.Y + 4), Color.Black * .65f, 2);
        } else if (over)
            canvas.Line(new(bounds.X + 7, bounds.Bottom - 3), new(bounds.Right - 7, bounds.Bottom - 3), Blue, 2);
    }
}
