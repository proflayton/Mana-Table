using System.Text.Json;
using System.Text.RegularExpressions;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// The existing two-size GPU journey uses the same authored public checkpoint as
// the composed checks. Pointer input, Canvas painting and scoped commands are
// production paths; this fixture does not resolve Magic rules or real matches.
internal sealed partial class CardReadingSmoke
{
    private Action? stackDockFailureCleanup;

    private void ReleaseStackDockFixture()
    {
        var cleanup = stackDockFailureCleanup;
        stackDockFailureCleanup = null;
        cleanup?.Invoke();
    }

    private void QueueStackDockChecks()
    {
        int replies = -1;
        string game = "", decision = "";
        Prepared(() => !view.Busy && view.Overlay.Length == 0, 2, () => {
            view.SetAutomatic(false); view.SetTableStudy(true); Move(new(10, 70));
            Publish(StackCombatHudScenario.Snapshot(2));
            replies = scenario.Replies.Count; game = scenario.State.Id; decision = scenario.State.Decision!.Id;
        }, "The previous reading journey must finish before the authored combat and pending-trigger checkpoint");
        Prepared(() => StackDockReady(2), 1, () => {
            CheckStackDockPaint("baseline");
            Check(view.Match!.Combat!.Attackers.Length == 2 && view.Match.Combat.Attackers.All(attack => !attack.Blocked && attack.BlockerIds.Length == 0)
                && scenario.Replies.Count == replies,
                "The physical combat checkpoint retains two unblocked attackers beside the current pending Mithril Coat trigger without a command");
            CaptureStackDock("stack-dock-00-combat-and-pending");
            MoveHit(StackDockEffectHit()!.Id);
        }, "The current combat, exact pending effect and response controls must render before their physical hover");
        Prepared(() => view.ReferencePreviewStackId == StackCombatHudScenario.Trigger && view.ReferencePreviewBounds != null, 1, () => {
            CheckStackDockPaint("hover");
            string text = StackDockPreviewText();
            Check(text.Contains(StackCombatHudScenario.TriggerText, StringComparison.Ordinal)
                && text.Contains("Triggered ability", StringComparison.Ordinal)
                && view.ReferencePreviewCardId == StackCombatHudScenario.Coat,
                "Physical hover reads the complete actual Mithril Coat triggered ability rather than its Equip action or a truncated summary");
            Check(view.Match?.Id == game && view.Match?.Decision?.Id == decision && scenario.Replies.Count == replies
                && view.Match!.Tempo!.Clocks.Single().RemainingMs == 12_000,
                "Reading the pending ability preserves the current scoped response, immutable engine clock budget and command count");
            CaptureStackDock("stack-dock-01-full-trigger-hover");
            Move(new(10, 70));
        }, "The exact pending ability's complete Rules face must appear after its deliberate physical hover");
        Prepared(() => view.ReferencePreviewBounds == null && StackDockCurrent("combat-review"), .6,
            () => MoveHit("combat-review"), "Leaving the ability reader must expose the current Combat gesture");
        Click();
        Prepared(() => view.Overlay == "combat" && StackDockCurrent("close-overlay"), 1, () => {
            Check(view.Match?.Id == game && view.Match?.Decision?.Id == decision && scenario.Replies.Count == replies,
                "Opening Combat from beside a pending trigger inspects the two assignments without answering priority");
            CaptureStackDock("stack-dock-02-combat-inspection");
            MoveHit("close-overlay");
        }, "The real Combat gesture must open its read-only assignment inspection");
        Click();
        Prepared(() => view.Overlay.Length == 0 && StackDockReady(2), 1, () => {
            Check(view.Match?.Id == game && view.Match?.Decision?.Id == decision && scenario.Replies.Count == replies,
                "Returning from Combat restores the same pending trigger, target and current priority response without a command");
            CheckStackDockPaint("returned");
        }, "Returning from Combat must restore the same readable response checkpoint");

        QueueStackDockResponse(resolve: false);
        QueueStackDockResponse(resolve: true);
    }

