using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Same production composition and physical pointer script in headless/GPU modes.
// The canonical lab connection supplies authored states, never Magic rules.
internal sealed class PriorityFlowSmoke : ITableAutomation
{
    private readonly TableApplication.Probe view;
    private readonly TableWindow.Probe? window;
    private readonly RecordingLabEngine engine;
    private readonly Point viewport;
    private readonly Queue<(Func<bool>? Ready, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private readonly List<object> observations = [];
    private readonly HashSet<string> inspectedQueueIds = new(StringComparer.Ordinal);
    private MouseState mouse;
    private double lastStep;
    private bool finished;
    private string passGame = "", passDecision = "", recipientDecision = "", libraryDecision = "";
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private PresentationFrame Current => view.Frame ?? throw new InvalidOperationException("No composed priority frame.");

    internal static void RunHeadless(string profile)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string directory = Path.Combine(profile, $"{size.X}x{size.Y}"); Prepare(directory);
            var engine = new RecordingLabEngine();
            using var application = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State);
            application.Automated = true; application.Initialize();
            var driver = new PriorityFlowSmoke(application.TestPort, engine, size);
            var watch = Stopwatch.StartNew(); double now = 0;
            try {
                while (!driver.finished) {
                    if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Priority flow did not finish within 45 seconds.");
                    now += 1.0 / 60;
                    var time = new GameTime(TimeSpan.FromSeconds(now), TimeSpan.FromSeconds(1.0 / 60));
                    application.Update(time, driver.ReadMouse(), new(), size);
                    application.ReadPresentation(); driver.Advance(); Thread.Yield();
                }
            } catch (Exception exception) { driver.Save(exception.ToString()); throw; }
        }
    }

    internal static void Run(string profile, Point? size)
    {
        var viewport = size ?? new Point(1000, 740); Prepare(profile);
        var engine = new RecordingLabEngine();
        using var window = new TableWindow(TestTableServices.For(engine), profile, null, null, viewport, initialState: engine.State, artSource: new CardReadingArtFixture());
        window.Automation = new PriorityFlowSmoke(window.TestPort, engine, viewport, window.TestPort);
        window.Run();
    }
    private static void Prepare(string profile)
    {
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(profile);
    }
    private PriorityFlowSmoke(TableApplication.Probe view, RecordingLabEngine engine, Point viewport, TableWindow.Probe? window = null)
    {
        this.view = view; this.engine = engine; this.viewport = viewport; this.window = window;
        Move(new(20, 60));
        Wait(() => view.Loaded && !view.ActivityOpen && view.StackRows.Count == 3, () => {
            Observe("local-response"); Capture("01-local-response");
            Check(window == null || window.GraphicsDevice.Viewport.Width == viewport.X && window.GraphicsDevice.Viewport.Height == viewport.Y,
                "Priority pointer input uses the actual requested physical viewport");
            Check(view.PrioritySeats.Keys.SequenceEqual([0]) && view.Match!.ActivePlayerId == 2,
                "Only the reported local responder has the teal portrait token, separate from another player's turn");
            Check(Text().Contains("Your priority"), "The local pending-effect heading states current priority without framing your own action as an interruption");
            Check(Text().Contains("Pass priority") && view.Hits.Any(hit => hit.Id == "confirm")
                && !Text().Any(value => value.Contains("Let it resolve", StringComparison.OrdinalIgnoreCase)),
                "The enabled primary action says Pass priority without promising that an effect will resolve");
            Check(view.DecisionDock is { Status: DecisionDockStatus.Priority, ActorRole: DecisionDockActorRole.Priority,
                    ActorId: 0, ActionLabel: "Pass priority" } && view.DecisionText.Length == 0
                && !view.Hits.Any(hit => hit.Id == "instructions")
                && view.Hits.Any(hit => hit.Id == "ability-pass:optional-mesa"),
                "The current role and real pass action explain priority without repeated prose, while an optional activation retains its own pass control");
            AssertQueue(1, 2, 3); MoveHit("stack-queue-next");
        }); Click();
        Step(() => {
            AssertQueue(4, 5);
            Check(inspectedQueueIds.Count == 5 && engine.Replies.Count == 0,
                "Every one of five pending effects is reachable through the real queue pages without sending a game command");
            Capture("02-later-stack-page"); MoveHit("stack-effect:" + view.StackRows.Last().Id);
        }); Click();
        Step(() => {
            Check(view.ZoneName == "Stack" && view.InspectedStack == engine.State.Stack[4].Id
                && Text().Any(value => value.Contains(engine.State.Stack[4].Name)) && engine.Replies.Count == 0,
                "A collapsed effect reference inspects its exact stack-item ID and public name without activating its source or passing");
            Capture("03-stack-item-details"); MoveHit("close-overlay");
        }); Click();
        Step(() => {
            Check(view.ZoneName == null && engine.Replies.Count == 0, "Closing stack details preserves the pending response window");
            MoveHit("stack-queue-prev");
        }); Click();
        Step(() => {
            AssertQueue(1, 2, 3);
            passGame = view.Match!.Id; passDecision = view.Match.Decision!.Id;
            MoveHit("confirm");
        }); Click();
        Wait(() => !view.Busy && engine.Replies.Count == 1 && view.Match?.PriorityPlayerId == 1, () => {
            Check(engine.Replies.Single() is PassPriority pass && pass.GameId == passGame && pass.DecisionId == passDecision
               ,
                "Pass priority submits exactly one current scoped Confirm, with no library selection or recipient command");
            Check(view.Match!.Decision == null && view.Match.ActivePlayerId == 2 && view.PrioritySeats.Keys.SequenceEqual([1])
                && view.Match.Stack.Any(item => item.Id == "search-pending-trigger")
                && !view.Hits.Any(hit => hit.Id == "confirm") && !Text().Any(value => value == "JUST RESOLVED"),
                "After the local pass the turn owner stays unchanged, the trigger stays pending, the reported opponent has the token, and no local pass or false resolution appears");
            Check(Text().Contains(StateClarity.PlayerName(view.Match!, view.Match!.PriorityPlayerId!.Value) + " has priority"),
                "The opponent pending-effect heading names the engine-reported priority holder");
            AssertQueue(1);
            Observe("opponent-response"); Capture("04-opponent-pending");
            view.Accept(engine.AdvanceToRecipient());
        });
        Step(() => {
            Check(view.Match?.Decision is { Intent: DecisionIntent.Selection } && view.PrioritySeats.Count == 0
                && view.Hits.Any(hit => hit.Id == "recipient:0") && Text().Any(value => value.Contains("Search trigger (scripted)"))
                && !Text().Contains("Pass priority"),
                "The required recipient choice names its source, removes the priority token and exposes a direct You control");
            Check(engine.Replies.Count == 1, "The authored other-seat handoff sends no action on behalf of that player");
            recipientDecision = view.Match!.Decision!.Id;
            Observe("required-recipient"); Capture("05-required-recipient"); MoveHit("recipient:0");
        }); Click();
        Wait(() => !view.Busy && engine.Replies.Count == 2 && view.Match?.Decision is { Kind: "choice", LibraryCards.Length: > 0 }, () => {
            Check(engine.Replies[1] is SelectPlayer { Player: 0 } recipient
                && recipient.DecisionId == recipientDecision && recipient.GameId == passGame,
                "Only clicking You submits the current required recipient choice in the same game epoch");
            Check(view.Match!.Decision is { Min: 1, Max: 1 } && view.Match.Decision.LibraryCards.Length == 3
                && view.Match.Decision.LibraryCards.All(item => item.Card.VisualId.Length == 0 && item.Card.Key.Length == 0)
                && view.PrioritySeats.Count == 0 && view.Selections.Count == 0 && !view.Hits.Any(hit => hit.Id == "choicesdone"),
                "The library search remains a separate required selection, with private prompt-scoped faces and disabled confirmation until chosen");
            libraryDecision = view.Match.Decision!.Id;
            Observe("required-library-search"); Capture("06-required-library-search"); MoveHit("choice:0");
        }); Click();
        Step(() => {
            Check(view.Selections.SequenceEqual([0]) && engine.Replies.Count == 2 && view.Hits.Any(hit => hit.Id == "choicesdone"),
                "Clicking a permitted search result stages it without silently completing the search");
            Capture("07-staged-search-result"); MoveHit("choicesdone");
        }); Click();
        Wait(() => !view.Busy && engine.Replies.Count == 3 && view.Match?.Viewer?.Zone("Hand").Cards.Length == 1, () => {
            Check(engine.Replies[2] is ChooseOptions choice && choice.GameId == passGame
                && choice.DecisionId == libraryDecision && choice.Choices!.SequenceEqual([0])
                && view.Match!.Viewer!.Zone("Hand").Cards.Single().Name == "Forest",
                "Only explicit search confirmation sends the current scoped Choose and shows the authored chosen card in hand");
            Observe("search-completed"); Capture("08-search-completed"); finished = true; Save(null); window?.Exit();
        });
    }

    private void AssertQueue(params int[] positions)
    {
        Check(view.StackRows.Select(row => row.Position).SequenceEqual(positions), "The queue page preserves exact global next-to-resolve positions " + string.Join(",", positions));
        foreach (var row in view.StackRows) {
            var entry = view.Match!.Stack[row.Position - 1]; inspectedQueueIds.Add(row.Id);
            Check(row.Id == entry.Id && !view.Hits.Any(hit => hit.Id == "stack-row:" + entry.Id),
                "The painted pending-effect row preserves its exact identity without making its whole surface a button: " + entry.Id);
            if (row == view.StackRows.First()) {
                var source = view.Hits.Single(hit => hit.Id == "stack-source:" + entry.Id);
                Check(view.CurrentHit(source) && Hit(Pixel(source.Bounds.Center.ToVector2()))?.Id == source.Id
                    && (entry.Card is not { FaceDown: false } || source.Card?.VisualId == entry.Card.VisualId),
                    "The expanded effect exposes its bounded current source reference instead of a whole-row action: " + entry.Id);
            } else Check(view.Hits.Any(hit => hit.Id == "stack-effect:" + entry.Id),
                "A collapsed effect retains one exact inspection reference: " + entry.Id);
        }
        var expanded = view.StackRows.First();
        var pending = view.Match!.Stack[expanded.Position - 1];
        string painted = string.Join(" ", Current.Observe().Text.Where(text => expanded.Bounds.Contains(new Point(text.Bounds.X, text.Bounds.Y)))
            .Select(text => text.Value));
        Check(painted.Contains(pending.Name, StringComparison.Ordinal) && painted.Contains(pending.Text, StringComparison.Ordinal)
            && (pending.Card == null || painted.Contains(pending.Card.Name, StringComparison.Ordinal))
            && (pending.ControllerId == null || painted.Contains(StateClarity.PlayerName(view.Match, pending.ControllerId.Value), StringComparison.Ordinal))
            && pending.Targets.All(target => painted.Contains(target.Name, StringComparison.Ordinal)),
            "The first visible effect shows its actual pending text, source, controller and reported targets before inspection: " + pending.Id);
        Check(!view.Hits.Any(hit => hit.Id is "stack" or "action-details") && !Current.Cards.Any(card => card.Zone == "Stack"),
            "Pending effects have no redundant Stack/Details opens or copied source-card surface on the ordinary board");
    }
    private string[] Text() => Current.Drawing.Commands.Select(command => command switch {
        DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null
    }).Where(value => value != null).Cast<string>().ToArray();
    private float Scale => Math.Min(viewport.X / 1600f, viewport.Y / (float)view.DesignHeight);
    private Point Pixel(Vector2 logical) => new((int)MathF.Round((viewport.X - 1600 * Scale) / 2 + logical.X * Scale),
        (int)MathF.Round((viewport.Y - view.DesignHeight * Scale) / 2 + logical.Y * Scale));
    private TableHit? Hit(Point physical) => view.HitAt(new((int)((physical.X - (viewport.X - 1600 * Scale) / 2) / Scale),
        (int)((physical.Y - (viewport.Y - view.DesignHeight * Scale) / 2) / Scale)));
    private void MoveHit(string id)
    {
        var hit = view.Hits.LastOrDefault(hit => hit.Id == id) ?? throw new InvalidOperationException("Missing priority input target: " + id);
        var physical = Pixel(hit.Bounds.Center.ToVector2());
        if (Hit(physical)?.Id != id) throw new InvalidOperationException("Covered physical priority input target: " + id);
        Move(physical);
    }
    private void Move(Point point) => mouse = new(point.X, point.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private void Click() { Step(() => mouse = new(mouse.X, mouse.Y, 0, ButtonState.Pressed, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released)); Step(() => Move(new(mouse.X, mouse.Y))); }
    private void Step(Action action) => steps.Enqueue((null, action));
    private void Wait(Func<bool> ready, Action action) => steps.Enqueue((ready, action));
    private void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); checks.Add(message); }
    private void Observe(string stage) => observations.Add(new { stage, view.Now, viewport.X, viewport.Y, view.DecisionText,
        game = view.Match?.Id, decision = view.Match?.Decision?.Id, actionClass = view.Match?.ActionClass, priorityPlayerId = view.Match?.PriorityPlayerId,
        seats = view.PrioritySeats.ToArray(), rows = view.StackRows.ToArray(), text = Text(), replies = engine.Replies.ToArray() });
    private void Capture(string name)
    {
        if (window == null) return;
        using var output = File.Create(Path.Combine(view.Profile, name + ".png")); window.Surface.SaveAsPng(output, window.Surface.Width, window.Surface.Height);
        window.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    private void Save(string? error) => File.WriteAllText(Path.Combine(view.Profile, "priority-flow-results.json"), JsonSerializer.Serialize(new {
        passed = error == null, error, checks, observations, replies = engine.Replies.ToArray(), gpu = window != null,
        scope = "Mechanical production composition/input checks over canonical authored states. Scoped actions are recorded; no Magic rules simulation or usability/clarity acceptance."
    }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    private void Advance()
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (!steps.TryPeek(out var next)) return;
        if (next.Ready?.Invoke() == false) { if (view.Now - lastStep > 5) throw new TimeoutException("Priority handoff did not reach its required state."); return; }
        steps.Dequeue(); next.Action(); lastStep = view.Now;
    }
    public void Frame(Texture2D surface) { try { Advance(); } catch (Exception error) { Capture("failure"); Save(error.ToString()); window?.Exit(); throw; } }

    private sealed class RecordingLabEngine : ICardEngine
    {
        private readonly ExperienceLabSession session = new("trigger-search.v1");
        private readonly ExperienceLabEngine inner;
        private GameSnapshot? projection;
        internal List<GameCommand> Replies { get; } = [];
        internal GameSnapshot State => projection ?? session.State;
        internal RecordingLabEngine()
        {
            inner = new(session);
            var other = ExperienceScenarios.Find("stack-priority.v1").Create()[2].Snapshot.Stack;
            var pending = session.State.Stack[0];
            projection = session.State with {
                Stack = [pending, .. other, pending with { Id = "second-search-ability", Name = "Second search ability" }],
                Decision = session.State.Decision! with { PriorityAbilities = [new("optional-mesa", "optional-key", "Arid Mesa", "Search for a land.", false)] } };
        }
        internal GameSnapshot AdvanceToRecipient() { projection = null; return session.AdvanceTo(2); }
        public string Name => inner.Name;
        public Task InitializeAsync() => inner.InitializeAsync();
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public async Task ExecuteAsync(GameCommand reply) { await inner.ExecuteAsync(reply); Replies.Add(reply); projection = null; }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        private static Task<T> Unavailable<T>() => Task.FromException<T>(new NotSupportedException("Priority flow uses only its canonical authored match."));
        private static Task Unavailable() => Task.FromException(new NotSupportedException("Priority flow uses only its canonical authored match."));
        public Task<ImmutableArray<DeckSummary>> DecksAsync() => inner.DecksAsync();
        public Task<ImmutableArray<DeckPreset>> PresetsAsync() => inner.PresetsAsync();
        public Task<DeckSummary> ImportAsync(string name, string text) => Unavailable<DeckSummary>();
        public Task<DeckSummary> ImportPresetAsync(string id) => Unavailable<DeckSummary>();
        public Task<DeckDetails> OpenDeckAsync(string id) => Unavailable<DeckDetails>();
        public Task<DeckDetails> SetCommandersAsync(string deckId, long revision, ImmutableArray<string> cardIds) => Unavailable<DeckDetails>();
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Unavailable<CatalogPage>();
        public Task<DeckDetails> CreateDeckAsync(string name) => Unavailable<DeckDetails>();
        public Task<DeckDetails> EditDeckAsync(string deckId, long revision, ImmutableArray<DeckEdit> edits) => Unavailable<DeckDetails>();
        public Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => Unavailable<DeckDetails>();
        public Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => Unavailable<DeckDetails>();
        public Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => Unavailable<DeckDetails>();
        public Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => Unavailable<DeckDetails>();
        public Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => Unavailable<DeckDetails>();
        public Task<string> ExportDeckAsync(string deckId, long revision) => Unavailable<string>();
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => Unavailable<SoloSetup>();
        public Task StartSoloAsync(string deckId, ImmutableArray<string> opponents) => Unavailable();
        public Task<Lobby> HostAsync(int players, bool forwarding) => Unavailable<Lobby>();
        public Task<Lobby> JoinAsync(string address) => Unavailable<Lobby>();
        public Task<Lobby> LobbyAsync() => Unavailable<Lobby>();
        public Task SelectDeckAsync(string id) => Unavailable();
        public Task SetReadyAsync(bool ready) => Unavailable();
        public Task StartAsync() => Unavailable();
        public Task ReturnToLobbyAsync() => Unavailable();
        public Task LeaveAsync() => Unavailable();
        public Task ConcedeAsync(string gameId) => Unavailable();
    }
}
