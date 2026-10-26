using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Exercises real native input and disk persistence without loading Forge or
// making a gameplay decision on behalf of a player.
internal static class OnboardingSmoke
{
    internal static void Run(string profile, Point? size)
    {
        var checks = new List<string>();
        string scenarios = Path.Combine(profile, "onboarding-scenarios", Guid.NewGuid().ToString("N"));
        string Case(string name) { string path = Path.Combine(scenarios, name); Directory.CreateDirectory(path); return path; }
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks.Add(message);
            File.AppendAllText(Path.Combine(profile, "onboarding-progress.log"), message + "\n");
        }
        void Scene(string path, ICardEngine engine, Action<Script, TableWindow.Probe> arrange, GameSnapshot? state = null, string? fixture = null)
        {
            using var game = new TableWindow(TestTableServices.For(engine), path, null, fixture, size, initialState: state, artSource: new EmptyArt());
            var script = new Script(game.TestPort);
            arrange(script, game.TestPort); game.Automation = script; game.Run();
        }
        string fresh = Case("fresh-home");
        Directory.CreateDirectory(Path.Combine(fresh, "decks"));
        var startup = new StartupEngine();
        bool homeAutomatic = false, homeHeld = false;
        Scene(fresh, startup, (s, view) => {
            s.Then(() => {
                Check(view.Match == null && !view.TableGuideOpen, "Fresh profiles open their deck home without an active game or forced controls tour");
                Check(view.Hits.Any(h => h.Id == "change-deck") && view.Hits.Any(h => h.Id == "friends-tab")
                    && !view.Hits.Any(h => h.Id == "start-solo"), "A fresh home exposes real deck and table choices while an empty library cannot start a game");
                homeAutomatic = view.Automatic; homeHeld = view.Held;
                s.Capture("01-deck-home");
            });
            s.Pulse(Keys.Space, Keys.F, Keys.C, Keys.LeftControl, Keys.Enter);
            s.Then(() => Check(view.Match == null && view.SetupModal.Length == 0 && !view.ChatOpen
                    && view.Automatic == homeAutomatic && view.Held == homeHeld && startup.Mutations.Count == 0,
                "Gameplay shortcuts at home neither change play policy nor import, connect, ready or start anything"));
            s.Click("friends-tab");
            s.Then(() => {
                Check(!view.SoloTab && view.Hits.Any(h => h.Id == "host") && view.Hits.Any(h => h.Id == "join")
                    && view.Match == null && startup.Mutations.Count == 0, "Choosing Friends reveals host and invite actions in place without opening a table");
                Check(!File.Exists(Path.Combine(fresh, "experience.json")), "Ordinary home navigation does not write an introduction or guide-completion state");
            });
            s.Click("solo-tab");
            s.Click("change-deck");
            s.Then(() => Check(view.SetupModal == "decks" && startup.Mutations.Count == 0,
                "Opening the deck library is browsing, without an implicit import or game"));
            s.Click("setup-close");
            s.Click("experience-home");
            s.Then(() => Check(view.Match == null && view.Hits.Any(h => h.Id == "change-deck") && !view.TableGuideOpen
                    && startup.Mutations.Count == 0, "Play returns to the same usable deck home without a marketing page or controls tour"));
            s.Pulse(Keys.F1);
            s.Then(() => { view.Accept(null); view.Accept(null); });
            s.Then(() => Check(view.TableGuideOpen, "Repeated idle observations do not dismiss an explicitly requested F1 controls reference"));
            s.Click("experience-guide-close");
        });
        Scene(fresh, new StartupEngine(), (s, view) => s.Then(() => {
            Check(view.HomeVisible && !view.TableGuideOpen && view.Hits.Any(h => h.Id == "change-deck")
                    && view.Hits.Any(h => h.Id == "friends-tab") && !File.Exists(Path.Combine(fresh, "experience.json")),
                "A later launch opens ordinary deck navigation without creating or consulting introduction flags");
            s.Capture("02-returning-deck-home");
        }));

        string escape = Case("fresh-escape");
        Scene(escape, new StartupEngine(), (s, view) => {
            s.Pulse(Keys.Escape);
            s.Then(() => Check(view.Match == null && view.SetupModal.Length == 0 && !view.TableGuideOpen
                    && view.Hits.Any(h => h.Id == "change-deck"), "Escape at home leaves the real deck/play choices usable instead of dismissing into another setup page"));
        });

