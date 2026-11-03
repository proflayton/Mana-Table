using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Production composition and physical pointer dispatch over declared public
// checkpoints. This does not establish Forge trigger creation or resolution.
internal static class StackCombatLayoutChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckWorkspaceFit(profile, check);
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            // Start with the reported long description WITHOUT hovering: the
            // main competition must be caught independently of reader placement.
            using (var longText = new Journey(profile, size, 1.3f, true, 2, "provider-description",
                StackCombatHudScenario.ProviderDescription())) {
                CheckLayout(longText, "non-hover-provider-description", check);
                CheckReading(longText, check);
            }
            foreach (float scale in new[] { 1f, 1.3f })
            foreach (bool reduced in new[] { false, true })
            foreach (int count in new[] { 1, 2 }) {
                using var journey = new Journey(profile, size, scale, reduced, count);
                CheckLayout(journey, "initial", check);
                CheckReading(journey, check);
                CheckCombat(journey, check);
                if (count == 1) CheckPass(journey, check);
            }
            using (var focused = new Journey(profile, size, 1.3f, true, 2, "focus")) {
                var original = focused.View.Match;
                var world = focused.Frame.Scene!.BattlefieldDrawOrder.ToDictionary(card => card.Id, card => card.Pose);
                focused.View.OpenBattlefield(1); focused.Tick(); focused.Tick(advance: .7);
                CheckLayout(focused, "focused-opponent", check);
                check(ReferenceEquals(original, focused.View.Match) && focused.Commands.IsEmpty
                        && focused.Frame.Scene!.BattlefieldDrawOrder.All(card => world[card.Id] == card.Pose),
                    focused.Label + ": focusing the attacking seat preserves the same physical occurrences and unanswered priority");
                CheckReading(focused, check);
            }
            using var resolve = new Journey(profile, size, 1.3f, true, 2, "resolve");
            CheckResolve(resolve, check);
            CheckGrouped(profile, size, check);
        }
        CheckEmptyAnnouncement(profile, check);
    }

    private sealed record Painted(string Name, Rectangle Logical, Rectangle Physical);
    private static Rectangle PanelPaint(Rectangle bounds) => Rectangle.Union(bounds,
        new(bounds.X + 2, bounds.Y + 5, bounds.Width, bounds.Height));
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    private static Rectangle TextBounds(DrawCommand.Text text, float scale)
    {
        var measured = new TextMetrics().TextSize(text.Value ?? "", text.Size, text.Width, text.MaxLines, text.Bold, scale);
        return new(text.X, text.Y, measured.X, measured.Y);
    }

    private static void CheckWorkspaceFit(string profile, Action<bool, string> check)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var fullViewport = new Rectangle(0, 0, 1600, 900);
        var fullPreferred = new Rectangle(1040, 500, 410, 574);
        var overlapping = Enumerable.Range(0, 52).Select(index =>
            new Rectangle(920 + index % 4 * 10, 510 + index % 3 * 8, 480, 160)).ToArray();
        var full = DecisionWorkspaceLayout.Fit(fullPreferred, fullViewport, overlapping, minimumWidth: 300);
        check(full is { } fullFace && fullFace.Size == fullPreferred.Size && fullViewport.Contains(fullFace)
                && overlapping.All(obstacle => !fullFace.Intersects(obstacle)),
            "Repeated overlapping HUD pieces translate a complete physical face without changing its size/aspect or intersecting any reservation");

        var narrowViewport = new Rectangle(0, 0, 600, 600);
        var sideReservations = Enumerable.Range(0, 52).Select(index =>
            new Rectangle(265 + index % 4 * 4, 0, 335, 600)).ToArray();
        var fitted = DecisionWorkspaceLayout.Fit(fullPreferred, narrowViewport, sideReservations, minimumWidth: 200);
        check(fitted is { Width: 265 } smaller && smaller.Height == (int)Math.Round(smaller.Width * (double)fullPreferred.Height / fullPreferred.Width)
                && narrowViewport.Contains(smaller) && sideReservations.All(obstacle => !smaller.Intersects(obstacle)),
            "A width-limited physical face retains its full aspect and finds the largest available integer width despite52 duplicate/overlapping reservations");
        var covered = Enumerable.Repeat(narrowViewport, 52).ToArray();
        var unavailable = DecisionWorkspaceLayout.Fit(fullPreferred, narrowViewport, covered, minimumWidth: 300);
        check(unavailable == null, "A fully occupied viewport cannot return an intersecting or partial physical face after repeated HUD reservations");
        using var stream = new FileStream(Path.Combine(profile, "stack-combat-workspace-fit.json"), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, new { fullViewport, fullPreferred, overlapping, full, narrowViewport, sideReservations, fitted,
            coveredReservations = covered.Length, unavailable, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            timingScope = "Observed diagnostic only; no fragile per-machine wall-clock assertion or expanded lane timeout." },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
    }

    private static void CheckLayout(Journey run, string stage, Action<bool, string> check)
    {
        var frame = run.Frame;
        var rows = run.View.StackRows;
        var panels = frame.Drawing.Commands.OfType<DrawCommand.Panel>().ToArray();
        var stack = new List<Painted>();
        var competing = new List<Painted>();
        Painted Paint(string name, Rectangle logical) => new(name, logical, run.Physical(logical));
        foreach (var row in rows) {
            var panel = panels.SingleOrDefault(part => part.Bounds == row.Bounds);
            check(panel != null, run.Label + ": the pending-effect footprint comes from its actual painted panel");
            stack.Add(Paint("stack:" + row.Id, PanelPaint(panel!.Bounds)));
        }
        var first = rows.FirstOrDefault();
        bool grouped = run.View.StackWorkloads.Count > 0;
        var header = first == null ? null : frame.Drawing.Commands.OfType<DrawCommand.Rounded>()
            .FirstOrDefault(part => part.Bounds.Left == first.Bounds.Left && part.Bounds.Width == first.Bounds.Width
                && part.Bounds.Bottom <= first.Bounds.Top && first.Bounds.Top - part.Bounds.Bottom <= 8);
        if (grouped) {
            var sourceHeader = frame.Drawing.Commands.OfType<DrawCommand.Text>()
                .SingleOrDefault(text => text.Value == run.PendingCount + " pending · by source");
            check(first != null && sourceHeader != null, run.Label + ": the grouped stack's actual source/count header remains present");
            stack.Add(Paint("stack-source-header", TextBounds(sourceHeader!, run.Scale)));
            foreach (var text in frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.Value != null
                && (text.Value.StartsWith("Triggered ·", StringComparison.Ordinal) || text.Value.StartsWith("Pending trigger ·", StringComparison.Ordinal)
                    || text.Value == StackCombatHudScenario.ProviderTriggerText)))
                stack.Add(Paint("current-trigger-announcement", TextBounds(text, run.Scale)));
        } else {
            check(first != null && header != null, run.Label + ": the actual stack header and pending trigger remain present");
            stack.Add(Paint("stack-header", header!.Bounds));
        }
        string effectId = (grouped ? "stack-workload:" : run.View.TableStudyEnabled ? "study-stack-effect:" : "stack-effect:") + StackCombatHudScenario.Trigger;
        check(frame.Hits.Any(hit => hit.Id == effectId), run.Label + ": the exact Mithril trigger remains directly readable");

        foreach (string id in new[] { "confirm", "combat-review", "stack-resolution-arm" }) {
            var hit = frame.Hits.SingleOrDefault(candidate => candidate.Id == id);
            if (id == "stack-resolution-arm" && run.PendingCount == 1) {
                check(hit == null, run.Label + ": a single trigger does not invent bulk-resolution consent"); continue;
            }
            check(hit != null && run.View.CurrentHit(hit), run.Label + ": the current " + id + " action is present in its live scope");
            var panel = panels.SingleOrDefault(part => part.Bounds == hit!.Bounds);
            check(panel != null, run.Label + ": the " + id + " footprint includes its actual button and shadow");
            competing.Add(Paint(id, PanelPaint(panel!.Bounds)));
            check(run.TryPoint(hit!, out _), run.Label + ": " + id + " has an exposed physical pointer target");
        }
        var combat = frame.Drawing.Commands.OfType<DrawCommand.Text>()
            .SingleOrDefault(text => text.Value == "2 attacking · 0 blocked");
        check(combat != null, run.Label + ": combat retains the complete two-attacker, zero-block summary");
        competing.Add(Paint("combat-summary", TextBounds(combat!, run.Scale)));
        foreach (var plan in run.View.ReadoutPlans.Values)
        foreach (var item in plan.Text.Where(item => item.Role is SeatReadoutTextRole.LandTotal or SeatReadoutTextRole.LandState
                     or SeatReadoutTextRole.Identity or SeatReadoutTextRole.ResourceSummary)) {
            var text = frame.Drawing.Commands.OfType<DrawCommand.Text>().SingleOrDefault(text => text.Value == item.Text
                && text.X == item.Bounds.X && text.Y == item.Bounds.Y && text.Size == item.Size);
            check(text != null && item.Bounds.Contains(TextBounds(text, run.Scale)),
                run.Label + ": owner " + plan.PlayerId + " retains its complete measured land/identity readout");
            competing.Add(Paint("seat-" + plan.PlayerId + ":" + item.Role, TextBounds(text!, run.Scale)));
        }
        var localLands = PublicResources.Lands(run.View.Match!.Viewer!);
        foreach (string value in new[] { localLands.Total + " lands", localLands.Untapped + " untapped · " + localLands.Tapped + " tapped" }) {
            var text = frame.Drawing.Commands.OfType<DrawCommand.Text>().SingleOrDefault(text => text.Value == value);
            check(text != null, run.Label + ": the local owner retains its complete actual " + value + " readout");
            competing.Add(Paint("seat-0:" + value, TextBounds(text!, run.Scale)));
        }
        check(run.View.Match.Players.Where(player => player.Id != run.View.Match.ViewerId)
                .All(player => run.View.ReadoutPlans.ContainsKey(player.Id)),
            run.Label + ": all four owners retain their resource summaries");
        foreach (var text in frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.Value != null
            && (text.Value.StartsWith("Your response", StringComparison.Ordinal) || text.Value.StartsWith("Your priority", StringComparison.Ordinal))
            && run.View.DecisionPanelBounds.Contains(new Point(text.X, text.Y))))
            competing.Add(Paint("priority-heading", TextBounds(text, run.Scale)));

        var collisions = stack.SelectMany(source => competing.Where(other => source.Physical.Intersects(other.Physical))
                .Select(other => new { first = source.Name, second = other.Name, source.Physical, other = other.Physical }))
            .Concat(competing.Where(item => item.Name is "confirm" or "combat-review" or "stack-resolution-arm")
                .SelectMany(control => competing.Where(other => other.Name != control.Name && control.Physical.Intersects(other.Physical))
                    .Select(other => new { first = control.Name, second = other.Name, Physical = control.Physical, other = other.Physical })))
            .ToArray();
        run.Record(stage, new { stack, competing, collisions, run.View.StudyHudConstrained, frame = frame.Observe(),
            scope = "Authored disclosed checkpoint; production composition and physical input, not rules resolution or human acceptance." });
        check(!run.View.StudyHudConstrained, run.Label + ": the ordinary stack/combat fixture fits without entering the explicit constrained-HUD fallback");
        check(stack.Concat(competing).All(item => item.Physical.Width > 0 && item.Physical.Height > 0
                && new Rectangle(0, 0, run.Size.X, run.Size.Y).Contains(item.Physical)),
            run.Label + ": full painted stack, summaries and button shadows remain inside the actual window");
        check(collisions.Length == 0, run.Label + ": actual stack/header/shadows, combat, owner resources and both priority-button shadows do not overlap; "
            + string.Join(", ", collisions.Select(pair => pair.first + "/" + pair.second)));
        if (!grouped) {
            string expected = Normalize(run.View.Match.Stack[0].Text);
            var effect = frame.Drawing.Commands.OfType<DrawCommand.Text>()
                .FirstOrDefault(text => Normalize(text.Value ?? "") == expected);
            check(effect != null && effect.Size >= .66f && rows.Any(row => row.Bounds.Contains(TextBounds(effect, run.Scale))),
                run.Label + ": the stack paints the full Mithril provider text at its normal readable font within its measured panel");
        }
    }

    private static void CheckReading(Journey run, Action<bool, string> check)
    {
        string game = run.View.Match!.Id, decision = run.View.Match.Decision!.Id;
        string scope = run.View.Scope;
        double began = run.View.Now;
        var tempo = run.View.Match.Tempo;
        string expected = Normalize(run.View.Match.Stack[0].Text);
        string id = (run.View.StackWorkloads.Count > 0 ? "stack-workload:" : run.View.TableStudyEnabled ? "study-stack-effect:" : "stack-effect:") + StackCombatHudScenario.Trigger;
        var hit = run.Hit(id);
        run.Tick(run.Point(hit)); run.Tick(advance: .4);
        var preview = run.View.ReferencePreviewBounds;
        string reading = Normalize(string.Join(" ", run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
            .Where(text => preview?.Contains(text.X, text.Y) == true).Select(text => text.Value)));
        check(preview != null && run.View.ReferencePreviewStackId == StackCombatHudScenario.Trigger
                && reading.Contains(expected, StringComparison.Ordinal),
            run.Label + ": hovering the actual pending trigger reads its complete current ability");
        var readerBounds = preview!.Value; readerBounds.Inflate(1, 1);
        var readerPaint = run.Physical(readerBounds);
        var protectedControls = run.Frame.Hits.Where(control => control.Id is "confirm" or "combat-review" or "stack-resolution-arm")
            .Select(control => new { control.Id, Logical = control.Bounds, PaintedLogical = PanelPaint(control.Bounds),
                Physical = run.Physical(PanelPaint(control.Bounds)) }).ToArray();
        var readouts = run.View.SeatReadouts.Select((bounds, index) => new { Index = index, Logical = bounds, Physical = run.Physical(bounds) }).ToArray();
        var collisions = protectedControls.Where(control => readerPaint.Intersects(control.Physical))
            .Select(control => new { Surface = control.Id, Logical = control.PaintedLogical, control.Physical })
            .Concat(readouts.Where(readout => readerPaint.Intersects(readout.Physical))
                .Select(readout => new { Surface = "seat-readout-" + readout.Index, readout.Logical, readout.Physical })).ToArray();
        run.Record("trigger-reading-before-assert", new { preview, readerBounds, readerPaint, protectedControls, SeatReadouts = readouts,
            ReadoutPlans = run.View.ReadoutPlans.Values.Select(plan => new { plan.PlayerId, Pieces = plan.Pieces.Select(bounds =>
                new { Logical = bounds, Physical = run.Physical(bounds) }).ToArray(), plan.Text }),
            collisions, reading, game, decision, scope, currentScope = run.View.Scope, time = run.View.Now, began,
            commands = run.Commands.Count, frame = run.Frame.Observe() });
        check(collisions.Length == 0,
            run.Label + ": the full effect reader preserves combat, response controls and each owner's current land readout");
        run.Tick(advance: 1.1);
        check(run.Commands.IsEmpty && run.View.Match is { } state && state.Id == game && state.Decision?.Id == decision
                && run.View.Scope == scope && run.Frame.Stamp.DecisionId == decision && run.View.Selections.Count == 0
                && run.View.Now >= began + 1.4 && ReferenceEquals(tempo, state.Tempo) && state.Tempo!.Clocks.Single().RemainingMs == 12_000,
            run.Label + ": trigger reading sends no command, selects nothing and never resets or changes the engine's clock budget");
        run.Record("trigger-reading", new { preview, reading, game, decision, commands = run.Commands.Count, frame = run.Frame.Observe() });
        run.Tick(new Point(10, 70)); run.Tick();
        check(run.View.ReferencePreviewBounds == null && run.Commands.IsEmpty, run.Label + ": leaving the trigger dismisses only its transient explanation");
    }

    private static void CheckCombat(Journey run, Action<bool, string> check)
    {
        string decision = run.View.Match!.Decision!.Id;
        run.Click("combat-review"); run.Tick();
        check(run.View.Overlay == "combat" && run.Commands.IsEmpty && run.View.Match.Decision?.Id == decision,
            run.Label + ": the physical Combat action opens its review without answering the pending trigger");
        run.Click("close-overlay"); run.Tick();
        check(run.View.Overlay.Length == 0 && run.Commands.IsEmpty && run.View.Match.Decision?.Id == decision,
            run.Label + ": closing combat returns to the same unanswered response and queue");
    }

    private static void CheckPass(Journey run, Action<bool, string> check)
    {
        var before = run.View.Match!;
        run.Click("confirm"); run.Until(() => run.Commands.Count == 1);
        check(run.Commands.Single() is PassPriority command && command.GameId == before.Id && command.DecisionId == before.Decision!.Id
                && GameCommandPermissions.Allows(before, command) && run.View.FiniteResolution == null,
            run.Label + ": a physical Pass once submits exactly the current scoped pass without arming a queue");
        for (int frame = 0; frame < 4; frame++) run.Tick();
        check(run.Commands.Count == 1, run.Label + ": the held pass cannot replay while its original response remains in flight");
    }

    private static void CheckResolve(Journey run, Action<bool, string> check)
    {
        var before = run.View.Match!;
        check(FiniteResolutionIntent.TryArm(before, before.Stack.Select(item => item.Id)) != null,
            run.Label + ": explicit current execution metadata certifies this exact finite queue");
        run.Click("stack-resolution-arm"); run.Until(() => run.Commands.Count == 1);
        check(run.View.FiniteResolution is { } intent && intent.CapturedStackIds.SequenceEqual(before.Stack.Select(item => item.Id))
                && intent.CapturedExecutionIds.SequenceEqual(before.Stack.Select(item => item.ExecutionId))
                && run.Commands.Single() is PassPriority command && command.GameId == before.Id && command.DecisionId == before.Decision!.Id
                && command.StackInterruptionVersion == before.StackInterruptionVersion && GameCommandPermissions.Allows(before, command),
            run.Label + ": physical Resolve these captures only both current occurrences and sends one ordinary guarded pass");
        for (int frame = 0; frame < 4; frame++) run.Tick();
        check(run.Commands.Count == 1 && run.View.StackResolutionRemaining == 2,
            run.Label + ": an in-flight captured pass neither replays nor forecasts completed trigger work");
    }

    private static void CheckGrouped(string profile, Point size, Action<bool, string> check)
    {
        var grouped = StackCombatHudScenario.Grouped();
        // A null execution frame resets the consumer cursor. Establish a known
        // empty cursor first so the next three transitions are actual arrivals,
        // rather than historical work first disclosed at a rejoin baseline.
        var before = grouped with { Stack = [], Executions = new(1, 0, [], []) };
        using var run = new Journey(profile, size, 1.3f, true, 3, "grouped",
            before);
        run.Present(grouped); run.Tick();
        var visibleAnnouncement = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.Value is { } value
            && (value.StartsWith("Triggered ·", StringComparison.Ordinal) && value.Contains("Mithril Coat", StringComparison.Ordinal)
                || value == StackCombatHudScenario.ProviderTriggerText)).ToArray();
        run.Record("grouped-before-assert", new { before.Executions, afterExecutions = run.View.Match?.Executions,
            expectedStack = grouped.Stack.Select(item => new { item.Id, item.ExecutionId, item.ControllerId }),
            actualStack = run.View.Match?.Stack.Select(item => new { item.Id, item.ExecutionId, item.ControllerId }),
            workloads = run.View.StackWorkloads, run.View.AnnouncementId, run.View.AnnouncementCount, visibleAnnouncement,
            scope = run.View.Scope, time = run.View.Now, commands = run.Commands.Count, frame = run.Frame.Observe() });
        check(run.View.StackWorkloads is [{ MemberIds.Length: 3, IsNext: true } group]
                && group.MemberIds.SequenceEqual(grouped.Stack.Select(item => item.Id)) && group.Positions.SequenceEqual([1, 2, 3])
                && run.View.AnnouncementId != null && visibleAnnouncement.Length == 2 && run.Commands.IsEmpty,
            run.Label + ": three exact same-source occurrences form one current pile without losing order or its new trigger announcement");
        CheckLayout(run, "grouped-current-trigger", check);
        CheckReading(run, check);
    }

    private static void CheckEmptyAnnouncement(string profile, Action<bool, string> check)
    {
        using var run = new Journey(profile, new(1000, 740), 1.3f, true, 1, "empty-announcement");
        run.Present(StackCombatHudScenario.CompletedAnnouncement()); run.Tick();
        var announced = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
            .Where(text => text.Value is { } value && (value == StackCombatHudScenario.TriggerText
                || value.StartsWith("Resolved ·", StringComparison.Ordinal) && value.Contains("Mithril Coat", StringComparison.Ordinal)))
            .Select(text => run.Physical(TextBounds(text, run.Scale))).ToArray();
        var buttons = run.Frame.Hits.Where(hit => hit.Id is "confirm" or "combat-review")
            .Select(hit => run.Physical(PanelPaint(hit.Bounds))).ToArray();
        var combat = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().SingleOrDefault(text => text.Value == "2 attacking · 0 blocked");
        var protectedPaint = buttons.Concat(run.View.SeatReadouts.Select(run.Physical))
            .Concat(combat == null ? [] : new[] { run.Physical(TextBounds(combat, run.Scale)) }).ToArray();
        run.Record("stack-empty-announcement", new { announced, protectedPaint, frame = run.Frame.Observe() });
        check(run.View.Match?.Stack.IsEmpty == true && run.View.StackRows.Count == 0 && run.View.AnnouncementId != null
                && announced.Length == 2 && combat != null && run.Commands.IsEmpty,
            run.Label + ": an actual terminal receipt may announce while the rules stack stays empty and combat remains unchanged");
        check(announced.All(bounds => new Rectangle(0, 0, run.Size.X, run.Size.Y).Contains(bounds)
                && protectedPaint.All(other => !bounds.Intersects(other))),
            run.Label + ": the stack-empty terminal announcement leaves combat, priority-button shadows and owner summaries readable");
    }

    private sealed class Journey : IDisposable
    {
        private readonly PresentationRun app;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Point pointer = new(10, 70);
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal Point Size { get; }
        internal float Scale { get; }
        internal int PendingCount { get; }
        internal string Label { get; }
        private string DirectoryPath { get; }
        internal TableApplication.Probe View => app.View;
        internal PresentationFrame Frame => app.Frame;

        private readonly InteractionScenarioEngine engine;
        internal Journey(string profile, Point size, float scale, bool reduced, int pendingCount, string suffix = "matrix", GameSnapshot? initial = null)
        {
            Size = size; Scale = scale; PendingCount = pendingCount;
            Label = $"{size.X}x{size.Y}/{scale * 100:0}%/{(reduced ? "reduced" : "motion")}/{pendingCount}/{suffix}";
            DirectoryPath = Path.Combine(profile, $"stack-combat-{size.X}-{(int)(scale * 100)}-{reduced}-{pendingCount}-{suffix}");
            Directory.CreateDirectory(DirectoryPath);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = reduced, TextScale = scale, Stops = [] }.Save(DirectoryPath);
            engine = new InteractionScenarioEngine(); engine.Present(initial ?? StackCombatHudScenario.Snapshot(pendingCount));
            engine.ReplyOverride = async command => {
                if (command is not PassPriority || !GameCommandPermissions.Allows(engine.State, command))
                    throw new InvalidOperationException("The layout journey received an unauthorized or non-priority game command.");
                Commands.Enqueue(command); await release.Task;
            };
            app = new(TestTableServices.For(engine), DirectoryPath, engine.State, size);
            app.EnableLiveTableStudy(); Tick(); Tick(advance: .7);
        }
        internal void Present(GameSnapshot state) { engine.Present(state); app.QueueObservation(engine.State); }
        internal void Tick(Point? physical = null, ButtonState left = ButtonState.Released, double advance = 1.0 / 60)
        {
            pointer = physical ?? pointer;
            app.Tick(TimeSpan.FromSeconds(advance), new(pointer.X, pointer.Y, 0, left, default, default, default, default));
        }
        internal Rectangle Physical(Rectangle logical)
        {
            var mapping = RenderViewport.Fit(Size, View.DesignHeight);
            var bounds = mapping.ToSurface(logical); bounds.Offset(mapping.Bounds.Location); return bounds;
        }
        internal TableHit Hit(string id) => Frame.Hits.Single(hit => hit.Id == id);
        internal bool TryPoint(TableHit hit, out Point physical)
        {
            var bounds = Physical(hit.Bounds);
            var mapping = RenderViewport.Fit(Size, View.DesignHeight);
            bool Matches(Point point) => point.X >= 0 && point.Y >= 0 && point.X < Size.X && point.Y < Size.Y
                && View.HitAt(mapping.ToLogical(point)) is { } actual && actual.Id == hit.Id && actual.Scope == hit.Scope;
            physical = bounds.Center;
            if (Matches(physical)) return true;
            for (int y = Math.Max(0, bounds.Top); y < Math.Min(Size.Y, bounds.Bottom); y++)
            for (int x = Math.Max(0, bounds.Left); x < Math.Min(Size.X, bounds.Right); x++)
                if (Matches(new(x, y))) { physical = new(x, y); return true; }
            return false;
        }
        internal Point Point(TableHit hit) => TryPoint(hit, out var physical) ? physical
            : throw new InvalidOperationException(Label + ": no exposed physical target for " + hit.Id);
        internal void Click(string id) { var at = Point(Hit(id)); Tick(at, ButtonState.Pressed); Tick(at); }
        internal void Until(Func<bool> condition)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!condition()) {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(Label + ": scoped command did not start.");
                Tick(); Thread.Yield();
            }
        }
        internal void Record(string stage, object value)
        {
            string path = Path.Combine(DirectoryPath, stage + ".json");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        }
        public void Dispose() { release.TrySetResult(); app.Dispose(); }
    }
}
