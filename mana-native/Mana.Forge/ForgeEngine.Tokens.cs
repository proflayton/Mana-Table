using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IDeckTokens
{
    public bool SupportsDeckTokens => current is { DeckTokensAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully && !disposed;
    private void RequireDeckTokens()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SupportsDeckTokens) throw new NotSupportedException("This rules adapter does not expose deck token artwork.");
    }
    public async Task<DeckTokenCatalog> DeckTokensAsync(string deckId, long revision)
    {
        RequireDeckTokens();
        var value = await Request("deckTokens", new { deckId, revision });
        return new(Text(value, "deckId"), value.GetProperty("revision").GetInt64(), value.GetProperty("tokens").EnumerateArray().Select(token =>
            new DeckTokenAppearance(Text(token, "id"), MapCatalogCard(token.GetProperty("card")), Text(token, "printingId"),
                Text(token, "defaultPrintingId"), token.GetProperty("automatic").GetBoolean(), token.GetProperty("unavailable").GetBoolean(),
                token.GetProperty("printingCount").GetInt32(), Read<ImmutableArray<string>>(token.GetProperty("sources")))).ToImmutableArray());
    }
    public async Task<CatalogPage> TokenPrintingsAsync(string deckId, long revision, string tokenId, int offset = 0, int limit = 4)
    {
        RequireDeckTokens();
        var value = await Request("tokenPrintings", new { deckId, revision, tokenId, offset, limit });
        return new(value.GetProperty("total").GetInt32(), value.GetProperty("offset").GetInt32(),
            value.GetProperty("cards").EnumerateArray().Select(MapCatalogCard).ToImmutableArray());
    }
    public async Task<DeckDetails> SetTokenPrintingAsync(string deckId, long revision, string tokenId, string? printingId)
    {
        RequireDeckTokens();
        return MapDeck(await Request("setTokenPrinting", new { deckId, revision, tokenId, printingId }));
    }
}
