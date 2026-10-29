using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Authored provider publications exercise the shipping pointer/key/session
// boundary. Actual rules restoration is independently tested through Forge.
internal static class GameplayUndoPresentationChecks
{
    private const string Prefix = "undo-certified-action:";
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            foreach (bool reduced in new[] { false, true })
            foreach (bool keyboard in new[] { false, true }) Journey(profile, viewport, reduced, keyboard, check);
            Guards(profile, viewport, check);
            AutomaticCorrectionWindow(profile, viewport, check);
            UnverifiedHandDrag(profile, viewport, check);
            RetainedRestoration(profile, viewport, check);
        }
    }

    private static void Journey(string profile, Point viewport, bool reduced, bool keyboard, Action<bool, string> check)
    {
        using var run = new NativeRun(profile, "undo-journey-" + keyboard + "-" + reduced, viewport, reduced);
        check(run.Undo == null && run.Port.Commands.Count == 0, "No certified offer invents no gameplay Undo control at " + viewport);
        run.Pulse(new(Keys.LeftControl, Keys.Z));
        check(!run.View.Held && run.Port.Commands.Count == 0, "Ctrl+Z without an offer neither toggles Hold nor submits a command at " + viewport);
        run.TapSource();
        var undo = run.Undo!;
        var label = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().Single(text => text.Value == "Undo tap " + UndoPort.SourceName);
        var confirm = run.Frame.Hits.Single(hit => hit.Id == "confirm");
        check(run.View.Match!.Viewer!.Zone("Battlefield").Cards.Single().Tapped
                && run.View.Match.Viewer.Mana.GetValueOrDefault("C") == 2 && run.Port.Commands is [UseOfferedAction]
                && label.MaxLines > 1 && label.Width <= undo.Bounds.Width && !undo.Bounds.Intersects(confirm.Bounds)
                && !undo.Bounds.Intersects(run.View.ManaPoolBadge(0)!.Bounds)
                && run.View.HitAt(run.PointFor(undo))?.Id == undo.Id,
            "A manual offered mana activation shows its exact tap, produced mana and reachable full-name contextual Undo clear of current priority/resources at " + viewport);
        run.Capture("offered");
        var offered = run.View.Match.Undo!.Offer;
        run.ReadSource();
        var oldReader = run.Frame.Hits.FirstOrDefault(hit => hit.Id == "read-card-action");
        run.Pulse(new(Keys.Escape)); run.Tick(elapsed: .8);
        run.Tick(new(Keys.W), elapsed: .05); run.Tick();
        var camera = run.View.BoardView;
        check(camera != BoardView.Identity && run.View.Match.Undo.Offer == offered && run.Port.Commands.Count == 1,
            "Reading, reader source queries and a real camera gesture preserve the exact one-step offer without rules work at " + viewport);
        if (keyboard) {
            run.View.SetAutomatic(true); run.Tick();
            run.Tick(new(Keys.LeftControl));
            check(!run.View.Held && run.View.DecisionDock?.PauseReason == Mana.Magic.DecisionDockPauseReason.Interaction,
                "A pending Ctrl modifier pauses automatic progression without changing Hold or issuing a command at " + viewport);
            run.Tick(new(Keys.LeftControl, Keys.Z));
            run.Tick(new(Keys.LeftControl, Keys.Z)); run.Tick();
        } else run.Click(run.Undo!.Id);
        run.Until(() => !run.View.Busy && run.View.Match?.Undo?.RestorationVersion == 1, "complete restoration");
        check(run.Port.Commands is [UseOfferedAction, UndoAction]
                && run.View.Match!.Viewer!.Zone("Battlefield").Cards.Single() is { Tapped: false, Key: "restored:source" }
                && run.View.Match.Viewer.Mana.GetValueOrDefault("C") == 0 && run.Undo == null
                && run.View.BoardView == camera && !run.View.Held && run.View.Automatic == keyboard
                && run.Frame.Cards.All(card => !card.Focused && card.Layer is not CardPresentationLayer.Playing)
                && run.View.AnnouncementCount == 0,
            (keyboard ? "Staged Ctrl+Z" : "Pointer Undo") + " consumes one certified action and reconciles the complete restored source/resource state while preserving the camera at " + viewport);
        int replies = run.Port.Commands.Count;
        run.Pulse(new(Keys.RightControl, Keys.Z)); run.View.Activate(undo);
        if (oldReader != null) run.View.Activate(oldReader);
        run.Tick();
        check(run.Port.Commands.Count == replies && !run.View.Busy && run.View.Match.Undo.Offer == null,
            "Consumed Undo, a held/repeated chord and a captured pre-restoration reader cannot replay or activate a different action at " + viewport);
        run.Capture("restored");
    }

    private static void Guards(string profile, Point viewport, Action<bool, string> check)
    {
        using var run = new NativeRun(profile, "undo-guards", viewport, true);
        run.TapSource();
        var original = run.Undo!; int before = run.Port.Commands.Count;
        var at = run.PointFor(original);
        run.Tick(point: at); run.Tick(point: at, left: ButtonState.Pressed);
        run.Port.ReplaceOffer(); run.View.Accept(run.Port.State);
        run.Tick(point: at, left: ButtonState.Pressed); run.Tick(point: at);
        check(run.Port.Commands.Count == before && run.Undo?.Id != original.Id && !run.View.Activate(original),
            "Replacing the offer during hover/press cannot retarget its release to the new action even inside the same decision at " + viewport);
        var displayed = run.Undo!;
        run.Tick(new(Keys.LeftControl));
        run.Port.ReplaceOffer(); run.View.Accept(run.Port.State);
        run.Tick(new(Keys.LeftControl, Keys.Z)); run.Tick();
        check(run.Port.Commands.Count == before && !run.View.Held && !run.View.Activate(displayed),
            "A newly accepted but not yet displayed offer cannot replace the keyboard gesture's previous target at " + viewport);
        var focusHit = run.Undo!;
        run.Tick(new(Keys.RightControl, Keys.Z), active: false);
        bool background = run.View.Activate(focusHit);
        run.Tick(new(Keys.RightControl, Keys.Z)); run.Tick();
        check(!background && run.Port.Commands.Count == before && !run.View.Held,
            "Background keys, captured direct hits and held keys on refocus cannot dispatch Undo before neutral foreground input at " + viewport);
        run.Click("settings"); run.Pulse(new(Keys.LeftControl, Keys.Z));
        check(run.Port.Commands.Count == before && run.View.Overlay == "settings" && !run.View.Held,
            "Settings owns its keyboard input; Ctrl+Z cannot act through the overlay at " + viewport);
        run.Click("close-overlay");
        run.Pulse(new(Keys.LeftControl, Keys.LeftShift, Keys.Z));
        check(run.Port.Commands.Count == before && !run.View.Held,
            "Ctrl+Shift+Z does not invent Redo or change the Hold preference at " + viewport);
        var expired = run.Undo!;
        run.Port.Expire(); run.View.Accept(run.Port.State); run.Tick();
        run.Pulse(new(Keys.LeftControl, Keys.Z));
        check(run.Undo == null && !run.View.Activate(expired) && run.Port.Commands.Count == before,
            "Authoritative expiry removes Undo and rejects a retained pointer or keyboard gesture without fabricating disabled source actions at " + viewport);
        run.Pulse(new(Keys.LeftControl));
        check(run.View.Held && run.Port.Commands.Count == before, "A bare Ctrl release retains the existing Hold gesture without a rules command at " + viewport);
        run.Pulse(new(Keys.LeftControl));
        check(!run.View.Held, "A second bare Ctrl release retires Hold as before at " + viewport);
    }

    private static void RetainedRestoration(string profile, Point viewport, Action<bool, string> check)
    {
        using var run = new NativeRun(profile, "undo-retained-restoration", viewport, false);
        run.Port.AddEarlierCompletion(); run.View.Accept(run.Port.State); run.Tick();
        run.TapSource(); run.ReadSource();
        var earlierActivity = run.View.Match!.Activity;
        check(run.Frame.Cards.Any(card => card.Focused), "The retained-restoration fixture has an actual reader before reconciliation at " + viewport);
        run.Port.PublishSkippedRestoration(); run.View.Accept(run.Port.State); run.Tick();
        var restored = run.View.Match;
        check(restored?.Undo is { RestorationVersion: 1, Offer: null, LastRestoration.ActionId: "tap-action" }
                && run.View.AnnouncementCount == 0 && run.View.PublicChanges.Count == 0
                && run.Frame.Cards.All(card => !card.Focused && card.Layer == CardPresentationLayer.Table
                    && Vector2.Distance(card.Display.Center, card.Rest.Center) < .01f
                    && Vector2.Distance(card.Display.Size, card.Rest.Size) < .01f)
                && restored.Activity.SequenceEqual(earlierActivity)
                && run.Port.Commands is [UseOfferedAction],
            "A retained restoration marker clears pre-restore readers, queued motion/cues and announcements without needing a restoration event or deleting earlier action history at " + viewport);
        run.ReadSource();
        run.Port.RefreshSameRestoration(); run.View.Accept(run.Port.State); run.Tick();
        check(run.Frame.Cards.Any(card => card.Focused) && run.View.Match!.Undo!.RestorationVersion == 1,
            "An unchanged retained marker does not repeatedly clear a newly opened current reader at " + viewport);
        run.Capture("retained-restoration");
    }

    private static void AutomaticCorrectionWindow(string profile, Point viewport, Action<bool, string> check)
    {
        using var run = new NativeRun(profile, "undo-automatic", viewport, true);
        run.TapSource();
        run.Port.AllowAutomaticPass(); run.View.Accept(run.Port.State);
        run.View.SetAutomatic(true);
        for (int i = 0; i < 8; i++) run.Tick(elapsed: 1);
        check(run.View.Automatic && run.View.Match!.Decision!.Payload is PriorityPayload { CanAutoPass: true }
                && run.Port.Commands is [UseOfferedAction] && run.Undo != null && !run.View.Busy,
            "Auto preserves a deliberate manual tap's certified correction window even when the provider permits passing and no other response is available at " + viewport);
        run.Pulse(new(Keys.LeftControl, Keys.Z));
        run.Until(() => run.View.Match?.Undo?.RestorationVersion == 1 && !run.View.Busy, "automatic correction");
        check(run.Port.Commands is [UseOfferedAction, UndoAction] && run.View.Automatic && !run.View.Held,
            "The preserved Auto correction window remains immediately usable by Ctrl+Z without a timed delay or mode change at " + viewport);
    }

    private static void UnverifiedHandDrag(string profile, Point viewport, Action<bool, string> check)
    {
        foreach (string zone in new[] { "Hand", "Command", "Exile" })
        foreach (string outcome in new[] { "cast", "unavailable", "expired", "escape", "guide" }) {
            using var run = new NativeRun(profile, "undo-source-drag-" + zone + "-" + outcome, viewport, true);
            run.TapSource(); run.Port.AddUnverifiedPlayCard(zone); run.View.Accept(run.Port.State); run.Tick();
            var before = run.View.Match!;
            var hand = run.Frame.Cards.Single(card => card.Id == UndoPort.HandId);
            var start = run.PointFor(hand.Hit); var drop = new Point(710, 440);
            run.Tick(point: start, left: ButtonState.Pressed); run.Tick(point: drop, left: ButtonState.Pressed);
            run.Until(() => run.Port.HandReadStarted, "unverified hand source query", drop, ButtonState.Pressed);
            run.Tick(point: drop); run.Tick();
            check(!before.Viewer!.Zone(zone).Cards.Single().Selectable
                    && run.Port.Commands is [UseOfferedAction] && run.View.Match == before
                    && run.Frame.Cards.Single(card => card.Id == UndoPort.HandId).Display.Center == drop.ToVector2() + new Vector2(0, -72)
                    && run.Frame.Cards.All(card => !card.Focused),
                "A direct known " + zone + " source drag in the certified correction window retains its physical release and queries one exact source without claiming playability or submitting an unverified cast at " + viewport);
            if (outcome == "expired") { run.Port.Expire(); run.View.Accept(run.Port.State); run.Tick(); }
            if (outcome == "escape") run.Pulse(new(Keys.Escape));
            if (outcome == "guide") run.Pulse(new(Keys.F1));
            run.Port.CompleteHandRead(outcome != "unavailable");
            if (outcome == "cast") {
                run.Until(() => !run.View.Busy && run.Port.Commands.Count == 2, "queried hand cast");
                check(run.Port.Commands is [UseOfferedAction, UseOfferedAction { ActionId: "cast-hand", Card: "hand:pending" }]
                        && run.View.Match!.Undo!.Offer == null,
                    "The released " + zone + " gesture proceeds once using only the exact queried cast offer and retires the previous Undo window at " + viewport);
            } else {
                if (outcome == "guide") {
                    for (int i = 0; i < 5; i++) run.Tick();
                    check(run.View.ExperienceOpen && run.Port.Commands is [UseOfferedAction],
                        "Opening the real F1 table guide cancels a pending " + zone + " release before its late source offer can cast behind the guide at " + viewport);
                    run.Pulse(new(Keys.Escape));
                }
                if (outcome == "unavailable")
                    run.Until(() => Vector2.Distance(run.Frame.Cards.Single(card => card.Id == UndoPort.HandId).Display.Center,
                        drop.ToVector2() + new Vector2(0, -72)) > 100, "unavailable hand return");
                int reads = run.Port.SourceReads;
                run.ReadSource(); run.Until(() => run.Port.SourceReads > reads, "retired hand-query callback");
                check(run.Port.Commands is [UseOfferedAction] && !run.View.Busy,
                    "An unavailable " + zone + " source, expired correction scope or cancelled release cannot turn a late query into a cast at " + viewport);
            }
        }
    }

    private sealed class NativeRun : IDisposable
    {
        private readonly PresentationRun app;
        private readonly string directory;
        private readonly Point viewport;
        internal UndoPort Port { get; } = new();
        internal TableApplication.Probe View => app.View;
        internal PresentationFrame Frame => app.Frame;
        internal TableHit? Undo => Frame.Hits.SingleOrDefault(hit => hit.Id.StartsWith(Prefix, StringComparison.Ordinal));
        internal NativeRun(string profile, string name, Point viewport, bool reduced) {
            this.viewport = viewport; directory = Path.Combine(profile, name + "-" + viewport.X);
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = reduced, Sounds = false, TextScale = 1.3f }.Save(directory);
            var setup = new InteractionScenarioEngine();
            app = new(new(setup, setup, Port, () => Task.CompletedTask) { SourceActions = Port }, directory, Port.State, viewport);
            Tick(); Tick(); View.SetTableStudy(true); Tick(elapsed: 2);
        }
        internal void Tick(KeyboardState keys = default, Point? point = null, ButtonState left = ButtonState.Released,
            double elapsed = .05, bool active = true) {
            var pointer = point ?? new Point(20, 60);
            var physical = RenderViewport.Fit(viewport, View.DesignHeight).ToPhysical(pointer.ToVector2());
            app.Tick(TimeSpan.FromSeconds(elapsed), new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
                left, default, default, default, default), keys, active: active);
        }
        internal void Pulse(KeyboardState keys) { Tick(keys); Tick(); }
        internal void Until(Func<bool> ready, string step, Point? point = null, ButtonState left = ButtonState.Released) {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && DateTime.UtcNow < deadline) { Tick(point: point, left: left); Thread.Sleep(1); }
            if (!ready()) {
                Capture("stopped-" + step.Replace(' ', '-'));
                throw new InvalidOperationException("Certified Undo presentation stopped at " + step + ": " + View.Error);
            }
        }
        internal Point PointFor(TableHit hit) {
            bool Exposed(Point p) => View.HitAt(p) is { } current && current.Id == hit.Id && current.Scope == hit.Scope;
            if (Exposed(hit.Bounds.Center)) return hit.Bounds.Center;
            for (int y = hit.Bounds.Top; y < hit.Bounds.Bottom; y += 2)
                for (int x = hit.Bounds.Left; x < hit.Bounds.Right; x += 2) if (Exposed(new(x, y))) return new(x, y);
            throw new InvalidOperationException("The Undo journey cannot reach " + hit.Id);
        }
        internal void Click(string id) {
            var point = PointFor(Frame.Hits.Last(hit => hit.Id == id));
            Tick(point: point, left: ButtonState.Pressed); Tick(point: point); Tick();
        }
        internal void ReadSource() {
            var source = Frame.Cards.Single(card => card.Id == UndoPort.SourceId);
            Click(source.Hit.Id);
            Until(() => Frame.Cards.Any(card => card.Id == UndoPort.SourceId && card.Focused), "source reader");
            Tick(elapsed: .8);
        }
        internal void TapSource() {
            ReadSource(); Until(() => Frame.Hits.Any(hit => hit.Id == "read-card-action"), "manual mana offer");
            Click("read-card-action");
            Until(() => !View.Busy && Undo != null, "settled certified offer");
        }
        internal void Capture(string stage) => File.WriteAllText(Path.Combine(directory, stage + ".json"),
            JsonSerializer.Serialize(new { snapshot = View.Match, frame = Frame.Observe(),
                commands = Port.Commands.Select(command => command.GetType().Name) }, new JsonSerializerOptions { WriteIndented = true }));
        public void Dispose() => app.Dispose();
    }

    private sealed class UndoPort : IMatchConnection, ISourceActions
    {
        internal const string SourceId = "certified-mana-source";
        internal const string SourceName = "Sol Ring with a deliberately complete long source name for the current certified activation";
        internal const string HandId = "unverified-hand-source";
        private readonly object gate = new();
        private GameSnapshot state;
        private TaskCompletionSource<SourceActionSet>? handRead;
        private SourceActionSet? pendingHandOffer;
        internal bool HandReadStarted { get { lock (gate) return handRead != null; } }
        internal int SourceReads { get; private set; }
        internal List<GameCommand> Commands { get; } = [];
        internal GameSnapshot State { get { lock (gate) return state; } }
        internal UndoPort() {
            var initial = PresentationSmoke.Example();
            state = initial with { Id = "certified-undo-presentation", Revision = 1, BoardRevision = 1, ViewerId = 0,
                Turn = 4, PhaseKey = "MAIN1", Phase = "Main phase", ActivePlayerId = 0, DecisionOwnerId = 0, PriorityPlayerId = 0,
                Combat = null, Stack = [], Activity = [], Executions = new(1, 0, [], []), Undo = new(0, 0, null, null),
                Decision = Priority("before"), Players = initial.Players.Select(player => player with {
                    Mana = ImmutableDictionary<string, int>.Empty, Priority = player.Id == 0,
                    Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                        Count = player.Id == 0 ? 1 : 0, Cards = player.Id == 0 ? [new Card { VisualId = SourceId, CombatId = SourceId,
                            Key = "before:source", Name = SourceName, Type = "Artifact", Text = "{T}: Add {C}{C}.", Selectable = true }] : []
                    }).ToImmutableArray() }).ToImmutableArray() };
        }
        private static Decision Priority(string id) => new(id, new(), new PriorityPayload(new("Go to combat", true), CanAutoPass: false));
        public bool SupportsSourceActions => true;
        public Task<SourceActionSet> SourceActionsAsync(string game, string decision, string key) {
            lock (gate) {
                SourceReads++;
                if (key == "hand:pending") {
                    pendingHandOffer = new(game, decision, key, HandId, [new("cast-hand", "cast", "Cast Pending artifact.")]) { Revision = state.Revision };
                    handRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return handRead.Task;
                }
                return Task.FromResult(new SourceActionSet(game, decision, key, SourceId,
                    state.Viewer!.Zone("Battlefield").Cards.Single().Tapped ? [] : [new("manual-mana", "activate", "{T}: Add {C}{C}.")]) { Revision = state.Revision });
            }
        }
        public Task<GameSnapshot?> ObserveAsync() { lock (gate) return Task.FromResult<GameSnapshot?>(state); }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("This gesture never concedes.");
        public Task ExecuteAsync(GameCommand command) {
            lock (gate) {
                if (command.GameId != state.Id || command is not DecisionCommand decision || decision.DecisionId != state.Decision?.Id)
                    throw new EngineCommandRejectedException("Expired authored interaction scope.");
                if (command is UseOfferedAction { ActionId: "manual-mana" } offered
                    && offered.Card == state.Viewer!.Zone("Battlefield").Cards.Single().Key) {
                    var occurrence = new ExecutionOccurrence("tap-execution", "tap-execution", ExecutionKind.Mana, ExecutionStep.Complete) {
                        Outcome = ExecutionOutcome.Resolved, ActorPlayerId = 0, SourceCardId = SourceId, SourceName = SourceName };
                    long sequence = state.Executions!.LastSequence + 1;
                    state = state with { Revision = state.Revision + 1, BoardRevision = state.BoardRevision + 1, Decision = Priority("after-tap"),
                        Executions = new(1, sequence, [.. state.Executions.Occurrences, occurrence],
                            [.. state.Executions.Transitions, new(sequence, occurrence.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
                        Undo = new(state.Undo!.BarrierVersion + 1, 0,
                            new("undo-offer", "tap-action", occurrence.Id, "after-tap", 0, SourceId, SourceName, state.Undo.BarrierVersion + 1), null) };
                    SetResources(true, 2, "tapped:source");
                } else if (command is UseOfferedAction { ActionId: "cast-hand", Card: "hand:pending" })
                    state = state with { Revision = state.Revision + 1, Decision = Priority("after-hand"),
                        Undo = state.Undo! with { BarrierVersion = state.Undo.BarrierVersion + 1, Offer = null } };
                else if (command is UndoAction undo && UndoFacts.Command(state) == undo) Restore();
                else throw new EngineCommandRejectedException("The authored provider did not offer this command.");
                Commands.Add(command); return Task.CompletedTask;
            }
        }
        private void SetResources(bool tapped, int mana, string key) => state = state with {
            Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                Mana = mana == 0 ? ImmutableDictionary<string, int>.Empty : ImmutableDictionary<string, int>.Empty.Add("C", mana),
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card with { Tapped = tapped, Selectable = !tapped, Key = key }).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray() };
        private void Restore() {
            state = state with { Revision = state.Revision + 1, BoardRevision = state.BoardRevision + 1, Decision = Priority("restored"),
                Undo = new(state.Undo!.BarrierVersion + 1, 1, null, new(1, "tap-action", "tap-execution", 0)) };
            SetResources(false, 0, "restored:source");
        }
        internal void ReplaceOffer() { lock (gate) {
            long next = state.Undo!.BarrierVersion + 1;
            state = state with { Revision = state.Revision + 1, Undo = state.Undo with { BarrierVersion = next,
                Offer = state.Undo.Offer! with { Id = "replacement-" + next, BarrierVersion = next } } };
        } }
        internal void Expire() { lock (gate) state = state with { Revision = state.Revision + 1,
            Undo = state.Undo! with { BarrierVersion = state.Undo.BarrierVersion + 1, Offer = null } }; }
        internal void AllowAutomaticPass() { lock (gate) state = state with { Revision = state.Revision + 1,
            Decision = state.Decision! with { Payload = new PriorityPayload(new("Go to combat", true), CanAutoPass: true) } }; }
        internal void AddUnverifiedPlayCard(string sourceZone) { lock (gate) state = state with { Revision = state.Revision + 1,
            PlayAccesses = sourceZone != "Exile" ? [] : [new(0, "Exile", HandId,
                [new("known-route", PlayAccessKind.Cast, SourceId, SourceName, "Cast", false)])],
            Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != sourceZone ? zone : zone with { Count = 1,
                    Cards = [new Card { Key = "hand:pending", VisualId = HandId, Name = "Pending artifact", Type = "Artifact", Selectable = false }]
                }).ToImmutableArray() }).ToImmutableArray() }; }
        internal void CompleteHandRead(bool offered) { lock (gate) handRead!.TrySetResult(
            offered ? pendingHandOffer! : pendingHandOffer! with { Actions = [] }); }
        internal void AddEarlierCompletion() { lock (gate) {
            var occurrence = new ExecutionOccurrence("earlier-spell", "earlier-spell", ExecutionKind.Spell, ExecutionStep.Complete) {
                Outcome = ExecutionOutcome.Resolved, ActorPlayerId = 0, SourceName = "Earlier completed spell" };
            state = state with { Revision = state.Revision + 1, BoardRevision = state.BoardRevision + 1,
                Executions = new(1, 3, [occurrence], [new(1, occurrence.Id, ExecutionStep.Stack), new(2, occurrence.Id, ExecutionStep.Resolution),
                    new(3, occurrence.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
                Activity = [new(1, state.Turn, state.PhaseKey, "resolved", 0, "Earlier spell resolved.", null, "Earlier completed spell") {
                    ExecutionId = occurrence.Id, ExecutionStep = ExecutionStep.Complete }] };
        } }
        internal void PublishSkippedRestoration() { lock (gate) Restore(); }
        internal void RefreshSameRestoration() { lock (gate) state = state with { Revision = state.Revision + 1 }; }
    }
}
