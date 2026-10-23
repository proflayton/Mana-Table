using System.Collections.Concurrent;
using Mana.Contracts;
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
        }
    }

    private sealed class Runner : IDisposable
    {
        private readonly TableApplication app;
        private readonly Action<bool, string> check;
        private double now;
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;
        internal Runner(string profile, string name, PregameEngine engine, Action<bool, string> check) {
            string directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            this.check = check; app = new(TestTableServices.For(engine), directory) { Automated = true };
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
        internal void Until(Func<bool> ready, string description) {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!ready() || Frame == null) && View.Error.Length == 0 && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready() && View.Error.Length == 0, description + (View.Error.Length > 0 ? ": " + View.Error : "")); Tick();
        }
        internal Mana.Table.Interaction.TableHit Hit(string id) => Frame.Hits.LastOrDefault(hit => hit.Id == id)
            ?? throw new InvalidOperationException("Missing current pregame control: " + id);
        internal void Click(string id) {
            Until(() => !View.Busy && !View.ActivityOpen && Frame.Hits.Any(hit => hit.Id == id), "The current pregame intent is exposed: " + id);
            ClickAt(Hit(id).Bounds.Center);
        }
        internal void ClickAt(Point point) { Tick(point, ButtonState.Pressed); Tick(point); }
        public void Dispose() => app.Dispose();
    }

    private sealed class PregameEngine : ICardEngine
    {
        internal ConcurrentQueue<string> Calls { get; } = new();
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
        public Task<IReadOnlyList<DeckSummary>> DecksAsync() => Task.FromResult<IReadOnlyList<DeckSummary>>([new("a", "Deck a"), new("b", "Deck b")]);
        public Task<IReadOnlyList<DeckPreset>> PresetsAsync() => Task.FromResult<IReadOnlyList<DeckPreset>>([]);
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
        public Task StartSoloAsync(string id, IReadOnlyList<string> opponents) {
            if (id is not ("a" or "b") || opponents.Count != 3 || opponents.Any(opponent => !Setup(id).Opponents.Any(available => available.Id == opponent)))
                throw new InvalidOperationException("Unprepared or incompatible quick-play input.");
            Calls.Enqueue("start-solo:" + id); Interlocked.Increment(ref starts); StartDeck = id; StartOpponents = opponents.ToArray();
            state = PresentationSmoke.Example() with { Id = "quick-play-match" }; return Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult(state);
        public Task<Lobby> HostAsync(int players, bool forwarding) {
            Calls.Enqueue("host:" + players);
            lobby = new("hosting", "Open", null, false, "Open seats", false, "disabled", null, [],
                Enumerable.Range(0, players).Select(index => new Seat(index, index == 0 ? "You" : "Open", index == 0 ? "HUMAN" : "OPEN", null, false, index == 0)).ToArray(), "pregame-table");
            return Task.FromResult(lobby);
        }
        public Task<Lobby> LobbyAsync() => Task.FromResult(lobby ?? throw new InvalidOperationException("No connected table."));
        public Task SelectDeckAsync(string id) {
            if (lobby == null || id is not ("a" or "b")) throw new InvalidOperationException("Illegal lobby deck selection.");
            Calls.Enqueue("select:" + id); lobby = lobby with { Seats = lobby.Seats.Select(seat => seat.Local ? seat with { Deck = "Deck " + id, Ready = false } : seat).ToArray() }; return Task.CompletedTask;
        }
        public Task SetReadyAsync(bool ready) {
            if (lobby == null || ready && lobby.Seats.Single(seat => seat.Local).Deck == null) throw new InvalidOperationException("Ready requires an assigned deck.");
            Calls.Enqueue("ready:" + ready); Interlocked.Increment(ref readies);
            lobby = lobby with { Seats = lobby.Seats.Select(seat => seat.Local ? seat with { Ready = ready } : seat).ToArray() }; return Task.CompletedTask;
        }
        public Task StartAsync() { Interlocked.Increment(ref friendStarts); throw new InvalidOperationException("A table with open seats cannot start."); }
        private static Task<T> Unsupported<T>() => throw new NotSupportedException();
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Task.FromResult(new CatalogPage(0, 0, []));
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
        public Task<Lobby> JoinAsync(string address) => Unsupported<Lobby>();
        public Task ReturnToLobbyAsync() => throw new NotSupportedException();
        public Task LeaveAsync() => throw new NotSupportedException();
        public Task ExecuteAsync(GameCommand command) => throw new NotSupportedException();
        public Task ConcedeAsync(string id) => throw new NotSupportedException();
    }
}
