using Mana.Contracts;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed class PrintingsSmoke(TableGame.Probe view) : ITableAutomation
{
    private readonly List<string> checks = [];
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(4);
    private int stage, nextStage, mousePhase;
    private Point mouse;
    private string original = "", replacement = "", forestOriginal = "", forestReplacement = "";
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse()
    {
        bool down = mousePhase == 1;
        if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private DeckDetails Deck => view.DeckDetails!;
    private DeckEntry Commander => Deck.Entries.Single(e => e.Section == "Commander");
    private void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException($"Printings stage {stage}: {description}");
        checks.Add(description);
    }
    private void Click(string id, int next)
    {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Missing printing control " + id);
        var point = hit.Bounds.Center; var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + point.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + point.Y * scale));
        ExpectedClick = id; ClickAccepted = false; nextStage = next; mousePhase = 1;
    }
    private void Capture(string name)
    {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png"));
        view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
    }
    public void Frame(Texture2D surface)
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Printing UI timeout at " + stage);
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) {
            if (!ClickAccepted) throw new InvalidOperationException("Printing click rejected: " + ExpectedClick);
            stage = nextStage; mousePhase = 0; ExpectedClick = null;
        }
        if (!view.Loaded || view.Busy || view.ActivityOpen) return;
        switch (stage) {
            case 0: view.ImportDeck("Commander printings", "Commander\n1 Toph, the First Metalbender (TLA) 362 *F*\nMain\n99 Forest (M13) 249"); stage++; break;
            case 1:
                if (view.DeckDetails == null) break;
                Check(Commander.Card.CollectorNumber == "362" && Commander.Card.Foil && Commander.Card.ArtSet == "tla", "Text import maps the commander's exact artwork and foil finish");
                original = Commander.Id; Capture("printings-01-imported"); Click("deck-printings", 2); break;
            case 2:
                if (view.Printings == null) break;
                Check(view.Printings.Cards.All(c => c.Card.Name == Commander.Card.Name) && view.Printings.Total >= 6, "Chooser lists only printings of the selected commander, including alternate art and foils");
                Capture("printings-02-chooser");
                replacement = view.Printings.Cards.First(c => c.Id != original && !c.Card.Foil).Id;
                Click("printing-use:" + replacement, 3); break;
            case 3:
                Check(Commander.Id == replacement && Deck.Entries.Sum(e => e.Quantity) == 100 && Deck.Problem.Length == 0, "Choosing a printing preserves the command zone and legal 100-card list");
                Check(view.DeckSelection?.Id == replacement && view.WorkshopDialog == "", "Builder follows the newly selected printing");
                Capture("printings-03-selected"); Click("deck-undo", 4); break;
            case 4:
                Check(Commander.Id == original, "One Undo restores the imported commander printing");
                Click("deck-redo", 5); break;
            case 5:
                Check(Commander.Id == replacement, "Redo restores the chosen printing");
                Click("deck-card:Commander:" + replacement, 6); break;
            case 6: Click("deck-printings", 7); break;
            case 7:
                if (view.Printings == null) break;
                Capture("printings-04-current"); Click("deck-search-printing", 701); break;
            case 701: view.TypeDeckText("TLA 362"); stage = 702; break;
            case 702:
                if (view.Printings is not { Total: 2 }) break;
                Check(view.Printings.Cards.All(c => c.Card.CollectorNumber == "362"), "Set and collector filtering locates the requested artwork");
                Click("printings-finish", 703); break;
            case 703:
                if (view.Printings is not { Total: 1 }) break;
                Check(!view.Printings.Cards[0].Card.Foil, "Finish filtering locates only nonfoil printings");
                Click("printings-finish", 704); break;
            case 704:
                if (view.Printings is not { Total: 1 }) break;
                Check(view.Printings.Cards[0].Card.Foil, "Finish filtering locates only foil printings");
                Capture("printings-04-filtered"); Click("workshop-dialog-close", 8); break;
            case 8:
                Check(Commander.Id == replacement && view.WorkshopDialog == "", "Closing the chooser leaves the saved printing unchanged");
                forestOriginal = Deck.Entries.Single(e => e.Card.Name == "Forest").Id;
                Click("deck-card:Main:" + forestOriginal, 9); break;
            case 9: Click("deck-printings", 10); break;
            case 10:
                if (view.Printings == null) break;
                Click("printings-next", 11); break;
            case 11:
                if (view.Printings is not { Offset: 6 } forests) break;
                Check(forests.Cards.All(c => c.Card.Name == "Forest"), "Printing pagination stays scoped to the selected card");
                forestReplacement = forests.Cards.First(c => c.Id != forestOriginal).Id;
                Capture("printings-05-paged"); Click("printing-use:" + forestReplacement, 12); break;
            case 12:
                Check(Deck.Entries.Single(e => e.Card.Name == "Forest") is { Quantity: 99 } land && land.Id == forestReplacement && Commander.Id == replacement,
                    "Changing a land printing retains all 99 copies and the chosen commander");
                Click("deck-undo", 13); break;
            case 13:
                Check(Deck.Entries.Single(e => e.Card.Name == "Forest").Id == forestOriginal, "Undo restores the entire land printing row");
                Click("deck-search-library", 14); break;
            case 14: view.TypeDeckText("Forest"); stage++; break;
            case 15:
                if (view.CatalogBusy || view.Catalog == null) break;
                Click("catalog-card:" + view.Catalog.Cards.Single(c => c.Card.Name == "Forest").Id, 16); break;
            case 16: Click("deck-printings", 17); break;
            case 17:
                if (view.Printings == null) break;
                forestReplacement = view.Printings.Cards.First(c => c.Id != forestOriginal && c.Id != view.DeckSelection!.Id).Id;
                Click("printing-use:" + forestReplacement, 18); break;
            case 18:
                Check(view.DeckSelection?.Id == forestReplacement && Deck.Entries.Sum(e => e.Quantity) == 100,
                    "Choosing a library printing previews it before adding copies");
                Click("deck-add", 19); break;
            case 19:
                Check(Deck.Entries.Any(e => e.Id == forestReplacement && e.Quantity == 1) && Deck.Entries.Any(e => e.Id == forestOriginal && e.Quantity == 99),
                    "Adding a library printing supports multiple printings of the same card");
                Capture("printings-06-mixed"); Click("deck-undo", 20); break;
            case 20:
                Check(Deck.Entries.Sum(e => e.Quantity) == 100 && Commander.Id == replacement, "Undoing the extra printing returns to the same valid commander deck");
                File.WriteAllText(Path.Combine(view.Profile, "printings-ui-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
