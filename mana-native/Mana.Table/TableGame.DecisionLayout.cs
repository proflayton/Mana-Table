using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private Rectangle decisionPanelBounds = new(1240, 712, 360, 188);
    private Rectangle decisionTextBounds;
    private string decisionText = "";
    private bool decisionTextOverflow;

    private (int StatusY, int TextY, int Lines) PlanDecisionText(string status, string instruction, bool abilityControls)
    {
        int lines = (int)Math.Ceiling(8 * preferences.TextScale);
        var shown = canvas.TextSize(instruction, .75f, 295, lines);
        var complete = canvas.TextSize(instruction, .75f, 295, 80);
        int textBottom = abilityControls ? 764 : 789;
        int textY = textBottom - shown.Y;
        decisionTextOverflow = complete.Y > shown.Y;
        int statusHeight = Math.Max(23, canvas.TextSize(status, .7f, decisionTextOverflow ? 180 : 298, 1, true).Y);
        int statusY = textY - statusHeight - 6;
        decisionPanelBounds = new(1240, Math.Min(712, statusY - 12), 360, 900 - Math.Min(712, statusY - 12));
        decisionTextBounds = new(1266, textY, 295, shown.Y);
        decisionText = instruction;
        return (statusY, textY, lines);
    }
}
