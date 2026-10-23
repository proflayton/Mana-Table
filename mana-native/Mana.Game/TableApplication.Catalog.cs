using Mana.Contracts;
using Mana.Client;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly LatestRead<CardQuery, CatalogPage> catalogRead;
    private string catalogText = "", deckTextFocus = "", deckMenu = "";
    private int catalogType, catalogMana, catalogOffset;
    private int? catalogColors;
    private bool catalogCommanderColors, catalogManaSort, deckSelectAll;
    private double catalogAfter;
    private static readonly string[] CatalogTypes = ["", "Creature", "Instant", "Sorcery", "Artifact", "Enchantment", "Planeswalker", "Land", "Legendary"];
    private void ResetCatalog()
    {
        catalogText = ""; catalogType = catalogMana = 0; catalogColors = null;
        catalogCommanderColors = false; catalogManaSort = false;
        deckTextFocus = deckMenu = ""; textFocus = false; ChangeCatalog();
    }
    private void ChangeCatalog(bool paging = false)
    {
        if (!paging) catalogOffset = 0;
        var query = new CardQuery(catalogText.Trim(), CatalogTypes[catalogType], catalogCommanderColors ? CommanderIdentity : catalogColors,
            catalogMana == 0 ? null : catalogMana - 1, catalogManaSort ? "mana" : "name", catalogOffset);
        catalogRead.Request(query, () => catalog.SearchCardsAsync(query));
        catalogAfter = now + .25; librarySelection = null;
    }
    private void UpdateDeckCatalog()
    {
        if (match != null || !deckEditor || !loaded) return;
        if (now >= catalogAfter) catalogRead.Start();
        if (workshopDialog == "printings" && printingSelection?.IsCurrent(selectedDeckDetails) == true && now >= printingAfter) printingRead.Start();
        else if (workshopDialog != "printings" || printingSelection?.IsCurrent(selectedDeckDetails) != true) printingRead.Clear();
    }
    private void FocusDeckText(string field) { deckTextFocus = field; textFocus = true; deckSelectAll = false; }
    private void DeckText(char character)
    {
        if (deckTextFocus.Length == 0) return;
        string value = DeckTextValue;
        if (character == '\b') value = deckSelectAll ? "" : value.Length == 0 ? value : value[..^1];
        else if (!char.IsControl(character)) value = (deckSelectAll ? "" : value) + character;
        else return;
        SetDeckText(value); deckSelectAll = false;
    }
    private void SetDeckText(string value)
    {
        value = value[..Math.Min(value.Length, 200)];
        if (deckTextFocus == "library") { catalogText = value; ChangeCatalog(); }
        else if (deckTextFocus == "deck") { deckFilter = value; deckCardsPage = 0; }
        else if (deckTextFocus == "printing") { printingFilter = value; LoadPrintings(0); }
    }
    private string DeckTextValue => deckTextFocus == "printing" ? printingFilter : deckTextFocus == "library" ? catalogText : deckFilter;
    private void DeckKeys(KeyboardState keys)
    {
        if (!deckEditor || match != null || deckTextFocus.Length == 0 || workshopDialog.Length > 0 && workshopDialog != "printings") return;
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (control && keys.IsKeyDown(Keys.A) && previousKeys.IsKeyUp(Keys.A)) deckSelectAll = true;
        if (control && keys.IsKeyDown(Keys.V) && previousKeys.IsKeyUp(Keys.V) && Clipboard.ContainsText()) {
            string old = DeckTextValue;
            SetDeckText((deckSelectAll ? "" : old) + Clipboard.GetText().Replace("\r", " ").Replace("\n", " ")); deckSelectAll = false;
        }
        if (keys.IsKeyDown(Keys.Enter) && previousKeys.IsKeyUp(Keys.Enter)) { catalogAfter = now; deckTextFocus = ""; textFocus = false; }
    }
}
