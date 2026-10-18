using System.Text.Json;
using Mana.Contracts;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Real native frames with deliberately unfinished adapter requests. The screen
// must describe the actual outstanding work and protect the accepted action.
internal static class LoadingSmoke
{
    internal static void Run(string profile, Point? size)
    {
        var checks = new List<string>();
        string run = Path.Combine(profile, "loading-scenarios", Guid.NewGuid().ToString("N"));
        string Case(string name) { string path = Path.Combine(run, name); Directory.CreateDirectory(path); return path; }
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks.Add(message); File.AppendAllText(Path.Combine(profile, "loading-progress.log"), message + "\n");
        }
        void Scene(string path, GatedEngine engine, Action<Script, TableGame.Probe> arrange, GameSnapshot? state = null)
        {
            using var game = new TableGame(engine, path, null, null, size, initialState: state, artSource: new EmptyArt());
            var script = new Script(game.TestPort); arrange(script, game.TestPort); game.Automation = script; game.Run();
        }

        var startup = new GatedEngine();
        string startupPath = Case("startup-and-deck");
        Scene(startupPath, startup, (s, view) => {
            s.Until(() => startup.InitializeCalls == 1);
            s.Then(() => {
                Check(view.StartupVisible && view.ActivityOpen && view.Busy && !view.Loaded, "A pending first initialization shows startup activity before setup is ready");
                Check(view.ActivityStage == "Card library" && startup.DeckReads == 0 && startup.PresetReads == 0, "Startup identifies the pending card library without claiming later stages have started");
                Check(!view.Hits.Any(h => h.Id.StartsWith("experience-") || h.Id is "host" or "change-deck" or "start-solo"), "Startup exposes no welcome, table, or deck actions behind loading");
                s.Capture("01-starting-library");
            });
            s.Pulse(Keys.Escape, Keys.Enter, Keys.Space, Keys.F, Keys.LeftControl, Keys.F1);
            s.ClickAt(new(800, 650));
            s.Wait(.25);
            s.Then(() => {
                Check(view.ActivityStage == "Card library" && startup.InitializeCalls == 1 && startup.DeckReads == 0 && !view.Automatic && !view.Held && !view.TableGuideOpen, "Input and elapsed time cannot advance startup or change play settings behind an unfinished request");
                startup.Initializing.SetResult();
            });
            s.Until(() => startup.DeckReads == 1 && view.ActivityStage == "Your decks");
            s.Then(() => {
                Check(view.StartupVisible && view.ActivityOpen && startup.PresetReads == 0, "Saved decks become the current stage only after initialization completes");
                startup.ReadingDecks.SetResult();
            });
            s.Until(() => startup.PresetReads == 1 && view.ActivityStage == "Commander presets");
            s.Then(() => {
                Check(view.StartupVisible && view.ActivityOpen && startup.SavedOpens == 0, "Commander presets become current after the saved deck list completes");
                startup.ReadingPresets.SetResult();
            });
            s.Until(() => startup.SavedOpens == 1 && view.ActivityStage == "Your table");
            s.Then(() => {
                Check(!view.Loaded && view.ActivityOpen && view.SelectedDeck == null, "Startup keeps setup protected while the selected deck is still loading");
                s.Capture("02-preparing-table"); startup.OpeningSaved.SetResult();
            });
            s.Until(() => view.Loaded && !view.ActivityOpen && !view.Busy);
            s.Then(() => Check(view.WelcomeOpen && view.SelectedDeck?.Id == "saved" && view.Hits.Any(h => h.Id == "experience-ai"), "A completed startup hands off to the welcome with the selected deck ready"));
            s.Click("experience-skip");
            s.Click("change-deck");
            s.Click("picker-presets");
            Point presetPoint = default;
            s.Then(() => presetPoint = view.Hits.Single(h => h.Id == "preset:starter").Bounds.Center);
            s.Click("preset:starter");
            s.Then(() => {
                Check(view.ActivityOpen && !view.StartupVisible && view.Busy && view.ActivityTitle.Contains("deck", StringComparison.OrdinalIgnoreCase), "An accepted precon click shows contextual deck feedback without reopening the startup scene");
                Check(!view.Hits.Any(h => h.Id.StartsWith("preset:") || h.Id == "setup-close"), "Foreground work removes duplicate deck and modal actions while it is pending");
                s.Capture("03-loading-deck");
            });
            s.Until(() => startup.Imports == 1);
            s.ClickAt(presetPoint);
            s.Pulse(Keys.Escape, Keys.Enter, Keys.Space, Keys.F, Keys.LeftControl);
            s.Then(() => view.TypeText("This must not reach setup"));
            s.Wait(.25);
            s.Then(() => {
                Check(startup.Imports == 1 && view.ActivityOpen && view.SetupModal == "decks" && !view.Automatic && !view.Held, "Duplicate pointer input and shortcuts cannot repeat or dismiss an accepted deck request");
                startup.Importing.SetResult();
            });
            s.Until(() => startup.ImportedOpens == 1);
            s.Then(() => {
                Check(view.ActivityOpen && view.SelectedDeck?.Id == "saved", "Deck activity remains visible through the follow-up deck-details request");
                startup.OpeningImported.SetResult();
            });
            s.Until(() => !view.ActivityOpen && !view.Busy);
            s.Then(() => {
                Check(view.SelectedDeck?.Id == "imported" && view.SetupModal == "confirm" && !view.DeckConfirmed, "Finished deck loading hands off to the imported deck review without confirming play");
                Check(startup.Imports == 1 && startup.OtherWrites == 0 && view.Error.Length == 0, "Loading feedback completes exactly one intended mutation with no extra engine writes");
                s.Capture("04-deck-ready");
            });
            s.Click("confirm-edit");
            s.Until(() => view.DeckEditor && !view.Busy && !view.ActivityOpen);
            s.Click("deck-main");
            s.Click("deck-card:Main:forest");
            s.Click("deck-add");
            s.Until(() => startup.Edits == 1 && view.Busy);
            s.Then(() => {
                Check(!view.ActivityOpen && !view.StartupVisible && view.InlineActivityVisible && view.DeckEditor, "Routine deck edits show inline saving feedback while keeping the builder on screen");
                Check(view.Hits.Any(h => h.Id == "deck-search-library") && view.Hits.Any(h => h.Id == "deck-search-deck"), "Pending autosave preserves library and deck browsing controls");
                s.Capture("05-inline-saving");
            });
            s.Click("deck-search-library");
            s.Then(() => startup.Editing.SetResult());
            s.Until(() => !view.Busy);
            s.Then(() => Check(!view.ActivityOpen && !view.StartupVisible && view.DeckDetails!.Entries.Single(e => e.Id == "forest").Quantity == 100 && startup.Edits == 1, "An inline save finishes once and updates the deck without a modal handoff"));
        });

