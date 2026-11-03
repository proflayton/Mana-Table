using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Authored current permissions and held receipts exercise the actual visible
// controls, session and pointer path. They do not simulate Magic resolution.
internal static class VisibleAutoPriorityChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckModeProjection(check);
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (bool reduced in new[] { false, true }) {
            CheckEnableAndResume(profile, size, reduced, check);
        }
        foreach (string gesture in new[] { "auto", "F" }) CheckHeldToggle(profile, gesture, check);
        CheckFirstLand(profile, check);
    }

    private static void CheckEnableAndResume(string profile, Point size, bool reduced, Action<bool, string> check)
    {
        using var run = new Journey(profile, "enable-resume", size, reduced);
        string context = size + "; reduced=" + reduced;
        var original = run.View.Match!;
        check(!run.View.Automatic && !run.View.Held && !PlayPreferences.Load(run.Path).Automatic
                && run.Run.Elapsed == TimeSpan.Zero && run.Port.Commands.IsEmpty,
            "The visible Auto journey starts from a saved off preference before any reading beat has elapsed at " + context);
        run.Label("quiet-priority-arm", "Auto pass", check);
        run.Label("auto", "Auto off", check);
        check(run.Text.Contains("Auto off"), "The current portrait names Auto off without requiring icon hover at " + context);
        var startBounds = run.Hit("quiet-priority-arm").Bounds;
        run.Click("quiet-priority-arm"); run.Move(new(20, 60));
        bool first = run.Pump(() => run.Port.Commands.Count == 1);
        run.Capture("first-dispatch");
        check(first && run.Run.Elapsed == TimeSpan.Zero && run.View.Busy && run.View.Automatic && !run.View.Held
                && PlayPreferences.Load(run.Path).Automatic
                && run.Port.Commands.Single() is PassForNow { YieldToken: "visible-auto-original", DecisionId: "visible-auto-current" } pass
                && pass.GameId == original.Id && GameCommandPermissions.Allows(original, pass),
            "Physical Auto pass enables saved Auto and dispatches only the original owned empty-window token without aging the current presentation at " + context);
        check(run.Hit("quiet-priority-stop").Bounds == startBounds,
            "Take control replaces Auto pass in the same physical action position at " + context);
        for (int frame = 0; frame < 12; frame++) run.Tick();
        check(run.Port.Commands.Count == 1 && run.Port.Active == 1 && run.Port.MaximumActive == 1,
            "A held first Auto pass owns the only command or observation RPC without replay at " + context);

        run.Click("quiet-priority-stop");
        check(run.View.Automatic && run.View.Held && PlayPreferences.Load(run.Path).Automatic && run.View.Busy,
            "Physical Take control pauses this turn while retaining the saved Auto preference and one in-flight receipt at " + context);
        var resume = original with { Revision = 3, Decision = original.Decision! with {
            Id = "visible-auto-resume", Payload = ((PriorityPayload)original.Decision.Payload) with { QuietYieldToken = "visible-auto-resume-token" }
        } };
        run.Port.Publish(resume); run.Port.Release(0);
        check(run.Pump(() => !run.View.Busy && run.View.Match?.Decision?.Id == resume.Decision.Id),
            "The stopped pass reconciles its actual current publication before another visible action at " + context);
        run.Label("quiet-priority-arm", "Resume Auto", check);
        run.Label("auto", "Auto paused", check);
        check(run.View.Automatic && run.View.Held && run.Text.Contains("Auto paused") && run.Port.Commands.Count == 1,
            "A held Auto preference exposes Resume Auto and its paused state rather than making the player toggle Auto off at " + context);
        run.Click("quiet-priority-arm"); run.Move(new(20, 60));
        bool second = run.Pump(() => run.Port.Commands.Count == 2);
        run.Capture("resumed-dispatch");
        check(second && run.Run.Elapsed == TimeSpan.Zero && run.View.Automatic && !run.View.Held
                && PlayPreferences.Load(run.Path).Automatic && run.View.Busy
                && run.Port.Commands.Last() is PassForNow { YieldToken: "visible-auto-resume-token", DecisionId: "visible-auto-resume" },
            "Physical Resume Auto keeps Auto on and uses only the fresh current permission, never the retired token, at " + context);
        run.Label("auto", "Auto on", check);

        // Ordinary hand hover enlarges its face; the actual noncommitting
        // reading gesture pins it on click, including during a held reply.
        var hand = run.Run.Frame.Hits.Last(hit => hit.Card?.VisualId == "visible-auto-instant");
        check(run.View.CurrentHit(hand) && hand.Intent is MatchIntent.InspectOccurrence,
            "The actual instant exposes its current noncommitting card-reading gesture at " + context);
        run.Click(hand.Id); run.Tick();
        run.Capture("reading-after-click");
        check(run.View.DecisionDock?.PauseReason == DecisionDockPauseReason.Inspection && run.Port.Commands.Count == 2
                && run.View.Match?.Decision?.Id == resume.Decision.Id,
            "Reading the actual available instant visibly pauses Auto while preserving the already sent response at " + context);
        var afterReading = resume with { Revision = 4, Decision = resume.Decision with { Id = "visible-auto-after-reading" } };
        run.Port.Publish(afterReading); run.Port.Release(1);
        check(run.Pump(() => !run.View.Busy && run.View.Match?.Decision?.Id == afterReading.Decision.Id)
                && run.Port.Commands.Count == 2,
            "A fresh owned priority window stays unanswered while the player is reading at " + context);
        run.Move(new(20, 60)); run.Tick(keys: new(Keys.Escape)); run.Tick();
        run.Capture("reader-closed-before-ordinary-beat");
        check(run.Run.Elapsed == TimeSpan.Zero && run.View.DecisionDock?.PauseReason == DecisionDockPauseReason.Presentation
                && run.Port.Commands.Count == 2 && run.View.Match?.Decision?.Id == afterReading.Decision.Id,
            "Closing the reader restores the current ordinary presentation pause after the explicit startup allowance was consumed at " + context);
        // Both deliberate startup dispatches above happened at clock zero.
        // Subsequent automatic batches still wait for ordinary visible work;
        // advancing this presentation clock does not widen any worker deadline.
        run.Tick(elapsed: TimeSpan.FromSeconds(3));
        bool continued = run.Pump(() => run.Port.Commands.Count == 3);
        run.Capture("reader-closed-after-ordinary-beat");
        check(continued && run.Port.Commands.Last() is PassForNow {
                YieldToken: "visible-auto-resume-token", DecisionId: "visible-auto-after-reading" },
            "After reading and its ordinary presentation beat the same bounded quiet intent answers only the fresh current decision at " + context);

        var response = Response(afterReading);
        run.Port.Publish(response); run.Run.QueueObservation(response);
        check(run.Pump(() => run.View.Match?.Revision == response.Revision && !run.Has("quiet-priority-stop"))
                && run.View.Busy && run.Port.Commands.Count == 3 && run.View.Automatic && !run.View.Held,
            "A new spell interrupts visible Auto before its held pass finishes without disabling the saved preference or opening a second RPC at " + context);
        run.Port.Release(2);
        check(run.Pump(() => !run.View.Busy), "The held response reconciles after the new action at " + context);
        run.Tick(elapsed: TimeSpan.FromSeconds(3));
        for (int frame = 0; frame < 8; frame++) run.Tick();
        run.Capture("new-response-stopped");
        check(run.Port.Commands.Count == 3 && run.Port.MaximumActive == 1 && run.Has("confirm")
                && !run.Has("quiet-priority-arm") && !run.Has("quiet-priority-stop")
                && run.View.Match?.Decision?.Id == response.Decision!.Id
                && run.View.DecisionDock?.AutomaticPassing.StateLabel == "Auto on · Your response"
                && run.Text.Contains("Your response") && run.Text.Contains("Auto paused"),
            "The new response remains manually actionable after all reading time elapses; Auto cannot adopt an unavailable token or replay any pass at " + context);
    }

    private static void CheckModeProjection(Action<bool, string> check)
    {
        var state = State(available: false, ownTurn: false);
        foreach (var example in new[] {
            (Controls: new DecisionDockControlFacts { Automatic = false, Held = true }, Expected: new AutomaticPassingView("Auto off", "Auto pass", "Auto off", true)),
            (Controls: new DecisionDockControlFacts { Automatic = true, Held = true }, Expected: new AutomaticPassingView("Auto paused", "Resume Auto", "Auto paused", true)),
            (Controls: new DecisionDockControlFacts { Automatic = true, ResponsePending = true }, Expected: new AutomaticPassingView("Auto on · Your response", "Resume Auto", "Auto on", false))
        }) check(DecisionDockPresentation.From(state, example.Controls).AutomaticPassing == example.Expected,
            "The effective Auto projection distinguishes saved mode, temporary Hold and a real pending response: " + example.Expected);
    }

    private static void CheckHeldToggle(string profile, string gesture, Action<bool, string> check)
    {
        using var run = new Journey(profile, "held-" + gesture, new(1000, 740), true, available: false);
        run.Label("auto", "Auto off", check);
        run.Click("auto");
        run.Capture("global-enabled-before-hold");
        run.Label("auto", "Auto on", check);
        check(run.View.Automatic && !run.View.Held && PlayPreferences.Load(run.Path).Automatic && run.Port.Commands.IsEmpty,
            "The physical global Auto control enables the saved mode before " + gesture + " takeover without inventing missing pass permission");
        run.Click("hold");
        run.Capture("global-held-before-label");
        run.Label("auto", "Auto paused", check);
        check(run.View.Automatic && run.View.Held && run.Port.Commands.IsEmpty,
            "The actual Hold gesture starts the " + gesture + " resume control with Auto still saved on");
        if (gesture == "F") { run.Tick(keys: new(Keys.F)); run.Tick(); }
        else run.Click("auto");
        run.Label("auto", "Auto on", check);
        run.Capture("global-resume");
        check(run.View.Automatic && !run.View.Held && PlayPreferences.Load(run.Path).Automatic
                && run.Port.Commands.IsEmpty && !run.Has("quiet-priority-arm"),
            "The physical " + gesture + " gesture resumes held Auto instead of switching it off, while missing engine permission still authorizes no pass");
    }

    private static void CheckFirstLand(string profile, Action<bool, string> check)
    {
        using var run = new Journey(profile, "first-land", new(1000, 740), true, ownTurn: true);
        run.Label("quiet-priority-arm", "Auto pass", check); run.Click("quiet-priority-arm");
        check(!run.View.Automatic && !PlayPreferences.Load(run.Path).Automatic && !run.View.Held
                && run.Port.Commands.IsEmpty && run.Has("turn-pass-confirm"),
            "The first-land warning precedes enabling saved Auto or capturing a quiet pass");
        run.Click("turn-pass-keep");
        check(!run.View.Automatic && run.Port.Commands.IsEmpty && run.Has("quiet-priority-arm"),
            "Keep playing dismisses first-land Auto activation without changing the saved mode");
        run.Click("quiet-priority-arm"); run.Click("turn-pass-confirm"); run.Move(new(20, 60));
        check(run.Pump(() => run.Port.Commands.Count == 1) && run.Run.Elapsed == TimeSpan.Zero
                && run.View.Automatic && PlayPreferences.Load(run.Path).Automatic
                && run.Port.Commands.Single() is PassForNow { YieldToken: "visible-auto-original", DecisionId: "visible-auto-current" },
            "Only confirmed first-land consent enables Auto and immediately dispatches that exact current token");
    }

    private static GameSnapshot State(bool available, bool ownTurn)
    {
        var state = PriorityAbilitySmoke.Example();
        var instant = new Card { Key = "visible-auto-instant-key", VisualId = "visible-auto-instant", CombatId = "visible-auto-instant",
            Name = "Lightning Bolt", Type = "Instant", Text = "Lightning Bolt deals 3 damage to any target.", ManaCost = "{R}", Selectable = true };
        return state with { Id = "visible-auto-game", Revision = 2, BoardRevision = 2, Turn = 7,
            ActivePlayerId = ownTurn ? state.ViewerId : 1, Phase = "Main phase", PhaseKey = "MAIN1",
            PriorityPlayerId = state.ViewerId, DecisionOwnerId = state.ViewerId, ActionClass = "priority",
            Stack = [], Combat = null, StackInterruptionVersion = 0, StackActionVersion = 0, ResolutionChoiceVersion = 0,
            Executions = new(1, 0, [], []),
            Activity = [new(1, 7, "MAIN1", "life", 1, "An opponent lost one life.", null, null)],
            Players = state.Players.Select(player => player with { Life = player.Id == 1 ? 39 : 40,
                LandPlaysUsed = player.Id == state.ViewerId ? ownTurn ? 0 : 1 : player.LandPlaysUsed,
                LandPlayLimit = player.Id == state.ViewerId ? 1 : player.LandPlayLimit,
                Zones = player.Zones.Select(zone => player.Id != state.ViewerId ? zone
                    : zone.Name == "Battlefield" ? zone with { Count = 0, Cards = [] }
                    : zone.Name == "Hand" ? zone with { Count = 1, Cards = [instant] } : zone).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = new("visible-auto-current", new(), new PriorityPayload(new("Pass priority", true),
                CanAutoPass: false, QuietYieldToken: available ? "visible-auto-original" : "", Abilities: [])) };
    }

    private static GameSnapshot Response(GameSnapshot state)
    {
        var occurrence = new ExecutionOccurrence("visible-auto-response", "visible-auto-response", ExecutionKind.Spell, ExecutionStep.Stack) {
            ActorPlayerId = 1, SourceName = "Opponent spell" };
        return state with { Revision = state.Revision + 1, BoardRevision = state.BoardRevision + 1,
            StackInterruptionVersion = 1, StackActionVersion = 1,
            Stack = [new("Opponent spell", "Draw a card.", StackActionKind.Spell, null) { Id = "visible-auto-response-stack", ExecutionId = occurrence.Id, ControllerId = 1 }],
            Executions = new(1, 1, [occurrence], [new(1, occurrence.Id, ExecutionStep.Stack)]),
            Activity = state.Activity.Add(new(2, 7, "MAIN1", "cast", 1, "An opponent cast a spell.", null, null) {
                ExecutionId = occurrence.Id, ExecutionStep = ExecutionStep.Stack }),
            Decision = state.Decision! with { Id = "visible-auto-new-response", Payload = ((PriorityPayload)state.Decision.Payload) with { QuietYieldToken = "" } } };
    }

    private sealed class Journey : IDisposable
    {
        internal PresentationRun Run { get; }
        internal TableApplication.Probe View => Run.View;
        internal Port Port { get; }
        internal string Path { get; }
        private Point pointer = new(20, 60);
        internal string Text => string.Join(" ", Run.Frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => "" }));
        internal Journey(string profile, string name, Point size, bool reduced, bool automatic = false, bool available = true, bool ownTurn = false)
        {
            Path = System.IO.Path.Combine(profile, "visible-auto-" + name + "-" + size.X + (reduced ? "-reduced" : "-motion"));
            Directory.CreateDirectory(Path);
            new PlayPreferences { Automatic = automatic, ReducedMotion = reduced, Sounds = false, TextScale = 1.3f }.Save(Path);
            var current = State(available, ownTurn); Port = new(current);
            var before = current with { Revision = 1, BoardRevision = 1, Activity = [], Decision = null,
                PriorityPlayerId = null, DecisionOwnerId = null, ActionClass = "resolving",
                Players = current.Players.Select(player => player with { Life = 40 }).ToImmutableArray() };
            var setup = new InteractionScenarioEngine();
            Run = new(new(setup, setup, Port, () => Task.CompletedTask), Path, before, size);
            Tick(); Tick(); Run.QueueObservation(current);
            if (!Pump(() => View.Match?.Revision == current.Revision)) throw new InvalidOperationException("The current Auto permission was not presented.");
        }
        internal bool Has(string id) => Run.Frame.Hits.Any(hit => hit.Id == id);
        internal TableHit Hit(string id) => Run.Frame.Hits.Single(hit => hit.Id == id);
        internal void Label(string id, string label, Action<bool, string> check)
        {
            CompleteActionLabelChecks.Run(Run.Frame, id, label, check);
            var hit = Hit(id);
            check(View.CurrentHit(hit) && View.HitAt(hit.Bounds.Center)?.Id == id,
                label + " owns an exposed current physical pointer target");
        }
        internal void Move(Point point) { pointer = point; Tick(); }
        internal void Click(string id) { pointer = Hit(id).Bounds.Center; Tick(ButtonState.Pressed); Tick(); }
        internal void Tick(ButtonState left = ButtonState.Released, TimeSpan elapsed = default, KeyboardState keys = default)
        {
            var physical = RenderViewport.Fit(Run.Viewport, View.DesignHeight).ToPhysical(pointer.ToVector2());
            Run.Tick(elapsed, new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
                left, default, default, default, default), keys);
        }
        internal bool Pump(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
            return ready();
        }
        internal void Capture(string stage) => File.WriteAllText(System.IO.Path.Combine(Path, stage + ".json"), JsonSerializer.Serialize(new {
            elapsedTicks = Run.ElapsedTicks, View.Automatic, View.Held, View.Busy, View.Polling, View.DecisionDock,
            persistedAuto = PlayPreferences.Load(Path).Automatic, scope = View.Match?.Decision?.Id,
            commands = Port.Commands.Select(command => new { kind = command.GetType().Name, command.GameId, command.DecisionId,
                token = (command as PassForNow)?.YieldToken }), Port.Active, Port.MaximumActive,
            Port.Observations, globalControl = GlobalControlFacts(), frame = Run.Frame.Observe()
        }, new JsonSerializerOptions { WriteIndented = true }));
        private object GlobalControlFacts()
        {
            var auto = Run.Frame.Hits.SingleOrDefault(hit => hit.Id == "auto");
            var hold = Run.Frame.Hits.SingleOrDefault(hit => hit.Id == "hold");
            var metrics = new TextMetrics();
            return new {
                View.Automatic, View.Held, effective = View.DecisionDock?.AutomaticPassing,
                textScale = Run.Frame.Drawing.TextScale,
                auto = auto == null ? null : new { auto.Bounds, auto.Scope, current = View.CurrentHit(auto),
                    centerHit = View.HitAt(auto.Bounds.Center)?.Id },
                hold = hold == null ? null : new { hold.Bounds, hold.Scope, current = View.CurrentHit(hold),
                    centerHit = View.HitAt(hold.Bounds.Center)?.Id },
                centerLabels = Run.Frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
                    .Where(text => auto != null && auto.Bounds.Intersects(text.Bounds))
                    .Take(8).Select(text => new { text.Value, text.Bounds, text.Size, text.Bold,
                        autoPausedWidth = metrics.TextWidth("Auto paused", text.Size, text.Bold, Run.Frame.Drawing.TextScale) }).ToArray(),
                positionedLabels = Run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
                    .Where(text => auto != null && auto.Bounds.Contains(text.X, text.Y))
                    .Take(8).Select(text => new { text.Value, text.X, text.Y, text.Width, text.Size, text.Bold,
                        autoPausedWidth = metrics.TextWidth("Auto paused", text.Size, text.Bold, Run.Frame.Drawing.TextScale) }).ToArray()
            };
        }
        public void Dispose() { Port.ReleaseAll(); Run.Dispose(); }
    }

    private sealed class Port(GameSnapshot initial) : IMatchConnection
    {
        private readonly object gate = new();
        private GameSnapshot state = initial;
        private readonly List<TaskCompletionSource> pending = [];
        private bool closing;
        private int active, maximum, observations;
        internal int Active => Volatile.Read(ref active);
        internal int MaximumActive => Volatile.Read(ref maximum);
        internal int Observations => Volatile.Read(ref observations);
        internal ConcurrentQueue<DecisionCommand> Commands { get; } = new();
        private void Enter() { int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count); }
        internal void Publish(GameSnapshot next) { lock (gate) state = next; }
        internal void Release(int index) { lock (gate) pending[index].TrySetResult(); }
        internal void ReleaseAll() { lock (gate) { closing = true; foreach (var item in pending) item.TrySetResult(); } }
        public async Task ExecuteAsync(GameCommand command)
        {
            Task wait;
            lock (gate) {
                if (command is not PassForNow pass || !GameCommandPermissions.Allows(state, pass))
                    throw new InvalidOperationException("Visible Auto attempted a different command or stale permission.");
                Enter(); Commands.Enqueue(pass);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(release); wait = release.Task;
                if (closing) release.TrySetResult();
            }
            try { await wait; } finally { Interlocked.Decrement(ref active); }
        }
        public Task<GameSnapshot?> ObserveAsync()
        {
            lock (gate) {
                Enter(); Interlocked.Increment(ref observations);
                try { return Task.FromResult<GameSnapshot?>(state); }
                finally { Interlocked.Decrement(ref active); }
            }
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
