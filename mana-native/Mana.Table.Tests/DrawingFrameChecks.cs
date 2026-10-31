using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class DrawingFrameChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var pixels in new[] { new Point(1000, 740), new Point(1540, 980), new Point(1920, 1080), new Point(3840, 2160) }) {
            foreach (int height in new[] { 900, 1000 }) {
                var mapping = RenderViewport.Fit(pixels, height);
                var logical = new Rectangle(0, 0, 1600, height);
                var surface = mapping.ToSurface(logical);
                check(mapping.Bounds.Width <= pixels.X && mapping.Bounds.Height <= pixels.Y
                        && mapping.Bounds.Left >= 0 && mapping.Bounds.Top >= 0
                        && surface.Location == Point.Zero && Math.Abs(surface.Width - mapping.Bounds.Width) <= 1
                        && Math.Abs(surface.Height - mapping.Bounds.Height) <= 1
                        && mapping.ToLogical(mapping.Bounds.Location) == Point.Zero
                        && mapping.ToLogical(new(mapping.Bounds.Left - 1, mapping.Bounds.Top - 1)).X < 0,
                    $"Physical target and pointer mapping share the same fitted pixel bounds at {pixels.X}x{pixels.Y} / {height}");
            }
        }
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
        drawing.BeginComposition();
        drawing.BeginClip(new(0, 54, 1600, 724));
        drawing.Fill(new(-100, -100, 2000, 1200), Color.Gold);
        bool unclosed = false;
        try { drawing.Complete(); } catch (InvalidOperationException) { unclosed = true; }
        drawing.EndClip();
        drawing.Text("Fixed hand", 300, 850, Color.White);
        var clipped = drawing.Complete();
        check(unclosed && clipped.Commands is [DrawCommand.BeginClip, DrawCommand.Fill, DrawCommand.EndClip, DrawCommand.Text],
            "World clipping brackets only its drawing commands and an unclosed clip cannot leak into a published frame or the fixed hand");
        var outer = new BoardView(new(31.25f, -12.5f), 1.5f);
        var inner = new BoardView(new(-8, 16), .8f);
        var combined = outer.Compose(inner);
        var viewport = new Vector2(.625f, .75f);
        var matrix = Canvas.TransformMatrix(combined, viewport);
        check(Vector2.Distance(combined.Apply(new Vector2(10, 20)), new(31.25f, 35.5f)) < .0001f
                && Vector2.Distance(Vector2.Transform(new(10, 20), matrix), new(19.53125f, 26.625f)) < .0001f
                && Vector3.Distance(Vector3.Transform(new(10, 20, 0), matrix), new(19.53125f, 26.625f, 0)) < .0001f,
            "Nested world transforms apply child then parent before the nonuniform physical viewport for both sprite and triangle coordinates");
        check(Canvas.PixelBounds(new(1, 2, 12, 7), combined, viewport) == new Rectangle(12, 10, 10, 7)
                && Canvas.PixelBounds(new(-3, 5, 14, 9), BoardView.Identity, viewport) == new Rectangle(-2, 3, 9, 8),
            "Clips opened inside world scopes enclose transformed fractional pixels while identity retains the original outward rounding");
        var origin = Canvas.TextOrigin(new(10.3f, 20.2f), combined, viewport);
        var textPixels = Vector2.Transform(origin, matrix);
        var density = viewport * combined.Scale;
        check(Vector2.Distance(textPixels, new(20, 27)) < .0001f
                && Vector2.Distance(density, new(.75f, .9f)) < .0001f
                && Vector2.Distance((Vector2.One / density) * combined.Scale * viewport, Vector2.One) < .0001f,
            "World text snaps after pan and zoom and cancels the full raster density so its bitmap keeps one texel per target pixel");
        var measured = drawing.TextSize("World land count", .8f, 180, 4);
        drawing.BeginComposition(); drawing.BeginTransform(outer);
        bool unclosedTransform = false;
        try { drawing.Complete(); } catch (InvalidOperationException) { unclosedTransform = true; }
        check(drawing.TextSize("World land count", .8f, 180, 4) == measured,
            "A world drawing scope does not change canonical text measurement or the layout's available width");
        drawing.EndTransform(); drawing.Complete();
        bool unmatchedTransform = false;
        try { drawing.EndTransform(); } catch (InvalidOperationException) { unmatchedTransform = true; }
        drawing.BeginComposition();
        int rejectedTransforms = 0;
        foreach (var invalid in new[] { new BoardView(new(float.NaN, 0)), new BoardView(Vector2.Zero, -1),
            new BoardView(Vector2.Zero, float.PositiveInfinity) }) {
            try { drawing.BeginTransform(invalid); } catch (ArgumentOutOfRangeException) { rejectedTransforms++; }
        }
        drawing.BeginTransform(new(Vector2.Zero, 1e30f));
        bool combinedOverflow = false;
        try { drawing.BeginTransform(new(Vector2.Zero, 1e30f)); } catch (ArgumentOutOfRangeException) { combinedOverflow = true; }
        drawing.EndTransform();
        check(combinedOverflow && drawing.Complete().Commands.Count == 2
                && BoardView.Identity.Compose(default).IsIdentity,
            "A finite nested view whose combined zoom overflows is rejected before recording while the default zero view keeps identity semantics");
        drawing.BeginComposition();
        drawing.BeginClip(new(0, 54, 1600, 724)); drawing.BeginTransform(outer);
        drawing.Text("Player's mat", 10, 20, Color.White);
        drawing.BeginTransform(inner); drawing.BeginClip(new(1, 2, 12, 7));
        drawing.Polygon(polygon, Color.Gold);
        drawing.EndClip(); drawing.EndTransform();
        drawing.Line(Vector2.Zero, Vector2.One, Color.White);
        drawing.EndTransform(); drawing.EndClip(); drawing.Text("Fixed portrait", 800, 744, Color.White);
        var transformed = drawing.Complete(); outer = BoardView.Identity;
        drawing.BeginComposition(); drawing.Fill(new(0, 0, 1, 1), Color.Black); drawing.Complete();
        check(unclosedTransform && unmatchedTransform && rejectedTransforms == 3
                && transformed.Commands is [DrawCommand.BeginClip, DrawCommand.BeginTransform { View: var savedOuter },
                    DrawCommand.Text, DrawCommand.BeginTransform { View: var savedInner }, DrawCommand.BeginClip,
                    DrawCommand.Polygon, DrawCommand.EndClip, DrawCommand.EndTransform, DrawCommand.Line,
                    DrawCommand.EndTransform, DrawCommand.EndClip, DrawCommand.Text]
                && savedOuter == new BoardView(new(31.25f, -12.5f), 1.5f) && savedInner == inner,
            "Copied nested transforms and clips preserve their boundaries through later compositions; invalid or unbalanced scopes cannot publish or alter the fixed portrait");
        drawing.BeginComposition();
        drawing.BeginClip(new(0, 54, 1600, 724)); drawing.BeginTransform(inner);
        drawing.BeginGroup(); drawing.Text("Mat resources", 20, 40, Color.White);
        drawing.BeginGroup(); drawing.Fill(new(20, 40, 100, 20), Color.Gold);
        drawing.Text("Occluded nested label", 20, 40, Color.White); drawing.EndGroup(visible: false);
        drawing.BeginGroup(); drawing.Line(Vector2.Zero, Vector2.One, Color.White);
        drawing.Text("Visible nested label", 30, 60, Color.White); drawing.EndGroup(); drawing.EndGroup();
        drawing.BeginGroup(); drawing.BeginClip(new(1, 2, 30, 40)); drawing.BeginTransform(inner);
        drawing.Panel(new(10, 20, 100, 40), Color.Black, Color.White);
        drawing.Text("Occluded whole readout", 10, 20, Color.White);
        drawing.EndTransform(); drawing.EndClip(); drawing.EndGroup(visible: false);
        drawing.EndTransform(); drawing.EndClip(); drawing.Text("Fixed hand survives", 300, 850, Color.White);
        var grouped = drawing.Complete();
        drawing.BeginComposition(); drawing.Text("Next frame", 0, 0, Color.White); drawing.Complete();
        check(grouped.Commands is [DrawCommand.BeginClip, DrawCommand.BeginTransform,
                    DrawCommand.Text { Value: "Mat resources" }, DrawCommand.Line,
                    DrawCommand.Text { Value: "Visible nested label" }, DrawCommand.EndTransform,
                    DrawCommand.EndClip, DrawCommand.Text { Value: "Fixed hand survives" }],
            "Discarding overlapping readout groups removes all their paint and local scopes while preserving nested visible paint, the outer camera, fixed hand and published frame");
        drawing.BeginComposition(); drawing.BeginGroup(); drawing.BeginClip(new(0, 0, 10, 10));
        drawing.Text("Incomplete hidden readout", 0, 0, Color.White);
        bool unbalancedGroup = false, unpublishedGroup = false;
        try { drawing.EndGroup(visible: false); } catch (InvalidOperationException) { unbalancedGroup = true; }
        try { drawing.Complete(); } catch (InvalidOperationException) { unpublishedGroup = true; }
        drawing.EndClip(); drawing.EndGroup(visible: false);
        check(unbalancedGroup && unpublishedGroup && drawing.Complete().Commands.Count == 0,
            "A hidden readout cannot discard or publish an unclosed clip; closing its owned scope permits discarding the complete group without residual paint");
        drawing.BeginComposition(); drawing.BeginGroup(); drawing.Text("Unfinished group", 0, 0, Color.White);
        bool openGroupOnly = false;
        try { drawing.Complete(); } catch (InvalidOperationException) { openGroupOnly = true; }
        drawing.EndGroup(visible: false);
        check(openGroupOnly && drawing.Complete().Commands.Count == 0,
            "Even with balanced clips and transforms an unfinished drawing group cannot publish a partial readout");
        drawing.BeginComposition(); drawing.BeginClip(new(0, 54, 1600, 724)); drawing.BeginTransform(inner); drawing.BeginGroup();
        bool parentClipProtected = false, parentTransformProtected = false;
        try { drawing.EndClip(); } catch (InvalidOperationException) { parentClipProtected = true; }
        try { drawing.EndTransform(); } catch (InvalidOperationException) { parentTransformProtected = true; }
        bool openGroupRejected = false;
        try { drawing.Complete(); } catch (InvalidOperationException) { openGroupRejected = true; }
        drawing.Text("Owned readout", 10, 20, Color.White); drawing.EndGroup();
        drawing.EndTransform(); drawing.EndClip();
        check(parentClipProtected && parentTransformProtected && openGroupRejected
                && drawing.Complete().Commands is [DrawCommand.BeginClip, DrawCommand.BeginTransform,
                    DrawCommand.Text { Value: "Owned readout" }, DrawCommand.EndTransform, DrawCommand.EndClip],
            "A readout group cannot close its enclosing viewport or camera and failed scope closure leaves the valid enclosing frame intact");
        bool unmatchedGroup = false;
        try { drawing.EndGroup(); } catch (InvalidOperationException) { unmatchedGroup = true; }
        check(unmatchedGroup, "Ending an absent drawing group cannot mutate an already completed frame");
        string rows = string.Join("\n", Enumerable.Range(0, 82).Select(index => $"Creature {index} · +1/+1 counters 2 → 3"));
        var pages = drawing.TextPages(rows, .84f, 1150, 460, 24);
        check(string.Concat(pages) == rows && pages.Length > 1 && pages.All(page =>
            drawing.TextSize(page, .84f, 1150, 80).Y <= Math.Min(460, drawing.TextSize(page, .84f, 1150, 24).Y)),
            "Measured result pagination retains all 82 complete rows within the actual painted line and height limits");
    }
}
