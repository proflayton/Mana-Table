using Mana.Contracts;
using Mana.Magic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

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
        var token = new Card { Name = "Zombie Token", GamePieceType = "token", ArtSet = "tm21", CollectorNumber = "5" };
        check(CardPresentation.Artwork(token) is { Set: "tm21", CollectorNumber: "5" }
                && CardPresentation.Artwork(token with { CollectorNumber = "" }) == null,
            "Token artwork uses its exact printing; missing printing metadata cannot fall back to a different same-name token or real card");
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
        handler.Missing = false;
        string cache = Path.Combine(profile, "art");
        var cachedFiles = Directory.GetFiles(cache).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cancelled = original with { CollectorNumber = "cancelled" };
        using (var cancellation = new CancellationTokenSource()) {
            var pending = art.LoadAsync(cancelled, cancellation.Token);
            cancellation.Cancel();
            try { await pending; throw new Exception("Cancelled artwork request completed"); }
            catch (OperationCanceledException) {
                check(Directory.GetFiles(cache).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(cachedFiles),
                    "Cancelling an uncached image leaves every existing cache entry intact and publishes no incomplete image");
            }
        }
        int beforeRetry = handler.Urls.Count;
        var retried = await art.LoadAsync(cancelled, default);
        check(retried is { Length: > 0 } && handler.Urls.Count == beforeRetry + 1,
            "A cancelled image remains fetchable and does not strand the source's request ownership");
        using (var reopened = new MagicArtSource(profile, handler)) {
            int beforeReopen = handler.Urls.Count;
            check((await reopened.LoadAsync(cancelled, default))!.SequenceEqual(retried!) && handler.Urls.Count == beforeReopen,
                "Only the completed retry becomes a reusable image cache entry after reopening");
        }
        var blocked = original with { CollectorNumber = "blocked-publication" };
        string blockedPath = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blocked.Key))) + ".jpg");
        Directory.CreateDirectory(blockedPath);
        cachedFiles = Directory.GetFiles(cache).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { await art.LoadAsync(blocked, default); throw new Exception("Blocked image publication succeeded"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            check(Directory.GetFiles(cache).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(cachedFiles),
                "A failed cache publication removes its completed temporary image and preserves existing entries");
        }
        Directory.Delete(blockedPath);
        beforeRetry = handler.Urls.Count;
        check(await art.LoadAsync(blocked, default) is { Length: > 0 } && handler.Urls.Count == beforeRetry + 1,
            "Clearing a cache publication failure permits a fresh exact-printing request");
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
