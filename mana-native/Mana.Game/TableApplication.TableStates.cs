using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawTableStates(GameSnapshot state, Rectangle? area = null)
    {
        if (state.States.Length == 0) return;
        int end = area?.Right ?? (matchPresentation.Changes.Count > 0 ? 1000 : 1180);
        int x = area?.X ?? 840, top = area?.Y ?? 12, height = area?.Height ?? 33;
        foreach (var item in state.States) {
            string caption = PublicStatePresentation.Caption(item);
            int width = Math.Min(320, canvas.TextWidth(caption, .58f, true) + 20);
            if (x + width > end - 60) break;
            var bounds = new Rectangle(x, top, width, height);
            canvas.Panel(bounds, Panel, Gold, 5);
            canvas.CenterText(caption, bounds, Gold, .58f, true);
            hits.Add(new("table-state:" + item.Id, Scope, bounds, () => {
                tableStatePage = 0; overlay = "table-state";
            }));
            x += width + 8;
        }
        Button("table-states", "All", end - 56, top, 56, () => { tableStatePage = 0; overlay = "table-state"; }, height: height);
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
