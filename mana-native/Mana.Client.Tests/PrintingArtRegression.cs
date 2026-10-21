using Mana.Contracts;
using Mana.Magic;
using System.Net;

internal static class PrintingArtRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string profile = Path.Combine(Path.GetTempPath(), "mana-printing-art-" + Guid.NewGuid());
        var handler = new Images();
        using var art = new MagicArtSource(profile, handler);
        var original = new ArtRequest("Toph, the First Metalbender", "front", "TLA", "247");
        var alternate = original with { CollectorNumber = "362" };
        var back = alternate with { Face = "back" };
        check(original.Key != alternate.Key && alternate.Key != back.Key, "Artwork caches distinguish collector numbers and faces");
        check(new ArtRequest("Forest", "front").Key == "Forest", "Legacy name-only artwork retains its cache key");
        var card = new Card { Name = original.Name, ArtSet = original.Set, CollectorNumber = original.CollectorNumber };
        check(CardPresentation.Artwork(card) == original && CardPresentation.Artwork(card with { FaceDown = true }) == null
            && CardPresentation.Artwork(card with { GamePieceType = "effect" }) == null,
            "Exact printing metadata reaches artwork requests while face-down cards and rules effects keep their safe fallback");
        var catalogCard = new CatalogCard("printing", card, 4, 16, "Main", "TLA");
        var deck = new DeckDetails("deck", "My deck", 7, [new("Main", catalogCard.Id, card, 2, 4, 16, "TLA")], [], "", "");
        var selection = new PrintingSelection(deck.Id, deck.Revision, "Main", catalogCard, false);
        check(selection.IsCurrent(deck) && !selection.IsCurrent(deck with { Id = "other" })
            && !selection.IsCurrent(deck with { Revision = 8 }) && !selection.IsCurrent(deck with { Entries = [] })
            && !(selection with { Section = "Commander" }).IsCurrent(deck),
            "A deck printing choice is bound to the exact document, revision, section and original occurrence row");
        check((selection with { FromLibrary = true }).IsCurrent(deck with { Entries = [] })
            && !(selection with { FromLibrary = true }).IsCurrent(deck with { Revision = 8 }),
            "Library printing previews do not require an owned row and still expire when their opening deck changes");
        var first = await art.LoadAsync(original, default);
        var other = await art.LoadAsync(alternate, default);
        await art.LoadAsync(back, default);
        check(handler.Urls[0].AbsolutePath == "/cards/tla/247" && handler.Urls[1].AbsolutePath == "/cards/tla/362"
            && handler.Urls[2].Query.Contains("face=back"), "Image requests address the selected set, collector number and face");
        check(!first!.SequenceEqual(other!), "Different printings retain different cached image bytes");
        await art.LoadAsync(original, default);
        check(handler.Urls.Count == 3, "Loading a cached exact printing requires no network request");
        using (var reopened = new MagicArtSource(profile, handler)) {
            check((await reopened.LoadAsync(alternate, default))!.SequenceEqual(other!), "An exact printing cache survives reopening the art source");
        }
        handler.Missing = true;
        try { await art.LoadAsync(alternate with { CollectorNumber = "missing" }, default); throw new Exception("Missing printing accepted"); }
        catch (HttpRequestException) { check(handler.Urls.Count == 4, "Missing printing images do not fall back to unrelated named artwork"); }
        // Only this fixture-created directory is removed.
        Directory.Delete(profile, true);
    }
    private sealed class Images : HttpMessageHandler
    {
        internal List<Uri> Urls { get; } = [];
        internal bool Missing;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Urls.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(Missing ? HttpStatusCode.NotFound : HttpStatusCode.OK) {
                Content = new ByteArrayContent([(byte)Urls.Count, 2, 3])
            });
        }
    }
}