        var retry = new GatedEngine();
        retry.Initializing.SetException(new IOException("Controlled startup failure"));
        retry.ReadingDecks.SetResult(); retry.ReadingPresets.SetResult(); retry.OpeningSaved.SetResult();
        Scene(Case("startup-retry"), retry, (s, view) => {
            s.AllowError = true;
            s.Until(() => view.StartupFailed && !view.Busy);
            s.Then(() => {
                Check(view.StartupVisible && view.ActivityOpen && !view.Loaded && view.Error.Contains("Controlled startup failure"), "Startup failure stays on a readable loading error instead of revealing an incomplete lobby");
                Check(view.Hits.Any(h => h.Id == "activity-retry") && view.Hits.Any(h => h.Id == "activity-exit") && !view.Hits.Any(h => h.Id.StartsWith("experience-")), "Failed startup offers Retry and Exit while keeping play paths unavailable");
                s.Capture("05-startup-failed");
            });
            s.Click("activity-retry");
            s.Until(() => retry.InitializeCalls == 2);
            s.Then(() => {
                Check(view.ActivityOpen && !view.StartupFailed && view.Error.Length == 0 && view.ActivityStage == "Card library", "Retry clears the stale failure and acknowledges the new initialization attempt");
                retry.Retrying.SetResult();
            });
            s.Until(() => view.Loaded && !view.ActivityOpen && !view.Busy);
            s.Then(() => {
                Check(retry.InitializeCalls == 2 && view.Error.Length == 0 && view.WelcomeOpen && view.SelectedDeck?.Id == "saved", "A successful retry reaches the fully loaded welcome without restarting the application");
                s.AllowError = false;
            });
        });

