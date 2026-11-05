using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckQuickMana(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 900) }) {
            using (var run = new QuickManaRun(profile, "quick-mana-direct-" + size.X, size, trace)) {
                var family = run.Frame.Scene!.Cohort(QuickManaRun.First);
                if (family?.MemberIds.Count != 2) throw new InvalidOperationException("The fixture must display one two-copy land cohort.");
                string first = family.Front.Id, second = family.MemberIds.Single(id => id != first);
                var original = run.BeginReading(first);
                string firstSourceKey = run.Card(first).Key;
                check(run.Reads.Count == 1 && run.Commands.Count == 0 && run.Focused(first) && run.OriginalStillNames(original, first),
                    "One land click reads its actual source while its original grouped-copy anchor remains reachable at " + size.X);
                run.Click(original);
                bool submitted = run.WaitFor(() => run.Commands.Count >= 1 && !run.View.Busy);
                var command = run.Commands.FirstOrDefault();
                check(submitted && run.Commands.Count == 1 && command is UseOfferedAction { ManaOutput: { } output } offered
                        && offered.Card == run.Card(first).Key && offered.ActionId == run.OfferId(first)
                        && output.Colors.Count == 1 && output.Colors.GetValueOrDefault("G") == 1 && run.View.Held,
                    "The second click on the original land submits exactly its current tap and declared green output while taking control at " + size.X);
                run.Double(original);
                run.Pump();
                check(run.Commands.Count == 1 && run.Menu.Length == 0,
                    "A provider ACK and unchanged readback do not establish consumption or authorize replaying the quick tap at " + size.X);
                var samePublicationOther = run.BeginReading(second);
                run.Click(samePublicationOther); run.Until(() => !run.View.Busy);
                run.Double(original); run.Until(() => !run.View.Busy); run.Pump();
                check(run.Commands.Count == 1 && run.Menu.Length == 0,
                    "An unchanged admitted publication blocks A-to-B-to-A quick taps across different physical land copies at " + size.X);
                var next = run.Rules.State with { Revision = run.Rules.State.Revision + 1,
                    Decision = run.Rules.State.Decision! with { Id = "next-priority" },
                    Players = run.Rules.State.Players.Select(player => player with {
                        Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                            Key = "next:" + card.VisualId, Tapped = card.VisualId == first
                        }).ToImmutableArray() }).ToImmutableArray()
                    }).ToImmutableArray() };
                run.Publish(next);
                var other = run.BeginReading(second);
                run.Click(other); run.Until(() => run.Commands.Count == 2 && !run.View.Busy);
                check(run.Commands.Last() is UseOfferedAction otherCommand && otherCommand.Card == run.Card(second).Key
                        && otherCommand.Card != firstSourceKey && run.Card(first).Tapped && !run.Card(second).Tapped,
                    "A fresh scoped gesture chooses the other physical land copy rather than reusing the earlier family source at " + size.X);
            }
            using (var run = new QuickManaRun(profile, "quick-mana-menu-" + size.X, size, trace)) {
                run.Sources.Outputs = [Bundle(("G", 1)), Bundle(("U", 1))];
                var original = run.BeginReading(QuickManaRun.First);
                run.Click(original); run.Until(() => run.Menu.Length == 2);
                run.CaptureChooser("pointer-chooser");
                var menu = run.Menu;
                check(run.Commands.Count == 0 && menu.All(hit => hit.Bounds.Left >= 0 && hit.Bounds.Top >= 0
                        && hit.Bounds.Right <= 1600 && hit.Bounds.Bottom <= run.Frame.DesignHeight)
                        && !menu[0].Bounds.Intersects(menu[1].Bounds)
                        && Vector2.Distance(menu[0].Bounds.Center.ToVector2(), run.Logical(original).ToVector2()) < 450,
                    "Two outputs of one mana ability open two bounded pointer choices; the opening release selects neither at " + size.X);
                var selected = run.PointFor(menu[1]);
                check(run.View.HitAt(run.Logical(selected)) is { } current && current.Id == menu[1].Id && current.Scope == menu[1].Scope,
                    "The actual physical chooser pixel resolves to its foreground choice instead of the land underneath at " + size.X);
                run.Click(selected); run.Until(() => run.Commands.Count == 1 && !run.View.Busy);
                check(run.Commands.Single() is UseOfferedAction { ManaOutput: { } output } chosen
                        && chosen.ActionId == run.OfferId(QuickManaRun.First) && output.Colors.Count == 1 && output.Colors.GetValueOrDefault("U") == 1,
                    "Choosing the second pointer row submits that exact blue bundle and opaque offered action at " + size.X);
            }
        }
        CheckQuickManaBoundaries(profile, check, trace);
        CheckQuickManaRetirement(profile, check, trace);
        CheckQuickManaMenuRetirement(profile, check, trace);
        CheckUnmarkedManaOffers(profile, check, trace);
    }

    private static void CheckQuickManaBoundaries(string profile, Action<bool, string> check, List<object> trace)
    {
        foreach (bool expired in new[] { false, true }) {
            using var run = new QuickManaRun(profile, "quick-mana-window-" + expired, new(1540, 900), trace);
            var at = run.BeginReading(QuickManaRun.First);
            var deadline = run.Elapsed + TimeSpan.FromSeconds(TableApplication.QuickManaDoubleClickSeconds)
                + (expired ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero);
            run.ClickAt(at, deadline); run.Pump();
            if (!expired) run.Until(() => run.Commands.Count == 1);
            check(run.Commands.Count == (expired ? 0 : 1),
                "The actual double-click window admits its exact inclusive boundary and rejects a later second release: " + expired);
        }
        using (var run = new QuickManaRun(profile, "quick-mana-simultaneous-bundle", new(1540, 900), trace)) {
            run.Sources.Outputs = [Bundle(("G", 1), ("U", 1))];
            var at = run.BeginReading(QuickManaRun.First); run.Click(at);
            run.Until(() => run.Commands.Count == 1);
            check(run.Menu.Length == 0 && run.Commands.Single() is UseOfferedAction { ManaOutput: { Colors.Count: 2 } output }
                    && output.Colors.GetValueOrDefault("G") == 1 && output.Colors.GetValueOrDefault("U") == 1,
                "One simultaneous two-color bundle remains one direct mana choice rather than two competing outputs");
        }
        using (var run = new QuickManaRun(profile, "quick-mana-two-actions", new(1540, 900), trace)) {
            run.Sources.ExtraAction = true;
            var at = run.BeginReading(QuickManaRun.First); run.Click(at); run.Until(() => run.Menu.Length == 2);
            run.Click(run.PointFor(run.Menu[1])); run.Until(() => run.Commands.Count == 1);
            check(run.Commands.Single() is UseOfferedAction { ManaOutput: { } output } chosen
                    && chosen.ActionId == run.OfferId(QuickManaRun.First) + ":other" && output.Colors.Count == 1 && output.Colors.GetValueOrDefault("R") == 1,
                "Different safe abilities retain their own opaque action IDs when flattened into pointer mana options");
        }
    }

    private static void CheckQuickManaRetirement(string profile, Action<bool, string> check, List<object> trace)
    {
        foreach (string change in new[] { "revision", "key", "owner", "tapped", "private", "required-payment", "foreground", "drag" }) {
            using var run = new QuickManaRun(profile, "quick-mana-retire-" + change, new(1000, 740), trace);
            var at = run.BeginReading(QuickManaRun.First);
            if (change is "foreground" or "drag") {
                if (change == "foreground") {
                    run.App.Deactivate(); run.Input(at, active: false); run.Input(at, ButtonState.Pressed); run.Input(at);
                } else {
                    run.Input(at, ButtonState.Pressed);
                    run.Input(at + new Point(30, 30), ButtonState.Pressed);
                    run.Input(at + new Point(30, 30), keys: new(Keys.Escape)); run.Input(at);
                }
                run.Click(at);
            } else {
                var state = run.Rules.State;
                var next = change switch {
                    "revision" => state with { Revision = state.Revision + 1 },
                    "owner" => state with { DecisionOwnerId = 1, PriorityPlayerId = 1 },
                    "required-payment" => state with { Decision = state.Decision! with {
                        Payload = new PaymentPayload(new("Pay", false), new("Cancel", true)) { PayerId = 1 }
                    } },
                    _ => state with { Players = state.Players.Select(player => player with {
                        Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.VisualId != QuickManaRun.First ? card : change switch {
                            "key" => card with { Key = card.Key + ":replaced" },
                            "tapped" => card with { Tapped = true },
                            _ => card with { FaceDown = true }
                        }).ToImmutableArray() }).ToImmutableArray()
                    }).ToImmutableArray() }
                };
                run.Publish(next);
                // A required payment retains its ordinary single-click contract;
                // this control retires the captured priority gesture without a new click.
                if (change != "required-payment") run.Click(at);
            }
            run.Pump();
            run.TraceBoundary(change);
            check(run.Commands.Count == 0 && run.Menu.Length == 0,
                "A captured quick-mana gesture loses authority after the accepted " + change + " boundary");
        }
        using (var run = new QuickManaRun(profile, "quick-mana-held-query", new(1540, 900), trace)) {
            run.Sources.HoldNext = true;
            var at = run.ClickOriginal(QuickManaRun.First);
            run.Until(() => run.Reads.Count == 1); run.Click(at);
            var held = run.Reads.Single();
            var state = run.Rules.State;
            run.Publish(state with { Revision = state.Revision + 1 });
            held.Completion.TrySetResult(held.Catalog);
            run.Until(() => run.Diagnostics.Any(row => row.Stage == ClientDiagnosticStage.SourceReadDiscarded));
            check(run.Commands.Count == 0 && run.Menu.Length == 0,
                "A late catalog cannot complete a retired double-click intent or dispatch after the ordinary publication changes");
        }
        using (var run = new QuickManaRun(profile, "quick-mana-busy", new(1540, 900), trace)) {
            run.Rules.Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var at = run.BeginReading(QuickManaRun.First); run.Click(at); run.Until(() => run.Commands.Count == 1);
            run.Double(at); run.Pump();
            check(run.View.Busy && run.Commands.Count == 1 && run.Menu.Length == 0,
                "An in-flight accepted source command admits no repeated quick taps or competing chooser");
            run.Rules.Ack.TrySetResult();
        }
        using (var run = new QuickManaRun(profile, "quick-mana-read-timeout", new(1540, 900), trace)) {
            run.Sources.HoldNext = true;
            var at = run.ClickOriginal(QuickManaRun.First); run.Until(() => run.Reads.Count == 1); run.Click(at);
            run.Reads.Single().Completion.TrySetException(new TimeoutException("Controlled source read timeout."));
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "source-actions-retry"));
            run.Advance(TimeSpan.FromSeconds(1)); run.Pump();
            check(run.Commands.Count == 0 && run.Menu.Length == 0 && run.Reads.Count == 1,
                "A source-read timeout retires the pending shortcut without dispatch, implicit retry or reusing its catalog");
        }
        using (var run = new QuickManaRun(profile, "quick-mana-foreign-source", new(1540, 900), trace)) {
            var state = run.Rules.State;
            var own = state.Viewer!.Zone("Battlefield").Cards;
            run.Publish(state with { Players = state.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Count = player.Id == 1 ? own.Length : 0, Cards = player.Id == 1 ? own : []
                }).ToImmutableArray()
            }).ToImmutableArray() });
            var at = run.BeginReading(QuickManaRun.First); run.Click(at); run.Pump();
            check(run.Commands.Count == 0 && run.Menu.Length == 0,
                "Inspecting a foreign battlefield land cannot turn even a returned descriptor into a local quick-mana gesture");
        }
    }

    private static void CheckQuickManaMenuRetirement(string profile, Action<bool, string> check, List<object> trace)
    {
        foreach (string boundary in new[] { "escape", "outside", "pressed-old-option" }) {
            using var run = new QuickManaRun(profile, "quick-mana-menu-retire-" + boundary, new(1000, 740), trace);
            run.Sources.Outputs = [Bundle(("G", 1)), Bundle(("U", 1))];
            var anchor = run.BeginReading(QuickManaRun.First); run.Click(anchor); run.Until(() => run.Menu.Length == 2);
            var old = run.Menu[1]; var at = run.PointFor(old);
            if (boundary == "escape") { run.Input(at, keys: new(Keys.Escape)); run.Input(at); run.Click(at); }
            else if (boundary == "outside") { run.Click(new(2, 2)); run.Click(at); }
            else {
                run.Input(at, ButtonState.Pressed);
                var state = run.Rules.State;
                run.Sources.Outputs = [Bundle(("R", 1))];
                run.Publish(state with { Revision = state.Revision + 1 });
                run.Input(at);
            }
            run.Pump();
            check(run.Commands.Count == 0 && !run.Frame.Hits.Any(hit => hit.Id == old.Id && hit.Scope == old.Scope),
                "A pointer mana option cannot survive " + boundary + " or dispatch through its retired geometry");
        }
    }

    private static void CheckUnmarkedManaOffers(string profile, Action<bool, string> check, List<object> trace)
    {
        using var run = new QuickManaRun(profile, "quick-mana-unmarked-cost", new(1540, 900), trace);
        run.Sources.Marked = false;
        var at = run.BeginReading(QuickManaRun.First); run.Click(at); run.Pump();
        check(run.Commands.Count == 0 && run.Menu.Length == 0,
            "One legacy or paid destructive activation cannot become quick mana from its land type, offer count or display text");
        if (!run.Frame.Hits.Any(hit => hit.Id == "read-card-action"))
            throw new InvalidOperationException("The refused shortcut must retain its current explicit reader action.");
        run.Click(run.PointFor(run.Frame.Hits.Last(hit => hit.Id == "read-card-action")));
        run.Until(() => run.Commands.Count == 1);
        check(run.Commands.Single() is UseOfferedAction { ManaOutput: null },
            "An unmarked offer still requires and retains its separate explicit manual reader action without choosing costs or output");
    }

    private static ManaProductionAlternative Bundle(params (string Color, int Count)[] colors)
        => new(colors.ToImmutableDictionary(row => row.Color, row => row.Count));

    private sealed class QuickManaRun : IDisposable
    {
        internal const string First = "quick-land-a", Second = "quick-land-b";
        internal readonly QuickManaRules Rules;
        internal readonly QuickManaSources Sources;
        internal readonly PresentationRun App;
        internal readonly ConcurrentQueue<ClientDiagnostic> Diagnostics = new();
        private readonly List<object> trace;
        private Point pointer = new(20, 20);
        private string previousTrace = "";
        internal PresentationFrame Frame => App.Frame;
        internal TableApplication.Probe View => App.View;
        internal TimeSpan Elapsed => App.Elapsed;
        internal IReadOnlyList<QuickManaRead> Reads => Sources.Reads.ToArray();
        internal IReadOnlyList<GameCommand> Commands => Rules.Commands.ToArray();
        internal TableHit[] Menu => Frame.Hits.Where(hit => hit.Id.StartsWith("quick-mana-choice:", StringComparison.Ordinal)).ToArray();

        internal QuickManaRun(string profile, string name, Point viewport, List<object> trace)
        {
            this.trace = trace;
            var basis = Example();
            var state = basis with { Id = name, Revision = 4, BoardRevision = 4, PriorityPlayerId = 0, DecisionOwnerId = 0,
                Decision = basis.Decision! with { Id = "quick-priority" },
                Players = basis.Players.Select(player => player with { Mana = ImmutableDictionary<string, int>.Empty,
                    Zones = player.Zones.Select(zone => zone with { Count = zone.Name == "Library" ? 80 : player.Id == 0 && zone.Name == "Battlefield" ? 2 : 0,
                        Cards = player.Id == 0 && zone.Name == "Battlefield" ? new[] { First, Second }.Select(id => new Card {
                            Key = "current:" + id, VisualId = id, CombatId = id, Name = "Forest", Type = "Basic Land - Forest",
                            Text = "{T}: Add {G}.", Selectable = true
                        }).ToImmutableArray() : [] }).ToImmutableArray()
                }).ToImmutableArray() };
            Rules = new(state); Sources = new(Rules);
            string directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(directory);
            var setup = new InteractionScenarioEngine();
            App = new(TestTableServices.For(setup) with { Match = Rules, SourceActions = Sources, Diagnostics = Diagnostics.Enqueue }, directory, state, viewport);
            Input(pointer); Input(pointer);
        }
        internal Card Card(string id) => CardViews.InZones(Rules.State).Single(row => row.Card.VisualId == id).Card;
        internal string OfferId(string id) => Rules.State.Decision!.Id + ":mana:" + id;
        internal bool Focused(string id) => Frame.Cards.Any(card => card.Id == id && card.Focused);
        internal Point Logical(Point physical) => RenderViewport.Fit(App.Viewport, Frame.DesignHeight).ToLogical(physical);
        internal bool OriginalStillNames(Point physical, string id) => View.HitAt(Logical(physical))?.Card is { } card
            && card.VisualId == id && card.Key == Card(id).Key;
        internal Point PointFor(TableHit hit, Rectangle? area = null)
        {
            var bounds = area ?? hit.Bounds;
            var mapping = RenderViewport.Fit(App.Viewport, Frame.DesignHeight);
            for (int y = 0; y < 7; y++) for (int x = 0; x < 7; x++) {
                var logical = new Point(bounds.Left + (x * 2 + 1) * bounds.Width / 14, bounds.Top + (y * 2 + 1) * bounds.Height / 14);
                var physical = mapping.ToPhysical(logical.ToVector2()).ToPoint();
                var actual = View.HitAt(mapping.ToLogical(physical));
                if (actual?.Id == hit.Id && actual.Scope == hit.Scope && hit.Contains(mapping.ToLogical(physical))) return physical;
            }
            throw new InvalidOperationException("No exposed physical quick-mana target: " + hit.Id);
        }
        internal Point ClickOriginal(string id)
        {
            var face = Frame.Cards.Single(card => card.Id == id);
            var hit = Frame.Hits.Last(hit => hit.Card?.VisualId == id && hit.Zone == "Battlefield");
            var at = PointFor(hit, face.Rest.Bounds); Click(at); return at;
        }
        internal Point BeginReading(string id)
        {
            var at = ClickOriginal(id);
            Until(() => Focused(id) && Frame.Hits.Any(hit => hit.Id == "read-card-action"));
            return at;
        }
        internal void Click(Point at) { Input(at, ButtonState.Pressed, elapsed: TimeSpan.FromMilliseconds(1)); Input(at, elapsed: TimeSpan.FromMilliseconds(1)); }
        internal void Double(Point at) { Click(at); Click(at); }
        internal void ClickAt(Point at, TimeSpan release) { App.UpdateAt(release - TimeSpan.FromMilliseconds(1), Mouse(at, ButtonState.Pressed)); pointer = at; App.UpdateAt(release, Mouse(at)); }
        internal void Advance(TimeSpan duration) => Input(pointer, elapsed: duration);
        internal void Input(Point at, ButtonState left = ButtonState.Released, KeyboardState? keys = null, bool active = true, TimeSpan? elapsed = null)
        {
            pointer = at; App.Tick(elapsed ?? TimeSpan.Zero, Mouse(at, left), keys ?? new(), active: active);
            string stamp = App.ElapsedTicks + ":" + left + ":" + Commands.Count + ":" + Reads.Count + ":" + Menu.Length + ":" + View.Match?.Revision;
            if (stamp != previousTrace) {
                trace.Add(new { quickMana = Rules.State.Id, time = App.Elapsed.TotalSeconds, commands = Commands.Count,
                    commandDetails = Commands.Select(CommandTrace).ToArray(), reads = Reads.Count, menu = Menu.Length,
                    currentDecision = View.Match?.Decision?.Id, revision = View.Match?.Revision });
                previousTrace = stamp;
            }
        }
        private static object CommandTrace(GameCommand command) => new {
            type = command.GetType().Name, command.GameId, decisionId = (command as DecisionCommand)?.DecisionId,
            sourceKey = command switch { UseOfferedAction offered => offered.Card, ActivateCard activate => activate.Card, _ => null },
            actionId = (command as UseOfferedAction)?.ActionId,
            manaOutput = (command as UseOfferedAction)?.ManaOutput?.Colors
        };
        internal void TraceBoundary(string boundary)
        {
            var accepted = View.Match;
            var source = accepted?.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
                .SingleOrDefault(card => card.VisualId == First);
            var hit = View.HitAt(Logical(pointer));
            trace.Add(new { quickManaBoundary = boundary, currentDecision = accepted?.Decision?.Id,
                revision = accepted?.Revision, boardRevision = accepted?.BoardRevision,
                body = source == null ? null : new { source.VisualId, source.Key, source.FaceDown, source.Tapped, source.Selectable },
                hitId = hit?.Id, hitSource = hit?.Card?.VisualId,
                commands = Commands.Select(CommandTrace).ToArray(), menu = Menu.Length });
        }
        internal void CaptureChooser(string name)
        {
            var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
            File.WriteAllText(Path.Combine(View.Profile, name + ".json"), JsonSerializer.Serialize(new {
                viewport = App.Viewport, pointer, frame = Frame.Observe(),
                drawing = new { Frame.Drawing.TextScale, Frame.Drawing.Scale,
                    commands = Frame.Drawing.Commands.Select(command => new {
                        type = command.GetType().Name, data = JsonSerializer.SerializeToElement(command, command.GetType(), options)
                    }).ToArray() }
            }, options));
        }
        private static MouseState Mouse(Point at, ButtonState left = ButtonState.Released) => new(at.X, at.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        internal void Publish(GameSnapshot next) { Rules.State = next; App.QueueObservation(next); Pump(); }
        internal void Pump() { for (int i = 0; i < 8; i++) { Input(pointer); Thread.Yield(); } }
        internal void Until(Func<bool> ready)
        {
            if (!WaitFor(ready)) throw new TimeoutException("Quick-mana input did not reach its required state.");
        }
        internal bool WaitFor(Func<bool> ready)
        {
            var wall = Stopwatch.StartNew();
            while (!ready()) { if (wall.Elapsed > TimeSpan.FromSeconds(5)) return false; Input(pointer); Thread.Yield(); }
            return true;
        }
        public void Dispose() { Rules.Ack?.TrySetResult(); foreach (var read in Reads) read.Completion.TrySetCanceled(); App.Dispose(); }
    }

    private sealed class QuickManaRules(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State = state;
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        internal TaskCompletionSource? Ack;
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ExecuteAsync(GameCommand command)
        {
            Commands.Enqueue(command);
            if (!GameCommandPermissions.Allows(State, command)) throw new InvalidOperationException("A quick-mana command crossed its current authored decision scope.");
            return Ack?.Task ?? Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => Task.FromException(new InvalidOperationException("This input fixture never concedes."));
    }
    private sealed record QuickManaRead(SourceActionSet Catalog, TaskCompletionSource<SourceActionSet> Completion);
    private sealed class QuickManaSources(QuickManaRules rules) : ISourceActions
    {
        internal readonly ConcurrentQueue<QuickManaRead> Reads = new();
        internal ImmutableArray<ManaProductionAlternative> Outputs = [Bundle(("G", 1))];
        internal bool HoldNext, ExtraAction, Marked = true;
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string key)
        {
            var state = rules.State;
            var card = CardViews.FindActionSource(state, key)?.Card ?? throw new InvalidOperationException("The source query lost its current physical occurrence.");
            var action = new SourceAction(decisionId + ":mana:" + card.VisualId, "activate", Marked ? "{T}: Add mana" : "Pay 1, tap and sacrifice: Add {G}") {
                QuickMana = Marked ? new(Outputs) : null
            };
            ImmutableArray<SourceAction> actions = ExtraAction ? ImmutableArray.Create(action, new SourceAction(action.Id + ":other", "activate", "Another declared mana ability") {
                QuickMana = new([Bundle(("R", 1))])
            }) : [action];
            var catalog = new SourceActionSet(gameId, decisionId, key, card.VisualId, actions) { Revision = state.Revision };
            var completion = new TaskCompletionSource<SourceActionSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            Reads.Enqueue(new(catalog, completion));
            if (!HoldNext) completion.SetResult(catalog); else HoldNext = false;
            return completion.Task;
        }
    }
}
