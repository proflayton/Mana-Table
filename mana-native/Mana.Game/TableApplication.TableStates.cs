using Mana.Contracts;
using Mana.Magic;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawTableStates(GameSnapshot state, Rectangle? area = null)
    {
        if (state.States.Length == 0) return;
        var available = area ?? MatchHeaderLayout.TableStates;
        int end = available.Right;
        int x = available.X, top = available.Y, height = available.Height;
        foreach (var item in state.States) {
            string caption = PublicStatePresentation.Caption(item);
            int width = Math.Min(320, canvas.TextWidth(caption, .58f, true) + 20);
            if (x + width > end - 60) break;
            var bounds = new Rectangle(x, top, width, height);
            canvas.Panel(bounds, Panel, Gold, 5);
            canvas.CenterText(caption, bounds, Gold, .58f, true);
            hits.Add(new("table-state:" + item.Id, Scope, bounds, new MatchIntent.OpenTableState(0)));
            x += width + 8;
        }
        Button("table-states", "All", end - 56, top, 56, new MatchIntent.OpenTableState(0), height: height);
    }
    private int tableStatePage;
    private void DrawTableStateDetails()
    {
        ModalFrame("Table state", new MatchIntent.CloseView());
        var pages = matchReadingPages;
        int page = Math.Clamp(tableStatePage, 0, pages.Length - 1);
        canvas.Text(pages[page], 204, 195, Ink, .9f, 1170, 17);
        if (pages.Length > 1) {
            Button("table-state-prev", "Previous", 204, 729, 160, new MatchIntent.SetPage(MatchPage.TableState, page - 1), page > 0);
            Button("table-state-next", "Next", 380, 729, 160, new MatchIntent.SetPage(MatchPage.TableState, page + 1), page + 1 < pages.Length);
        }
    }
}
