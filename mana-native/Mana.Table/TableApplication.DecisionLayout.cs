using Microsoft.Xna.Framework;
using Mana.Magic;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Rectangle decisionPanelBounds = new(1240, 712, 360, 188);
    private Rectangle decisionTextBounds;
    private string decisionText = "";
    private bool decisionTextOverflow;

    private (int StatusY, int StatusHeight, int CaptionY, int CaptionHeight, int TextY, int Lines) PlanDecisionDock(DecisionDockView view, string instruction, bool abilityControls)
    {
        const int lines = 8;
        var shown = canvas.TextSize(instruction, .75f, 295, lines);
        var complete = canvas.TextSize(instruction, .75f, 295, 80);
        int textBottom = abilityControls ? 764 : 789;
        int textY = textBottom - shown.Y;
        decisionTextOverflow = complete.Y > shown.Y;
        int captionHeight = view.Caption.Length == 0 ? 0 : canvas.TextSize(view.Caption, .68f, 258, 80).Y;
        int captionY = textY - (shown.Y > 0 ? 9 : 0) - captionHeight;
        int statusHeight = Math.Max(25, canvas.TextSize(view.Headline, .78f, 258, 80, true).Y);
        int statusY = captionY - (captionHeight > 0 ? 4 : 0) - statusHeight;
        int top = Math.Min(764, statusY - (decisionTextOverflow ? 39 : 12));
        decisionPanelBounds = new(1240, top, 360, 900 - top);
        decisionTextBounds = new(1266, textY, 295, shown.Y);
        decisionText = instruction;
        return (statusY, statusHeight, captionY, captionHeight, textY, lines);
    }
}