    private void QueueStackDockResponse(bool resolve)
    {
        int replies = -1;
        string game = "", decision = "";
        Func<GameCommand, Task>? previous = null;
        var received = new TaskCompletionSource<GameCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? previousReadiness = null;
        int readinessRecords = 0, lastReadinessIndex = -1;
        bool InFlightReady()
        {
            var finite = view.FiniteResolution;
            bool receivedResponse = received.Task.IsCompletedSuccessfully;
            bool currentStop = StackDockCurrent("stack-resolution-stop");
            var hit = view.HitAt(SmokePointer.Table(view, new(mouse.X, mouse.Y)));
            var facts = new {
                receivedResponse, receivedStatus = received.Task.Status.ToString(), view.Busy, view.Polling,
                finitePresent = finite != null, finiteActive = finite?.IsActive, finitePaused = finite?.IsPaused,
                finiteStopReason = finite?.StopReason, captured = finite?.CapturedStackIds.ToArray(),
                remaining = view.StackResolutionRemaining, currentStop, view.Automatic, view.Held,
                pauseReason = view.DecisionDock?.PauseReason.ToString(), view.ReferencePreviewBounds,
                snapshotScope = view.Scope, decisionId = view.Match?.Decision?.Id,
                decisionKind = view.Match?.Decision?.Payload.GetType().Name,
                pointerHit = hit?.Id, pointerScope = hit?.Scope, replies = scenario.Replies.Count
            };
            string serialized = JsonSerializer.Serialize(facts);
            if (serialized != previousReadiness) {
                previousReadiness = serialized;
                object observation = new { journey = "stack-dock-in-flight-readiness", view.Now, facts };
                // Retain the first changes and the latest condition without a
                // per-frame log or an unbounded new diagnostic stream.
                if (readinessRecords < 16) {
                    lastReadinessIndex = observations.Count; observations.Add(observation); readinessRecords++;
                } else observations[lastReadinessIndex] = observation;
            }
            return receivedResponse && view.Busy && finite is { IsActive: true } && currentStop;
        }
        Prepared(() => !view.Busy && view.Overlay.Length == 0, 1, () => {
            Move(new(10, 70));
            Publish(StackCombatHudScenario.Snapshot(resolve ? 2 : 1));
            replies = scenario.Replies.Count; game = scenario.State.Id; decision = scenario.State.Decision!.Id;
            previous = scenario.ReplyOverride;
            scenario.ReplyOverride = command => {
                if (command is not PassPriority pass || pass.GameId != game || pass.DecisionId != decision
                    || !received.TrySetResult(command))
                    throw new InvalidOperationException("The authored stack HUD fixture received an unexpected or repeated scoped response.");
                if (resolve) return release.Task;
                scenario.Replies.Add(command);
                scenario.Present(scenario.State with { Decision = null, DecisionOwnerId = null, PriorityPlayerId = null });
                return Task.CompletedTask;
            };
            stackDockFailureCleanup = () => { scenario.ReplyOverride = previous; release.TrySetCanceled(); };
        }, "The previous response must finish before the separate " + (resolve ? "Resolve these" : "Pass once") + " pointer fixture");
        string action = resolve ? "stack-resolution-arm" : "confirm";
        Prepared(() => StackDockReady(resolve ? 2 : 1) && StackDockCurrent(action), 1, () => {
            var hit = view.Hits.Single(hit => hit.Id == action);
            Check(resolve ? hit.Intent is MatchIntent.SetStackResolution { Armed: true } : hit.Intent is MatchIntent.ConfirmDecision,
                "The exposed " + (resolve ? "Resolve these" : "Pass once") + " gesture retains its own current production intent");
            CheckStackDockPaint(resolve ? "resolve-offered" : "pass-offered");
            CaptureStackDock(resolve ? "stack-dock-04-resolve-offered" : "stack-dock-03-pass-once");
            MoveHit(action);
        }, "The separate response's complete label and physical pixel must be current before commitment");
        Click();
        if (!resolve) {
            Prepared(() => received.Task.IsCompletedSuccessfully && !view.Busy, 2, () => {
                Check(scenario.Replies.Count == replies + 1 && scenario.Replies[^1] is PassPriority pass
                    && pass.GameId == game && pass.DecisionId == decision && view.FiniteResolution == null,
                    "Physical Pass once submits exactly one current scoped PassPriority without arming a captured queue");
                ReleaseStackDockFixture();
            }, "The deliberate Pass once must reconcile its one scoped response");
            return;
        }
        Prepared(InFlightReady, .6, () => {
            Check(view.FiniteResolution!.CapturedStackIds.SequenceEqual([StackCombatHudScenario.Trigger, StackCombatHudScenario.OlderSpell])
                && received.Task.Result is PassPriority pass && pass.GameId == game && pass.DecisionId == decision
                && scenario.Replies.Count == replies,
                "Physical Resolve these captures only the two currently disclosed stack occurrences and starts one current scoped pass");
            CheckStackDockPaint("resolving");
            CaptureStackDock("stack-dock-05-captured-resolution");
            MoveHit("stack-resolution-stop");
        }, "The captured queue and Take control must remain visible while its first authorized response is in flight");
        Click();
        Prepared(() => view.FiniteResolution == null && view.Busy, 1, () => {
            Check(scenario.Replies.Count == replies && received.Task.Result is PassPriority,
                "Physical Take control stops the local captured continuation while preserving its already submitted in-flight pass");
            // Publish an authored wait after the held reply. No trigger outcome,
            // attachment, second priority answer or game result is invented.
            scenario.Replies.Add(received.Task.Result);
            scenario.Present(scenario.State with { Decision = null, DecisionOwnerId = null, PriorityPlayerId = null });
            release.TrySetResult();
        }, "Take control must stop captured consent before the fixture releases its single pending reply");
        Prepared(() => !view.Busy, 2, () => {
            Check(scenario.Replies.Count == replies + 1 && view.FiniteResolution == null && view.Match?.Stack.Length == 2,
                "Releasing the first authored reply cannot restart the stopped captured queue or submit an extra response");
            ReleaseStackDockFixture();
        }, "The in-flight reply must settle without restarting the stopped continuation");
    }

