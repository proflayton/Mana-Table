using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Rectangle SystemDrawer(string title, string prefix, UiIntent close, int height, string? closeId = null)
    {
        hits.Clear(); hovered = null; ClearCardComposition();
        var bounds = new Rectangle(998, Math.Max(64, (DesignHeight - height) / 2), 564, height);
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .32f);
        hits.Add(new(prefix + "backdrop", ScopeFor(close), new(0, 0, 1600, DesignHeight), close, ShowPointer: false));
        canvas.Panel(bounds, new(15, 25, 32), LobbyTrim, 9);
        hits.Add(new(prefix + "body", ScopeFor(close), bounds, new UiIntent.Block(close.ScopeKind), ShowPointer: false));
        canvas.Text(title, bounds.X + 28, bounds.Y + 27, Ink, 1.3f, 380, 1, true);
        MenuAction(closeId ?? prefix + "close", "×", new(bounds.Right - 78, bounds.Y + 15, 50, 48), close, size: 1.25f);
        return bounds;
    }

    private void MenuAction(string id, string label, Rectangle bounds, UiIntent action, bool enabled = true, bool selected = false, float size = .85f)
    {
        bool over = enabled && IsControlHovered(id);
        bool down = over && pressed?.Id == id && CurrentHit(pressed);
        Color color = !enabled ? Muted * .45f : down ? Blue : over ? Ink : selected ? Gold : Muted;
        if (over || selected) canvas.Line(new(bounds.X + 8, bounds.Bottom - 3), new(bounds.Right - 8, bounds.Bottom - 3), color, selected ? 3 : 1);
        canvas.CenterText(label, bounds, color, size, selected || over);
        if (enabled) hits.Add(new(id, ScopeFor(action), bounds, action));
    }

    private void Button(string id, string label, Rectangle bounds, UiIntent action, bool enabled = true, bool accent = false)
        => Button(id, label, bounds.X, bounds.Y, bounds.Width, action, enabled, accent, bounds.Height);

    private void Button(string id, string label, int x, int y, int width, UiIntent action, bool enabled = true, bool accent = false, int height = 40, bool prominent = false)
    {
        var bounds = new Rectangle(x, y, width, height);
        bool over = enabled && IsControlHovered(id);
        bool down = over && pressed?.Id == id && CurrentHit(pressed);
        if (prominent) {
            Microsoft.Xna.Framework.Vector2[] Shape(int inset = 0, int drop = 0) => [
                new(x + inset + 15, y + inset + drop), new(x + width - inset - 15, y + inset + drop),
                new(x + width - inset, y + inset + 15 + drop), new(x + width - inset, y + height - inset - 15 + drop),
                new(x + width - inset - 15, y + height - inset + drop), new(x + inset + 15, y + height - inset + drop),
                new(x + inset, y + height - inset - 15 + drop), new(x + inset, y + inset + 15 + drop) ];
            if (enabled) canvas.Glow(bounds.Center.ToVector2(), new(width + 75, height * 2.4f), Gold * (over ? .18f : .07f));
            canvas.Polygon(Shape(drop: 5), Color.Black * .7f);
            canvas.Polygon(Shape(), !enabled ? LobbyTrim : over ? Ink : Gold * .85f);
            canvas.Polygon(Shape(2), !enabled ? new(23, 30, 33) : down ? new(44, 34, 23) : over ? new(126, 90, 45) : new(79, 58, 32));
            canvas.Line(new(x + 21, y + 4), new(x + width - 21, y + 4), (enabled ? Gold : Muted) * .35f, 1);
            canvas.CenterText(label, new(x + 16, y + 1, width - 32, height - 2), enabled ? Ink : Muted * .6f, 1.02f, true);
            if (enabled) hits.Add(new(id, ScopeFor(action), bounds, action));
            return;
        }
        bool decisionAction = match != null && action is MatchIntent.ConfirmDecision or MatchIntent.OpenDecision;
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
        if (enabled) hits.Add(new(id, ScopeFor(action), bounds, action));
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

    // An explicit decision must never lose its meaning to centered single-line
    // ellipsis. Callers reserve this measured block's complete height.
    private void CompleteActionButton(string id, string label, Rectangle bounds, UiIntent action, bool enabled = true)
    {
        Button(id, "", bounds, action, enabled, true);
        var size = canvas.TextSize(label, .76f, bounds.Width - 24, 80, true);
        canvas.Text(label, bounds.X + Math.Max(12, (bounds.Width - size.X) / 2),
            bounds.Y + Math.Max(4, (bounds.Height - size.Y) / 2), enabled ? Ink : Muted * .6f,
            .76f, bounds.Width - 24, 80, true);
    }
}
