using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Use the actual non-automated Initialize/reset paths with injected ports and
// a disposable profile. No production window, provider or update service exists.
internal static class StartupPlaybackChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (bool saved in new[] { false, true }) {
            string directory = Path.Combine(profile, "shipping-playback-" + saved + "-" + size.X);
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = saved, ReducedMotion = true, Sounds = false, Stops = ["MAIN1"] }.Save(directory);
            var initial = State("startup-" + saved + "-" + size.X);
            var setup = new ExperienceLabEngine(new ExperienceLabSession(ManaPaymentScenario.Id));
            var port = new Port(initial);
            var services = TestTableServices.For(setup) with { Match = port, SourceActions = null, Lab = null };
            using (var manual = new PresentationRun(services, directory, initial, size)) {
                manual.Tick(TimeSpan.Zero, new(20, 60, 0, default, default, default, default, default));
                check(!manual.View.Automatic && PlayPreferences.Load(directory).Automatic == saved && port.Commands.IsEmpty,
                    "The ordinary presentation harness keeps its manual default without rewriting saved playback at " + size + " / Auto " + saved);
            }
            using (var run = new PresentationRun(services, directory, initial, size, automated: false)) {
                Point pointer = new(20, 60);
                void Tick(ButtonState left = ButtonState.Released) => Step(run, pointer, left);
                void Until(Func<bool> ready, string step) => Pump(run, () => Tick(), ready, step);
                void Click(string id) {
                    pointer = run.Frame.Hits.Single(hit => hit.Id == id).Bounds.Center;
                    Tick(ButtonState.Pressed); Tick(); pointer = new(20, 60); Tick();
                }
                try {
                    Tick(); Tick();
                    check(run.View.Automatic == saved && !run.View.Held && PlayPreferences.Load(directory).Automatic == saved
                            && port.Commands.IsEmpty && port.Observations == 0,
                        "Shipping Initialize restores saved Auto " + saved + " without a setter, provider poll or gameplay command at " + size);
                    var priorHold = run.Frame.Hits.Single(hit => hit.Id == "hold");
                    Click("hold");
                    check(run.View.Held && run.View.Automatic == saved && PlayPreferences.Load(directory).Automatic == saved && port.Commands.IsEmpty,
                        "A real temporary-hold gesture preserves the saved startup preference at " + size + " / Auto " + saved);
                    run.QueueObservation(initial with { Revision = 2, BoardRevision = 2, Status = "finished", Result = "Victory",
                        Decision = null, DecisionOwnerId = null, PriorityPlayerId = null });
                    Until(() => run.View.Overlay == "result" && run.Frame.Hits.Any(hit => hit.Id == "return"), "finished result");
                    Click("return");
                    Until(() => run.View.Match == null && !run.View.Busy && !run.View.ActivityOpen, "return to setup");
                    check(run.View.Automatic == saved && !run.View.Held && PlayPreferences.Load(directory).Automatic == saved
                            && !run.View.CurrentHit(priorHold) && !run.View.Activate(priorHold) && port.Commands.IsEmpty,
                        "The real finished-table Return resets temporary Hold, restores saved Auto " + saved + " and retires old controls at " + size);
                } finally { Save(directory, "return", run, port); }
            }
            var reopenedState = State("reopened-" + saved + "-" + size.X);
            var reopenedPort = new Port(reopenedState);
            using var reopened = new PresentationRun(services with { Match = reopenedPort }, directory, reopenedState, size, automated: false);
            Point reopenedPointer = new(20, 60);
            void ReopenedTick(ButtonState left = ButtonState.Released) => Step(reopened, reopenedPointer, left);
            try {
                ReopenedTick(); ReopenedTick();
                check(reopened.View.Automatic == saved && !reopened.View.Held && reopened.View.Match?.Id == reopenedState.Id
                        && PlayPreferences.Load(directory).Automatic == saved && reopenedPort.Commands.IsEmpty,
                    "Reopening the same disposable profile restores Auto " + saved + " for a fresh match without a runtime setter at " + size);
                if (saved) {
                    reopenedPointer = reopened.Frame.Hits.Single(hit => hit.Id == "resume-phase-stop").Bounds.Center;
                    ReopenedTick(ButtonState.Pressed); ReopenedTick(); reopenedPointer = new(20, 60);
                    reopened.Tick(TimeSpan.FromSeconds(4), new(20, 60, 0, default, default, default, default, default));
                    Pump(reopened, () => ReopenedTick(), () => reopenedPort.Commands.Count == 1, "saved Auto first dispatch");
                    for (int i = 0; i < 12; i++) reopened.Tick(TimeSpan.Zero, new(20, 60, 0, default, default, default, default, default));
                    check(reopenedPort.Commands.ToArray() is [AutoPassPriority { GameId: var game, DecisionId: var decision }]
                            && game == reopenedState.Id && decision == reopenedState.Decision!.Id
                            && reopenedPort.MaximumActive == 1 && reopenedPort.Active == 1,
                        "Saved Auto dispatches once with the reopened match's exact authority while the first response is held at " + size);
                    reopenedPort.ReleaseWithNoDecision();
                    Pump(reopened, () => ReopenedTick(), () => !reopened.View.Busy && reopened.View.Match?.Decision == null, "saved Auto receipt");
                    check(reopenedPort.Commands.Count == 1 && reopenedPort.Active == 0 && reopened.View.Match?.Id == reopenedState.Id
                            && reopened.View.Automatic && PlayPreferences.Load(directory).Automatic,
                        "The saved-Auto command receipt settles once without replay or changing the reopened preference at " + size);
                } else {
                    reopened.Tick(TimeSpan.FromSeconds(4), new(20, 60, 0, default, default, default, default, default));
                    check(reopenedPort.Commands.IsEmpty && reopenedPort.Observations == 0,
                        "Saved full control remains manual after the normal presentation beat at " + size);
                }
            } finally { reopenedPort.ReleaseWithNoDecision(); Save(directory, "reopen", reopened, reopenedPort); }
        }
    }

    private static void Step(PresentationRun run, Point pointer, ButtonState left)
    {
        var physical = RenderViewport.Fit(run.Viewport, run.View.DesignHeight).ToPhysical(pointer.ToVector2());
        run.Tick(TimeSpan.FromSeconds(1d / 60), new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y),
            0, left, default, default, default, default));
    }
    private static void Pump(PresentationRun run, Action tick, Func<bool> ready, string step)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready() && deadline.Elapsed < TimeSpan.FromSeconds(5)) { tick(); Thread.Yield(); }
        if (!ready()) throw new InvalidOperationException("Shipping playback startup stopped at " + step + ": " + run.View.Error);
    }
    private static GameSnapshot State(string id)
    {
        var original = PresentationSmoke.Example();
        return original with { Id = id, Revision = 1, BoardRevision = 1, Status = "playing", ActivePlayerId = original.ViewerId,
            PriorityPlayerId = original.ViewerId, DecisionOwnerId = original.ViewerId, Turn = 9, PhaseKey = "MAIN1", Phase = "Main phase",
            Combat = null, Stack = [], Activity = [],
            Decision = new(id + "-decision", new(), new PriorityPayload(new("Pass priority", true), CanAutoPass: true)),
            Players = original.Players.Select(player => player with { LandPlaysUsed = 1, LandPlayLimit = 1,
                Zones = player.Zones.Select(zone => zone with { Cards = [], TopCard = null, Count = zone.Name == "Library" ? 80 : 0 }).ToImmutableArray()
            }).ToImmutableArray() };
    }
    private static void Save(string directory, string stage, PresentationRun run, Port port)
        => File.WriteAllText(Path.Combine(directory, "presentation-" + stage + ".json"), JsonSerializer.Serialize(new {
            stage, run.View.Automatic, run.View.Held, run.ElapsedTicks, run.View.Error, frame = run.View.Frame?.Observe(),
            port.Observations, port.Active, port.MaximumActive, commands = port.Commands.Select(command => new {
                kind = command.GetType().Name, command.GameId, command.DecisionId }),
            scope = "Actual non-automated startup and Return/reset with isolated injected ports, fresh profile and controlled input/time; not live Forge or human acceptance"
        }, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class Port(GameSnapshot initial) : IMatchConnection
    {
        private GameSnapshot state = initial;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<DecisionCommand> Commands { get; } = new();
        private int observations, active, maximum, released;
        internal int Observations => Volatile.Read(ref observations);
        internal int Active => Volatile.Read(ref active);
        internal int MaximumActive => Volatile.Read(ref maximum);
        public Task<GameSnapshot?> ObserveAsync() { Interlocked.Increment(ref observations); return Task.FromResult<GameSnapshot?>(Volatile.Read(ref state)); }
        public async Task ExecuteAsync(GameCommand command) {
            if (command is not AutoPassPriority pass || !GameCommandPermissions.Allows(Volatile.Read(ref state), pass))
                throw new InvalidOperationException("The startup fixture received foreign or unavailable Auto authority.");
            int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count); Commands.Enqueue(pass);
            try { await release.Task; } finally { Interlocked.Decrement(ref active); }
        }
        internal void ReleaseWithNoDecision() {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            var current = Volatile.Read(ref state);
            Volatile.Write(ref state, current with { Revision = current.Revision + 1, Decision = null, PriorityPlayerId = null, DecisionOwnerId = null });
            release.TrySetResult();
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