        foreach (string route in new[] { "ai", "friends", "decks" }) {
            string path = Case("route-" + route); var engine = new StartupEngine();
            Scene(path, engine, (s, view) => {
                s.Click(route == "ai" ? "solo-tab" : route == "friends" ? "friends-tab" : "change-deck");
                s.Then(() => {
                    Check(!view.TableGuideOpen && view.Match == null && !view.DeckConfirmed, $"The {route} home action leaves game entry and readiness explicit");
                    Check(route == "friends" ? !view.SoloTab && view.SetupModal.Length == 0 && view.Hits.Any(h => h.Id == "host") && view.Hits.Any(h => h.Id == "join")
                        : route == "decks" ? view.SetupModal == "decks" : view.SoloTab && view.SetupModal.Length == 0,
                        $"The {route} action changes only its intended mode or library context");
                    Check(engine.Mutations.Count == 0, $"Choosing the {route} path sends no implicit engine writes");
                    Check(!File.Exists(Path.Combine(path, "experience.json")), $"Choosing the {route} path requires no persisted introduction or guide state");
                    if (route == "friends") s.Capture("02-friends-path");
                });
            });
        }

        string tableProfile = Case("first-table");
        var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
        var initial = scenario.State;
        string helpDecision = "";
        Scene(tableProfile, scenario, (s, view) => {
            s.Then(() => {
                Check(!view.TableGuideOpen && view.Match?.Id == initial.Id, "A first playable table opens directly without a forced controls panel");
                Check(view.Hits.Any(h => h.Card != null) && view.Hits.Any(h => h.Id == "confirm") && view.Hits.Any(h => h.Id == "settings"), "A fresh player can use the actual cards and current game decision immediately");
                Check(scenario.Replies.Count == 0 && view.Match?.Decision?.Id == initial.Decision!.Id, "Entering the table never answers the player's pending decision");
                s.Capture("03-first-table-playable");
            });
            s.Click("settings");
            s.Pulse(Keys.F1);
            s.Then(() => view.SetAutomatic(true));
            s.Wait(1.2);
            s.Then(() => Check(scenario.Replies.Count == 0 && view.Match?.Decision?.Id == initial.Decision!.Id && view.Automatic, "Auto waits behind an explicitly opened reference without consuming the current decision"));
            s.Pulse(Keys.Space, Keys.F, Keys.C, Keys.LeftControl, Keys.Enter);
            s.ClickAt(new(1450, 810));
            s.ClickAt(new(800, 870), right: true);
            s.Then(() => {
                Check(view.TableGuideOpen && view.Automatic && !view.Held && view.Overlay == "settings" && !view.ChatOpen, "The optional reference consumes gameplay shortcuts without changing play settings or its source panel");
                Check(scenario.Replies.Count == 0 && view.Match?.Decision?.Id == initial.Decision!.Id, "Guide input never submits a gameplay command");
                view.SetHeld(true);
            });
            s.Click("experience-guide-close");
            s.Then(() => {
                Check(!view.TableGuideOpen && view.Automatic && view.Held, "Completing the guide preserves Auto and the current-turn hold");
                Check(!File.Exists(Path.Combine(tableProfile, "experience.json")) && view.Match?.Decision?.Id == initial.Decision!.Id,
                    "Closing an optional controls reference writes no completion flag and leaves the waiting game choice unanswered");
                view.SetAutomatic(false);
            });
            s.Pulse(Keys.F1);
            s.Then(() => Check(view.TableGuideOpen && view.Overlay == "settings" && !view.Automatic && view.Held, "Settings replays controls while preserving Full control, an existing hold, and the underlying panel"));
            s.Pulse(Keys.Escape);
            s.Then(() => {
                Check(!view.TableGuideOpen && view.Overlay == "settings" && !view.Automatic && view.Held && scenario.Replies.Count == 0, "Escape returns to Settings without changing play policy or making a choice");
            });
            s.Click("close-overlay");
            s.Then(() => {
                Check(!view.Hits.Any(hit => hit.Id == "instructions"), "Ordinary priority has no repeated prose or invisible decision-help target");
                scenario.Present(initial with { DecisionOwnerId = initial.ViewerId, ActionClass = "selection", Decision = new Decision("", new DecisionContext(Message: "Choose the required objects in the order reported by the engine."), new EntitySelectionPayload(new DecisionReply("Confirm choice", false), new DecisionReply("Cancel", false), [], null)) });
                view.Accept(scenario.State); helpDecision = scenario.State.Decision!.Id;
            });
            s.Click("instructions");
            s.Pulse(Keys.F1);
            s.Then(() => Check(view.TableGuideOpen && view.Overlay == "help", "The current-decision help can also replay table controls"));
            s.Click("experience-guide-close");
            s.Then(() => Check(view.Overlay == "help" && view.Match?.Decision?.Id == helpDecision && helpDecision != initial.Decision!.Id && scenario.Replies.Count == 0, "Closing replay returns to the actual required prompt's help with its new scope and no submitted choice"));
            s.Then(() => { scenario.Finish(); view.Accept(scenario.State); });
            s.Then(view.OpenTableGuide);
            s.Then(() => { view.Accept(scenario.State); view.Accept(scenario.State); });
            s.Then(() => Check(view.TableGuideOpen && view.Overlay == "result", "Repeated finished-match observations do not dismiss a manually opened controls guide"));
            s.Click("experience-guide-close");
            s.Then(() => Check(view.Overlay == "result" && scenario.Replies.Count == 0, "Closing controls after a finished match preserves the result screen"));
        }, initial);

