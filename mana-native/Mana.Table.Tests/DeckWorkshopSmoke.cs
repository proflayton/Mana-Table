using Mana.Contracts;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed class DeckWorkshopSmoke(TableWindow.Probe view) : ITableAutomation
{
    private readonly List<string> checks = [];
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(4);
    private int stage, next, mousePhase;
    private Point mouse;
    private string originalId = "";
    private string emptyDraftId = "";
    private long firstCardRevision;
    private string[] firstPage = [];
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    private DeckDetails Deck => view.DeckDetails!;
    private bool Valid => Deck.Problem.Length == 0 && Deck.SaveError.Length == 0;
    private DeckEntry Entry(string name) => Deck.Entries.First(e => e.Card.Name == name);
    private bool HasIssue(string code) => Deck.Issues.Any(issue => issue.Code == code);
    private bool MarkedIssue(string code, string name)
    {
        string id = Entry(name).Id;
        return Deck.Issues.Any(issue => issue.Code == code && issue.CardIds.Contains(id)) && view.RenderedDeckIssueCardIds.Contains(id);
    }
    private bool ShowIssue(string code, int retryStage)
    {
        if (view.RenderedDeckIssueCodes.Contains(code)) return true;
        if (view.Hits.Any(h => h.Id == "deck-issues-next")) { Click("deck-issues-next", retryStage); return false; }
        throw new InvalidOperationException("Workshop did not render the current validation issue " + code);
    }
    public MouseState ReadMouse()
    {
        bool down = mousePhase == 1;
        if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Check(bool valid, string text) {
        if (!valid) throw new InvalidOperationException("Workshop stage " + stage + ": " + text);
        checks.Add(text); File.AppendAllText(Path.Combine(view.Profile, "workshop-progress.log"), text + "\n");
    }
    private void Click(string id, int target)
    {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException($"Missing {id} at stage {stage}");
        var p = hit.Bounds.Center; var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + p.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + p.Y * scale));
        ExpectedClick = id; ClickAccepted = false; next = target; mousePhase = 1;
    }
    private void Row(string name, int target) { var e = Entry(name); Click("deck-card:" + e.Section + ":" + e.Id, target); }
    private void Type(string text) => view.TypeDeckText(new string('\b', 200) + text);
    private bool Result(string name, out CatalogCard card) {
        card = null!; if (view.CatalogBusy || view.Catalog == null) return false;
        card = view.Catalog.Cards.FirstOrDefault(c => c.Card.Name == name) ?? throw new InvalidOperationException("Search did not find " + name); return true;
    }
    private void Capture(string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
        File.WriteAllText(Path.Combine(view.Profile, name + ".json"), JsonSerializer.Serialize(new { Deck, Catalog = view.Catalog }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Frame(Texture2D surface)
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Workshop timeout at " + stage);
        if (view.Error.Length > 0) { Capture("failure"); throw new InvalidOperationException(view.Error); }
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) {
            if (!ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Rejected workshop click: " + ExpectedClick); }
            mousePhase = 0; ExpectedClick = null; stage = next;
        }
        // Ordinary input remains available during background observation;
        // slow rendered frames can miss the brief interval between polls.
        if (!view.Loaded || view.Busy || view.ActivityOpen) return;
        switch (stage) {
            case 0: Click("new-deck", 1); break;
            case 1:
                Check(view.DeckEditor && Deck.Entries.Length == 0, "New deck opens a saved empty workspace");
                Check(!view.Hits.Any(h => h.Id == "deck-export"), "Copy list has no active control for a fresh empty deck");
                Check(!view.Hits.Any(h => h.Id == "deck-review") && view.DeckIssueBounds is { Width: > 0, Height: > 0 }, "Live validation is visible in the builder without a separate Review deck control");
                emptyDraftId = Deck.Id;
                Click("deck-search-library", 2); break;
            case 2: Type("Rhys the Redeemed"); stage++; break;
            case 3:
                if (!Result("Rhys the Redeemed", out var rhys)) break;
                Click("catalog-card:" + rhys.Id, 4); break;
            case 4: Click("deck-add", 5); break;
            case 5:
                Check(Entry("Rhys the Redeemed").Quantity == 1, "Selecting a catalog face and pressing + adds the exact printing");
                Check(view.Hits.Any(h => h.Id == "deck-export"), "Adding the first card makes Copy list available");
                firstCardRevision = Deck.Revision;
                Click("deck-remove", 501); break;
            case 501:
                Check(view.DeckEditor && Deck.Id == emptyDraftId && Deck.Entries.Length == 0 && Deck.Revision > firstCardRevision && Deck.SaveError.Length == 0, "Removing the last card preserves the saved deck and its editable workspace");
                Check(!view.Hits.Any(h => h.Id == "deck-export"), "Removing the last card disables Copy list again");
                Click("deck-undo", 502); break;
            case 502:
                Check(Deck.Id == emptyDraftId && Entry("Rhys the Redeemed").Quantity == 1 && view.Hits.Any(h => h.Id == "deck-export"), "Undo restores the first card and its Copy list control without replacing the deck");
                Click("deck-set-commander", 6); break;
            case 6:
                Check(Entry("Rhys the Redeemed").Section == "Commander", "A commander can be found in the full library and assigned from the same workspace");
                Click("deck-search-library", 7); break;
            case 7: Type("Forest"); stage++; break;
            case 8:
                if (!Result("Forest", out var forest)) break;
                Click("catalog-card:" + forest.Id, 9); break;
            case 9: Click("deck-add", 10); break;
            case 10: view.DeckQuantity(99); stage++; break;
            case 11:
                Check(Valid && Deck.Entries.Sum(e => e.Quantity) == 100, "Bulk quantity entry creates a legal deck without repeated clicking");
                Check(Deck.Issues.Length == 0 && view.RenderedDeckIssueCodes.Count == 0, "Correcting the deck to 100 cards clears live validation warnings automatically");
                Click("deck-search-library", 12); break;
            case 12: Type("Sol Ring"); stage++; break;
            case 13:
                if (!Result("Sol Ring", out var ring)) break;
                Click("catalog-add:" + ring.Id, 14); break;
            case 14:
                Check(!Valid && Deck.Entries.Sum(e => e.Quantity) == 101, "Adding from a catalog tile updates the deck total and exposes the size problem");
                Check(HasIssue("MAIN_COUNT") && view.RenderedDeckIssueCodes.Contains("MAIN_COUNT") && view.DeckIssueBounds != null, "Adding a card immediately renders the authoritative deck-size issue in place");
                Check(Deck.SaveError.Length == 0 && view.InlineActivityVisible && HasIssue("MAIN_COUNT"), "Successful autosave feedback does not replace or clear deck validation");
                Click("deck-all", 15); break;
            case 15: Row("Forest", 16); break;
            case 16: Click("deck-remove", 17); break;
            case 17:
                Check(Valid && Entry("Forest").Quantity == 98, "Removing a copy restores a playable deck");
                Check(Deck.Issues.Length == 0 && view.RenderedDeckIssueCodes.Count == 0, "Removing the excess card clears its visible warning without opening review");
                Row("Sol Ring", 1700); break;
            case 1700: Click("deck-add", 1701); break;
            case 1701:
                if (!ShowIssue("CARD_COPY_LIMIT", 1701)) break;
                Check(Entry("Sol Ring").Quantity == 2 && MarkedIssue("CARD_COPY_LIMIT", "Sol Ring"), "A duplicate nonbasic card is identified by the engine and marked on its actual deck row");
                Capture("04-live-copy-limit"); Click("deck-remove", 1702); break;
            case 1702:
                Check(Valid && Deck.Issues.Length == 0 && !view.RenderedDeckIssueCardIds.Contains(Entry("Sol Ring").Id), "Removing a duplicate clears its row warning and restores validity immediately");
                Click("deck-move", 19); break;
            case 18: Click("deck-move", 19); break;
            case 19:
                Check(Entry("Sol Ring").Section == "Sideboard" && !Valid, "The move control transfers cards to sideboard and revalidates the main deck"); Click("deck-undo", 20); break;
            case 20:
                Check(Entry("Sol Ring").Section == "Main" && Valid, "Undo restores the moved card and legality");
                Check(Deck.Issues.Length == 0 && view.RenderedDeckIssueCodes.Count == 0, "Undo updates the live validation panel as well as the deck contents");
                Click("deck-redo", 21); break;
            case 21:
                Check(Entry("Sol Ring").Section == "Sideboard", "Redo restores the sideboard move"); Click("deck-undo", 22); break;
            case 22: Click("deck-search-library", 23); break;
            case 23: Type("Lightning Bolt"); stage = 90; break;
            case 90: Click("catalog-identity", 24); break;
            case 24:
                if (view.CatalogBusy || view.Catalog == null) break;
                Check(view.Catalog.Total == 0, "Leader-color filter hides off-color search results"); Click("catalog-identity", 25); break;
            case 25:
                if (!Result("Lightning Bolt", out var bolt)) break;
                Check(true, "Turning off the color filter restores those results");
                Click("catalog-add:" + bolt.Id, 2500); break;
            case 2500:
                if (!ShowIssue("COLOR_IDENTITY", 2500)) break;
                Check(MarkedIssue("COLOR_IDENTITY", "Lightning Bolt"), "An off-color card receives its authoritative color-identity issue and a visible row warning");
                Capture("05-live-color-identity"); Click("deck-issues-filter", 2501); break;
            case 2501:
                Check(view.Hits.Count(h => h.Id.StartsWith("deck-card:")) == 1 && view.Hits.Any(h => h.Id == "deck-card:Main:" + Entry("Lightning Bolt").Id), "Problems filtering exposes the affected card without guessing from its printed text");
                Click("deck-remove", 2502); break;
            case 2502:
                Check(Valid && Deck.Issues.Length == 0 && view.RenderedDeckIssueCardIds.Count == 0 && view.RenderedDeckIssueCodes.Count == 0, "Correcting an off-color addition clears live issues and card markers without a Review click");
                Click("deck-issues-filter", 2503); break;
            case 2503: Click("deck-search-library", 26); break;
            case 26: Type("Forest"); Type("Sol Ring"); stage++; break;
            case 27:
                if (!Result("Sol Ring", out _) || view.CatalogBusy) break;
                Check(!view.Catalog!.Cards.Any(c => c.Card.Name == "Forest"), "Rapid search changes display only the latest query");
                Click("deck-search-deck", 28); break;
            case 28: Type("No such deck card"); stage++; break;
            case 29:
                Check(!view.Hits.Any(h => h.Id.StartsWith("deck-card:")), "Deck filtering narrows the list without changing its contents"); Click("deck-clear-deck", 30); break;
            case 30: Row("Sol Ring", 31); break;
            case 31: Click("deck-export", 32); break;
            case 32:
                Check(Clipboard.GetText().Contains("Commander") && Clipboard.GetText().Contains("98 Forest"), "Copy list exports the edited quantities and commander section");
                view.RenameDeck("Native workshop regression"); stage++; break;
            case 33:
                Check(Deck.Name == "Native workshop regression" && view.SelectedDeck?.Name == Deck.Name, "Rename updates the current document and saved deck selection");
                originalId = Deck.Id; view.DuplicateDeck("Native workshop copy"); stage++; break;
            case 34:
                Check(Deck.Id != originalId && Valid, "Duplicate opens an independent saved copy");
                Capture("01-workspace"); Click("deck-done", 35); break;
            case 35:
                Check(!view.DeckEditor && view.SetupModal.Length == 0 && !view.DeckConfirmed, "Back to setup leaves the live-validated deck without forcing review or confirmation");
                Click("change-deck", 136); break;
            case 136: Click("deck:" + Deck.Id, 36); break;
            case 36:
                Check(Valid && Entry("Forest").Quantity == 98 && view.SetupModal.Length == 0, "Reopening the deck preserves the built list without a confirmation step"); Click("edit-deck", 38); break;
            case 38: Row("Sol Ring", 39); break;
            case 39: Click("deck-remove", 40); break;
            case 40:
                Check(!Deck.Entries.Any(e => e.Card.Name == "Sol Ring"), "Returning from match setup refreshes the editor revision before editing"); Click("deck-undo", 41); break;
            case 41:
                Check(Valid, "Undo recovers a card removed entirely from the deck");
                Click("catalog-type", 42); break;
            case 42: Click("catalog-option:type:7", 43); break;
            case 43:
                if (view.CatalogBusy || view.Catalog == null) break;
                Check(view.Catalog.Total > 0 && view.Catalog.Cards.All(c => c.Card.Type.Contains("Land")), "The type menu filters the library to lands");
                Click("catalog-mana", 44); break;
            case 44: Click("catalog-option:mana:1", 45); break;
            case 45:
                if (view.CatalogBusy || view.Catalog == null) break;
                Check(view.Catalog.Total > 0 && view.Catalog.Cards.All(c => c.ManaValue == 0), "The mana menu applies the selected maximum");
                Click("catalog-sort", 46); break;
            case 46: Click("catalog-option:sort:1", 47); break;
            case 47:
                if (view.CatalogBusy || view.Catalog == null) break;
                Check(view.Catalog.Cards.Select(c => c.ManaValue).SequenceEqual(view.Catalog.Cards.Select(c => c.ManaValue).Order()), "The sort menu orders library results by mana value");
                Capture("02-complete-deck");
                view.EditPreset(view.Presets[0].Id); stage++; break;
            case 48:
                Check(Valid && Deck.Entries.Length > 20, "The full precon remains editable in the same workspace");
                Click("deck-search-library", 49); break;
            case 49: Type("merfolk"); stage++; break;
            case 50:
                if (view.CatalogBusy || view.Catalog == null) break;
                firstPage = view.Hits.Where(h => h.Id.StartsWith("deck-card:")).Select(h => h.Id).ToArray();
                Click("deck-cards-next", 51); break;
            case 51:
                Check(view.Hits.Any(h => h.Id.StartsWith("deck-card:") && !firstPage.Contains(h.Id)), "Deck paging reveals later entries in a full Commander list");
                Click("deck-cards-prev", 52); break;
            case 52:
                Check(view.Hits.Where(h => h.Id.StartsWith("deck-card:")).Select(h => h.Id).SequenceEqual(firstPage), "Previous page restores the same entries without changing the deck");
                Capture("03-precon-workspace");
                File.WriteAllText(Path.Combine(view.Profile, "workshop-ui-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                stage++; view.Exit(); break;
        }
    }
}
