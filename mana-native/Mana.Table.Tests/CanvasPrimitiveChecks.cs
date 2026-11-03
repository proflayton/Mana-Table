using System.Text.Json;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Executes recorded drawing commands on the actual GPU painter. A GPU-free
// frame test cannot detect exceptions thrown later by DrawCommand.Paint.
internal static class CanvasPrimitiveChecks
{
    internal static void Run(GraphicsDevice device, string profile)
    {
        Directory.CreateDirectory(profile);
        var checks = new List<string>();
        string? error = null;
        var priorTargets = device.GetRenderTargets();
        var priorViewport = device.Viewport;
        try {
            using var target = new RenderTarget2D(device, 96, 72);
            using var painter = new Canvas(device);
            using var drawing = new DrawList();
            var pixels = new Color[target.Width * target.Height];
            foreach (var bounds in new[] {
                Rectangle.Empty, new Rectangle(20, 20, 0, 12), new Rectangle(20, 20, 12, 0),
                new Rectangle(20, 20, -2, 12), new Rectangle(20, 20, 12, -3),
                new Rectangle(20, 20, -1, -1), new Rectangle(20, 20, int.MinValue, 12)
            }) {
                foreach (int radius in new[] { -1, 0, 1, 10, int.MaxValue }) {
                    Paint(list => list.Rounded(bounds, Color.Gold, radius));
                    Check(pixels.All(pixel => pixel.A == 0), $"Empty rounded bounds {bounds} / radius {radius} paint no pixels");
                    Paint(list => list.Panel(bounds, Color.Blue, Color.White, radius));
                    Check(pixels.All(pixel => pixel.A == 0), $"Empty panel bounds {bounds} / radius {radius} paint no fill, trim or shadow");
                }
            }
            foreach (var size in new[] { new Point(1, 1), new Point(1, 2), new Point(2, 1),
                new Point(2, 2), new Point(3, 3), new Point(32, 18) }) {
                var bounds = new Rectangle(20, 20, size.X, size.Y);
                Paint(list => list.Panel(bounds, Color.Blue, Color.White));
                Check(pixels[bounds.Center.Y * target.Width + bounds.Center.X].A > 0,
                    $"A real {size.X}x{size.Y} panel still paints after its inset becomes empty");
            }
            Paint(list => list.Panel(new(20, 20, 32, 18), Color.Blue, Color.White));
            Check(pixels[29 * target.Width + 36] == Color.Blue,
                "Ordinary panel fill remains unchanged after degenerate drawing commands");

            void Paint(Action<DrawList> compose)
            {
                device.SetRenderTarget(target);
                device.Clear(Color.Transparent);
                drawing.BeginComposition();
                compose(drawing);
                painter.Draw(drawing.Complete());
                device.SetRenderTarget(null);
                target.GetData(pixels);
            }
            void Check(bool passed, string description)
            {
                if (!passed) throw new InvalidOperationException(description);
                checks.Add(description);
            }
        } catch (Exception failure) { error = failure.ToString(); throw; }
        finally {
            device.SetRenderTargets(priorTargets);
            device.Viewport = priorViewport;
            File.WriteAllText(Path.Combine(profile, "canvas-primitives-results.json"), JsonSerializer.Serialize(new {
                passed = error == null, error, checks,
                scope = "Actual GPU Canvas and recorded DrawCommand painting; no engine or authored substitute painter."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal sealed class Only(TableWindow window, string profile) : ITableAutomation
    {
        private bool completed;
        public string? ExpectedClick => null;
        public bool ClickAccepted { get; set; }
        public MouseState ReadMouse() => new();
        public KeyboardState? ReadKeyboard() => new KeyboardState();
        public void Frame(Texture2D surface)
        {
            if (completed) return;
            Run(surface.GraphicsDevice, profile);
            completed = true;
            window.Exit();
        }
    }
}
