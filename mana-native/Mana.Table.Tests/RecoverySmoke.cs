using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed class RecoverySmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly RecoveryEngine engine;
    private readonly List<string> checks;
    private readonly Queue<(Func<bool>? Ready, double Delay, Action Action)> steps = [];
    private MouseState mouse;
    private double last;
    private string savedBeforeImport = "";
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? size)
    {
        var checks = new List<string>();
        foreach (string mode in new[] { "joined", "hosting" }) {
            string path = Path.Combine(profile, mode); Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, Sounds = false }.Save(path);
            var engine = new RecoveryEngine(mode);
            using var game = new TableWindow(TestTableServices.For(engine), path, null, null, size, artSource: new CardReadingArtFixture());
            game.Automation = new RecoverySmoke(game.TestPort, engine, checks); game.Run();
        }
        File.WriteAllText(Path.Combine(profile, "recovery-results.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private RecoverySmoke(TableWindow.Probe view, RecoveryEngine engine, List<string> checks)
    {
        this.view = view; this.engine = engine; this.checks = checks;
        Wait(() => view.Loaded && !view.Busy && !view.ActivityOpen, () => {

            Check(view.SelectedDeck?.Id == "saved", "The original saved deck is selected before a failed import");
            savedBeforeImport = JsonSerializer.Serialize(view.DeckDetails);
            view.ImportFilePath(Path.Combine(view.Profile, "missing-deck.txt"));
        });
        Wait(() => !view.Busy && view.ActivityOpen && view.Error.Contains("Could not read"), () => {
            Check(view.SelectedDeck?.Id == "saved" && view.DeckDetails!.Entries.Sum(e => e.Quantity) == 100
                && JsonSerializer.Serialize(view.DeckDetails) == savedBeforeImport && engine.Imports == 0 && view.Loaded,
                "An unreadable file stays in the app, preserves the original deck and never calls the parser");
            view.CaptureWindow(Path.Combine(view.Profile, "01-file-read-failure-window.png"));
            Move("activity-dismiss");
        }); Click();
        Step(.2, () => {
            Check(!view.ActivityOpen && view.Error.Length == 0 && view.SelectedDeck?.Id == "saved", "Back recovers the import screen and original deck");
            engine.Active = true; view.SetConnectedTable(engine.Lobby); view.Accept(engine.State);
        });
        Wait(() => view.Match != null && view.Hits.Any(h => h.Id == "settings"), () => Move("settings")); Click();
        Step(.15, () => Move("text-size")); Click();
        Step(.15, () => Move("text-size")); Click();
        Step(.15, () => {
            Check(view.TextScale == 1.3f && PlayPreferences.Load(view.Profile).TextScale == 1.3f,
                "Text size changes through native settings and persists at 130 percent");
            view.CaptureWindow(Path.Combine(view.Profile, "02-large-text-window.png")); Move("close-overlay");
        }); Click();
        Step(.25, () => engine.FailReads = true);
        Wait(() => view.NeedsRecovery && !view.Polling, () => {
            Check(view.Match != null && view.Hits.Any(h => h.Id == "recovery-retry") && !view.Hits.Any(h => h.Id == "confirm"),
                "A failed connection keeps the inspectable table and replaces current-action controls with recovery");
            view.CaptureWindow(Path.Combine(view.Profile, "03-stale-table-window.png"));
            engine.FailReads = false; Move("recovery-retry");
        }); Click();
        Wait(() => !view.NeedsRecovery && view.Error.Length == 0 && view.Hits.Any(h => h.Id == "confirm"), () => {
            Check(engine.Replies == 0, "Reconnection reconciles the table without submitting a decision");
            engine.State = engine.State with { Revision = engine.State.Revision + 1, Decision = null,
                Players = engine.State.Players.Select(p => p with { Eliminated = p.Id == 0 }).ToImmutableArray() };
        });
        Wait(() => view.Match?.Viewer?.Eliminated == true && view.Hits.Any(h => h.Id == "eliminated-leave"), () => {
            Check(!view.Hits.Any(h => h.Id == "concede"), "An eliminated player watches and can leave without conceding again");
            view.CaptureWindow(Path.Combine(view.Profile, "04-watching-window.png")); Move("eliminated-leave");
        }); Click();
        Step(.15, () => {
            Check(view.Overlay == "leave-table" && view.Hits.Any(h => h.Id == "confirm-leave-table"), "Leaving opens an explicit departure confirmation for " + engine.Mode);
            view.CaptureWindow(Path.Combine(view.Profile, "05-departure-window.png")); Move("close-overlay");
        }); Click();
        Step(.2, () => {
            Check(engine.Leaves == 0 && view.Match != null, "Keeping the table open does not send a departure");
            engine.State = engine.State with { Status = "error", Error = "Controlled engine failure", Revision = engine.State.Revision + 1 };
        });
        Wait(() => view.Match?.Status == "error" && view.Hits.Any(h => h.Id == "error-leave"), () => Move("error-leave")); Click();
        Step(.15, () => Move("confirm-leave-table")); Click();
        Wait(() => !view.Busy && view.Match == null && !view.ActivityOpen, () => {
            Check(engine.Leaves == 1 && view.Lobby == null && view.SelectedDeck?.Id == "saved", "A multiplayer error has a working one-command exit to the preserved setup");
            view.Exit();
        });
    }
    private void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        checks.Add(engine.Mode + ": " + description);
    }
    private void Move(string id)
    {
        var hit = view.Hits.Single(h => h.Id == id); var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        int x = (int)((viewport.Width - 1600 * scale) / 2 + hit.Bounds.Center.X * scale);
        int y = (int)((viewport.Height - view.DesignHeight * scale) / 2 + hit.Bounds.Center.Y * scale);
        mouse = new(x, y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Click() { Step(.03, () => Buttons(ButtonState.Pressed)); Step(.05, () => Buttons(ButtonState.Released)); }
    private void Buttons(ButtonState left) => mouse = new(mouse.X, mouse.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void Step(double delay, Action action) => steps.Enqueue((null, delay, action));
    private void Wait(Func<bool> ready, Action action) => steps.Enqueue((ready, 10, action));
    public void Frame(Texture2D surface)
    {
        if (view.Now > 75) throw new TimeoutException("Recovery UI scenario did not finish");
        if (!steps.TryPeek(out var step)) return;
        if (step.Ready != null && !step.Ready()) {
            if (view.Now - last > step.Delay) throw new TimeoutException($"Recovery UI wait failed ({steps.Count} steps left): {view.Error}");
            return;
        }
        if (step.Ready == null && view.Now - last < step.Delay) return;
        steps.Dequeue(); step.Action(); last = view.Now;
    }

    private sealed class RecoveryEngine(string mode) : ICardEngine
    {
        internal string Mode => mode;
        internal bool Active, FailReads;
        internal int Replies, Imports, Leaves;
        internal GameSnapshot State = PresentationSmoke.Example() with { Revision = 1,
            Decision = new Decision("recovery-decision", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [])) };
        internal Lobby Lobby => new(mode, "Connected", null, false, null, Active, "", null, [], [], "recovery-table");
        public string Name => "Recovery scenario";
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<ImmutableArray<DeckSummary>> DecksAsync() => Task.FromResult<ImmutableArray<DeckSummary>>([new("saved", "Current deck")]);
        public Task<ImmutableArray<DeckPreset>> PresetsAsync() => Task.FromResult<ImmutableArray<DeckPreset>>([]);
        public Task<DeckDetails> OpenDeckAsync(string id) => Task.FromResult(new DeckDetails(id, "Current deck", 1,
            [new("Main", "saved-forest", new() { Name = "Forest", Type = "Basic Land — Forest" }, 99),
             new("Commander", "saved-commander", new() { Name = "Rhys the Redeemed", Type = "Legendary Creature — Elf Warrior", Power = 1, Toughness = 1 }, 1)], [], "", ""));
        public Task<GameSnapshot?> ObserveAsync() => FailReads ? Task.FromException<GameSnapshot?>(new IOException("Controlled connection interruption")) : Task.FromResult<GameSnapshot?>(State);
        public Task<Lobby> LobbyAsync() => FailReads ? Task.FromException<Lobby>(new IOException("Controlled connection interruption")) : Task.FromResult(Lobby);
        public Task ExecuteAsync(GameCommand reply) { Replies++; return Task.CompletedTask; }
        public Task LeaveAsync() { Leaves++; Active = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<DeckSummary> ImportAsync(string name, string text) { Imports++; throw new InvalidOperationException("Unreadable files must not reach the parser"); }
        public Task<DeckSummary> ImportPresetAsync(string id) => throw new NotSupportedException();
        public Task<DeckDetails> SetCommandersAsync(string id, long revision, ImmutableArray<string> cards) => throw new NotSupportedException();
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => throw new NotSupportedException();
        public Task<DeckDetails> CreateDeckAsync(string name) => throw new NotSupportedException();
        public Task<DeckDetails> EditDeckAsync(string id, long revision, ImmutableArray<DeckEdit> edits) => throw new NotSupportedException();
        public Task<DeckDetails> RenameDeckAsync(string id, long revision, string name) => throw new NotSupportedException();
        public Task<DeckDetails> DuplicateDeckAsync(string id, long revision, string name) => throw new NotSupportedException();
        public Task<DeckDetails> UndoDeckAsync(string id, long revision) => throw new NotSupportedException();
        public Task<DeckDetails> RedoDeckAsync(string id, long revision) => throw new NotSupportedException();
        public Task<DeckDetails> SaveDeckAsync(string id, long revision) => throw new NotSupportedException();
        public Task<string> ExportDeckAsync(string id, long revision) => throw new NotSupportedException();
        public Task<SoloSetup> PrepareSoloAsync(string id) => throw new NotSupportedException();
        public Task StartSoloAsync(string id, ImmutableArray<string> opponents) => throw new NotSupportedException();
        public Task<Lobby> HostAsync(int players, bool forward) => throw new NotSupportedException();
        public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
        public Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) => throw new NotSupportedException();
        public Task SetReadyAsync(bool ready) => throw new NotSupportedException();
        public Task StartAsync() => throw new NotSupportedException();
        public Task ReturnToLobbyAsync() => throw new NotSupportedException();
        public Task ConcedeAsync(string id) => throw new NotSupportedException();
    }
}
