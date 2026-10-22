using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool StudyFamilyNameVisible(StudyBattlefieldFamily family)
    {
        var area = family.NameBounds;
        if (area.Width < 36 || area.Height < 12) return false;
        int width = area.Width - 8;
        return family.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => canvas.TextWidth(word, BattlefieldNameSize, true) <= width)
            && canvas.TextSize(family.Name, BattlefieldNameSize, width, 80, true).Y <= area.Height;
    }

    private void DrawStudyFamily(StudyBattlefieldFamily family)
    {
        if (family.CohortCount <= 1) return;
        var bounds = family.Bounds; bounds.Inflate(2, 2);
        canvas.Border(bounds, new Color(105, 134, 137) * .55f);
        if (!StudyFamilyNameVisible(family)) return;
        var name = family.NameBounds;
        canvas.Fill(name, new(13, 24, 30));
        canvas.Text(family.Name, name.X + 4, name.Y, Ink, BattlefieldNameSize, name.Width - 8, 80, true);
    }
}
