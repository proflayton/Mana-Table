using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Authored disclosed checkpoints exercise the production gestures/session pump.
// They are presentation evidence, not a second implementation of Magic rules.
internal static class FiniteResolutionPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckGroupedAnnouncements(check);
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            CheckServerGrant(profile, viewport, check);
            using (var run = new Journey(profile, "complete-" + viewport.X, viewport)) {
                var accepted = new HashSet<long>();
                run.Run.Accepted += (state, _, _) => accepted.Add(state.Revision);
                check(run.Has("finite-resolution-decline") && run.Text.Contains("Decline 55 + resolve queue")
                        && run.View.Match?.Decision?.Payload is PaymentPayload,
                    "The actual optional source payment offers a finite Decline these 55 independently of a single payment answer at " + viewport);
                var decline = run.Hit("finite-resolution-decline");
                CompleteActionLabelChecks.Run(run.Run.Frame, "finite-resolution-decline", "Decline 55 + resolve queue", check);
                if (run.Has("mana-cancel")) CompleteActionLabelChecks.Run(run.Run.Frame, "mana-cancel", "Decline once", check);
                check(decline.Bounds.Height >= 40 && run.Run.View.HitAt(decline.Bounds.Center)?.Id == decline.Id,
                    "The finite optional alternative owns a directly reachable source action rather than tiny cancellation chrome at " + viewport);
                run.Click(decline.Id);
                run.Capture("armed");
                check(run.View.FiniteResolution is { IsActive: true, DeclinesOptionalPayments: true }
                        && run.Has("stack-resolution-stop") && run.Text.Contains("0 / 55 completed")
                        && run.Text.Contains("Panther Idol") && run.Port.Commands.IsEmpty,
                    "The deliberate negative batch retains its source, initial count and immediate Take control before any cost is declined at " + viewport);
                run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.View.FiniteResolution == null, "all 55 terminal receipts");
                run.Capture("completed");
                check(run.Port.Commands.Count == 109 && run.Port.Commands.OfType<CancelPayment>().Count() == 55
                        && run.Port.Commands.OfType<PassPriority>().Count() == 54
                        && run.Port.Commands.All(command => command is CancelPayment or PassPriority)
                        && run.View.FiniteResolutionProgress is { CapturedCount: 55, CompletedCount: 55, RemainingCount: 0, ResolvedCount: 55, DeclinedCount: 0 }
                        && accepted.Count == 109 && run.View.Match?.Activity.Length == 55,
                    "One physical batch gesture submits only fresh ordinary negative/payment and priority answers, retains every publication and reports all 55 real terminal outcomes at " + viewport);
                check(run.Text.Contains("55 / 55 completed") && run.Run.Elapsed.TotalSeconds < 20,
                    "The completed source workload has a visible final count without 55 separate full resolution dwell intervals at " + viewport);
            }

            using (var run = new Journey(profile, "interleaved-" + viewport.X, viewport, mixed: true)) {
                var accepted = new HashSet<long>();
                run.Run.Accepted += (state, _, _) => accepted.Add(state.Revision);
                check(run.Has("finite-resolution-decline") && run.Text.Contains("Decline 55 + resolve queue"),
                    "Interleaved Nadir Kraken costs and Toothy triggers offer explicit separate negative answers and whole-current-queue priority consent at " + viewport);
                run.Click("finite-resolution-decline");
                var intent = run.View.FiniteResolution;
                check(intent?.Progress.CapturedCount == 110 && intent.DeclineProgress?.CapturedCount == 55
                        && run.Text.Contains("Source: 0 / 55 completed") && run.Text.Contains("Queue: 0 / 110 completed"),
                    "The armed mixed queue distinguishes 55 source decisions from 110 total effects before any answer at " + viewport);
                run.Capture("armed-interleaved"); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.View.FiniteResolution == null, "all 110 interleaved terminal receipts");
                run.Capture("completed-interleaved");
                check(run.Port.Commands.Count == 164 && run.Port.Commands.OfType<CancelPayment>().Count() == 55
                        && run.Port.Commands.OfType<PassPriority>().Count() == 109 && accepted.Count == 164
                        && intent?.Progress is { CapturedCount: 110, CompletedCount: 110, DeclinedCount: 0, ResolvedCount: 110 }
                        && intent.DeclineProgress is { CapturedCount: 55, CompletedCount: 55, ResolvedCount: 55 }
                        && run.View.Match?.Activity.Length == 110,
                    "One explicit mixed gesture crosses original interleaved priority windows, declines only Nadir costs, and retains every distinct Toothy resolution and accepted observation at " + viewport);
            }

            using (var run = new Journey(profile, "different-cost-" + viewport.X, viewport, mixed: true, interrupt: "other-payment")) {
                run.Click("finite-resolution-decline"); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.View.FiniteResolution == null, "a different captured source needs its own optional answer");
                check(run.Port.Commands.Count == 1 && run.View.FiniteResolutionProgress?.CompletedCount == 1
                        && run.View.Match?.Decision?.Payload is PaymentPayload { OptionalDecline.SourceCardId: "toothy" }
                        && run.View.Match.Decision.Context.Source?.Card?.Name == "Toothy, Imaginary Friend",
                    "Whole-queue priority consent never declines an original interleaved source's different optional cost; its actual source and current choice take attention at " + viewport);
            }

            using (var run = new Journey(profile, "reading-stop-" + viewport.X, viewport, holdFirst: true)) {
                run.Click("finite-resolution-decline");
                var source = run.Run.Frame.Hits.Last(hit => hit.Card?.VisualId == "idol");
                run.Move(source.Bounds.Center);
                for (int i = 0; i < 180; i++) run.Tick();
                check(run.View.FiniteResolution is { IsActive: true, IsPaused: true }
                        && run.Port.Commands.IsEmpty && run.Has("stack-resolution-stop"),
                    "Hovering the actual source pauses the finite continuation for reading while Take control remains reachable at " + viewport);
                run.Move(new(20, 60)); run.Until(() => run.Port.Started.Task.IsCompleted, "first in-flight decline");
                check(run.Has("stack-resolution-stop") && run.View.Busy,
                    "Take control remains a current local gesture while an already sent optional answer is in flight at " + viewport);
                run.Click("stack-resolution-stop");
                run.Port.Release.TrySetResult();
                run.Until(() => !run.View.Busy, "reconcile stopped answer");
                check(run.View.FiniteResolution == null && run.View.Held && run.Port.Commands.Count == 1
                        && run.View.Match?.Executions?.Occurrences.Count(item => item.Step == ExecutionStep.Complete) == 1
                        && run.View.FiniteResolutionProgress?.CompletedCount == 1,
                    "Take control stops further work and still presents the one already committed answer without inventing an undo at " + viewport);
            }

            foreach (bool required in new[] { false, true }) {
                using var run = new Journey(profile, (required ? "required-" : "foreign-") + viewport.X, viewport,
                    interrupt: required ? "required" : "response");
                run.Click("finite-resolution-decline"); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.View.FiniteResolution == null, "outside work interrupts");
                check(run.Port.Commands.Count == 1 && run.View.FiniteResolutionProgress?.CompletedCount == 1
                        && (required ? run.View.Match?.Decision?.Payload is NumberPayload && run.Has("number-more")
                            : run.View.Match?.Stack.FirstOrDefault()?.Id == "foreign-response"
                                && run.View.AnnouncementId?.Contains("foreign-execution", StringComparison.Ordinal) == true),
                    "An unmatched required choice or new response takes attention after one actual outcome and cannot inherit repeated negative consent at " + viewport + "; required=" + required);
            }

            using (var run = new Journey(profile, "unreported-" + viewport.X, viewport, certified: false))
                check(!run.Has("finite-resolution-decline") && run.Port.Commands.IsEmpty,
                    "An optional-looking legacy payment without engine provenance keeps its single current choices and cannot acquire bulk authority at " + viewport);
        }
    }

    private static void CheckServerGrant(string profile, Point viewport, Action<bool, string> check)
    {
        foreach (string mode in new[] { "history-stop-delayed", "complete", "control", "history-stop" }) {
            bool manual = mode == "control", stopped = mode.StartsWith("history-stop", StringComparison.Ordinal),
                delayedPublication = mode == "history-stop-delayed";
            string path = Path.Combine(profile, "finite-server-" + mode + "-" + viewport.X);
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(path);
            using var port = new ServerPort(automatic: !manual, revokeReason: stopped ? "history-overflow" : "",
                holdStopPublication: delayedPublication);
            var setup = new InteractionScenarioEngine();
            using var run = new PresentationRun(new(setup, setup, port, () => Task.CompletedTask), path, port.State, viewport);
            Point pointer = new(20, 60);
            var checkpoints = new List<ServerCheckCheckpoint>();
            void Capture(string stage) => File.WriteAllText(Path.Combine(path, stage + ".json"), JsonSerializer.Serialize(new {
                frame = run.Frame.Observe(), progress = ObserveProgress(run.View.FiniteResolutionProgress),
                stopReason = run.View.FiniteResolution?.StopReason, busy = run.View.Busy,
                publishedRevision = run.View.Match?.Revision, publishedGrant = run.View.Match?.FiniteStackYield,
                providerRevision = port.State.Revision, providerGrant = port.State.FiniteStackYield,
                commands = port.Commands.Select(command => command.GetType().Name), checkpoints
            }, new JsonSerializerOptions { WriteIndented = true }));
            void Tick(ButtonState left = ButtonState.Released) {
                var point = RenderViewport.Fit(viewport, run.View.DesignHeight).ToPhysical(pointer.ToVector2());
                run.Tick(TimeSpan.FromSeconds(1d / 60), new((int)MathF.Ceiling(point.X), (int)MathF.Ceiling(point.Y), 0,
                    left, default, default, default, default));
                var text = string.Join(" ", run.Frame.Drawing.Commands.Select(item => item switch {
                    DrawCommand.Text line => line.Value, DrawCommand.CenterText line => line.Value, _ => "" }));
                var current = new ServerCheckCheckpoint(run.Elapsed.TotalSeconds, run.View.Match?.Revision, run.View.Busy,
                    run.View.FiniteResolution?.IsActive, run.View.FiniteResolution?.StopReason,
                    run.View.FiniteResolutionProgress?.CompletedCount, run.View.Match?.FiniteStackYield?.Status,
                    run.View.Match?.FiniteStackYield?.ReasonCode, text.Contains("Resolution stopped"),
                    text.Contains("Earlier results could not be confirmed"));
                if (checkpoints.Count == 0 || (current with { Time = checkpoints[^1].Time }) != checkpoints[^1]) checkpoints.Add(current);
            }
            void Until(Func<bool> ready, string step) {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
                if (!ready()) { Capture("timeout"); throw new InvalidOperationException("Finite server presentation stopped at " + step + "; " + run.View.Error); }
            }
            Tick(); Tick();
            pointer = run.Frame.Hits.Single(hit => hit.Id == "stack-resolution-arm").Bounds.Center;
            Tick(ButtonState.Pressed); Tick(); pointer = new(20, 60);
            check(port.Commands.IsEmpty && run.View.FiniteResolution?.Progress.CapturedCount == 55,
                "A server-capable source queue still presents its initial readable beat before sending any finite yield grant at " + viewport + "; manual=" + manual);
            if (delayedPublication) {
                Until(() => port.StopPublicationReady.Task.IsCompleted, "provider's held terminal observation");
                var intent = run.View.FiniteResolution!;
                // Pump takes a bounded FIFO prefix. Release the worker while
                // processing that prefix so its terminal receipt is pending
                // for the next update, but its shared policy is already ended.
                run.View.Post(() => {
                    port.ReleaseStopPublication.TrySetResult();
                    if (!SpinWait.SpinUntil(() => !intent.IsActive, TimeSpan.FromSeconds(2)))
                        throw new InvalidOperationException("The held server stop did not end its worker policy.");
                });
                Tick(); Capture("deferred-worker-before-publication");
                check(!intent.IsActive && run.View.Match?.FiniteStackYield?.Status != FiniteStackYieldStatus.Revoked,
                    "The controlled worker stop precedes its accepted revoked publication without inventing another authoritative snapshot at " + viewport);
                var stop = run.Frame.Hits.SingleOrDefault(hit => hit.Id == "stack-resolution-stop");
                check(run.View.ResolvingCapturedStack && run.View.FiniteResolution != null
                        && run.View.FiniteResolutionProgress is { CompletedCount: 0, RemainingCount: 55 }
                        && stop != null && run.View.HitAt(stop.Bounds.Center)?.Id == stop.Id
                        && !run.Frame.Hits.Any(hit => hit.Id is "confirm" or "stack-resolution-arm" or "finite-resolution-decline"),
                    "Pending publication retains its readable captured-queue view and Take control, exposes only accepted zero progress and no normal commit controls at " + viewport);
                var current = run.View.Match!;
                bool submitted = run.View.SubmitCurrent(new PassPriority(current.Id, current.Decision!.Id));
                check(!submitted && run.View.FiniteResolution != null && port.Commands.Count == 1,
                    "A retained manual action cannot commit or retire the pending receipt presentation before its accepted snapshot at " + viewport);
            }
            if (stopped) {
                Until(() => run.View.FiniteResolution == null && !run.View.Busy, "explicit provider stop");
                string text = string.Join(" ", run.Frame.Drawing.Commands.Select(item => item switch {
                    DrawCommand.Text line => line.Value, DrawCommand.CenterText line => line.Value, _ => "" }));
                Capture("history-stop-before-assert");
                check(run.View.FiniteResolutionProgress is { CapturedCount: 55, CompletedCount: 1, RemainingCount: 54 }
                        && text.Contains("Resolution stopped") && text.Contains("Earlier results could not be confirmed")
                        && port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked,
                    "A provider history-bound stop reports one proven outcome and a readable stopped reason, never a successful completion inferred from the remaining queue at " + viewport);
            } else if (!manual) {
                var progress = new HashSet<int>();
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while ((run.View.FiniteResolution != null || run.View.Busy) && DateTime.UtcNow < deadline) {
                    Tick(); progress.Add(run.View.FiniteResolutionProgress?.CompletedCount ?? 0); Thread.Sleep(1);
                }
                File.WriteAllText(Path.Combine(path, "stable-grant.json"), JsonSerializer.Serialize(new {
                    progress = ObserveProgress(run.View.FiniteResolutionProgress), observedCounts = progress,
                    stopReason = run.View.FiniteResolution?.StopReason, error = run.View.Error,
                    commands = port.Commands.Select(command => command.GetType().Name), revokedAt = port.RevokedAt,
                    activityCount = run.View.Match?.Activity.Length, frame = run.Frame.Observe()
                }, new JsonSerializerOptions { WriteIndented = true }));
                check(run.View.FiniteResolutionProgress is { CompletedCount: 55, ResolvedCount: 55 }
                        && port.Commands.OfType<GrantFiniteStackYield>().Count() == 1
                        && port.RevokedAt.All(count => count == 55) && !port.Commands.OfType<PassPriority>().Any()
                        && progress.Count > 2 && run.View.Match?.Activity.Length == 55,
                    "Captured progress pulses retain one stable server grant, show intermediate real counts and preserve all 55 terminal receipts without per-seat network pass commands at " + viewport);
            } else {
                Until(() => run.View.Match?.FiniteStackYield?.Status == FiniteStackYieldStatus.Active, "advertised granted queue");
                var current = run.View.Match!;
                bool sent = run.View.SubmitCurrent(new PassPriority(current.Id, current.Decision!.Id));
                Tick();
                string text = string.Join(" ", run.Frame.Drawing.Commands.Select(item => item switch {
                    DrawCommand.Text line => line.Value, DrawCommand.CenterText line => line.Value, _ => "" }));
                Until(() => !run.View.Busy && port.State.FiniteStackYield?.Status == FiniteStackYieldStatus.Revoked, "withdraw manual grant");
                for (int i = 0; i < 30; i++) Tick();
                check(!sent && text.Contains("Taking control") && run.View.Held
                        && port.Commands.OfType<RevokeFiniteStackYield>().Count() == 1
                        && !port.Commands.OfType<PassPriority>().Any() && run.View.FiniteResolution == null,
                    "A manual commit during a live server yield visibly takes control, withdraws the exact grant and requires a fresh action instead of silently dropping or replaying the old command at " + viewport);
            }
            File.WriteAllText(Path.Combine(path, "final.json"), JsonSerializer.Serialize(new { frame = run.Frame.Observe(),
                progress = ObserveProgress(run.View.FiniteResolutionProgress), commands = port.Commands.Select(command => command.GetType().Name) },
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed record ServerCheckCheckpoint(double Time, long? Revision, bool Busy, bool? IntentActive, string? StopReason,
        int? Completed, FiniteStackYieldStatus? GrantStatus, string? GrantReason, bool StoppedVisible, bool HistoryReasonVisible);

    private sealed class ServerPort(bool automatic, string revokeReason = "", bool holdStopPublication = false) : IMatchConnection, IDisposable
    {
        private readonly object gate = new();
        private GameSnapshot state = ServerProjection(0, 1, null);
        internal GameSnapshot State { get { lock (gate) return state; } }
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal ConcurrentQueue<int> RevokedAt { get; } = new();
        internal TaskCompletionSource StopPublicationReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseStopPublication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(GameCommand command)
        {
            lock (gate) {
                if (!GameCommandPermissions.Allows(state, command) || command is not (GrantFiniteStackYield or RevokeFiniteStackYield))
                    throw new InvalidOperationException("Finite server checkpoint received an unauthorized manual or stale command.");
                Commands.Enqueue(command);
                if (command is RevokeFiniteStackYield) RevokedAt.Enqueue(state.Executions!.Occurrences.Count(item => item.Step == ExecutionStep.Complete));
                state = command switch {
                    GrantFiniteStackYield grant => state with { Revision = state.Revision + 1,
                        FiniteStackYield = new(grant.GrantId, FiniteStackYieldStatus.Active, grant.ExecutionIds, grant.ExecutionIds, 1, 1) },
                    RevokeFiniteStackYield => state with { Revision = state.Revision + 1,
                        FiniteStackYield = state.FiniteStackYield! with { Status = FiniteStackYieldStatus.Revoked } },
                    _ => state
                };
            }
            return Task.CompletedTask;
        }
        public async Task<GameSnapshot?> ObserveAsync()
        {
            await Task.Delay(15);
            GameSnapshot observed;
            bool held;
            lock (gate) {
                if (automatic && state.FiniteStackYield is { Status: FiniteStackYieldStatus.Active } grant) {
                    int completed = state.Executions!.Occurrences.Count(item => item.Step == ExecutionStep.Complete) + 1;
                    state = ServerProjection(completed, state.Revision + 1, grant);
                    if (revokeReason.Length > 0) state = state with {
                        FiniteStackYield = state.FiniteStackYield! with { Status = FiniteStackYieldStatus.Revoked, ReasonCode = revokeReason }
                    };
                }
                observed = state;
                held = holdStopPublication && state.FiniteStackYield is { Status: FiniteStackYieldStatus.Revoked, ReasonCode: "history-overflow" };
            }
            if (held) { StopPublicationReady.TrySetResult(); await ReleaseStopPublication.Task; }
            return observed;
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
        public void Dispose() => ReleaseStopPublication.TrySetResult();
    }

    private static GameSnapshot ServerProjection(int completed, long revision, FiniteStackYieldState? grant)
    {
        var state = Projection(55, completed, paying: false, revision: revision);
        var source = state.Players[0].Zones[0].Cards[0] with { Name = "Chasm Skulker", Type = "Creature",
            Text = "Whenever you draw a card, put a +1/+1 counter on Chasm Skulker." };
        var occurrences = state.Executions!.Occurrences.Select(item => item with { SourceName = source.Name,
            Outcome = item.Step == ExecutionStep.Complete ? ExecutionOutcome.Resolved : null }).ToImmutableArray();
        return state with {
            Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => player.Id == 0 && zone.Name == "Battlefield"
                ? zone with { Cards = [source] } : zone).ToImmutableArray() }).ToImmutableArray(),
            Stack = state.Stack.Select(item => new StackItem(source.Name, source.Text, item.Kind, source) {
                Id = item.Id, ExecutionId = item.ExecutionId, ControllerId = item.ControllerId, Repetition = item.Repetition }).ToImmutableArray(),
            Executions = state.Executions with { Occurrences = occurrences,
                Transitions = state.Executions.Transitions.Select(item => item with { Outcome = ExecutionOutcome.Resolved }).ToImmutableArray() },
            Activity = state.Activity.Select(item => item with { CardName = source.Name, Message = "Draw trigger resolved" }).ToImmutableArray(),
            FiniteStackYield = grant == null ? new("", FiniteStackYieldStatus.Idle, [], [], 1, 1)
                : grant with { Status = completed == 55 ? FiniteStackYieldStatus.Completed : FiniteStackYieldStatus.Active,
                    RemainingExecutionIds = state.Stack.Select(item => item.ExecutionId).ToImmutableArray() }
        };
    }

    private static void CheckGroupedAnnouncements(Action<bool, string> check)
    {
        var state = Projection(55, paying: false);
        var transitions = state.Executions!.Occurrences.Select((item, index) => new ExecutionTransition(index + 1, item.Id, ExecutionStep.Stack)).ToImmutableArray();
        state = state with { Executions = state.Executions with { LastSequence = 55, Transitions = transitions } };
        var before = state with { Stack = [], Executions = null };
        var announcements = new StackArrivalPresentation();
        announcements.Observe(before, state, 0, executions: transitions);
        check(announcements.PendingCount == 55, "Ordinary presentation retains an individual announcement for each unconsented trigger occurrence");
        announcements.BeginFinite(state.Stack.Select(item => (item.ExecutionId, item.Repetition!.GroupKey)), .1);
        check(announcements.PendingCount == 1 && announcements.Current(.1)?.CapturedCount == 55 && !announcements.ProgressReady(.1),
            "Explicit finite consent consolidates only its exact repeated executions into one initial readable source beat");
        var settled = Projection(55, completed: 1, paying: false);
        settled = settled with { Executions = settled.Executions! with { LastSequence = 56,
            Transitions = [new(56, "idol-execution-0", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }] } };
        announcements.Observe(state, settled, .2, executions: settled.Executions!.Transitions);
        check(announcements.PendingCount == 1 && !announcements.ProgressReady(.2),
            "A captured terminal receipt updates retained history without starting another full per-trigger announcement");
        var foreign = Foreign(settled, required: false);
        announcements.Observe(settled, foreign, .3, executions: foreign.Executions!.Transitions.Where(item => item.ExecutionId == "foreign-execution").ToImmutableArray());
        check(announcements.Current(.3)?.ExecutionId == "foreign-execution" && announcements.PendingCount == 2,
            "A new real spell is promoted immediately ahead of the grouped captured beat and keeps its own announcement");
        announcements.EndFinite();
    }

    private sealed class Journey : IDisposable
    {
        internal PresentationRun Run { get; }
        internal TableApplication.Probe View => Run.View;
        internal BatchPort Port { get; }
        private Point pointer = new(20, 60);
        internal string Text => string.Join(" ", Run.Frame.Drawing.Commands.Select(item => item switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null
        }).Where(value => value != null));
        internal Journey(string profile, string name, Point viewport, bool holdFirst = false, string interrupt = "", bool certified = true, bool mixed = false)
        {
            string path = Path.Combine(profile, "finite-resolution-" + name); Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(path);
            Port = new(holdFirst, interrupt, certified, mixed);
            var setup = new InteractionScenarioEngine();
            Run = new(new(setup, setup, Port, () => Task.CompletedTask), path, Port.State, viewport);
            Tick(); Tick();
            if (certified && !Has("finite-resolution-decline"))
                File.WriteAllText(Path.Combine(path, "missing-offer.txt"), "candidate="
                    + (FiniteResolutionIntent.TryArm(Port.State, Port.State.Stack.Select(item => item.Id), ((PaymentPayload)Port.State.Decision!.Payload).OptionalDecline)?.Progress.CapturedCount.ToString() ?? "none")
                    + "\nUIdecision=" + View.Match?.Decision?.Payload.GetType().Name + "\nHits=" + string.Join(',', Run.Frame.Hits.Select(hit => hit.Id)) + "\n" + Text);
        }
        internal void Move(Point logical) { pointer = logical; Tick(); }
        internal void Capture(string name) => File.WriteAllText(Path.Combine(View.Profile, name + ".json"),
            JsonSerializer.Serialize(new { frame = Run.Frame.Observe(), progress = ObserveProgress(View.FiniteResolutionProgress),
                commands = Port.Commands.Select(command => command.GetType().Name) }, new JsonSerializerOptions { WriteIndented = true }));
        internal bool Has(string id) => Run.Frame.Hits.Any(hit => hit.Id == id);
        internal TableHit Hit(string id) => Run.Frame.Hits.Last(hit => hit.Id == id);
        internal void Tick(ButtonState left = ButtonState.Released)
        {
            var point = RenderViewport.Fit(Run.Viewport, View.DesignHeight).ToPhysical(pointer.ToVector2());
            Run.Tick(TimeSpan.FromSeconds(1d / 60), new((int)MathF.Ceiling(point.X), (int)MathF.Ceiling(point.Y), 0,
                left, default, default, default, default));
        }
        internal void Click(string id)
        {
            pointer = Hit(id).Bounds.Center; Tick(ButtonState.Pressed); Tick();
        }
        internal void Until(Func<bool> ready, string step)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
            if (!ready()) throw new InvalidOperationException("Finite presentation stopped at " + step + "; " + View.Error
                + "; commands=" + Port.Commands.Count + "; time=" + Run.Elapsed.TotalSeconds + "; busy=" + View.Busy
                + "; active=" + View.FiniteResolution?.IsActive + "; paused=" + View.FiniteResolution?.IsPaused
                + "; reason=" + View.FiniteResolution?.StopReason + "; progress=" + View.FiniteResolutionProgress
                + "; dock=" + View.DecisionDock?.PauseReason + "; decision=" + View.Match?.Decision?.Payload.GetType().Name);
        }
        public void Dispose() { Port.Release.TrySetResult(); Run.Dispose(); }
    }

    private static object? ObserveProgress(FiniteResolutionProgress? progress) => progress == null ? null : new {
        progress.CapturedCount, progress.CompletedCount, progress.RemainingCount, progress.UnknownCount,
        Outcomes = progress.Outcomes.Select(pair => new { Outcome = pair.Key.ToString(), Count = pair.Value })
    };

    private sealed class BatchPort(bool holdFirst, string interrupt, bool certified, bool mixed = false) : IMatchConnection
    {
        private readonly object gate = new();
        private GameSnapshot state = mixed ? MixedProjection(0, true, 1) : Projection(55, paying: true, certified: certified);
        internal GameSnapshot State { get { lock (gate) return state; } }
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(GameCommand command)
        {
            lock (gate) {
                if (!GameCommandPermissions.Allows(state, command) || command is not (CancelPayment or PassPriority))
                    throw new InvalidOperationException("Finite authored transport received an unexpected or stale command.");
                Commands.Enqueue(command);
            }
            if (holdFirst && Commands.Count == 1) { Started.TrySetResult(); await Release.Task; }
            lock (gate) {
                int completed = state.Executions!.Occurrences.Count(item => item.Step == ExecutionStep.Complete);
                bool resolvedToothy = mixed && command is PassPriority && completed % 2 == 1;
                if (command is CancelPayment || resolvedToothy) completed++;
                var next = mixed ? MixedProjection(completed, completed < 110 && command is PassPriority && !resolvedToothy, state.Revision + 1)
                    : Projection(55, completed, paying: completed < 55 && command is PassPriority,
                        revision: state.Revision + 1, certified: certified);
                state = completed == 1 && command is CancelPayment && interrupt.Length > 0
                    ? interrupt == "other-payment" ? OtherPayment(next) : Foreign(next, interrupt == "required") : next;
            }
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }

    private static GameSnapshot MixedProjection(int completed, bool paying, long revision)
    {
        var state = Projection(110, completed, paying, revision);
        var nadir = new Card { Key = "current-kraken", VisualId = "kraken", CombatId = "kraken", Name = "Nadir Kraken", Type = "Creature",
            Text = "Whenever you draw a card, you may pay {1}. If you do, put a +1/+1 counter on Nadir Kraken and create a 1/1 blue Tentacle creature token." };
        var toothy = new Card { Key = "current-toothy", VisualId = "toothy", CombatId = "toothy", Name = "Toothy, Imaginary Friend", Type = "Creature",
            Text = "Whenever you draw a card, put a +1/+1 counter on Toothy, Imaginary Friend." };
        Card Source(int index) => index % 2 == 0 ? nadir : toothy;
        string Group(int index) => index % 2 == 0 ? "kraken-optional-draw" : "toothy-draw-counter";
        int Index(string id) => int.Parse(id.Split('-')[^1]);
        var occurrences = state.Executions!.Occurrences.Select((item, index) => item with {
            SourceCardId = Source(index).VisualId, SourceName = Source(index).Name,
            Outcome = index < completed ? ExecutionOutcome.Resolved : null
        }).ToImmutableArray();
        var stack = state.Stack.Select(item => {
            int index = Index(item.ExecutionId); var source = Source(index);
            return new StackItem(source.Name, source.Text, StackActionKind.TriggeredAbility, source) {
                Id = item.Id, ExecutionId = item.ExecutionId, ControllerId = 0, Repetition = new(Group(index), source.VisualId)
            };
        }).ToImmutableArray();
        var decision = state.Decision;
        if (paying && decision?.Payload is PaymentPayload payment) decision = decision with {
            Context = new(Source: new(Card: nadir, Zone: "Battlefield")),
            Payload = payment with { OptionalDecline = new("kraken-optional-draw", "one-generic", "idol-execution-" + completed,
                nadir.VisualId, PaymentPurpose.EffectCost, 0, false, "kraken-offer-" + completed,
                completed == 0 ? "" : "kraken-offer-" + (completed - 2)) }
        };
        return state with { Stack = stack, Decision = decision,
            Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name == "Battlefield" && player.Id == 0
                ? zone with { Count = 2, Cards = [nadir, toothy] } : zone).ToImmutableArray() }).ToImmutableArray(),
            Executions = state.Executions with { Occurrences = occurrences,
                Transitions = occurrences.Take(completed).Select((item, index) => new ExecutionTransition(index + 1, item.Id, ExecutionStep.Complete) {
                    Outcome = item.Outcome }).ToImmutableArray() },
            Activity = occurrences.Take(completed).Select((item, index) => new ActivityEntry(index + 1, 7, "MAIN1", "resolved", 0,
                index % 2 == 0 ? "Optional Nadir Kraken payment declined" : "Toothy trigger resolved", item.SourceCardId, item.SourceName) {
                    ExecutionId = item.Id, ExecutionStep = ExecutionStep.Complete }).ToImmutableArray()
        };
    }

    private static GameSnapshot Projection(int count, int completed = 0, bool paying = true, long revision = 1, bool certified = true)
    {
        var source = new Card { Key = "current-idol", VisualId = "idol", CombatId = "idol", Name = "Panther Idol", Type = "Artifact",
            Text = "Whenever an opponent draws a card, you may pay {1}. If you do, draw a card.", GamePieceType = "card", ManaCost = "{5}" };
        var occurrences = Enumerable.Range(0, count).Select(index => new ExecutionOccurrence("idol-execution-" + index, "idol-execution-" + index,
            ExecutionKind.Trigger, index < completed ? ExecutionStep.Complete : paying && index == completed ? ExecutionStep.Cost : ExecutionStep.Stack) {
                ActorPlayerId = 0, SourceCardId = source.VisualId, SourceName = source.Name,
                Outcome = index < completed ? ExecutionOutcome.Resolved : null
            }).ToImmutableArray();
        var transitions = occurrences.Take(completed).Select((item, index) => new ExecutionTransition(index + 1, item.Id, ExecutionStep.Complete) {
            Outcome = item.Outcome }).ToImmutableArray();
        bool finished = completed == count;
        int first = completed + (paying && !finished ? 1 : 0);
        var stack = Enumerable.Range(first, count - first).Select(index => new StackItem(source.Name, source.Text, StackActionKind.TriggeredAbility, source) {
            Id = "idol-stack-" + index, ExecutionId = "idol-execution-" + index, ControllerId = 0,
            Repetition = certified ? new("idol-draw-payment", source.VisualId) : null
        }).ToImmutableArray();
        var payment = new PaymentPayload(new("Pay", true), new("Decline once", true)) {
            PayerId = 0, UnpaidManaParts = [new("{1}", 1)], OptionalDecline = certified ? new("idol-draw-payment", "one-generic",
                "idol-execution-" + completed, source.VisualId, PaymentPurpose.TriggeredAbilityCost, 0, false,
                "idol-offer-" + completed, completed == 0 ? "" : "idol-offer-" + (completed - 1)) : null
        };
        return new("finite-presentation", "playing", null, null, 0, 7, "Main phase", "MAIN1", 1,
            Enumerable.Range(0, 4).Select(seat => new Player(seat, seat == 0 ? "You" : "Seat " + seat, 40, seat == 0, false,
                ImmutableDictionary<string, int>.Empty, [new("Battlefield", seat == 0 ? 1 : 0, seat == 0 ? [source] : [], null),
                    new("Hand", seat == 0 ? 0 : 7, [], null), new("Library", 50, [], null), new("Graveyard", 0, [], null),
                    new("Command", 0, [], null), new("Exile", 0, [], null)])).ToImmutableArray(), stack, null,
            finished ? null : paying ? new("idol-payment-" + completed, new(Source: new(Card: source, Zone: "Battlefield")), payment) {
                ExecutionId = "idol-execution-" + completed, ExecutionStep = ExecutionStep.Cost
            } : new("idol-priority-" + completed, new(), new PriorityPayload(new("Pass once", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = finished ? null : 0, PriorityPlayerId = finished || paying ? null : 0,
                ActionClass = finished ? "resolving" : paying ? "payment" : "priority", StackInterruptionVersion = 10 + completed + (paying ? 1 : 0),
                StackActionVersion = certified ? 1 : null, ResolutionChoiceVersion = certified ? 1 : null,
                Executions = new(1, completed, occurrences, transitions),
                Activity = occurrences.Take(completed).Select((item, index) => new ActivityEntry(index + 1, 7, "MAIN1", "resolved", 0,
                    "Optional payment declined", source.VisualId, source.Name) { ExecutionId = item.Id, ExecutionStep = ExecutionStep.Complete }).ToImmutableArray()
            };
    }

    private static GameSnapshot Foreign(GameSnapshot state, bool required)
    {
        if (required) return state with { ResolutionChoiceVersion = state.ResolutionChoiceVersion + 1,
            Decision = new("unmatched-required", new(Message: "Choose an amount for a different effect."), new NumberPayload("1", new(0, 3))),
            PriorityPlayerId = null, ActionClass = "choice" };
        var occurrence = new ExecutionOccurrence("foreign-execution", "foreign-execution", ExecutionKind.Spell, ExecutionStep.Stack) {
            ActorPlayerId = 1, SourceName = "An actual new response" };
        var transition = new ExecutionTransition(state.Executions!.LastSequence + 1, occurrence.Id, ExecutionStep.Stack);
        return state with { StackActionVersion = state.StackActionVersion + 1, StackInterruptionVersion = state.StackInterruptionVersion + 1,
            Stack = state.Stack.Insert(0, new("An actual new response", "Counter target ability.", StackActionKind.Spell, null) {
                Id = "foreign-response", ExecutionId = occurrence.Id, ControllerId = 1 }),
            Executions = state.Executions with { LastSequence = transition.Sequence, Occurrences = state.Executions.Occurrences.Add(occurrence),
                Transitions = state.Executions.Transitions.Add(transition) }
        };
    }

    private static GameSnapshot OtherPayment(GameSnapshot state)
    {
        var current = state.Stack[0]; var source = current.Card!;
        var payment = new PaymentPayload(new("Pay", true), new("Decline once", true)) {
            PayerId = 0, UnpaidManaParts = [new("{1}", 1)], OptionalDecline = new(current.Repetition!.GroupKey,
                "a-different-cost", current.ExecutionId, source.VisualId, PaymentPurpose.EffectCost, 0, false,
                "other-source-offer", "kraken-offer-0")
        };
        return state with { Stack = state.Stack.RemoveAt(0), PriorityPlayerId = null, ActionClass = "payment",
            StackInterruptionVersion = state.StackInterruptionVersion + 1,
            Decision = new("other-source-payment", new(Source: new(Card: source, Zone: "Battlefield")), payment) {
                ExecutionId = current.ExecutionId, ExecutionStep = ExecutionStep.Cost },
            Executions = state.Executions! with { Occurrences = state.Executions.Occurrences.Select(item => item.Id == current.ExecutionId
                ? item with { Step = ExecutionStep.Cost } : item).ToImmutableArray() }
        };
    }
}
