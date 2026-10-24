using System.Collections.Immutable;
using System.Collections.Concurrent;
using Mana.Contracts;
using Mana.Client;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Controlled setup/connection checks cover command ordering and commitment.
// These do not simulate game rules or approve the visual home composition.
internal static class PregameFlowChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckDeckEditing(profile, check);
        CheckMissingPartner(profile, check);
        var solo = new PregameEngine();
        solo.Preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var run = new Runner(profile, "quick-solo", solo, check)) {
            check(!run.View.DeckConfirmed && run.View.SoloSetup == null && solo.PrepareCalls == 0,
                "Selecting a saved deck does not prepare a table or require confirming a review first");
            var play = run.Hit("start-solo");
            run.Click("start-solo");
            run.Until(() => solo.PrepareCalls == 1 && run.View.Busy, "One Play intent reaches authoritative AI preparation");
            run.ClickAt(play.Bounds.Center);
            check(solo.PrepareCalls == 1 && solo.StartCalls == 0 && run.View.SetupModal != "confirm",
                "A pending one-click Play cannot be repeated and does not stop in a review/confirmation screen");
            solo.Preparing.TrySetResult(solo.Setup("a"));
            run.Until(() => run.View.Match != null && !run.View.Busy, "The same Play intent completes preparation and enters the authored opening state");
            check(solo.PrepareCalls == 1 && solo.StartCalls == 1 && solo.StartDeck == "a"
                    && solo.StartOpponents.SequenceEqual(["ai-1", "ai-2", "ai-1"])
                    && run.View.LocalMatch && solo.Calls.ToArray().SequenceEqual(["open:a", "prepare:a", "start-solo:a"]),
                "Quick Play starts exactly the selected saved deck with compatible default AI seats and no multiplayer readiness mutation");
        }
        foreach (string failure in new[] { "draft", "save" }) {
            var invalid = new PregameEngine { Invalid = failure };
            using var run = new Runner(profile, "quick-solo-" + failure, invalid, check);
            check(!run.Frame.Hits.Any(hit => hit.Id == "start-solo") && invalid.PrepareCalls == 0 && invalid.StartCalls == 0,
                "An invalid " + failure + " deck has no committed Play path and does not prepare or start a game");
            run.Click("friends-tab"); run.Click("host");
            run.Until(() => run.View.Lobby?.Mode == "hosting" && !run.View.Busy, "An incomplete list can join a pregame table without being submitted");
            check(invalid.Calls.ToArray().SequenceEqual(["open:a", "host:4"])
                    && !run.Frame.Hits.Any(hit => hit.Id == "ready") && invalid.ReadyCalls == 0,
                "An invalid " + failure + " list is neither assigned nor readied when opening a friends table");
        }
        var friends = new PregameEngine();
        using (var run = new Runner(profile, "quick-friends", friends, check)) {
            run.Click("friends-tab"); run.Click("host");
            run.Until(() => run.View.Lobby?.Mode == "hosting" && !run.View.Busy, "Hosting opens its actual connected table");
            check(friends.Calls.ToArray().SequenceEqual(["open:a", "host:4", "select:a"])
                    && run.View.Lobby!.Seats.Single(seat => seat.Local) is { Deck: "Deck a", Ready: false }
                    && friends.ReadyCalls == 0 && friends.PrepareCalls == 0,
                "Hosting brings the valid selected deck directly, remains unready, and performs no solo preparation");
            run.Click("change-deck"); run.Click("deck:b");
            run.Until(() => run.View.SelectedDeck?.Id == "b" && !run.View.Busy, "Choosing another saved deck returns to the same pregame context");
            check(run.View.SetupModal.Length == 0 && !run.View.DeckConfirmed
                    && run.View.Lobby!.Seats.Single(seat => seat.Local).Ready == false
                    && friends.Calls.ToArray().All(call => call != "ready:True"),
                "Choosing a different saved list opens no forced review and never readies the player");
            run.Click("ready");
            run.Until(() => run.View.Lobby!.Seats.Single(seat => seat.Local).Ready && !run.View.Busy,
                "The explicit Ready intent submits the newly selected deck and readies its local seat");
            var calls = friends.Calls.ToArray();
            check(calls.TakeLast(2).SequenceEqual(["select:b", "ready:True"]) && run.View.DeckConfirmed
                    && friends.FriendStartCalls == 0 && !run.Frame.Hits.Any(hit => hit.Id == "start"),
                "Ready selects the exact current list before readiness and cannot start while other seats are still open");
            int before = friends.Calls.Count;
            run.Click("change-deck"); run.Click("deck:b");
            check(friends.Calls.Count == before && run.View.Lobby!.Seats.Single(seat => seat.Local).Ready && run.View.SetupModal.Length == 0,
                "Rechoosing the already selected list preserves readiness without reopening it in the provider");
            string policy = run.View.Lobby!.Tempo!.Revision;
            run.Click("tempo:Fast");
            run.Until(() => run.View.Lobby?.Tempo?.Preset == TempoPreset.Fast && !run.View.Busy,
                "The host changes tempo directly in the connected lobby without a modal");
            check(run.View.Lobby!.Tempo!.Revision != policy && !run.View.Lobby.Seats.Single(seat => seat.Local).Ready,
                "A new timing policy clears Ready consent while keeping the selected deck");
            run.Click("tempo-expiry:Warn");
            run.Until(() => run.View.Lobby?.Tempo?.Expiry == TempoExpiry.Warn && !run.View.Busy, "Warnings-only timing is selectable independently of the decision budget");
            run.Click("ready");
            run.Until(() => run.View.Lobby!.Seats.Single(seat => seat.Local).Ready && !run.View.Busy, "Ready acknowledges the exact displayed tempo policy");
            check(friends.AcknowledgedTempo == run.View.Lobby!.Tempo!.Revision, "Ready carries the policy revision the player actually reviewed");
        }
    }

    private static void CheckMissingPartner(string profile, Action<bool, string> check)
    {
        var storage = new EditingCatalog(); storage.RemovePartner();
        using var run = new Runner(profile, "find-missing-partner", new PregameEngine(), check, storage);
        run.Click("edit-deck");
        run.Until(() => run.View.DeckEditor && !run.View.Busy && !run.View.ActivityOpen, "A sole commander opens with its actual pairing discovery hint");
        check(run.Frame.Hits.Any(hit => hit.Id == "deck-choose-partner") && storage.Current.CommanderOptions.Single().Partners.Length == 0,
            "A provider-declared pairing commander offers Find a partner even when no compatible card is in the deck yet");
        run.Click("catalog-mana"); run.Click("catalog-option:mana:2");
        run.Click("deck-choose-partner");
        run.Until(() => run.View.Catalog is { Cards.Length: > 0 } && !run.View.CatalogBusy,
            "The partner route searches the library using the provider's actual named counterpart");
        check(storage.LastQuery?.Text == "Kediss, Emberclaw Familiar" && storage.LastQuery?.ColorIdentity == null && storage.LastQuery?.MaxManaValue == null,
            "A missing named partner is not hidden by previous mana-value filters or the current sole commander's color identity");
        run.Click("deck-issues-filter");
        check(run.Frame.Hits.Any(hit => hit.Id == "deck-card:Main:forest"),
            "Switching from an empty partner search to Problems clears the partner-only restriction and shows the actual affected card");
        run.Click("deck-choose-partner"); run.Click("deck-issue:0");
        check(run.Frame.Hits.Any(hit => hit.Id == "deck-card:Main:forest"),
            "Following a deck issue also leaves partner selection before exposing the affected card");
        run.Click("deck-choose-partner");
        run.Until(() => run.View.Catalog is { Cards.Length: > 0 } && !run.View.CatalogBusy, "Returning to partner discovery restores the actual named search");
        run.Click("catalog-card:kediss");
        check(!run.Frame.Hits.Any(hit => hit.Id == "deck-add-partner"),
            "A catalog result cannot be paired before the deck provider supplies its compatible in-deck handle");
        run.Click("deck-add");
        run.Until(() => !run.View.Busy && run.View.DeckDetails!.CommanderOptions.Single(option => option.Id == "rograkh").Partners.Contains("kediss"),
            "Adding the library card obtains its compatibility from the actual saved-deck response");
        run.Click("deck-add-partner");
        run.Until(() => !run.View.Busy && run.View.DeckDetails?.CommanderIds.Length == 2,
            "The missing partner can be found, added and paired in the same builder without a review gate");
    }

    private static void CheckDeckEditing(string profile, Action<bool, string> check)
    {
        var storage = new EditingCatalog();
        var lifetime = new DeckPersistenceSession();
        var original = storage.Current;
        lifetime.Observe(original);
        var abandoned = lifetime.Begin(new(original.Id, original.Revision))!;
        lifetime.Observe(original with { Id = "other" });
        check(!lifetime.Accept(abandoned, original with { Revision = 2 }) && !lifetime.Fail(abandoned, "late failure")
                && lifetime.View.Context?.DeckId == "other" && lifetime.View.Status == DeckPersistenceStatus.Saved,
            "Neither a late save receipt nor its failure can replace another document's acknowledged state");
        lifetime.Observe(original);
        var outdated = lifetime.Begin(new(original.Id, original.Revision))!;
        check(!lifetime.Accept(outdated, original with { Revision = 0 }) && lifetime.View.Status == DeckPersistenceStatus.Failed,
            "An older provider revision cannot acknowledge a current save");
        lifetime.Observe(original);
        var wrongDocument = lifetime.Begin(new(original.Id, original.Revision))!;
        check(!lifetime.Accept(wrongDocument, original with { Id = "other" }) && lifetime.View.Status == DeckPersistenceStatus.Failed,
            "A save returning an unrelated document fails closed instead of marking that document selected or saved");
        lifetime.Close();
        using var run = new Runner(profile, "commander-save-journey", new PregameEngine(), check, storage);
        bool Text(string value) => run.Frame.Drawing.Commands.Any(command => command switch {
            DrawCommand.Text text => text.Value == value, DrawCommand.CenterText text => text.Value == value, _ => false
        });
        run.Click("edit-deck");
        run.Until(() => run.View.DeckEditor && !run.View.Busy && !run.View.ActivityOpen, "The saved document opens directly in its builder");
        check(Text("Deck saved") && run.Frame.Hits.Any(hit => hit.Id == "deck-choose-commander")
            && !run.Frame.Hits.Any(hit => hit.Id is "deck-rename" or "deck-duplicate"),
            "A saved draft separates its storage acknowledgment from commander selection and keeps administration behind Deck options");
        run.Click("deck-choose-commander"); run.Click("deck-card:Main:rograkh");
        storage.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Click("deck-set-commander");
        run.Until(() => storage.CommanderWrites == 1 && run.View.Busy, "Choosing a commander reaches storage exactly once");
        check(Text("Saving…") && !Text("Deck saved") && run.View.DeckDetails!.Revision == 1
                && !run.Frame.Hits.Any(hit => hit.Id == "deck-done"),
            "An outstanding save retains the last acknowledged document, displays Saving, and cannot leave through Back to play");
        storage.Release.TrySetResult();
        run.Until(() => !run.View.Busy && run.View.DeckDetails?.CommanderIds.SequenceEqual(["rograkh"]) == true,
            "Only the actual storage reply selects and saves the first commander");
        check(Text("Deck saved") && Text("Rograkh, Son of Rohgahh") && run.Frame.Hits.Any(hit => hit.Id == "deck-choose-partner"),
            "The selected commander's full name and compatible partner route remain visible beside acknowledged save state");
        var staleSole = run.Hit("deck-leader:rograkh");
        storage.Release = null;
        run.Click("deck-choose-partner");
        check(run.Frame.Hits.Any(hit => hit.Id == "deck-card:Main:kediss")
                && !run.Frame.Hits.Any(hit => hit.Id == "deck-card:Main:forest" || hit.Id == "deck-card:Commander:rograkh"),
            "The partner route shows only the provider's compatible second-commanders, independent of printed type or name guesses");
        run.Click("deck-card:Main:kediss");
        check(Text("Pair with your commander"), "The compatible selected card makes pairing the primary action");
        storage.FailNextListRead = true;
        run.Click("deck-add-partner");
        run.Until(() => !run.View.Busy && run.View.DeckDetails?.CommanderIds.Length == 2, "The paired selection receives its actual persistence acknowledgment");
        check(Text("Rograkh, Son of Rohgahh") && Text("Kediss, Emberclaw Familiar")
                && Text("PAIRED COMMANDERS") && Text("Deck saved"),
            "Both full commander names and their paired relationship remain visible after saving");
        check(run.View.Error.Length == 0 && run.View.DeckDetails!.Revision == storage.Current.Revision && storage.ListFailures == 1,
            "A library-list refresh failure after a successful save retains the actual new revision and its saved acknowledgment");
        check(!run.View.Activate(staleSole), "A prior single-commander control cannot act after the saved revision changes");
        run.Click("deck-done");
        check(run.View.HomeVisible && Text("Deck saved") && run.Frame.Hits.Any(hit => hit.Id == "start-solo")
                && !run.View.DeckConfirmed && run.View.SetupModal.Length == 0,
            "Back to play retains the acknowledged pair and offers Play without another review or confirmation");
        run.Click("edit-deck");
        run.Until(() => !run.View.Busy && !run.View.ActivityOpen && run.View.DeckEditor, "Reopening reads the same persisted pair");
        check(run.View.DeckDetails!.CommanderIds.Length == 2, "Both commanders survive reopening through the catalog");
        run.Click("deck-leader:rograkh"); storage.NextSaveError = "Controlled disk rejection";
        run.Click("deck-set-commander");
        run.Until(() => !run.View.Busy && run.View.DeckDetails?.SaveError.Length > 0, "The storage rejection returns an explicit unsaved document", allowError: true);
        check(Text("Not saved") && !Text("Deck saved") && run.Frame.Hits.Any(hit => hit.Id == "deck-retry-save"),
            "A failed persistence reply stays visibly unsaved and exposes Retry save");
        run.Click("deck-retry-save");
        run.Until(() => !run.View.Busy && run.View.DeckDetails?.SaveError.Length == 0, "Retry waits for the actual successful persistence reply");
        check(Text("Deck saved"), "A successful retry restores acknowledged save state");
    }

    private sealed class Runner : IDisposable
    {
        private readonly TableApplication app;
        private readonly Action<bool, string> check;
        private double now;
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;
        internal Runner(string profile, string name, PregameEngine engine, Action<bool, string> check, IEngineCatalog? catalog = null) {
            string directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            this.check = check; app = new(TestTableServices.For(engine) with { Catalog = catalog ?? engine }, directory) { Automated = true };
            app.Initialize();
            Until(() => View.Loaded && !View.Busy && !View.ActivityOpen, "The controlled pregame opens its selected saved deck");
        }
        internal void Tick(Point? point = null, ButtonState left = ButtonState.Released) {
            now += .025;
            var time = new GameTime(TimeSpan.FromSeconds(now), TimeSpan.FromSeconds(.025));
            var physical = RenderViewport.Fit(new(1000, 740), View.DesignHeight).ToPhysical((point ?? new Point(20, 70)).ToVector2() + new Vector2(.5f));
            app.Update(time, new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default), new(), new(1000, 740));
            Frame = app.ReadPresentation();
        }
        internal void Until(Func<bool> ready, string description, bool allowError = false) {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!ready() || Frame == null) && (allowError || View.Error.Length == 0) && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready() && (allowError || View.Error.Length == 0), description + (View.Error.Length > 0 && !allowError ? ": " + View.Error : "")); Tick();
        }
        internal Mana.Table.Interaction.TableHit Hit(string id) => Frame.Hits.LastOrDefault(hit => hit.Id == id)
            ?? throw new InvalidOperationException("Missing current pregame control: " + id);
        internal void Click(string id) {
            Until(() => !View.Busy && !View.ActivityOpen && Frame.Hits.Any(hit => hit.Id == id), "The current pregame intent is exposed: " + id, allowError: id == "deck-retry-save");
            ClickAt(Hit(id).Bounds.Center);
        }
        internal void ClickAt(Point point) { Tick(point, ButtonState.Pressed); Tick(point); }
        public void Dispose() => app.Dispose();
    }

    private sealed class EditingCatalog : IEngineCatalog
    {
        private DeckDetails deck = new("a", "Partner deck", 1,
            [new("Main", "rograkh", new() { Name = "Rograkh, Son of Rohgahh", Type = "Legendary Creature" }, 1),
             new("Main", "kediss", new() { Name = "Kediss, Emberclaw Familiar", Type = "Legendary Creature" }, 1),
             new("Main", "forest", new() { Name = "Forest", Type = "Basic Land" }, 98)],
            [new("rograkh", ["kediss"]), new("kediss", ["rograkh"])], "Choose a commander.", "");
        internal TaskCompletionSource? Release;
        internal string NextSaveError = "";
        internal bool FailNextListRead;
        internal DeckDetails Current => deck;
        internal int ListFailures;
        internal CardQuery? LastQuery;
        private int writes;
        internal int CommanderWrites => Volatile.Read(ref writes);
        public Task<ImmutableArray<DeckSummary>> DecksAsync() {
            if (FailNextListRead) { FailNextListRead = false; ListFailures++; throw new IOException("Controlled list-read failure"); }
            return Task.FromResult<ImmutableArray<DeckSummary>>([new(deck.Id, deck.Name)]);
        }
        public Task<ImmutableArray<DeckPreset>> PresetsAsync() => Task.FromResult<ImmutableArray<DeckPreset>>([]);
        public Task<DeckDetails> OpenDeckAsync(string id) => Task.FromResult(id == deck.Id ? deck : throw new InvalidOperationException("Unknown saved deck"));
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) {
            LastQuery = query;
            return Task.FromResult(query.Text == "Kediss, Emberclaw Familiar"
                ? new CatalogPage(1, 0, [new("kediss", new() { Name = "Kediss, Emberclaw Familiar", Type = "Legendary Creature" }, 1, 8, "Main", "")])
                : new CatalogPage(0, 0, []));
        }
        internal void RemovePartner() {
            deck = deck with { Entries = deck.Entries.Where(entry => entry.Id != "kediss").Select(entry => entry.Id == "rograkh" ? entry with { Section = "Commander" } : entry).ToImmutableArray(),
                CommanderOptions = [new("rograkh", []) { CanPartner = true, PartnerName = "Kediss, Emberclaw Familiar" }], Problem = "Add one card.",
                Issues = [new("COLOR_IDENTITY", "A controlled issue on a card outside the partner candidates.", "Main", ["forest"])] };
        }
        public async Task<DeckDetails> SetCommandersAsync(string id, long revision, ImmutableArray<string> cards) {
            if (id != deck.Id || revision != deck.Revision || cards.Any(card => !deck.CommanderOptions.Any(option => option.Id == card)))
                throw new InvalidOperationException("Stale or ineligible commander selection");
            Interlocked.Increment(ref writes);
            if (Release != null) await Release.Task;
            deck = deck with { Revision = revision + 1, Entries = deck.Entries.Select(entry => entry with { Section = cards.Contains(entry.Id) ? "Commander" : "Main" }).ToImmutableArray(),
                Problem = cards.Length == 0 ? "Choose a commander." : "", SaveError = NextSaveError };
            NextSaveError = ""; return deck;
        }
        public Task<DeckDetails> SaveDeckAsync(string id, long revision) {
            if (id != deck.Id || revision != deck.Revision) throw new InvalidOperationException("Stale save retry");
            deck = deck with { SaveError = "" }; return Task.FromResult(deck);
        }
        private static Task<T> Unsupported<T>() => throw new NotSupportedException();
        public Task<DeckSummary> ImportAsync(string name, string text) => Unsupported<DeckSummary>();
        public Task<DeckSummary> ImportPresetAsync(string id) => Unsupported<DeckSummary>();
        public Task<DeckDetails> CreateDeckAsync(string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> EditDeckAsync(string id, long revision, ImmutableArray<DeckEdit> edits) {
            if (id != deck.Id || revision != deck.Revision || edits is not [{ CardId: "kediss", Section: "Main", Quantity: 1 }])
                throw new InvalidOperationException("Unexpected card edit");
            deck = deck with { Revision = revision + 1, Entries = [.. deck.Entries, new("Main", "kediss", new() { Name = "Kediss, Emberclaw Familiar", Type = "Legendary Creature" }, 1)],
                CommanderOptions = [new("rograkh", ["kediss"]), new("kediss", ["rograkh"])], Problem = "" };
            return Task.FromResult(deck);
        }
        public Task<DeckDetails> RenameDeckAsync(string id, long revision, string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> DuplicateDeckAsync(string id, long revision, string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> UndoDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<DeckDetails> RedoDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<string> ExportDeckAsync(string id, long revision) => Unsupported<string>();
    }

    private sealed class PregameEngine : ICardEngine, ITableTempo
    {
        internal ConcurrentQueue<string> Calls { get; } = new();
        internal string? AcknowledgedTempo;
        public Task<Lobby> ConfigureTempoAsync(string tableId, TempoPreset preset, TempoExpiry expiry) {
            if (lobby?.TableId != tableId) throw new InvalidOperationException("Stale table");
            lobby = lobby with { Tempo = new(preset, expiry, preset == TempoPreset.Fast ? 30 : 60, 60, Guid.NewGuid().ToString()),
                Seats = lobby.Seats.Select(seat => seat with { Ready = false }).ToImmutableArray() };
            return Task.FromResult(lobby);
        }
        public Task SetReadyAsync(string tableId, bool ready, string tempoRevision) {
            if (lobby?.TableId != tableId || lobby.Tempo?.Revision != tempoRevision) throw new InvalidOperationException("Stale tempo consent");
            AcknowledgedTempo = tempoRevision; return SetReadyAsync(ready);
        }
        public Task PresentedAsync(string gameId, string clockId) => Task.CompletedTask;
        internal TaskCompletionSource<SoloSetup>? Preparing;
        internal string Invalid { get; init; } = "";
        private int prepares, starts, readies, friendStarts;
        internal int PrepareCalls => Volatile.Read(ref prepares);
        internal int StartCalls => Volatile.Read(ref starts);
        internal int ReadyCalls => Volatile.Read(ref readies);
        internal int FriendStartCalls => Volatile.Read(ref friendStarts);
        internal string? StartDeck;
        internal string[] StartOpponents = [];
        private GameSnapshot? state;
        private Lobby? lobby;
        public string Name => "Controlled pregame";
        public Task InitializeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ImmutableArray<DeckSummary>> DecksAsync() => Task.FromResult<ImmutableArray<DeckSummary>>([new("a", "Deck a"), new("b", "Deck b")]);
        public Task<ImmutableArray<DeckPreset>> PresetsAsync() => Task.FromResult<ImmutableArray<DeckPreset>>([]);
        public Task<DeckDetails> OpenDeckAsync(string id) {
            Calls.Enqueue("open:" + id);
            return Task.FromResult(new DeckDetails(id, "Deck " + id, 1,
                [new("Commander", "commander", new() { Name = "Rhys the Redeemed", Type = "Legendary Creature" }, 1),
                    new("Main", "forest", new() { Name = "Forest", Type = "Basic Land" }, 99)], [],
                Invalid == "draft" ? "The saved draft is incomplete." : "", Invalid == "save" ? "The last save failed." : ""));
        }
        internal SoloSetup Setup(string id) => new(id, "Deck " + id,
            [new("ai-self", "Deck " + id, ""), new("ai-1", "Other deck one", ""), new("ai-2", "Other deck two", "")]) { MaxPlayers = 4 };
        public Task<SoloSetup> PrepareSoloAsync(string id) { Calls.Enqueue("prepare:" + id); Interlocked.Increment(ref prepares); return Preparing?.Task ?? Task.FromResult(Setup(id)); }
        public Task StartSoloAsync(string id, ImmutableArray<string> opponents) {
            if (id is not ("a" or "b") || opponents.Length != 3 || opponents.Any(opponent => !Setup(id).Opponents.Any(available => available.Id == opponent)))
                throw new InvalidOperationException("Unprepared or incompatible quick-play input.");
            Calls.Enqueue("start-solo:" + id); Interlocked.Increment(ref starts); StartDeck = id; StartOpponents = opponents.ToArray();
            state = PresentationSmoke.Example() with { Id = "quick-play-match" }; return Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult(state);
        public Task<Lobby> HostAsync(int players, bool forwarding) {
            Calls.Enqueue("host:" + players);
            lobby = new("hosting", "Open", null, false, "Open seats", false, "disabled", null, [],
                Enumerable.Range(0, players).Select(index => new Seat(index, index == 0 ? "You" : "Open", index == 0 ? "HUMAN" : "OPEN", null, false, index == 0)).ToImmutableArray(), "pregame-table") {
                    Tempo = new(TempoPreset.Off, TempoExpiry.Pass, 0, 60, "initial-policy")
                };
            return Task.FromResult(lobby);
        }
        public Task<Lobby> LobbyAsync() => Task.FromResult(lobby ?? throw new InvalidOperationException("No connected table."));
        public Task SelectDeckAsync(string id) {
            if (lobby == null || id is not ("a" or "b")) throw new InvalidOperationException("Illegal lobby deck selection.");
            Calls.Enqueue("select:" + id); lobby = lobby with { Seats = lobby.Seats.Select(seat => seat.Local ? seat with { Deck = "Deck " + id, Ready = false } : seat).ToImmutableArray() }; return Task.CompletedTask;
        }
        public Task SetReadyAsync(bool ready) {
            if (lobby == null || ready && lobby.Seats.Single(seat => seat.Local).Deck == null) throw new InvalidOperationException("Ready requires an assigned deck.");
            Calls.Enqueue("ready:" + ready); Interlocked.Increment(ref readies);
            lobby = lobby with { Seats = lobby.Seats.Select(seat => seat.Local ? seat with { Ready = ready } : seat).ToImmutableArray() }; return Task.CompletedTask;
        }
        public Task StartAsync() { Interlocked.Increment(ref friendStarts); throw new InvalidOperationException("A table with open seats cannot start."); }
        private static Task<T> Unsupported<T>() => throw new NotSupportedException();
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Task.FromResult(new CatalogPage(0, 0, []));
        public Task<DeckSummary> ImportAsync(string name, string text) => Unsupported<DeckSummary>();
        public Task<DeckSummary> ImportPresetAsync(string id) => Unsupported<DeckSummary>();
        public Task<DeckDetails> CreateDeckAsync(string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> EditDeckAsync(string id, long revision, ImmutableArray<DeckEdit> edits) => Unsupported<DeckDetails>();
        public Task<DeckDetails> RenameDeckAsync(string id, long revision, string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> DuplicateDeckAsync(string id, long revision, string name) => Unsupported<DeckDetails>();
        public Task<DeckDetails> UndoDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<DeckDetails> RedoDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<DeckDetails> SaveDeckAsync(string id, long revision) => Unsupported<DeckDetails>();
        public Task<string> ExportDeckAsync(string id, long revision) => Unsupported<string>();
        public Task<DeckDetails> SetCommandersAsync(string id, long revision, ImmutableArray<string> cards) => Unsupported<DeckDetails>();
        public Task<Lobby> JoinAsync(string address) => Unsupported<Lobby>();
        public Task ReturnToLobbyAsync() => throw new NotSupportedException();
        public Task LeaveAsync() => throw new NotSupportedException();
        public Task ExecuteAsync(GameCommand command) => throw new NotSupportedException();
        public Task ConcedeAsync(string id) => throw new NotSupportedException();
    }
}
