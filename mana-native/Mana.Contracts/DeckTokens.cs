using System.Collections.Immutable;

namespace Mana.Contracts;

// Cosmetic token definitions/printing handles belong to the catalog provider.
// They never add deck cards or grant a match action.
public interface IDeckTokens
{
    bool SupportsDeckTokens => true;
    Task<DeckTokenCatalog> DeckTokensAsync(string deckId, long revision);
    Task<CatalogPage> TokenPrintingsAsync(string deckId, long revision, string tokenId, int offset = 0, int limit = 4);
    Task<DeckDetails> SetTokenPrintingAsync(string deckId, long revision, string tokenId, string? printingId);
}

public sealed record DeckTokenAppearance(string Id, CatalogCard Card, string PrintingId, string DefaultPrintingId,
    bool Automatic, bool Unavailable, int PrintingCount, ImmutableArray<string> Sources);
public sealed record DeckTokenCatalog(string DeckId, long Revision, ImmutableArray<DeckTokenAppearance> Tokens);
