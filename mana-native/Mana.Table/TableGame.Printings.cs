using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private PrintingSelection? printingSelection;
    private CatalogPage? printingPage;
    private string printingError = "", printingFilter = "";
    private int printingGeneration, printingFinish;
    private void ClosePrintings()
    {
        workshopDialog = ""; deckTextFocus = ""; textFocus = false;
        printingSelection = null; printingPage = null; printingError = ""; printingGeneration++;
    }

    private static string PrintingLabel(Card card) => card.Edition + (card.CollectorNumber.Length > 0 ? " #" + card.CollectorNumber : "")
        + (card.Foil ? " · Foil" : " · Nonfoil");

    private void OpenPrintings()
    {
        if (selectedDeckDetails is not { } deck || DeckSelection is not { } card
            || engine is not ICardPrintings || deckCardSection == "Considering") return;
        printingSelection = new(deck.Id, deck.Revision, deckCardSection, card, librarySelection != null);
        printingFilter = ""; printingFinish = 0;
        deckTextFocus = ""; textFocus = false; workshopDialog = "printings";
        LoadPrintings(0);
    }

    private void LoadPrintings(int offset)
    {
        if (printingSelection is not { } selection || !selection.IsCurrent(selectedDeckDetails) || engine is not ICardPrintings source) return;
        int generation = ++printingGeneration;
        string filter = printingFilter.Trim(), finish = new[] { "", "nonfoil", "foil" }[printingFinish];
        printingPage = null; printingError = "";
        _ = Task.Run(async () => {
            try {
                await Task.Delay(150);
                if (generation != printingGeneration) return;
                var page = await source.CardPrintingsAsync(selection.Card.Id, offset, text: filter, finish: finish);
                updates.Enqueue(() => {
                    if (generation == printingGeneration && workshopDialog == "printings"
                        && selection == printingSelection && selection.IsCurrent(selectedDeckDetails)) printingPage = page;
                });
            } catch (Exception ex) {
                updates.Enqueue(() => { if (generation == printingGeneration && workshopDialog == "printings") printingError = ex.Message; });
            }
        });
    }

    private void ChoosePrinting(CatalogCard printing)
    {
        if (busy || printingSelection is not { } selection || engine is not ICardPrintings source) return;
        if (!selection.IsCurrent(selectedDeckDetails)) { printingError = "The deck changed. Close this chooser and reopen it from the card you want to change."; return; }
        if (printingPage?.Cards.Any(card => card.Id == printing.Id) != true) return;
        if (printing.Id == selection.Card.Id) { ClosePrintings(); return; }
        if (selection.FromLibrary) {
            librarySelection = printing; deckBackId = null; ClosePrintings();
            return;
        }
        EditDeck(async deck => {
            if (!selection.IsCurrent(deck)) throw new InvalidOperationException("The deck changed. Reopen its printing chooser.");
            var updated = await source.ReplacePrintingAsync(selection.DeckId, selection.Revision, selection.Section, selection.Card.Id, printing.Id);
            updates.Enqueue(() => { deckCardId = printing.Id; deckBackId = null; librarySelection = null; ClosePrintings(); });
            return updated;
        }, "Printing saved.");
    }

    private void DrawPrintings()
    {
        if (printingSelection is not { } selection) return;
        if (!selection.IsCurrent(selectedDeckDetails)) {
            canvas.Text("The deck changed. Go Back and reopen the chooser from the card you want to change.", 214, 380, Gold, .9f, 1140, 4);
            return;
        }
        var target = selection.Card;
        canvas.Text(target.Card.Name, 214, 230, Gold, 1.05f, 1160, 1, true);
        canvas.Text(selection.FromLibrary ? "Choose a printing to preview. Add copies from Build when you are ready."
            : "Replace " + selectedDeckDetails?.Entries.FirstOrDefault(entry => entry.Id == target.Id && entry.Section == selection.Section)?.Quantity
                + " copies in " + selection.Section + ". Other sections and your tested deck version stay in place.", 214, 269, Muted, .76f, 1160, 1);
        DeckSearchBox("printing", printingFilter, "Filter by set code or collector number…", new(214, 300, 800, 34));
        Button("printings-finish", new[] { "All finishes", "Nonfoil only", "Foil only" }[printingFinish], 1040, 300, 322, () => {
            printingFinish = (printingFinish + 1) % 3; LoadPrintings(0);
        }, !busy, height: 34);
        if (printingPage is { } page) {
            for (int i = 0; i < page.Cards.Length; i++) {
                var card = page.Cards[i]; int x = 214 + i * 194;
                PaintCard(card.Card, new Pose(new(x + 84, 471), new(168, 235)), target.Id == card.Id, false);
                canvas.Text(card.Edition, x, 606, Gold, .75f, 176, 1, true);
                canvas.Text("#" + card.Card.CollectorNumber + " · Art " + card.Card.ArtIndex, x, 635, Ink, .63f, 176, 1);
                canvas.Text(card.Card.Foil ? "Foil" : "Nonfoil", x, 662, Muted, .65f, 176, 1);
                Button("printing-use:" + card.Id, target.Id == card.Id ? "Current" : "Use printing", x, 705, 176,
                    () => ChoosePrinting(card), !busy && card.Id != target.Id, target.Id == card.Id, 38);
            }
            if (page.Total == 0) canvas.Text("No printings match. Try another set code or finish.", 214, 414, Muted, 1, 1150, 3);
            Button("printings-prev", "Previous", 214, 817, 140, () => LoadPrintings(Math.Max(0, page.Offset - 6)), !busy && page.Offset > 0, height: 36);
            Button("printings-next", "Next", 368, 817, 116, () => LoadPrintings(page.Offset + 6), !busy && page.Offset + page.Cards.Length < page.Total, height: 36);
            canvas.Text($"{(page.Total == 0 ? 0 : page.Offset + 1)}–{page.Offset + page.Cards.Length} of {page.Total} printings", 510, 828, Muted, .7f, 650, 1);
        } else if (printingError.Length > 0) {
            canvas.Text(printingError, 214, 380, Red, .9f, 1140, 5);
            Button("printings-retry", "Retry", 214, 705, 176, () => LoadPrintings(0), !busy, height: 38);
        } else {
            DrawLoadingIndicator(new(800, 448), 24, true, Blue);
            canvas.CenterText("Finding printings…", new(214, 502, 1160, 48), Ink, 1);
        }
    }
}