        var returningScenario = new InteractionScenarioEngine(); returningScenario.Present(PresentationSmoke.Example());
        Scene(tableProfile, returningScenario, (s, view) => s.Then(() => Check(!view.TableGuideOpen && returningScenario.Replies.Count == 0, "Later matches stay directly playable without reopening optional controls")), returningScenario.State);

        foreach (bool savedDeck in new[] { false, true }) {
            string path = Case(savedDeck ? "legacy-deck" : "legacy-preferences");
            if (savedDeck) { Directory.CreateDirectory(Path.Combine(path, "decks")); File.WriteAllText(Path.Combine(path, "decks", "existing.dck"), "Existing saved deck"); }
            else new PlayPreferences { Automatic = false, PlayerName = "Returning player" }.Save(path);
            Scene(path, new StartupEngine(), (s, view) => s.Then(() => {
                Check(!view.TableGuideOpen && view.Hits.Any(h => h.Id == "change-deck") && view.Hits.Any(h => h.Id == "friends-tab"), $"A returning profile with {(savedDeck ? "saved decks" : "play preferences")} opens usable deck/play choices without a forced controls tour");
                if (!savedDeck) Check(view.PlayerName == "Returning player" && PlayPreferences.Load(path).PlayerName == "Returning player", "The main menu preserves an existing player's display name");
            }));
            var legacy = new InteractionScenarioEngine(); legacy.Present(PresentationSmoke.Example());
            Scene(path, legacy, (s, view) => s.Then(() => {
                Check(!view.ExperienceOpen && view.Hits.Any(h => h.Id == "settings"), $"A legacy profile with {(savedDeck ? "saved decks" : "play preferences")} gets no forced tour");
                if (!savedDeck) Check(view.PlayerName == "Returning player" && !view.Automatic, "Onboarding migration preserves existing player name and play preferences");
                Check(legacy.Replies.Count == 0, "Legacy profile migration consumes no game decisions");
            }), legacy.State);
        }

