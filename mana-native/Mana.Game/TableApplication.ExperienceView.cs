using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawTableGuide()
    {
        hits.Clear(); hovered = null; ClearCardComposition();
        int top = (DesignHeight - 390) / 2;
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .82f);
        canvas.Panel(new(340, top, 920, 390), new(23, 32, 43), LobbyTrim, 18);
        canvas.Text("Help for this moment", 375, top + 28, Gold, .86f, 660, 1, true);
        Button("experience-guide-close", "Close", 1110, top + 22, 112, CloseTableGuide, height: 36);
        var cue = match == null ? null : Mana.Magic.ControlGuidance.Decision(match, selectedCombat);
        canvas.Text(cue?.Title ?? (match?.Status == "finished" ? "Your game has finished" : "Follow the next action"), 376, top + 99, Ink, 1.2f, 835, 2, true);
        canvas.Text(cue?.Text ?? (match?.Status == "finished" ? "Inspect the final battlefield, or return to setup for another game."
            : "Choose a play path from the main menu, or return to your current table."), 377, top + 158, Ink, .91f, 830, 3);
        Button("experience-guide-done", "Back to it", 940, top + 314, 282, CloseTableGuide, accent: true, height: 43);
    }
}