    private bool StackDockCurrent(string id) => view.Hits.Any(hit => hit.Id == id && view.CurrentHit(hit));
    private TableHit? StackDockEffectHit() => view.Hits.LastOrDefault(hit => hit.Id == "study-stack-effect:" + StackCombatHudScenario.Trigger
        && view.CurrentHit(hit));
    private bool StackDockReady(int pending) => view.Match?.Id == "stack-combat-hud" && view.Match.Stack.Length == pending
        && StackDockEffectHit() != null && StackDockCurrent("confirm") && StackDockCurrent("combat-review")
        && view.Frame!.Cards.Any(card => card.Id == StackCombatHudScenario.Geist)
        && view.StackRows.Any(row => row.Id == StackCombatHudScenario.Trigger);
    private string StackDockPreviewText() => view.ReferencePreviewBounds is { } preview
        ? Regex.Replace(string.Join(" ", view.Frame!.Drawing.Commands.OfType<DrawCommand.Text>()
            .Where(text => preview.Contains(text.X, text.Y)).Select(text => text.Value)), @"\s+", " ") : "";
    private static Rectangle StackDockPanelPaint(Rectangle bounds) => Rectangle.Union(bounds,
        new(bounds.X + 2, bounds.Y + 5, bounds.Width, bounds.Height));

