using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Controlled storage exercises the production setup/input path. Forge's separate
// storage checks establish durable files, metadata and restart behavior.
internal static class DeckLibraryPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        var engine = new LibraryEngine();
        using (var run = new Runner(profile, "deck-library-input", engine, check)) {
            run.Click("review-deck"); run.Click("confirm-deck");
            run.Until(() => run.View.DeckConfirmed && !run.View.Busy, "The controlled current deck confirms through its real setup action");
            string current = run.View.SelectedDeck!.Id;
            run.Click("choose-ai:0");
            check(!run.Frame.Hits.Any(hit => hit.Id == "picker-deleted" || hit.Id.StartsWith("deck-delete:", StringComparison.Ordinal)
                    || hit.Id.StartsWith("deck-restore:", StringComparison.Ordinal)) && run.View.DeckConfirmed,
                "An AI opponent's deck choices keep the player's confirmation and expose no saved-library removal controls");
            run.Click("setup-close");
            run.Click("change-deck");
            run.Until(() => engine.ReceiptReads >= 2, "Opening Your decks performs its receipt observation");
            var deleteOther = run.Hit("deck-delete:b");
            check(run.View.HitAt(deleteOther.Bounds.Center)?.Id == deleteOther.Id,
                "A saved deck's explicit Delete hit owns its pointer region ahead of the surrounding deck-selection tile");
            var deleteRelease = Gate();
            var staleReceipts = new TaskCompletionSource<IReadOnlyList<DeckLibraryDeletion>>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DeleteRelease = deleteRelease.Task;
            run.Click(deleteOther.Id);
            run.Until(() => engine.DeleteCalls == 1 && run.View.Busy, "Delete reaches storage once and waits for its actual result");
            check(run.View.SavedDecks.Any(deck => deck.Id == "b") && run.View.SelectedDeck?.Id == current && run.View.DeckConfirmed,
                "An unacknowledged delete keeps its saved row and the unrelated confirmed selection intact");
            deleteOther.Action();
            int reads = engine.ReceiptReads;
            engine.ReceiptReadOverride = () => staleReceipts.Task;
            run.View.RefreshDeckLibrary();
            run.Until(() => engine.ReceiptReads > reads, "A receipt observation starts while the delete command is in flight");
            deleteRelease.TrySetResult();
            run.Until(() => !run.View.Busy && run.View.DeletedDecks.Any(deck => deck.DeckId == "b"), "The acknowledged delete exposes its named Undo receipt");
            staleReceipts.TrySetResult([]);
            run.Until(() => engine.ReceiptCompletions > reads, "The older receipt observation completes after the successful delete");
            run.Tick(); run.Tick();
            check(engine.DeleteCalls == 1 && run.View.SavedDecks.All(deck => deck.Id != "b")
                    && run.View.DeletedDecks.Any(deck => deck.DeckId == "b") && run.View.SelectedDeck?.Id == current && run.View.DeckConfirmed,
                "Duplicate delete input and a late empty receipt observation cannot cancel the successful removal, lose Undo or withdraw another selected deck");
            engine.ReceiptReadOverride = null; engine.DeleteRelease = null;
            deleteOther.Action(); run.Tick();
            check(engine.DeleteCalls == 1, "A captured Delete callback cannot delete a row that has already disappeared");
            var otherReceipt = run.View.DeletedDecks.Single(deck => deck.DeckId == "b");
            var oldRestore = run.Hit("deck-restore:" + otherReceipt.RestoreToken);
            run.Click(oldRestore.Id);
            run.Until(() => !run.View.Busy && run.View.SavedDecks.Any(deck => deck.Id == "b"), "Undo returns the actual saved row to the visible active library");
            check(run.View.SelectedDeck?.Id == current && run.View.DeckConfirmed && run.Frame.Hits.Any(hit => hit.Id == "deck:b"),
                "Undo visibly reveals the restored active deck without selecting it over an unrelated confirmed list");
            oldRestore.Action(); run.Tick();
            check(engine.RestoreCalls == 1, "A consumed Undo receipt cannot be submitted again through a captured callback");
            run.Click("setup-close"); run.Click("edit-deck");
            run.Until(() => run.View.DeckEditor && !run.View.Busy && !run.View.ActivityOpen
                    && run.Frame.Hits.Any(hit => hit.Id == "deck-delete:a"), "The current saved deck opens in the actual editor with its exposed Delete control");
            var oldCurrentDelete = run.Hit("deck-delete:a");
            run.Click(oldCurrentDelete.Id);
            run.Until(() => !run.View.Busy && run.View.SelectedDeck == null, "Deleting the current deck clears its selection after storage acknowledges it");
            check(!run.View.DeckEditor && run.View.DeckDetails == null && !run.View.DeckConfirmed && run.View.SoloSetup == null
                    && run.View.SetupModal == "decks" && run.View.DeletedDecks.Any(deck => deck.DeckId == "a"),
                "Current-deck deletion closes stale editor/review state and clears confirmation and the prepared solo table while preserving named Undo");
            var receipt = run.View.DeletedDecks.Single(deck => deck.DeckId == "a");
            run.Click("picker-deleted"); run.Click("deck-restore:" + receipt.RestoreToken);
            run.Until(() => !run.View.Busy && run.Frame.Hits.Any(hit => hit.Id == "deck:a"), "Undo from Deleted reveals the restored active tile instead of leaving an empty Deleted page");
            check(run.View.SelectedDeck == null && run.View.DeckDetails == null, "Restoring a deleted current deck does not implicitly reopen or confirm it");
            oldCurrentDelete.Action(); run.Tick();
            check(engine.DeleteCalls == 2, "A captured pre-delete control cannot delete the restored identical saved bytes in a later library generation");
            run.Click("picker-presets");
            check(!run.Frame.Hits.Any(hit => hit.Id.StartsWith("deck-delete:", StringComparison.Ordinal) || hit.Id.StartsWith("deck-restore:", StringComparison.Ordinal)),
                "Precon choices expose neither saved-deck deletion nor restoration");
            run.Click("picker-saved");
            var lobbyDelete = run.Hit("deck-delete:b");
            run.View.PresentLobby(new("hosting", "Controlled lobby", null, false, null, false, "", null, [], []), true); run.Tick();
            lobbyDelete.Action(); run.Tick();
            check(!run.Frame.Hits.Any(hit => hit.Id.StartsWith("deck-delete:", StringComparison.Ordinal)) && engine.DeleteCalls == 2
                    && run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value?.Contains("Leave the lobby", StringComparison.Ordinal) == true),
                "A connected lobby explains the leave requirement and rejects even a previously captured Delete without silently withdrawing readiness");
            run.View.Accept(new InteractionScenarioEngine().State); run.Tick();
            lobbyDelete.Action(); run.Tick();
            check(run.View.Match != null && engine.DeleteCalls == 2
                    && !run.Frame.Hits.Any(hit => hit.Id.StartsWith("deck-delete:", StringComparison.Ordinal) || hit.Id.StartsWith("deck-restore:", StringComparison.Ordinal)),
                "A live table exposes no library mutations and rejects an older saved-deck Delete callback");
        }
        CheckRestartAndReusedId(profile, check);
        CheckUnsupportedAndUnsaved(profile, check);
    }

    private static void CheckRestartAndReusedId(string profile, Action<bool, string> check)
    {
        var engine = new LibraryEngine();
        engine.StoredReceipts.Add(new("a", "The original deleted list", "older-a") { DeletedAt = 1 });
        engine.StoredReceipts.Add(new("removed", "A persisted deleted Commander experiment with its complete saved name", "persisted") { DeletedAt = 2 });
        using var run = new Runner(profile, "deck-library-restart", engine, check);
        check(run.View.SelectedDeck?.Id == "a" && run.View.DeckDetails?.LibraryToken == "bytes:a:1",
            "An active saved file with a current token remains readable when an older deletion receipt reuses its opaque ID");
        run.Click("change-deck"); run.Click("picker-deleted");
        check(run.Frame.Hits.Any(hit => hit.Id == "deck-restore:persisted") && run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
                .Any(text => text.Value == engine.StoredReceipts.Single(receipt => receipt.RestoreToken == "persisted").Name && text.MaxLines > 1),
            "A fresh application discovers durable deletion receipts and offers Undo beside the full wrapped saved-deck name");
    }
    private static void CheckUnsupportedAndUnsaved(string profile, Action<bool, string> check)
    {
        using (var run = new Runner(profile, "deck-library-unsupported", new LibraryEngine { SupportsDeckLibrary = false }, check)) {
            run.Click("change-deck");
            check(!run.Frame.Hits.Any(hit => hit.Id == "picker-deleted" || hit.Id.StartsWith("deck-delete:", StringComparison.Ordinal)),
                "An engine without the negotiated optional deck-library capability exposes no unsupported Delete or Deleted controls");
        }
        var failed = new LibraryEngine(); failed.Saved["a"] = failed.Saved["a"] with { SaveError = "Controlled unsaved edit" };
        using (var run = new Runner(profile, "deck-library-unsaved", failed, check)) {
            run.Click("change-deck");
            check(!run.Frame.Hits.Any(hit => hit.Id == "deck-delete:a") && run.View.DeckDetails?.SaveError == "Controlled unsaved edit",
                "A current unsaved failure keeps its document/error and offers no destructive saved-deck action");
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Runner : IDisposable
    {
        private readonly TableApplication app;
        private readonly Action<bool, string> check;
        private double now;
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;
        internal Runner(string profile, string name, LibraryEngine engine, Action<bool, string> check)
        {
            string directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            this.check = check; app = new(TestTableServices.For(engine), directory) { Automated = true }; app.Initialize(); View.DismissWelcome();
            Until(() => View.Loaded && !View.Busy && !View.StartupVisible && !View.ActivityOpen, "The controlled deck library opens through normal startup");
        }
        internal void Tick(Point? pointer = null, ButtonState left = ButtonState.Released)
        {
            now += .025; var point = pointer ?? new Point(20, 70);
            var time = new GameTime(TimeSpan.FromSeconds(now), TimeSpan.FromSeconds(.025));
            var physical = RenderViewport.Fit(new(1000, 740), View.DesignHeight).ToPhysical(point.ToVector2() + new Vector2(.5f));
            app.Update(time, new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default), new(), new(1000, 740));
            Frame = app.ComposePresentation(time);
        }
        internal void Until(Func<bool> ready, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!ready() || Frame == null) && View.Error.Length == 0 && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready() && View.Error.Length == 0, description + (View.Error.Length > 0 ? ": " + View.Error : ""));
            Tick();
        }
        internal Mana.Table.Interaction.TableHit Hit(string id) => Frame.Hits.LastOrDefault(hit => hit.Id == id)
            ?? throw new InvalidOperationException("Missing deck-library control " + id);
        internal void Click(string id)
        {
            Until(() => !View.Busy && !View.ActivityOpen && Frame.Hits.Any(hit => hit.Id == id), "The current deck-library control is exposed: " + id);
            var hit = Hit(id); Tick(hit.Bounds.Center, ButtonState.Pressed); Tick(hit.Bounds.Center);
        }
        public void Dispose() => app.Dispose();
    }

    private sealed class LibraryEngine : ICardEngine, IDeckLibrary
    {
        internal readonly Dictionary<string, DeckDetails> Saved = new() { ["a"] = Details("a", "A Current Commander deck"), ["b"] = Details("b", "B Commander experiments with counters and landfall") };
        internal readonly List<DeckLibraryDeletion> StoredReceipts = [];
        private readonly Dictionary<string, DeckDetails> removed = [];
        internal Func<Task<IReadOnlyList<DeckLibraryDeletion>>>? ReceiptReadOverride;
        internal Task? DeleteRelease;
        private int deletes, restores, reads, completedReads;
        internal int DeleteCalls => Volatile.Read(ref deletes);
        internal int RestoreCalls => Volatile.Read(ref restores);
        internal int ReceiptReads => Volatile.Read(ref reads);
        internal int ReceiptCompletions => Volatile.Read(ref completedReads);
        public bool SupportsDeckLibrary { get; init; } = true;
        public string Name => "Controlled saved-deck library";
        private static DeckDetails Details(string id, string name) => new(id, name, 1,
            [new("Commander", "rhys", new() { Name = "Rhys the Redeemed", Type = "Legendary Creature" }, 1),
                new("Main", "forest", new() { Name = "Forest", Type = "Basic Land" }, 99)], [], "", "") { LibraryToken = "bytes:" + id + ":1" };
        public Task InitializeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IReadOnlyList<DeckSummary>> DecksAsync() => Task.FromResult<IReadOnlyList<DeckSummary>>(Saved.Values.Select(deck => new DeckSummary(deck.Id, deck.Name) { LibraryToken = deck.LibraryToken }).ToArray());
        public Task<IReadOnlyList<DeckPreset>> PresetsAsync() => Task.FromResult<IReadOnlyList<DeckPreset>>([new("precon", "A precon", "A complete deck")]);
        public Task<DeckDetails> OpenDeckAsync(string id) => Task.FromResult(Saved[id]);
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Task.FromResult(new CatalogPage(0, 0, []));
        public async Task<IReadOnlyList<DeckLibraryDeletion>> DeletedDecksAsync()
        {
            Interlocked.Increment(ref reads);
            var result = ReceiptReadOverride == null ? StoredReceipts.ToArray() : await ReceiptReadOverride();
            Interlocked.Increment(ref completedReads); return result;
        }
        public async Task<DeckLibraryDeletion> DeleteDeckAsync(string id, string token)
        {
            Interlocked.Increment(ref deletes);
            if (DeleteRelease != null) await DeleteRelease;
            if (!Saved.TryGetValue(id, out var deck) || deck.LibraryToken != token) throw new InvalidOperationException("Stale controlled deck token");
            var receipt = new DeckLibraryDeletion(id, deck.Name, "restore:" + id + ":" + deletes);
            Saved.Remove(id); removed.Add(receipt.RestoreToken, deck); StoredReceipts.Insert(0, receipt); return receipt;
        }
        public Task<DeckSummary> RestoreDeckAsync(string id, string token)
        {
            Interlocked.Increment(ref restores);
            if (!removed.Remove(token, out var deck) || deck.Id != id || Saved.ContainsKey(id)) throw new InvalidOperationException("Stale controlled restore token");
            Saved.Add(id, deck); StoredReceipts.RemoveAll(receipt => receipt.RestoreToken == token);
            return Task.FromResult(new DeckSummary(id, deck.Name) { LibraryToken = deck.LibraryToken });
        }
        public Task<SoloSetup> PrepareSoloAsync(string id) => Task.FromResult(new SoloSetup(id, Saved[id].Name, [new("ai", "AI deck", "Controlled opponent")]));
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(null);
        private static Task<T> Unsupported<T>() => throw new InvalidOperationException("Unexpected deck-library scenario action");
        private static Task Unsupported() => throw new InvalidOperationException("Unexpected deck-library scenario action");
        public Task<DeckSummary> ImportAsync(string name, string text) => Unsupported<DeckSummary>();
        public Task<DeckSummary> ImportPresetAsync(string id) => Unsupported<DeckSummary>();
        public Task<DeckDetails> CreateDeckAsync(string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> EditDeckAsync(string id, long revision, IReadOnlyList<DeckEdit> edits) => Unsupported<DeckDetails>();
        public Task<DeckDetails> RenameDeckAsync(string id, long revision, string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> DuplicateDeckAsync(string id, long revision, string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> UndoDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<DeckDetails> RedoDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<DeckDetails> SaveDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<string> ExportDeckAsync(string id, long revision) => Unsupported<string>();
        public Task<DeckDetails> SetCommandersAsync(string id, long revision, IReadOnlyList<string> cards) => Unsupported<DeckDetails>();
        public Task StartSoloAsync(string id, IReadOnlyList<string> opponents) => Unsupported();
        public Task<Lobby> HostAsync(int players, bool forwarding) => Unsupported<Lobby>();
        public Task<Lobby> JoinAsync(string address) => Unsupported<Lobby>();
        public Task<Lobby> LobbyAsync() => Unsupported<Lobby>();
        public Task SelectDeckAsync(string id) => Unsupported();
        public Task SetReadyAsync(bool ready) => Unsupported();
        public Task StartAsync() => Unsupported();
        public Task ReturnToLobbyAsync() => Unsupported();
        public Task LeaveAsync() => Unsupported();
        public Task ExecuteAsync(GameCommand reply) => Unsupported();
        public Task ConcedeAsync(string id) => Unsupported();
    }
}
