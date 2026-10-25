using System.Collections.Immutable;
using System.Threading.Channels;
using Mana.Contracts;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class CardArtPreloadChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckScheduling(check).GetAwaiter().GetResult();
        CheckSourceFailure(check).GetAwaiter().GetResult();
        CheckUploadBackpressure(check).GetAwaiter().GetResult();
        CheckQueuedReadiness(check).GetAwaiter().GetResult();
        CheckTablePreparation(check);
        CheckOpeningHandPreparation(check);
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
        check(art.Readiness([broken]) is { Total: 1, Ready: 0, Unavailable: 1, Settled: true },
            "A failed image settles to a readable fallback without pretending a GPU texture exists");
        for (int i = 0; i < 30; i++) art.Get(broken);
        recovered.Complete();
        check(await source.NonePending(), "Repeated painting after an unexpected source failure does not spam retries");
        art.Get(Face("New request after drain"));
        var later = await source.Next();
        check(later.Request.Name == "New request after drain", "Worker ownership is released after a failure and queue drain so later card requests can start");
        later.Complete();
    }

    private static async Task CheckQueuedReadiness(Action<bool, string> check)
    {
        var source = new ControlledArt();
        using var art = new CardArt(source);
        Card queued = Face("Opening card awaiting upload"), unavailable = Face("Opening card without artwork");
        var barrier = Face("Readiness worker barrier");
        art.Preload([queued, queued, unavailable, barrier]);
        var first = await source.Next();
        using var pixels = new CardReadingArtFixture();
        first.Bytes.TrySetResult(await pixels.LoadAsync(first.Request, CancellationToken.None));
        var second = await source.Next(); // The worker has queued the first image.
        check(first.Request.Name == queued.Name && second.Request.Name == unavailable.Name
            && art.Readiness([queued, queued]) is { Total: 1, Ready: 0, Unavailable: 0, Settled: false },
            "Downloaded image bytes and duplicate faces cannot satisfy opening readiness before an actual GPU upload");
        second.Bytes.TrySetResult(null);
        var afterMissing = await source.Next();
        check(afterMissing.Request.Name == barrier.Name && art.Readiness([queued, unavailable]) is { Total: 2, Ready: 0, Unavailable: 1, Settled: false },
            "A missing image settles independently while another downloaded image still awaits its GPU upload");
        afterMissing.Complete();
        var gate = new TableArtworkPreparation();
        gate.Update(PresentationSmoke.Example(), [unavailable], art.Readiness, 0);
        check(!gate.Waiting && gate.Progress is { Total: 1, Ready: 0, Unavailable: 1 },
            "Opening a table with an unavailable face uses its fallback rather than waiting for an impossible texture");
    }

    private static void CheckTablePreparation(Action<bool, string> check)
    {
        var state = PresentationSmoke.Example();
        Card first = Face("First table opening"), second = Face("Replacement table opening");
        var gate = new TableArtworkPreparation();
        int reads = 0;
        ArtworkReadiness Pending(Card[] cards) { reads++; return new(cards.Length, 0, 0); }
        var starting = state with { Status = "starting", Players = [] };
        gate.Update(starting, [], Pending, 0);
        gate.Update(starting with { Revision = starting.Revision + 1 }, [], Pending, 30);
        check(gate.Waiting && gate.WaitingForTable && gate.Progress.Total == 0 && reads == 0,
            "A delayed starting table never releases its opening screen on a zero-of-zero artwork count");
        gate.Update(state with { ViewerId = int.MaxValue }, [], Pending, 40);
        check(gate.Waiting && gate.WaitingForTable && reads == 0,
            "A snapshot without its disclosed viewer waits for the table instead of preparing a guessed seat");
        gate.Update(state, [first], Pending, 50);
        gate.Update(state with { Revision = state.Revision + 1 }, [first], Pending, 57.99);
        check(gate.Waiting && !gate.WaitingForTable && gate.Progress.Total == 1,
            "The artwork budget starts at the first playable snapshot, after engine starting time");
        gate.Update(state with { Revision = state.Revision + 2 }, [first], Pending, 58);
        check(!gate.Waiting && reads == 3,
            "Fresh same-table observations cannot restart the eight-second opening budget");
        gate.Update(state with { Revision = state.Revision + 3 }, [first, second], Pending, 100);
        check(!gate.Waiting && reads == 3,
            "Later ordinary observations do not reopen artwork loading for a table already revealed");

        var readyNames = new HashSet<string>();
        ArtworkReadiness Current(Card[] cards) => new(cards.Length, cards.Count(card => readyNames.Contains(card.Name)), 0);
        var replacement = state with { Id = state.Id + "-replacement" };
        gate.Update(replacement, [second], Current, 101);
        readyNames.Add(first.Name);
        gate.Update(replacement with { Revision = replacement.Revision + 1 }, [second], Current, 102);
        check(gate.Waiting && gate.Progress is { Total: 1, Ready: 0 },
            "Replacing the game renews opening preparation and a late old face cannot release the new table");
        readyNames.Add(second.Name);
        gate.Update(replacement, [second], Current, 103);
        check(!gate.Waiting && gate.Progress.Ready == 1,
            "The replacement table releases when its own current required face is ready");
        int otherViewer = state.Players.First(player => player.Id != state.ViewerId).Id;
        gate.Update(replacement with { ViewerId = otherViewer }, [first], Pending, 104);
        check(gate.Waiting && gate.Progress.Ready == 0,
            "Changing the viewer invalidates the previous seat's completed artwork handoff");
        gate.Update(null, [], Pending, 105);
        check(!gate.Waiting && !gate.WaitingForTable && gate.Progress == default,
            "Resetting the table clears pending artwork and its previous progress");
        gate.Update(state, [first], Pending, 106);
        check(gate.Waiting, "Opening after a reset prepares the table again even when its game ID is reused");
        double terminalNow = 107;
        foreach (string status in new[] { "error", "finished" }) {
            gate.Update(state with { Status = status }, [first], Pending, terminalNow);
            check(!gate.Waiting && !gate.WaitingForTable,
                "An " + status + " table clears artwork blocking and exposes its actual terminal state");
            gate.Update(null, [], Pending, terminalNow + 1);
            gate.Update(state, [first], Pending, terminalNow + 2);
            terminalNow += 3;
        }
        gate.Update(state with { Revision = state.Revision + 1 }, [], _ => default, terminalNow);
        check(!gate.Waiting && gate.Progress.Total == 0,
            "Revoking the last disclosed face removes it from opening requirements instead of waiting for stale artwork");
    }

    private static void CheckOpeningHandPreparation(Action<bool, string> check)
    {
        var sample = PresentationSmoke.Example();
        Card publicFace = Face("Public commander before dealing"), privateFace = Face("Newly dealt private opening card");
        var beforeDeal = sample with { Turn = 0, Decision = new() {
            Id = "pre-deal-starting-player", Task = DecisionTask.Options, Message = "Choose who starts.", Min = 1, Max = 1, Choices = [new(0, "You", null), new(1, "Opponent 1", null)]
        }, Players = sample.Players.Select(player => player.Id != sample.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Cards = [], Count = 0 } : zone).ToImmutableArray()
        }).ToImmutableArray() };
        var ready = new HashSet<string> { publicFace.Name };
        int reads = 0;
        ArtworkReadiness Readiness(Card[] cards) { reads++; return new(cards.Length, cards.Count(card => ready.Contains(card.Name)), 0); }
        var gate = new TableArtworkPreparation();
        gate.Update(beforeDeal, [publicFace], Readiness, 0);
        gate.Update(beforeDeal with { Revision = 1 }, [publicFace], Readiness, 30);
        check(!gate.Waiting && !gate.WaitingForTable && reads == 1 && beforeDeal.Viewer!.Zone("Hand").Count == 0,
            "Cached public pre-deal artwork releases the required starting-player choice without waiting for a hand the choice must precede");

        var dealt = beforeDeal with { Revision = 2, Decision = new() {
            Id = "opening-hand-keep", Task = DecisionTask.OpeningHand, Message = "Keep this hand?", Ok = "Keep", OkEnabled = true, Cancel = "Mulligan", CancelEnabled = true
        }, Players = beforeDeal.Players.Select(player => player.Id != sample.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Cards = [privateFace], Count = 1 } : zone).ToImmutableArray()
        }).ToImmutableArray() };
        gate.Update(dealt, [publicFace, privateFace], Readiness, 31);
        check(gate.Waiting && !gate.WaitingForTable && gate.Progress is { Total: 2, Ready: 1 },
            "The first disclosed Turn-zero hand rearms artwork preparation after an already visible pre-deal choice");
        ready.Add(privateFace.Name);
        gate.Update(dealt with { Revision = 3 }, [publicFace, privateFace], Readiness, 32);
        check(!gate.Waiting && gate.Progress is { Total: 2, Ready: 2 },
            "That hand's preparation releases only when the newly dealt private face is actually ready");
        gate.Update(dealt with { Revision = 4, Turn = 1 }, [publicFace, privateFace, Face("Later ordinary draw")], Readiness, 33);
        check(!gate.Waiting && reads == 3,
            "A normal same-game turn and new draw do not reopen loading after the opening hand was prepared");
    }

    private static void CheckPrivacy(Action<bool, string> check)
    {
        var sample = PresentationSmoke.Example();
        var publicCard = Face("Exposed card") with { OtherFace = new("Exposed reverse", "Creature", "", "", 2, 2, "Exposed card", "back") };
        var hidden = Face("Hidden identity") with { FaceDown = true, ArtName = "Private artwork", OtherFace = publicCard.OtherFace };
        var marker = Face("A rules effect") with { GamePieceType = "effect" };
        var exposedTop = Face("Disclosed opponent library top");
        var state = sample with { Players = sample.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => player.Id == sample.ViewerId && zone.Name == "Battlefield"
                ? zone with { Cards = [publicCard, hidden, marker], Count = 3 }
                : player.Id != sample.ViewerId && zone.Name is "Hand" or "Library"
                    ? zone with { Cards = [hidden], Count = 12, TopCard = zone.Name == "Library" ? exposedTop : null }
                    : zone).ToImmutableArray()
        }).ToImmutableArray() };
        var engine = new InteractionScenarioEngine(); engine.Present(state);
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableNative-tests", "art-preload-" + Guid.NewGuid());
        Directory.CreateDirectory(profile);
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: state);
        app.Initialize();
        var plan = app.ArtPreloadCards();
        check(plan.Visible.Any(card => card.Name == publicCard.Name) && plan.Visible.Any(card => card.ArtFace == "back" && card.ArtName == "Exposed card")
            && plan.Visible.Any(card => card.Name == exposedTop.Name)
            && plan.Visible.All(card => !card.FaceDown && card.GamePieceType != "effect" && card.ArtName != "Private artwork")
            && plan.Anticipated.Length == 0,
            "During play preloading includes disclosed reverses and a public library top, excluding anonymous hand/library identities, hidden objects, rules effects and guessed opponent decks");
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
