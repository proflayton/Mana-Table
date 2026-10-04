using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
namespace Mana.Table;
internal sealed partial class SmokeAutomation(TableGame.Probe view, bool interaction) : ITableAutomation
{
    public MouseState ReadMouse() => interaction ? InteractionMouse() : SmokeMouse();
    public string? ExpectedClick => smokePendingClick;
    public bool ClickAccepted { get => smokeClickAccepted; set => smokeClickAccepted = value; }
    public void Frame(Texture2D surface)
    {
        if (smokeScreenshot == null) return;
        using var output = File.Create(Path.Combine(view.Profile, smokeScreenshot));
        surface.SaveAsPng(output, surface.Width, surface.Height); smokeScreenshot = null;
    }
}
