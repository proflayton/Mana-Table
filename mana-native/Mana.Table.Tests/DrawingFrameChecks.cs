using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal static class DrawingFrameChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var drawing = new DrawList();
        Vector2[] polygon = [new(1, 2), new(3, 4), new(5, 6)];
        drawing.TextScale = 1.25f;
        drawing.BeginComposition();
        drawing.Polygon(polygon, Color.White);
        drawing.Text("Public state", 10, 20, Color.Gold);
        var first = drawing.Complete();
        polygon[0] = new(999);
        drawing.BeginComposition();
        drawing.Fill(new(0, 0, 10, 10), Color.Black);
        var second = drawing.Complete();
        check(first.Commands.Count == 2 && first.Commands[0] is DrawCommand.Polygon { Points: var points }
            && points[0] == new Vector2(1, 2) && first.Commands[1] is DrawCommand.Text { Value: "Public state" }
            && second.Commands.Count == 1 && first.TextScale == 1.25f,
            "A completed GPU-free drawing frame keeps its order, copied geometry and font scale after another composition");
        bool rejected = false;
        try { drawing.Text("Stale composition", 0, 0, Color.White); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Published drawing data cannot be changed through an inactive composer");
        drawing.BeginComposition();
        bool changed = false;
        try { drawing.TextScale = 2; }
        catch (InvalidOperationException) { changed = true; }
        drawing.Complete();
        check(changed, "Font measurement and painting use one stable text scale throughout a frame");
    }
}
