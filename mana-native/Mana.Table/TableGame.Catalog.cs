using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private CatalogPage? catalogPage;
    private string catalogText = "", catalogError = "", deckTextFocus = "", deckMenu = "";
    private int catalogType, catalogMana, catalogOffset, catalogGeneration;
    private int? catalogColors;
    private bool catalogCommanderColors, catalogManaSort, catalogPending, catalogInFlight, deckSelectAll;
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
        catalogGeneration++; catalogPending = true; catalogAfter = now + .25;
        catalogPage = null; catalogError = ""; librarySelection = null;
    }
    private void UpdateDeckCatalog()
    {
        if (match != null || !deckEditor || !loaded || !catalogPending || catalogInFlight || now < catalogAfter) return;
        int generation = catalogGeneration;
        var query = new CardQuery(catalogText.Trim(), CatalogTypes[catalogType], catalogCommanderColors ? CommanderIdentity : catalogColors,
            catalogMana == 0 ? null : catalogMana - 1, catalogManaSort ? "mana" : "name", catalogOffset);
        catalogPending = false; catalogInFlight = true;
        _ = Task.Run(async () => {
            try {
                var page = await engine.SearchCardsAsync(query);
                updates.Enqueue(() => { if (generation == catalogGeneration) catalogPage = page; });
            } catch (Exception ex) { updates.Enqueue(() => { if (generation == catalogGeneration) catalogError = ex.Message; }); }
            finally { updates.Enqueue(() => catalogInFlight = false); }
        });
    }
    private void FocusDeckText(string field) { deckTextFocus = field; textFocus = true; deckSelectAll = false; }
    private void DeckText(char character)
    {
        if (deckTextFocus.Length == 0) return;
        string value = deckTextFocus == "library" ? catalogText : deckFilter;
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
    }
    private void DeckKeys(KeyboardState keys)
    {
        if (!deckEditor || match != null || deckTextFocus.Length == 0) return;
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (control && keys.IsKeyDown(Keys.A) && previousKeys.IsKeyUp(Keys.A)) deckSelectAll = true;
        if (control && keys.IsKeyDown(Keys.V) && previousKeys.IsKeyUp(Keys.V) && Clipboard.ContainsText()) {
            string old = deckTextFocus == "library" ? catalogText : deckFilter;
            SetDeckText((deckSelectAll ? "" : old) + Clipboard.GetText().Replace("\r", " ").Replace("\n", " ")); deckSelectAll = false;
        }
        if (keys.IsKeyDown(Keys.Enter) && previousKeys.IsKeyUp(Keys.Enter)) { catalogAfter = now; deckTextFocus = ""; textFocus = false; }
    }
}
