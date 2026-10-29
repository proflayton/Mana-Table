using Mana.Renderer;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class CompleteActionLabelChecks
{
    internal static void Run(PresentationFrame frame, string id, string label, Action<bool, string> check)
    {
        var control = frame.Hits.Single(hit => hit.Id == id);
        var metrics = new TextMetrics();
        bool fits = frame.Drawing.Commands.Any(command => command switch {
            DrawCommand.CenterText text when text.Value == label && control.Bounds.Contains(text.Bounds) =>
                metrics.TextWidth(label, text.Size, text.Bold, frame.Drawing.TextScale) <= text.Bounds.Width,
            DrawCommand.Text text when text.Value == label && control.Bounds.Contains(text.X, text.Y) =>
                label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(word =>
                    metrics.TextWidth(word, text.Size, text.Bold, frame.Drawing.TextScale) <= text.Width)
                && text.Y + metrics.TextSize(label, text.Size, text.Width, 80, text.Bold, frame.Drawing.TextScale).Y <= control.Bounds.Bottom,
            _ => false
        });
        check(fits, "The actual rendered text block for " + id + " reserves every word and its full measured height at " + frame.Drawing.TextScale + " text scale: " + label);
    }
}
