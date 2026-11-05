using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckTablePresence(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 900) }) {
            using var run = new InspectionRun(profile, "shared-inspection-" + size.X, size, actor: 1);
            run.Until(() => run.View.TableInspectionCues.Length == 1);
            var face = run.Frame.Cards.Single(card => card.Card.VisualId == "public-ward");
            bool Outline(Presentation.PresentationFrame frame) => frame.Drawing.Commands.OfType<DrawCommand.CardFrame>()
                .Any(command => command.Width == 2 && command.Pose.Center == face.Display.Center
                    && command.Pose.Size == face.Display.Size + new Vector2(7));
            check(Outline(run.Frame) && run.Port.Updates.All(update => update.Subject == null),
                "An observing seat renders another player's current public-card inspection at its real table pose at " + size.X);
            run.Capture("public-card-inspection");
            var beforePan = face.Display.Center;
            bool continuous = true;
            for (int index = 0; index < 8; index++) {
                run.Input(new(10, 30), elapsed: TimeSpan.FromMilliseconds(50), keys: new(Keys.W));
                face = run.Frame.Cards.Single(card => card.Card.VisualId == "public-ward");
                continuous &= run.View.TableInspectionCues.Length == 1 && Outline(run.Frame);
            }
            check(continuous && face.Display.Center != beforePan,
                "Remote attention follows the actual card pose continuously through keyboard camera movement at " + size.X);
            run.Input(new(10, 30), keys: new(Keys.Home)); run.Input(new(10, 30));
            face = run.Frame.Cards.Single(card => card.Card.VisualId == "public-ward");
            run.Input(run.PointFor(face.Hit));
            run.Input(run.PointFor(face.Hit), ButtonState.Pressed); run.Input(run.PointFor(face.Hit));
            run.Until(() => run.Frame.Cards.Any(card => card.Card.VisualId == "public-ward" && card.Focused));
            var reader = run.Frame.Cards.Single(card => card.Card.VisualId == "public-ward");
            check(run.Frame.Drawing.Commands.OfType<DrawCommand.CardFrame>().Any(command => command.Width == 2
                    && command.Pose.Center == reader.Rest.Center && command.Pose.Size == reader.Rest.Size + new Vector2(7))
                    && run.Rules.Commands.IsEmpty,
                "Opening a local reader leaves the remote inspection marker on the physical table card and issues no game action at " + size.X);
            run.Input(new(10, 30), active: false);
            check(run.View.TableInspectionCues.Length == 0,
                "Losing foreground immediately clears remote attention rather than showing activity beneath another application at " + size.X);
            run.Input(new(10, 30)); run.Input(new(10, 30));
            run.Until(() => run.View.TableInspectionCues.Length == 1);
            var next = run.Rules.State with { Revision = 101, BoardRevision = 11,
                PublicInspection = new("replacement-context", 11, 1),
                Players = run.Rules.State.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Cards = zone.Cards.Select(card => card.VisualId == "public-ward" ? card with { FaceDown = true } : card).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray() };
            run.Publish(next);
            check(run.View.TableInspectionCues.Length == 0 && !run.Frame.Cards.Any(card => card.Focused)
                    && run.Rules.Commands.IsEmpty,
                "A fresh concealed occurrence and replaced authority context remove both its reader and remote attention at " + size.X);
        }
        using (var run = new InspectionRun(profile, "inspection-publisher", new(1000, 740), actor: 0)) {
            var face = run.Frame.Cards.Single(card => card.Card.VisualId == "public-ward");
            run.Input(run.PointFor(face.Hit));
            run.Until(() => run.Port.Updates.Any(update => update.Subject?.Id == "public-ward"));
            check(run.Port.Updates.Last().Subject is { Kind: TableInspectionKind.BattlefieldCard, Id: "public-ward" }
                    && run.Port.Updates.Last().ContextId == "inspection-context" && run.Rules.Commands.IsEmpty,
                "The production pointer publishes the exact public occurrence under its current authority context without committing an action");
            int beforePan = run.Port.Updates.Count;
            for (int index = 0; index < 8; index++) run.Input(run.PointFor(run.Frame.Cards.Single(card => card.Card.VisualId == "public-ward").Hit),
                elapsed: TimeSpan.FromMilliseconds(50), keys: new(Keys.D));
            run.Until(() => run.Port.Updates.Count > beforePan, new(Keys.D));
            check(run.Port.Updates.Skip(beforePan).All(update => update.Subject == null),
                "Camera movement clears outgoing inspection even while the pointer remains over the moving public card");
            run.Input(new(10, 30), keys: new(Keys.Home)); run.Input(new(10, 30));
            var privateFace = run.Frame.Cards.First(card => card.Zone == "Hand");
            run.Input(run.PointFor(privateFace.Hit));
            run.Until(() => run.Port.Updates.Last().Subject == null);
            check(run.Port.Updates.All(update => update.Subject?.Id != privateFace.Card.VisualId),
                "Moving from a public permanent to a readable own-hand face clears public attention without exposing that private card");
            var settings = run.Frame.Hits.Last(hit => hit.Id == "settings");
            run.Input(run.PointFor(settings), ButtonState.Pressed); run.Input(run.PointFor(settings));
            int before = run.Port.Updates.Count;
            for (int index = 0; index < 30; index++) run.Input(new(10, 30), elapsed: TimeSpan.FromMilliseconds(50));
            check(run.Port.Updates.Count > before && run.Port.Updates.Skip(before).All(update => update.Subject == null) && run.Rules.Commands.IsEmpty,
                "Opening settings clears outgoing inspection while optional presence reads remain separate from gameplay");
        }
        using (var run = new InspectionRun(profile, "inspection-stack-receiver", new(1000, 740), actor: 1, stack: true)) {
            run.Until(() => run.View.TableInspectionCues is [{ Subject.Kind: TableInspectionKind.StackItem }]);
            var other = run.View.StackRows.Single(row => row.Id == "stack-a");
            check(!run.Frame.Drawing.Commands.OfType<DrawCommand.CardFrame>().Any(command => command.Width == 2
                    && command.Pose.Center == other.Bounds.Center.ToVector2() && command.Pose.Size == other.Bounds.Size.ToVector2()),
                "An unexpanded lower stack item cannot borrow the visible top ability's cue even when both share a source");
            run.Port.Subject = new(TableInspectionKind.StackItem, "stack-a");
            run.Until(() => run.View.TableInspectionCues is [{ Subject.Id: "stack-a" }]);
            var row = run.View.StackRows.Single(row => row.Id == "stack-a");
            check(run.Frame.Drawing.Commands.OfType<DrawCommand.CardFrame>().Any(command => command.Width == 2
                    && command.Pose.Center == row.Bounds.Center.ToVector2() && command.Pose.Size == row.Bounds.Size.ToVector2()),
                "Remote stack inspection marks the exact pending item rather than another ability of the same source");
        }
        using (var run = new InspectionRun(profile, "inspection-stack-publisher", new(1000, 740), actor: 0, stack: true)) {
            var hit = run.Frame.Hits.Last(hit => hit.Intent is MatchIntent.InspectPendingEffect { Id: "stack-a" });
            run.Input(run.PointFor(hit));
            run.Until(() => run.Port.Updates.Last().Subject is { Kind: TableInspectionKind.StackItem, Id: "stack-a" });
            check(run.Rules.Commands.IsEmpty,
                "Hovering a pending effect publishes its exact stack occurrence without selecting a target or activating its physical source");
        }
        using (var run = new InspectionRun(profile, "inspection-held-provider", new(1000, 740), actor: 0, hold: true)) {
            run.Until(() => run.Port.Updates.Count == 1);
            for (int index = 0; index < 350; index++) {
                if (index == 100) run.Publish(run.Rules.State with { Revision = 101, PublicInspection = new("other-context", 10, 0) });
                if (index == 200) run.Publish(run.Rules.State with { Revision = 102, PublicInspection = new("inspection-context", 10, 0) });
                run.Input(new(30 + index % 100, 120), elapsed: TimeSpan.FromMilliseconds(50));
            }
            bool accepted = run.View.SubmitCurrent(new PassPriority(run.Rules.State.Id, run.Rules.State.Decision!.Id));
            run.Until(() => run.Rules.Commands.Count == 1);
            check(run.Port.Updates.Count == 1 && accepted && run.Rules.Commands.Single() is PassPriority,
                "Seventeen seconds of pointer churn and A-to-B-to-A input scopes retain one actual presence task while the native game path still submits priority");
            // This is a controlled independent-port test, not a claim about
            // head-of-line latency on the actual shared TCP/IPC transport.
        }
    }

    private sealed class InspectionRun : IDisposable
    {
        internal readonly InspectionRules Rules;
        internal readonly InspectionPort Port;
        internal readonly PresentationRun App;
        internal TableApplication.Probe View => App.View;
        internal Presentation.PresentationFrame Frame => App.Frame;
        private readonly string directory;
        private Point pointer = new(10, 30);
        internal InspectionRun(string profile, string name, Point viewport, int actor, bool hold = false, bool stack = false)
        {
            var basis = Example();
            var state = basis with { Id = name, Revision = 100, BoardRevision = 10, ActivePlayerId = actor,
                PriorityPlayerId = actor, DecisionOwnerId = actor, ActionClass = "priority",
                Decision = actor == 0 ? basis.Decision! with { Id = "inspection-priority" } : null,
                PublicInspection = new("inspection-context", 10, actor),
                Stack = stack ? [new("Ability A", "Draw a card.", StackActionKind.ActivatedAbility, basis.Viewer!.Zone("Battlefield").Cards[0]) { Id = "stack-a", ControllerId = 1 },
                    new("Ability B", "Gain 1 life.", StackActionKind.ActivatedAbility, basis.Viewer!.Zone("Battlefield").Cards[0]) { Id = "stack-b", ControllerId = 1 }] : [] };
            Rules = new(state); Port = new(Rules, actor, hold, stack);
            directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true }.Save(directory);
            var setup = new InteractionScenarioEngine();
            App = new(TestTableServices.For(setup) with { Match = Rules, Presence = Port }, directory, state, viewport);
            App.EnableLiveTableStudy(); Input(pointer); Input(pointer);
        }
        internal void Input(Point at, ButtonState left = ButtonState.Released, bool active = true, TimeSpan? elapsed = null, KeyboardState? keys = null)
        {
            pointer = at;
            App.Tick(elapsed ?? TimeSpan.FromMilliseconds(10), new(at.X, at.Y, 0, left,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released), keys ?? new(), active: active);
        }
        internal Point PointFor(TableHit hit)
        {
            var mapping = RenderViewport.Fit(App.Viewport, Frame.DesignHeight);
            for (int y = 1; y < 10; y++) for (int x = 1; x < 10; x++) {
                var logical = new Point(hit.Bounds.Left + hit.Bounds.Width * x / 10, hit.Bounds.Top + hit.Bounds.Height * y / 10);
                var physical = mapping.ToPhysical(logical.ToVector2()).ToPoint();
                if (View.HitAt(mapping.ToLogical(physical))?.Id == hit.Id) return physical;
            }
            throw new InvalidOperationException("No exposed public inspection pointer point: " + hit.Id);
        }
        internal void Publish(GameSnapshot state) { Rules.State = state; App.QueueObservation(state); Input(pointer); }
        internal void Until(Func<bool> ready, KeyboardState? keys = null)
        {
            var timer = Stopwatch.StartNew();
            while (!ready()) {
                if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Public inspection did not reach its required state.");
                Input(pointer, keys: keys); Thread.Sleep(1);
            }
            Input(pointer, keys: keys);
        }
        internal void Capture(string name)
        {
            var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
            File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new {
                viewport = App.Viewport, pointer, frame = Frame.Observe(),
                drawing = new { Frame.Drawing.TextScale, Frame.Drawing.Scale,
                    commands = Frame.Drawing.Commands.Select(command => new {
                        type = command.GetType().Name, data = JsonSerializer.SerializeToElement(command, command.GetType(), options)
                    }).ToArray() }
            }, options));
        }
        public void Dispose() { App.Dispose(); Port.Held?.TrySetResult(new TablePresenceResult.Superseded()); }
    }
    private sealed class InspectionRules(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State = state;
        internal ConcurrentQueue<GameCommand> Commands = new();
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ExecuteAsync(GameCommand command)
        {
            if (!GameCommandPermissions.Allows(State, command)) throw new InvalidOperationException("The inspection fixture received an unscoped game command.");
            Commands.Enqueue(command); return Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
    private sealed class InspectionPort(InspectionRules rules, int actor, bool hold, bool stack) : ITablePresence
    {
        internal readonly ConcurrentQueue<TablePresenceUpdate> Updates = new();
        internal readonly TaskCompletionSource<TablePresenceResult>? Held = hold ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal TableInspectionSubject Subject = stack ? new(TableInspectionKind.StackItem, "stack-b") : new(TableInspectionKind.BattlefieldCard, "public-ward");
        public Task<TablePresenceResult> ExchangePresenceAsync(TablePresenceUpdate update, CancellationToken cancellation = default)
        {
            Updates.Enqueue(update);
            if (Held != null) return Held.Task;
            return Task.FromResult<TablePresenceResult>(new TablePresenceResult.Current(new(update.GameId, update.BoardRevision, update.Sequence,
                [new(actor, rules.State.PublicInspection?.Id ?? "", Subject, 1500)])));
        }
    }
}
