using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void DrawTableStates(GameSnapshot state)
    {
        if (state.States.Length == 0) return;
        int x = 840;
        foreach (var item in state.States) {
            string caption = PublicStatePresentation.Caption(item);
            int width = Math.Min(320, canvas.TextWidth(caption, .58f, true) + 20);
            if (x + width > 1120) break;
            var bounds = new Rectangle(x, 12, width, 33);
            canvas.Panel(bounds, Panel, Gold, 5);
            canvas.CenterText(caption, bounds, Gold, .58f, true);
            hits.Add(new("table-state:" + item.Id, Scope, bounds, () => {
                tableStatePage = 0; overlay = "table-state";
            }));
            x += width + 8;
        }
        Button("table-states", "All", 1124, 12, 56, () => { tableStatePage = 0; overlay = "table-state"; }, height: 33);
    }
    private int tableStatePage;
    private void DrawTableStateDetails()
    {
        ModalFrame("Table state", () => overlay = "");
        var pages = TextPages(string.Join("\n\n", match!.States.Select(PublicStatePresentation.Description)), ReadingPageSize(1100));
        tableStatePage = Math.Clamp(tableStatePage, 0, pages.Length - 1);
        canvas.Text(pages[tableStatePage], 204, 195, Ink, .9f, 1170, 17);
        if (pages.Length > 1) {
            Button("table-state-prev", "Previous", 204, 729, 160, () => tableStatePage--, tableStatePage > 0);
            Button("table-state-next", "Next", 380, 729, 160, () => tableStatePage++, tableStatePage + 1 < pages.Length);
        }
    }
}
