using System.Threading.Channels;
using Mana.Contracts;

namespace Mana.Table;

internal static class CardArtPreloadChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckScheduling(check).GetAwaiter().GetResult();
        CheckSourceFailure(check).GetAwaiter().GetResult();
        CheckUploadBackpressure(check).GetAwaiter().GetResult();
        CheckPrivacy(check);
    }

    private static Card Face(string name) => new() { Name = name, VisualId = name, Type = "Creature" };

    private static async Task CheckScheduling(Action<bool, string> check)
    {
        var source = new ControlledArt();
        using var art = new CardArt(source);
        var first = Face("Selected deck card");
        var stale = Face("Old catalog card");
        var current = Face("Current visible card");
        var upcoming = Face("Next selected deck card");
        var reading = Face("Card being read");
        art.Preload([], [first, stale]);
        var initial = await source.Next();
        check(initial.Request.Name == first.Name, "Selected deck artwork starts warming before its first paint");
        art.Preload([current, current], [upcoming]);
        for (int i = 0; i < 30; i++) art.Get(reading);
        initial.Complete();
        var focused = await source.Next();
        check(focused.Request.Name == reading.Name, "Reading a card takes precedence over visible and speculative downloads");
        art.Get(reading); focused.Complete();
        var visible = await source.Next();
        check(visible.Request.Name == current.Name, "Current visible faces take precedence over the rest of the selected deck");
        visible.Complete();
        var anticipated = await source.Next();
        check(anticipated.Request.Name == upcoming.Name, "A changed scene replaces obsolete speculative work rather than extending its queue");
        anticipated.Complete();
        check(await source.NonePending(), "Repeated draw and prefetch requests share one load per exact face without duplicate in-flight downloads");
    }

    private static async Task CheckUploadBackpressure(Action<bool, string> check)
    {
        var source = new ControlledArt();
        using var art = new CardArt(source);
        art.Preload(Enumerable.Range(0, 800).Select(index => Face("Crowded face " + index)));
        for (int i = 0; i < 16; i++) (await source.Next()).Complete();
        check(await source.NonePending(), "A cached or very crowded scene stops at sixteen pending GPU uploads instead of retaining hundreds of encoded images");
    }

    private static async Task CheckSourceFailure(Action<bool, string> check)
    {
        var source = new ControlledArt();
        using var art = new CardArt(source);
        var broken = Face("Unexpected source failure");
        var next = Face("Readable after failure");
        art.Preload([broken, next]);
        var failed = await source.Next();
        failed.Bytes.TrySetException(new InvalidOperationException("An injected source failed unexpectedly."));
        var recovered = await source.Next();
        check(failed.Request.Name == broken.Name && recovered.Request.Name == next.Name,
            "An unexpected image-source exception backs off that request and continues loading the next card");
        for (int i = 0; i < 30; i++) art.Get(broken);
        recovered.Complete();
        check(await source.NonePending(), "Repeated painting after an unexpected source failure does not spam retries");
        art.Get(Face("New request after drain"));
        var later = await source.Next();
        check(later.Request.Name == "New request after drain", "Worker ownership is released after a failure and queue drain so later card requests can start");
        later.Complete();
    }

    private static void CheckPrivacy(Action<bool, string> check)
    {
        var sample = PresentationSmoke.Example();
        var publicCard = Face("Exposed card") with { OtherFace = new("Exposed reverse", "Creature", "", "", 2, 2, "Exposed card", "back") };
        var hidden = Face("Hidden identity") with { FaceDown = true, ArtName = "Private artwork", OtherFace = publicCard.OtherFace };
        var marker = Face("A rules effect") with { GamePieceType = "effect" };
        var state = sample with { Players = sample.Players.Select(player => player.Id != sample.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Cards = [publicCard, hidden, marker], Count = 3 } : zone).ToArray()
        }).ToArray() };
        var engine = new InteractionScenarioEngine(); engine.Present(state);
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableNative-tests", "art-preload-" + Guid.NewGuid());
        Directory.CreateDirectory(profile);
        using var app = new TableApplication(engine, profile, initialState: state);
        app.Initialize();
        var plan = app.ArtPreloadCards();
        check(plan.Visible.Any(card => card.Name == publicCard.Name) && plan.Visible.Any(card => card.ArtFace == "back" && card.ArtName == "Exposed card")
            && plan.Visible.All(card => !card.FaceDown && card.GamePieceType != "effect" && card.ArtName != "Private artwork")
            && plan.Anticipated.Length == 0,
            "During play preloading uses exposed snapshot faces, including exposed reverses, without hidden objects, rules effects or a guessed opponent deck");
        var source = new ControlledArt();
        using var art = new CardArt(source);
        art.Preload([hidden, marker]); art.Get(hidden); art.Get(marker);
        check(source.NonePending().GetAwaiter().GetResult(), "Face-down and effect identities never reach the actual image source through either loading path");
    }

    private sealed record Pending(ArtRequest Request, TaskCompletionSource<byte[]?> Bytes)
    {
        internal void Complete() => Bytes.TrySetResult([1, 2, 3]);
    }

    private sealed class ControlledArt : ICardArtSource
    {
        private readonly Channel<Pending> requests = Channel.CreateUnbounded<Pending>();
        public async Task<byte[]?> LoadAsync(ArtRequest request, CancellationToken cancellationToken)
        {
            var bytes = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Writer.TryWrite(new(request, bytes));
            return await bytes.Task.WaitAsync(cancellationToken);
        }
        internal async Task<Pending> Next() => await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        internal async Task<bool> NonePending()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            try { await requests.Reader.ReadAsync(timeout.Token); return false; }
            catch (OperationCanceledException) { return true; }
        }
        public void Dispose() { }
    }
}