    private void CheckStackDockPaint(string phase)
    {
        var frame = view.Frame!;
        Check(!view.StudyHudConstrained,
            "The authored " + phase + " combat and pending-trigger checkpoint fits without a constrained HUD fallback");
        var metrics = new TextMetrics();
        var controls = frame.Hits.Where(hit => hit.Id is "confirm" or "stack-resolution-arm" or "stack-resolution-stop" or "combat-review")
            .Select(hit => (hit.Id, Bounds: StackDockPanelPaint(hit.Bounds))).ToArray();
        var rows = view.StackRows.Select(row => StackDockPanelPaint(row.Bounds)).ToArray();
        var summaries = frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.Value is { } value
            && value.Contains("attacking", StringComparison.Ordinal) && value.Contains("blocked", StringComparison.Ordinal)).Select(text => {
                var size = metrics.TextSize(text.Value!, text.Size, text.Width, text.MaxLines, text.Bold, frame.Drawing.TextScale);
                return new Rectangle(text.X, text.Y, size.X, size.Y);
            }).ToArray();
        Check(controls.Any(item => item.Id == "combat-review") && rows.Length > 0 && summaries.Length > 0
            && controls.All(control => rows.All(row => !control.Bounds.Intersects(row))
                && summaries.All(summary => !control.Bounds.Intersects(summary)))
            && controls.All(control => controls.All(other => control.Id == other.Id || !control.Bounds.Intersects(other.Bounds)))
            && summaries.All(summary => rows.All(row => !summary.Intersects(row)))
            && controls.All(control => HitReachable(control.Id)),
            "Actual " + phase + " stack, measured combat readout and response/control paint including Panel shadows remain separate and physically reachable");
        foreach (var control in controls) {
            string label = control.Id switch {
                "confirm" => view.DecisionDock!.ActionLabel,
                "stack-resolution-arm" => "Resolve these",
                "stack-resolution-stop" => "Take control",
                _ => "Combat"
            };
            CompleteActionLabelChecks.Run(frame, control.Id, label, Check);
        }
        if (view.ReferencePreviewBounds is { } preview) {
            var painted = new CardPaintGeometry(new(preview.Center.ToVector2(), preview.Size.ToVector2())).Bounds;
            var targets = frame.Cards.Where(card => card.Id is StackCombatHudScenario.Geist or StackCombatHudScenario.Coat)
                .Select(card => new CardPaintGeometry(card.Display).Bounds).ToArray();
            Check(preview.Width >= 300 && new Rectangle(0, 54, 1600, view.DesignHeight - 54).Contains(painted)
                && controls.All(control => !painted.Intersects(control.Bounds)) && rows.All(row => !painted.Intersects(row))
                && summaries.All(summary => !painted.Intersects(summary)) && targets.Length == 2
                && targets.All(target => !painted.Intersects(target)),
                "The complete pending-ability reader and its physical shadow leave the source, target, stack, combat readout and live response controls visible");
        }
    }

    private void CaptureStackDock(string name)
    {
        Capture(name);
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
        File.WriteAllText(Path.Combine(view.Profile, name + ".json"), JsonSerializer.Serialize(new {
            scope = "Authored public StackCombatHudScenario; actual synthetic physical pointer and production Canvas GPU output; no real-game or rules-resolution claim.",
            artwork = "CardReadingArtFixture reads --art-cache when supplied; otherwise its explicitly synthetic deterministic artwork is used.",
            humanReview = "pending", viewport = new { view.GraphicsDevice.Viewport.Width, view.GraphicsDevice.Viewport.Height },
            view.TextScale, view.DesignHeight, decisionScope = view.Scope, view.Overlay, view.ReferencePreviewBounds, view.ReferencePreviewStackId,
            studyHudConstrained = view.StudyHudConstrained,
            rows = view.StackRows, controls = view.Hits.Where(hit => hit.Id is "confirm" or "combat-review" or "stack-resolution-arm" or "stack-resolution-stop")
                .Select(hit => new { hit.Id, hit.Bounds, painted = StackDockPanelPaint(hit.Bounds), current = view.CurrentHit(hit) }),
            pointer = new { mouse.X, mouse.Y }, replies = scenario.Replies.Count,
            surface = name + ".png", window = name + "-window.png"
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }), System.Text.Encoding.UTF8);
    }
}
