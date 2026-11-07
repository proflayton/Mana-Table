using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// Actual pointer input, accepted metadata and held negative receipts. The port
// supplies permissions, not a simulated Magic engine; Java tests own rules passes.
internal static class ServerAutoPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (bool reduced in new[] { false, true }) {
            var path = Path.Combine(profile, "server-auto-" + size.X + "-" + reduced);
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = reduced, Sounds = false, Stops = [], TextScale = 1.3f }.Save(path);
            var initial = PriorityAbilitySmoke.Example();
            int viewer = initial.ViewerId;
            initial = initial with { Id = "server-auto-ui", Revision = 1, BoardRevision = 1, Turn = 4,
                Status = "playing", ActivePlayerId = viewer, PriorityPlayerId = viewer, DecisionOwnerId = viewer,
                PhaseKey = "MAIN2", Stack = [], Activity = [], Executions = new(1, 0, [], []),
                PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
                PriorityObservation = new(1, 0, 0, 0, false, false),
                Decision = new("server-auto-decision", new(), new PriorityPayload(new("Pass priority", true), CanAutoPass: true) {
                    ResponseAvailability = PriorityResponseAvailability.Absent }) };
            var port = new Port(initial);
            var setup = new InteractionScenarioEngine();
            using var run = new PresentationRun(new(setup, setup, port, () => Task.CompletedTask), path, initial, size);
            Point pointer = new(20, 60);
            void Tick(TimeSpan elapsed = default, ButtonState button = ButtonState.Released, bool active = true) {
                var physical = RenderViewport.Fit(size, run.View.DesignHeight).ToPhysical(pointer.ToVector2());
                run.Tick(elapsed, new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
                    button, default, default, default, default), active: active);
            }
            void Until(Func<bool> predicate, bool active = true) {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!predicate() && DateTime.UtcNow < deadline) { Tick(active: active); Thread.Sleep(1); }
                if (!predicate()) throw new InvalidOperationException("Server Auto presentation did not settle: " + run.View.Error);
            }
            void Click(string id) {
                pointer = run.Frame.Hits.Single(hit => hit.Id == id).Bounds.Center;
                Tick(button: ButtonState.Pressed); Tick(); pointer = new(20, 60); Tick();
            }
            Tick(); run.View.SetAutomatic(true); Tick(TimeSpan.FromSeconds(3));
            Until(() => port.Commands.OfType<GrantPriorityContinuation>().Any() && !run.View.Busy);
            check(run.View.PriorityContinuationReserved && port.Commands.OfType<GrantPriorityContinuation>().Count() == 1
                    && !port.Commands.Any(command => command is AutoPassPriority or PassForNow),
                "Visible Auto installs one server policy after actual observer registration without a second client pass loop at " + size + "; reduced=" + reduced);

            var resolving = port.State with { Revision = port.State.Revision + 1, Status = "resolving", Decision = null };
            port.State = resolving; run.QueueObservation(resolving); Tick(); Tick();
            check(run.View.PriorityContinuationReserved && !port.Commands.OfType<RevokePriorityContinuation>().Any(),
                "Ordinary resolving observations retain the acknowledged server policy without revoke/regrant churn");
            var returned = resolving with { Revision = resolving.Revision + 1, Status = "playing", Decision = initial.Decision };
            port.State = returned; run.QueueObservation(returned); Tick(); Tick();
            check(run.Frame.Hits.Any(hit => hit.Id == "priority-take-control"), "Server Auto keeps its physical Take control target available");
            Click("priority-take-control");
            Until(() => port.Commands.OfType<RevokePriorityContinuation>().Any());
            check(run.View.Held && run.View.PriorityContinuationWithdrawing && port.Commands.OfType<GrantPriorityContinuation>().Count() == 1,
                "Physical Take control holds local Auto and shows an outstanding negative acknowledgement without issuing another grant");
            CompleteActionLabelChecks.Run(run.Frame, "auto", "Stopping…", check);
            port.Release.TrySetResult(); Until(() => !run.View.PriorityContinuationWithdrawing && !run.View.Busy);
            check(run.View.Match?.PriorityContinuation?.Status == PriorityContinuationStatus.Revoked && run.View.Held,
                "Only the actual negative receipt retires the server reservation, preserving the player's held turn");

            run.View.SetHeld(false); Tick(TimeSpan.FromSeconds(3));
            Until(() => port.Commands.OfType<GrantPriorityContinuation>().Count() == 2 && !run.View.Busy);
            int acknowledged = port.Commands.OfType<AcknowledgePriorityObservation>().Count();
            var unread = port.State with { Revision = port.State.Revision + 1, BoardRevision = port.State.BoardRevision + 1,
                Activity = Enumerable.Range(1, 16).Select(id => new ActivityEntry(id, 4, "MAIN2", "life", viewer,
                    "A public life change while the window was inactive.", null, null)).ToImmutableArray(),
                PriorityObservation = port.State.PriorityObservation! with { BoardRevision = port.State.BoardRevision + 1, PublicActivitySequence = 16 } };
            port.State = unread; run.QueueObservation(unread);
            Tick(active: false);
            Until(() => port.Commands.OfType<RevokePriorityContinuation>().Count() == 2, active: false);
            for (int frame = 0; frame < 12; frame++) Tick(active: false);
            check(port.Commands.OfType<AcknowledgePriorityObservation>().Count() == acknowledged,
                "Losing foreground withdraws automatic consent without acknowledging unseen presentation activity");
        }
    }

    private sealed class Port(GameSnapshot initial) : IMatchConnection
    {
        internal GameSnapshot State = initial;
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsPriorityContinuation => true;
        public async Task ExecuteAsync(GameCommand command) {
            if (!GameCommandPermissions.Allows(State, command)) throw new InvalidOperationException("Stale authored server Auto command.");
            Commands.Enqueue(command);
            switch (command) {
                case AcknowledgePriorityObservation ack:
                    State = State with { PriorityObservation = State.PriorityObservation! with { Registered = true,
                        AcknowledgedBoardRevision = ack.BoardRevision, AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } }; break;
                case GrantPriorityContinuation grant:
                    State = State with { Revision = State.Revision + 1, PriorityContinuation = new(grant.Sequence, grant.GrantId,
                        grant.Mode, PriorityContinuationStatus.Active, "", 0, grant.PhaseStops) }; break;
                case RevokePriorityContinuation:
                    await Release.Task;
                    State = State with { Revision = State.Revision + 1,
                        PriorityContinuation = State.PriorityContinuation! with { Status = PriorityContinuationStatus.Revoked } }; break;
                default: throw new InvalidOperationException("A server-owned policy must not dispatch individual client passes.");
            }
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
