using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Actual pointer input, accepted metadata and held negative receipts. The port
// supplies permissions, not a simulated Magic engine; Java tests own rules passes.
internal static class ServerAutoPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (var availability in new[] { PriorityResponseAvailability.Available, PriorityResponseAvailability.Unknown })
            ArmedResponse(profile, size, availability, check);
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
            void Tick(TimeSpan elapsed = default, ButtonState button = ButtonState.Released, bool active = true, KeyboardState keys = default) {
                var physical = RenderViewport.Fit(size, run.View.DesignHeight).ToPhysical(pointer.ToVector2());
                run.Tick(elapsed, new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
                    button, default, default, default, default), keys, active: active);
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
            Tick(); run.View.SetAutomatic(true); run.Deactivate();
            Tick(TimeSpan.FromSeconds(3), active: false, keys: new(Keys.F, Keys.LeftControl, Keys.Enter, Keys.Space));
            Until(() => port.Commands.OfType<GrantPriorityContinuation>().Any() && !run.View.Busy, active: false);
            var firstGrant = port.Commands.OfType<GrantPriorityContinuation>().Single();
            check(run.View.PriorityContinuationReserved && port.Commands.OfType<GrantPriorityContinuation>().Count() == 1
                    && run.View.Automatic && !run.View.Held && firstGrant.GameId == initial.Id
                    && firstGrant.DecisionId == initial.Decision!.Id && firstGrant.Mode == PriorityContinuationMode.NoResponse
                    && port.State.PriorityContinuation?.GrantId == firstGrant.GrantId
                    && port.State.PriorityObservation?.Registered == true
                    && !port.Commands.Any(command => command is AutoPassPriority or PassForNow),
                "Previously enabled Auto starts one acknowledged scoped NoResponse policy while inactive after actual observer registration, without background shortcuts or a client pass loop at " + size + "; reduced=" + reduced);

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
            Tick(TimeSpan.FromSeconds(3), active: false);
            Until(() => port.Commands.OfType<AcknowledgePriorityObservation>().Count() > acknowledged && !run.View.Busy, active: false);
            for (int frame = 0; frame < 12; frame++) Tick(active: false);
            var credits = port.Commands.OfType<AcknowledgePriorityObservation>().ToArray();
            var currentGrant = port.Commands.OfType<GrantPriorityContinuation>().Last();
            check(credits.Length == acknowledged + 1 && credits.Last() == new AcknowledgePriorityObservation(unread.Id,
                        unread.BoardRevision, unread.PriorityObservation!.PublicActivitySequence)
                    && run.View.Match?.PriorityObservation?.AcknowledgedBoardRevision == unread.BoardRevision
                    && run.View.Match?.PriorityObservation?.AcknowledgedPublicActivitySequence == 16
                    && run.View.PriorityContinuationReserved && port.State.PriorityContinuation?.GrantId == currentGrant.GrantId
                    && currentGrant.Sequence > firstGrant.Sequence && port.Commands.OfType<GrantPriorityContinuation>().Count() == 2
                    && port.Commands.OfType<RevokePriorityContinuation>().Count() == 1,
                "Inactive presentation adopts and credits exactly its fresh board/activity prefix once while retaining acknowledged server consent without revoke/regrant churn");

            var required = port.State with { Revision = port.State.Revision + 1,
                Decision = new("background-server-required", new(), new ConfirmationPayload(new("Continue", true))),
                PriorityContinuation = port.State.PriorityContinuation! with { Status = PriorityContinuationStatus.Held, ReasonCode = "required-choice" } };
            port.State = required; run.QueueObservation(required);
            int beforeChoice = port.Commands.Count;
            Tick(active: false, keys: new(Keys.Enter, Keys.Space, Keys.F));
            for (int frame = 0; frame < 12; frame++) Tick(active: false);
            check(port.Commands.Count == beforeChoice && run.View.Automatic && !run.View.Held
                    && run.View.Match?.Decision?.Id == "background-server-required"
                    && run.View.Match.PriorityContinuation?.Status == PriorityContinuationStatus.Held,
                "A fresh required choice holds the server policy and remains unanswered while inactive shortcuts cannot change Auto or submit its answer");

            run.View.SetHeld(true); Tick(active: false);
            Until(() => port.Commands.OfType<RevokePriorityContinuation>().Count() == 2 && !run.View.Busy, active: false);
            for (int frame = 0; frame < 12; frame++) Tick(active: false);
            check(run.View.Held && !run.View.PriorityContinuationReserved
                    && port.State.PriorityContinuation?.Status == PriorityContinuationStatus.Revoked
                    && port.Commands.OfType<GrantPriorityContinuation>().Count() == 2
                    && !port.Commands.Any(command => command is AutoPassPriority or PassForNow)
                    && port.MaximumConcurrentCalls == 1,
                "Explicit Hold withdraws the current grant once while inactive; all permission-validated Execute/Observe calls stay serialized without a replacement policy or client pass");
        }
    }

    private static void ArmedResponse(string profile, Point size, PriorityResponseAvailability availability, Action<bool, string> check)
    {
        var path = Path.Combine(profile, "armed-auto-response-" + size.X + "-" + availability);
        Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, Sounds = false, Stops = [], TextScale = 1.3f }.Save(path);
        var initial = PriorityAbilitySmoke.Example();
        initial = initial with { Id = "armed-auto-ui", Revision = 1, BoardRevision = 1, Turn = 4,
            ActivePlayerId = initial.ViewerId, PriorityPlayerId = initial.ViewerId, DecisionOwnerId = initial.ViewerId,
            PhaseKey = "UPKEEP", Stack = [], Activity = [], Executions = new(1, 0, [], []),
            PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
            PriorityObservation = new(1, 0, 1, 0, true, false),
            Decision = new("armed-response", new(), new PriorityPayload(new("Pass priority", true), CanAutoPass: false) {
                ResponseAvailability = availability }) };
        var port = new Port(initial); var setup = new InteractionScenarioEngine();
        using var run = new PresentationRun(new(setup, setup, port, () => Task.CompletedTask), path, initial, size);
        void Tick(TimeSpan elapsed = default) => run.Tick(elapsed, default);
        Tick(); run.View.SetAutomatic(true); Tick(TimeSpan.FromSeconds(3));
        for (int frame = 0; frame < 20; frame++) Tick();
        check(run.View.PriorityContinuationReserved && !run.View.Busy && port.Commands.IsEmpty
                && run.Frame.Hits.Any(hit => hit.Id == "confirm"),
            "Visible Auto stays armed beside an immediately usable " + availability + " response without starting consent RPCs at " + size
                + "; reserved=" + run.View.PriorityContinuationReserved + "; busy=" + run.View.Busy
                + "; commands=" + port.Commands.Count + "; dock=" + run.View.DecisionDock?.Surface);
        var target = run.Frame.Hits.Single(hit => hit.Id == "confirm").Bounds.Center;
        var physical = RenderViewport.Fit(size, run.View.DesignHeight).ToPhysical(target.ToVector2());
        run.Tick(TimeSpan.Zero, new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
            ButtonState.Pressed, default, default, default, default));
        run.Tick(TimeSpan.Zero, new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
            ButtonState.Released, default, default, default, default));
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while ((run.View.Busy || port.Commands.IsEmpty) && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
        check(port.Commands.ToArray() is [PassPriority { DecisionId: "armed-response" }] && !run.View.Busy,
            "The physical response gesture submits once without an unnecessary grant or negative receipt at " + size + "; " + availability);
    }

    private sealed class Port(GameSnapshot initial) : IMatchConnection
    {
        private int concurrentCalls;
        internal int MaximumConcurrentCalls { get; private set; }
        internal GameSnapshot State = initial;
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsPriorityContinuation => true;
        public async Task ExecuteAsync(GameCommand command) {
            Enter();
            try {
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
                    case PassPriority:
                        State = State with { Revision = State.Revision + 1, Decision = null }; break;
                    default: throw new InvalidOperationException("A server-owned policy must not dispatch individual client passes.");
                }
            } finally { Interlocked.Decrement(ref concurrentCalls); }
        }
        public Task<GameSnapshot?> ObserveAsync() {
            Enter();
            try { return Task.FromResult<GameSnapshot?>(State); }
            finally { Interlocked.Decrement(ref concurrentCalls); }
        }
        private void Enter() {
            int active = Interlocked.Increment(ref concurrentCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            if (active <= 1) return;
            Interlocked.Decrement(ref concurrentCalls);
            throw new InvalidOperationException("Server Auto exchanges must stay serialized.");
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