        var table = new GatedEngine(); table.Scenario.Present(PresentationSmoke.Example());
        string tablePath = Case("table-background");
        File.WriteAllText(Path.Combine(tablePath, "experience.json"), JsonSerializer.Serialize(new { WelcomeSeen = true, TableGuideSeen = true }));
        Scene(tablePath, table, (s, view) => {
            s.Until(() => table.Observations > 0 && view.Polling);
            s.Then(() => Check(!view.ActivityOpen && !view.StartupVisible && view.Hits.Any(h => h.Id == "settings"), "Background table observation leaves the scene interactive without a loading overlay"));
            s.Click("settings");
            s.Then(() => {
                Check(view.Overlay == "settings" && view.Polling && !view.ActivityOpen, "A player can open Settings while a background snapshot request is unfinished");
                s.Capture("06-background-poll");
            });
            s.Click("close-overlay");
            s.Then(() => table.Observing.SetResult());
            // A new observation proves the first has drained even when slow
            // software-rendered frames miss the brief interval between polls.
            s.Until(() => table.Observations > 1);
            s.Wait(.35);
            s.ClickCard("seat-0-1");
            s.Until(() => table.Replies == 1 && view.Busy);
            s.Then(() => {
                Check(!view.ActivityOpen && view.Hits.Any(h => h.Id == "settings"), "A pending gameplay reply keeps the table visible without a setup-loading overlay");
                table.Replying.SetResult();
            });
            s.Until(() => !view.Busy && view.Match?.Decision?.Kind == "choice");
            s.Then(() => Check(!view.ActivityOpen && table.Replies == 1 && table.Scenario.Replies.Count == 1, "The gameplay reply completes normally with its own decision presentation"));
            s.Then(() => { table.Scenario.Present(PresentationSmoke.Example()); view.Accept(table.Scenario.State); view.SetAutomatic(true); });
            s.Move(new(0, 0));
            s.Until(() => table.AutoReplies == 1 && view.Busy);
            s.Then(() => {
                Check(!view.ActivityOpen && view.Hits.Any(h => h.Id == "auto"), "An in-flight automatic priority pass never opens setup-loading feedback");
                table.AutomaticPassing.SetResult();
            });
            s.Until(() => !view.Busy && view.Match?.Decision == null);
            s.Then(() => Check(!view.ActivityOpen && table.AutoReplies == 1 && table.Scenario.Replies.Count == 2, "Auto completes its authorized pass with the table continuously visible"));
        }, table.Scenario.State);
        File.WriteAllText(Path.Combine(profile, "loading-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class EmptyArt : ICardArtSource
    {
        public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public void Dispose() { }
    }

    private sealed class Script(TableGame.Probe view) : ITableAutomation
    {
        private readonly Queue<(Action? Action, Func<bool>? Ready)> steps = [];
        private MouseState mouse;
        private KeyboardState keyboard;
        private Point target;
        private int mousePhase;
        private double until;
        internal bool AllowError { get; set; }
        public string? ExpectedClick { get; private set; }
        public bool ClickAccepted { get; set; }
        public KeyboardState? ReadKeyboard() => keyboard;
        public MouseState ReadMouse()
        {
            if (mousePhase is not (1 or 2)) return mouse;
            bool down = mousePhase++ == 1; var viewport = view.GraphicsDevice.Viewport;
            float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
            mouse = new((int)((viewport.Width - 1600 * scale) / 2 + target.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + target.Y * scale), 0,
                down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
            return mouse;
        }
        internal void Then(Action action) => steps.Enqueue((action, null));
        internal void Until(Func<bool> condition) => steps.Enqueue((null, condition));
        internal void Wait(double seconds) => Then(() => until = view.Now + seconds);
        internal void Pulse(params Keys[] keys) { Then(() => keyboard = new(keys)); Then(() => keyboard = new()); }
        internal void Click(string id) => Then(() => StartClick(view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Loading smoke is missing " + id)));
        internal void ClickCard(string id) => Then(() => StartClick(view.Hits.Last(h => h.Card?.VisualId == id && h.Zone == "Battlefield")));
        private void StartClick(TableGame.Hit hit)
        {
            target = hit.Bounds.Center; ExpectedClick = hit.Id; ClickAccepted = false; mousePhase = 1;
        }
        internal void ClickAt(Point point) => Then(() => { target = point; ExpectedClick = null; mousePhase = 1; });
        internal void Move(Point point) => Then(() => {
            var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
            mouse = new((int)((viewport.Width - 1600 * scale) / 2 + point.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + point.Y * scale), 0,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        });
        internal void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
        public void Frame(Texture2D surface)
        {
            if (!AllowError && view.Error.Length > 0) { Capture("failure"); throw new InvalidOperationException(view.Error); }
            if (view.Now > 35) { Capture("timeout"); throw new TimeoutException("Loading smoke did not finish at " + view.ActivityStage + "; remaining steps " + steps.Count); }
            if (mousePhase is 1 or 2) return;
            if (mousePhase == 3) {
                if (ExpectedClick != null && !ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Loading smoke rejected " + ExpectedClick); }
                mousePhase = 0; ExpectedClick = null;
            }
            if (view.Now < until) return;
            if (!steps.TryPeek(out var step)) { view.Exit(); return; }
            if (step.Ready != null && !step.Ready()) return;
            steps.Dequeue(); step.Action?.Invoke();
        }
    }

    private sealed class GatedEngine : ICardEngine
    {
        internal readonly TaskCompletionSource Initializing = Gate(), Retrying = Gate(), ReadingDecks = Gate(), ReadingPresets = Gate(), OpeningSaved = Gate(), Importing = Gate(), OpeningImported = Gate(), Editing = Gate(), Observing = Gate(), Replying = Gate(), AutomaticPassing = Gate();
        internal readonly InteractionScenarioEngine Scenario = new();
        private int initializes, decks, presets, savedOpens, importedOpens, imports, edits, observations, replies, autoReplies, otherWrites;
        private bool imported;
        private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int InitializeCalls => Volatile.Read(ref initializes);
        internal int DeckReads => Volatile.Read(ref decks);
        internal int PresetReads => Volatile.Read(ref presets);
        internal int SavedOpens => Volatile.Read(ref savedOpens);
        internal int ImportedOpens => Volatile.Read(ref importedOpens);
        internal int Imports => Volatile.Read(ref imports);
        internal int Edits => Volatile.Read(ref edits);
        internal int Observations => Volatile.Read(ref observations);
        internal int Replies => Volatile.Read(ref replies);
        internal int AutoReplies => Volatile.Read(ref autoReplies);
        internal int OtherWrites => Volatile.Read(ref otherWrites);
        public string Name => "Controlled loading scenario";
        public async Task InitializeAsync() => await (Interlocked.Increment(ref initializes) == 1 ? Initializing : Retrying).Task;
        public async Task<IReadOnlyList<DeckSummary>> DecksAsync()
        {
            Interlocked.Increment(ref decks); await ReadingDecks.Task;
            return imported ? [new("saved", "Saved commander deck"), new("imported", "Imported commander deck")] : [new("saved", "Saved commander deck")];
        }
        public async Task<IReadOnlyList<DeckPreset>> PresetsAsync()
        {
            Interlocked.Increment(ref presets); await ReadingPresets.Task;
            return [new("starter", "Starter commander deck", "A controlled precon import")];
        }
        public async Task<DeckDetails> OpenDeckAsync(string id)
        {
            if (id == "saved") { Interlocked.Increment(ref savedOpens); await OpeningSaved.Task; }
            else { Interlocked.Increment(ref importedOpens); await OpeningImported.Task; }
            return Details(id);
        }
        private static DeckDetails Details(string id, int forestQuantity = 99, long revision = 1) => new(id, id == "saved" ? "Saved commander deck" : "Imported commander deck", revision,
                [new("Commander", "commander", new() { Name = "Goreclaw, Terror of Qal Sisma", Type = "Legendary Creature — Bear" }, 1),
                    new("Main", "forest", new() { Name = "Forest", Type = "Basic Land — Forest" }, forestQuantity)], [], "", "");
        public async Task<DeckSummary> ImportPresetAsync(string id) { Interlocked.Increment(ref imports); await Importing.Task; imported = true; return new("imported", "Imported commander deck"); }
        public async Task<GameSnapshot?> ObserveAsync() { if (Interlocked.Increment(ref observations) == 1) await Observing.Task; return Scenario.State; }
        public async Task ReplyAsync(DecisionReply reply)
        {
            Interlocked.Increment(ref replies);
            if (reply.Action == ReplyAction.AutoPass) { Interlocked.Increment(ref autoReplies); await AutomaticPassing.Task; }
            else await Replying.Task;
            await Scenario.ReplyAsync(reply);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private Task Reject(string operation) { Interlocked.Increment(ref otherWrites); throw new InvalidOperationException("Unexpected loading scenario mutation: " + operation); }
        private Task<T> Reject<T>(string operation) { Interlocked.Increment(ref otherWrites); throw new InvalidOperationException("Unexpected loading scenario mutation: " + operation); }
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Task.FromResult(new CatalogPage(0, 0, []));
        public Task<DeckSummary> ImportAsync(string name, string text) => Reject<DeckSummary>("import");
        public Task<DeckDetails> CreateDeckAsync(string name) => Reject<DeckDetails>("create");
        public async Task<DeckDetails> EditDeckAsync(string deckId, long revision, IReadOnlyList<DeckEdit> changes)
        {
            Interlocked.Increment(ref edits); await Editing.Task;
            return Details(deckId, changes.Single().Quantity, revision + 1);
        }
        public Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => Reject<DeckDetails>("rename");
        public Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => Reject<DeckDetails>("duplicate");
        public Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => Reject<DeckDetails>("undo");
        public Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => Reject<DeckDetails>("redo");
        public Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => Reject<DeckDetails>("save");
        public Task<string> ExportDeckAsync(string deckId, long revision) => Reject<string>("export");
        public Task<DeckDetails> SetCommandersAsync(string deckId, long revision, IReadOnlyList<string> cardIds) => Reject<DeckDetails>("commanders");
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => Reject<SoloSetup>("prepare-solo");
        public Task StartSoloAsync(string deckId, IReadOnlyList<string> opponentIds) => Reject("start-solo");
        public Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => Reject<Lobby>("host");
        public Task<Lobby> JoinAsync(string address) => Reject<Lobby>("join");
        public Task<Lobby> LobbyAsync() => throw new InvalidOperationException("No connected lobby exists in this scenario");
        public Task SelectDeckAsync(string id) => Reject("select-deck");
        public Task SetReadyAsync(bool ready) => Reject("ready");
        public Task StartAsync() => Reject("start");
        public Task ReturnToLobbyAsync() => Reject("return");
        public Task LeaveAsync() => Reject("leave");
        public Task ConcedeAsync(string gameId) => Reject("concede");
    }
}
