using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
namespace Mana.Table;
internal sealed partial class SmokeAutomation(TableWindow.Probe view, bool interaction) : ITableAutomation
{
    private bool canvasPrimitivesChecked;
    public MouseState ReadMouse() => interaction ? InteractionMouse() : SmokeMouse();
    public string? ExpectedClick => smokePendingClick;
    public bool ClickAccepted { get => smokeClickAccepted; set => smokeClickAccepted = value; }
    public void Frame(Texture2D surface)
    {
        if (interaction && !canvasPrimitivesChecked) {
            CanvasPrimitiveChecks.Run(surface.GraphicsDevice, Path.Combine(view.Profile, "canvas-primitives"));
            canvasPrimitivesChecked = true;
        }
        if (smokeScreenshot == null) return;
        using var output = File.Create(Path.Combine(view.Profile, smokeScreenshot));
        surface.SaveAsPng(output, surface.Width, surface.Height); smokeScreenshot = null;
    }
}