        string visualProfile = Case("fixture"); var visual = new InteractionScenarioEngine();
        Scene(visualProfile, visual, (s, view) => s.Then(() => Check(!view.ExperienceOpen, "Visual fixtures remain unobscured by first-run onboarding")), visual.State, "controlled-frames");
        File.WriteAllText(Path.Combine(profile, "onboarding-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class EmptyArt : ICardArtSource
    {
        public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public void Dispose() { }
    }

    private sealed class Script(TableWindow.Probe view) : ITableAutomation
    {
        private readonly Queue<Action> steps = [];
        private MouseState mouse;
        private KeyboardState keyboard;
        private Point target;
        private bool rightClick;
        private int mousePhase;
        private double until;
        public string? ExpectedClick { get; private set; }
        public bool ClickAccepted { get; set; }
        public KeyboardState? ReadKeyboard() => keyboard;
        public MouseState ReadMouse()
        {
            if (mousePhase is not (1 or 2)) return mouse;
            bool down = mousePhase++ == 1;
            var viewport = view.GraphicsDevice.Viewport;
            float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
            mouse = new((int)((viewport.Width - 1600 * scale) / 2 + target.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + target.Y * scale), 0,
                down && !rightClick ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, down && rightClick ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released);
            return mouse;
        }
        internal void Then(Action action) => steps.Enqueue(action);
        internal void Wait(double seconds) => Then(() => until = view.Now + seconds);
        internal void Pulse(params Keys[] keys) { Then(() => keyboard = new(keys)); Then(() => keyboard = new()); }
        internal void Click(string id) => Then(() => {
            var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Onboarding is missing the native control " + id);
            target = hit.Bounds.Center; ExpectedClick = id; ClickAccepted = false; rightClick = false; mousePhase = 1;
        });
        internal void ClickAt(Point point, bool right = false) => Then(() => { target = point; ExpectedClick = null; rightClick = right; mousePhase = 1; });
        internal void Capture(string name) { using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height); }
        public void Frame(Texture2D surface)
        {
            if (view.Error.Length > 0) { Capture("failure"); throw new InvalidOperationException(view.Error); }
            if (view.Now > 30) { Capture("timeout"); throw new TimeoutException("Onboarding smoke did not finish"); }
            if (mousePhase is 1 or 2) return;
            if (mousePhase == 3) {
                if (ExpectedClick != null && !ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Onboarding rejected pointer input for " + ExpectedClick); }
                mousePhase = 0; ExpectedClick = null;
            }
            if (!view.Loaded || view.Busy || view.ActivityOpen || view.Now < until) return;
            if (steps.TryDequeue(out var step)) step(); else view.Exit();
        }
    }

    // Startup only: every mutation fails and is recorded so a route can never
    // quietly create a deck, claim a seat, or launch a match during this test.
    private sealed class StartupEngine : ICardEngine
    {
        internal List<string> Mutations { get; } = [];
        public string Name => "Onboarding startup scenario";
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<ImmutableArray<DeckSummary>> DecksAsync() => Task.FromResult<ImmutableArray<DeckSummary>>([]);
        public Task<ImmutableArray<DeckPreset>> PresetsAsync() => Task.FromResult<ImmutableArray<DeckPreset>>([new("starter", "Starter deck", "A deck-picker presentation fixture")]);
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private Task Reject(string operation) { Mutations.Add(operation); throw new InvalidOperationException("Onboarding unexpectedly wrote to the engine: " + operation); }
        private Task<T> Reject<T>(string operation) { Mutations.Add(operation); throw new InvalidOperationException("Onboarding unexpectedly wrote to the engine: " + operation); }
        public Task<DeckDetails> OpenDeckAsync(string id) => throw new InvalidOperationException("No saved decks exist in this startup fixture");
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Task.FromResult(new CatalogPage(0, 0, []));
        public Task<DeckSummary> ImportAsync(string name, string text) => Reject<DeckSummary>("import");
        public Task<DeckSummary> ImportPresetAsync(string id) => Reject<DeckSummary>("import-preset");
        public Task<DeckDetails> CreateDeckAsync(string name) => Reject<DeckDetails>("create-deck");
        public Task<DeckDetails> EditDeckAsync(string deckId, long revision, ImmutableArray<DeckEdit> edits) => Reject<DeckDetails>("edit-deck");
        public Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => Reject<DeckDetails>("rename-deck");
        public Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => Reject<DeckDetails>("duplicate-deck");
        public Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => Reject<DeckDetails>("undo-deck");
        public Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => Reject<DeckDetails>("redo-deck");
        public Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => Reject<DeckDetails>("save-deck");
        public Task<string> ExportDeckAsync(string deckId, long revision) => Reject<string>("export-deck");
        public Task<DeckDetails> SetCommandersAsync(string deckId, long revision, ImmutableArray<string> cardIds) => Reject<DeckDetails>("set-commanders");
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => Reject<SoloSetup>("prepare-solo");
        public Task StartSoloAsync(string deckId, ImmutableArray<string> opponentIds) => Reject("start-solo");
        public Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => Reject<Lobby>("host");
        public Task<Lobby> JoinAsync(string address) => Reject<Lobby>("join");
        public Task<Lobby> LobbyAsync() => throw new InvalidOperationException("Onboarding unexpectedly requested a connected lobby");
        public Task SelectDeckAsync(string id) => Reject("select-deck");
        public Task SetReadyAsync(bool ready) => Reject("ready");
        public Task StartAsync() => Reject("start");
        public Task ReturnToLobbyAsync() => Reject("return-to-lobby");
        public Task LeaveAsync() => Reject("leave");
        public Task ExecuteAsync(GameCommand reply) => Reject("reply");
        public Task ConcedeAsync(string gameId) => Reject("concede");
    }
}
