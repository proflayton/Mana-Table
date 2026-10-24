using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private IDeckTokens? TokenEngine => services.Tokens is { SupportsDeckTokens: true } service ? service : null;
    private sealed record TokenDeckQuery(string DeckId, long Revision);
    private sealed record TokenPrintingQuery(TokenDeckQuery Deck, string TokenId, int Offset);
    private readonly LatestRead<TokenDeckQuery, DeckTokenCatalog> deckTokensRead;
    private readonly LatestRead<TokenPrintingQuery, CatalogPage> tokenPrintingsRead;
    private string selectedTokenId = "";
    private int tokenListPage, tokenPrintingOffset;
    private string TokenScope => $"{selectedTokenId}:{tokenListPage}:{tokenPrintingOffset}:{deckTokensRead.View.Generation}:{tokenPrintingsRead.View.Generation}";
    private DeckTokenCatalog? CurrentDeckTokens => selectedDeckDetails is { } deck
        && deckTokensRead.View.Context == new TokenDeckQuery(deck.Id, deck.Revision)
        && deckTokensRead.View.Value is { } result && result.DeckId == deck.Id && result.Revision == deck.Revision ? result : null;
    private DeckTokenAppearance? SelectedToken => CurrentDeckTokens?.Tokens.FirstOrDefault(token => token.Id == selectedTokenId);

    private void UpdateTokenCatalog()
    {
        if (workshopTab != "tokens" || TokenEngine is not { } service || selectedDeckDetails is not { } deck) {
            deckTokensRead.Clear(); tokenPrintingsRead.Clear(); return;
        }
        var scope = new TokenDeckQuery(deck.Id, deck.Revision);
        if (deckTokensRead.View.Context != scope) {
            deckTokensRead.Request(scope, async () => {
                var result = await service.DeckTokensAsync(scope.DeckId, scope.Revision);
                if (result.DeckId != scope.DeckId || result.Revision != scope.Revision) throw new InvalidDataException("The deck changed. Reload its token artwork.");
                return result;
            });
            tokenPrintingsRead.Clear();
        }
        if (busy) return;
        deckTokensRead.Start();
        if (CurrentDeckTokens is not { } catalog) return;
        if (!catalog.Tokens.Any(token => token.Id == selectedTokenId)) {
            selectedTokenId = catalog.Tokens.FirstOrDefault()?.Id ?? "";
            tokenListPage = tokenPrintingOffset = 0;
        }
        if (SelectedToken is not { } token) { tokenPrintingsRead.Clear(); return; }
        var query = new TokenPrintingQuery(scope, token.Id, tokenPrintingOffset);
        if (tokenPrintingsRead.View.Context != query)
            tokenPrintingsRead.Request(query, () => service.TokenPrintingsAsync(scope.DeckId, scope.Revision, token.Id, query.Offset));
        tokenPrintingsRead.Start();
    }

    private void SelectToken(string id)
    {
        if (busy || CurrentDeckTokens?.Tokens.Any(token => token.Id == id) != true) return;
        selectedTokenId = id; tokenPrintingOffset = 0; tokenPrintingsRead.Clear();
    }

    private void SetTokenPrinting(string? printingId)
    {
        if (busy || SelectedToken is not { } token || CurrentDeckTokens is not { } current || TokenEngine is not { } service) return;
        if (printingId != null && tokenPrintingsRead.View.Value?.Cards.Any(card => card.Id == printingId) != true) return;
        if (printingId == null && token.Automatic || printingId == token.PrintingId && !token.Automatic && !token.Unavailable) return;
        EditDeck(deck => {
            if (deck.Id != current.DeckId || deck.Revision != current.Revision) throw new InvalidOperationException("The deck changed. Choose its token artwork again.");
            return service.SetTokenPrintingAsync(deck.Id, deck.Revision, token.Id, printingId);
        }, "Token artwork saved.");
    }

    private void DrawDeckTokens()
    {
        if (WorkshopEngine == null)
            MenuAction("tokens-back", "‹ Build", new(48, 190, 134, 32), new DeckIntent.ShowWorkshopTab(WorkshopTab.Build));
        canvas.Text("Token artwork", 48, 250, Ink, 1.2f, 1000, 1, true);
        if (CurrentDeckTokens is not { } catalog) {
            string error = deckTokensRead.View.Error;
            if (error.Length == 0) { DrawLoadingIndicator(new(72, 334), 12, true, Gold); canvas.Text("Loading your deck's tokens…", 100, 323, Muted, .85f, 1000, 2); }
            else { canvas.Text(error, 48, 320, Red, .85f, 1200, 3); Button("tokens-retry", "Try again", 48, 420, 180, new DeckIntent.RefreshTokens()); }
            return;
        }
        if (catalog.Tokens.Length == 0) {
            canvas.Text("This deck has no predefined tokens.", 48, 330, Ink, 1, 1400, 2);
            canvas.Text("Tokens created during play use the newest available artwork.", 48, 380, Muted, .8f, 1400, 2); return;
        }
        const int pageSize = 5;
        tokenListPage = Math.Clamp(tokenListPage, 0, (catalog.Tokens.Length - 1) / pageSize);
        for (int i = 0; i < pageSize; i++) {
            int index = tokenListPage * pageSize + i;
            if (index >= catalog.Tokens.Length) break;
            var token = catalog.Tokens[index];
            var area = new Rectangle(48, 310 + i * 114, 350, 108);
            bool chosen = token.Id == selectedTokenId, over = IsControlHovered("deck-token:" + token.Id);
            if (chosen || over) canvas.Panel(area, Panel, over ? HoverAccent : Gold * .8f, 5);
            PaintCard(token.Card.Card, new(new(area.X + 40, area.Y + 54), new(68, 95)), false, false);
            canvas.Text(token.Card.Card.Name, area.X + 86, area.Y + 9, chosen ? Gold : Ink, .78f, 250, 2, true);
            string stats = token.Card.Card.Power is { } power && token.Card.Card.Toughness is { } toughness ? power + "/" + toughness + " · " : "";
            canvas.Text(stats + token.Card.Card.Type, area.X + 86, area.Y + 60, Muted, .6f, 250, 2);
            hits.Add(new("deck-token:" + token.Id, Scope, area, new DeckIntent.SelectToken(token.Id)));
        }
        if (catalog.Tokens.Length > pageSize) {
            Button("tokens-prev", "Previous", 48, 902, 150, new DeckIntent.PageTokens(-1), tokenListPage > 0, height: 34);
            Button("tokens-next", "Next", 210, 902, 150, new DeckIntent.PageTokens(1), (tokenListPage + 1) * pageSize < catalog.Tokens.Length, height: 34);
        }
        if (SelectedToken is not { } selected) return;
        canvas.Text(selected.Card.Card.Name, 450, 310, Gold, 1.05f, 790, 2, true);
        string sources = "Created by " + string.Join(", ", selected.Sources.Take(2)) + (selected.Sources.Length > 2 ? " +" + (selected.Sources.Length - 2) + " more" : "");
        int sourceY = 322 + canvas.TextSize(selected.Card.Card.Name, 1.05f, 790, 2, true).Y;
        canvas.Text(sources, 450, sourceY, Muted, .65f, 1080, 2);
        int statusY = Math.Max(414, sourceY + canvas.TextSize(sources, .65f, 1080, 2).Y + 12);
        int artTop = Math.Max(470, statusY + 36), artHeight = Math.Min(308, 778 - artTop);
        Button("token-newest", "Use newest", 1350, 310, 202, new DeckIntent.ChooseTokenPrinting(null), !busy && !selected.Automatic, selected.Automatic, 35);
        canvas.Text(selected.PrintingCount == 0 ? "No printed artwork is available in this catalog" : selected.Unavailable ? "Saved printing unavailable · using newest" : selected.Automatic ? "Newest printing · updates automatically" : "Your saved printing",
            450, statusY, selected.Unavailable ? Gold : Muted, .7f, 1050, 1);
        if (tokenPrintingsRead.View.Value is not { } variants) {
            string error = tokenPrintingsRead.View.Error;
            if (error.Length == 0) DrawLoadingIndicator(new(990, 620), 16, true, Gold);
            else { canvas.Text(error, 450, 540, Red, .8f, 1000, 3); Button("token-printings-retry", "Try again", 450, 650, 180, new DeckIntent.RefreshTokens()); }
            return;
        }
        for (int i = 0; i < variants.Cards.Length; i++) {
            var card = variants.Cards[i]; int x = 450 + i * 276;
            var pose = new Pose(new(x + 110, artTop + artHeight / 2f), new(artHeight / 1.4f, artHeight));
            bool chosen = selected.PrintingId == card.Id, over = IsControlHovered("token-printing:" + card.Id);
            PaintCard(card.Card, pose, false, false);
            if (chosen || over) canvas.CardFrame(pose, over ? HoverAccent : Gold, over ? 3 : 2);
            var bounds = new Rectangle(x - 4, artTop - 4, 232, 864 - artTop);
            canvas.Text(card.Edition + "  #" + card.Card.CollectorNumber, x, 798, chosen ? Gold : Ink, .76f, 240, 2, true);
            if (chosen) canvas.Text(selected.Automatic ? "Newest" : "Selected", x, 838, Gold, .65f, 240, 1);
            hits.Add(new("token-printing:" + card.Id, Scope, bounds, new DeckIntent.ChooseTokenPrinting(card.Id)));
        }
        if (variants.Total > 4) {
            Button("token-printings-prev", "Previous", 450, 902, 160, new DeckIntent.PageTokenPrintings(-1), tokenPrintingOffset > 0 && !busy, height: 34);
            Button("token-printings-next", "More artwork", 622, 902, 200, new DeckIntent.PageTokenPrintings(1), tokenPrintingOffset + 4 < variants.Total && !busy, height: 34);
            canvas.Text($"{tokenPrintingOffset + 1}–{Math.Min(variants.Total, tokenPrintingOffset + 4)} / {variants.Total}", 850, 912, Muted, .65f, 600, 1);
        }
    }
}
