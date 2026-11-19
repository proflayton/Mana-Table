using System.Collections.Concurrent;
using System.Collections.Immutable;
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
            CheckOverviewFallback(profile, size, check);
            foreach (float textScale in new[] { 1f, 1.3f })
            foreach (bool reducedMotion in new[] { false, true })
                CheckExplore(profile, size, textScale, reducedMotion, check);
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

    private static void CheckOverviewFallback(string profile, Point size, Action<bool, string> check)
    {
        // Authored layout stress, not new provider facts or rules receipts. The
        // oversized non-current source leaves the actual primary unchanged.
        var state = Mana.Scenarios.ExploreTriggersScenario.Snapshot(1);
        var overfull = state with { Revision = state.Revision + 1, BoardRevision = state.BoardRevision + 1,
            Stack = state.Stack.SetItem(3, state.Stack[3] with {
                Name = string.Join(" ", Enumerable.Repeat("Complete source name", 120)) }) };
        using (var run = new Journey(profile, size, 1f, true, 4, "overview-active-no-fit", state)) {
            run.Click("stack-workload-overview"); run.Tick();
            run.View.SetAutomatic(true); run.Tick();
            run.Record("overview-start", new { run.View.StackWorkloadInspectionActive, run.View.ZoneName, run.View.DecisionDock,
                run.View.StackWorkloads, run.View.SeatReadouts, frame = run.Frame.Observe() });
            check(run.View.StackWorkloadInspectionActive && run.View.DecisionDock?.PauseReason == DecisionDockPauseReason.Inspection,
                run.Label + ": an explicit overview owns the current inspection pause before a no-fit transition");
            run.Present(overfull); run.Tick();
            check(!run.View.StackWorkloadInspectionActive && run.View.ZoneName == "Stack"
                    && run.View.DecisionDock?.PauseReason is DecisionDockPauseReason.Inspection or DecisionDockPauseReason.Overlay
                    && run.Commands.IsEmpty,
                run.Label + ": a no-fit overview transfers its explicit reading intent atomically to the existing full-stack inspector");
            run.Tick(advance: .7);
            check(run.View.ZoneName == "Stack" && run.View.DecisionDock?.PauseReason == DecisionDockPauseReason.Overlay
                    && run.View.Automatic && run.Commands.IsEmpty,
                run.Label + ": the fallback retains enabled Auto behind an uninterrupted reading pause without dispatching a pass");
        }
        using (var run = new Journey(profile, size, 1.3f, true, 4, "overview-default-no-fit", overfull)) {
            var bar = run.Hit("stack-workload-overview");
            check(bar.Intent is MatchIntent.OpenStack && run.TryPoint(bar, out _),
                run.Label + ": an unplaceable complete overview exposes the existing full-stack route as a reachable current control");
            run.Click(bar.Id); run.Tick();
            check(run.View.ZoneName == "Stack" && !run.View.StackWorkloadInspectionActive && run.Commands.IsEmpty,
                run.Label + ": the no-fit queue control opens its declared inspector once without an invisible overview or gameplay command");
        }
        using (var run = new Journey(profile, size, 1f, true, 4, "overview-required-no-fit", state)) {
            run.Click("stack-workload-overview"); run.Tick();
            run.View.SetAutomatic(true);
            var required = overfull with { Decision = new("required-overview-layout", new("Make the current required choice."),
                new ConfirmationPayload(new("Continue", true))), ActionClass = "confirmation" };
            var acceptedRequired = run.Present(required); run.Tick();
            var answer = run.Hit("confirm");
            check(!run.View.StackWorkloadInspectionActive && run.View.ZoneName == null
                    && run.View.Match!.Decision?.Id == acceptedRequired.Decision!.Id && run.View.CurrentHit(answer)
                    && run.TryPoint(answer, out _) && run.Commands.IsEmpty,
                run.Label + ": a fresh required choice retires overview intent before no-fit fallback and keeps its exact answer exposed");
        }
    }
    private static Rectangle PanelPaint(Rectangle bounds) => Rectangle.Union(bounds,
        new(bounds.X + 2, bounds.Y + 5, bounds.Width, bounds.Height));
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    private static Rectangle TextBounds(DrawCommand.Text text, float scale)
    {
        var measured = new TextMetrics().TextSize(text.Value ?? "", text.Size, text.Width, text.MaxLines, text.Bold, scale);
        return new(text.X, text.Y, measured.X, measured.Y);
    }

    private static void CheckExplore(string profile, Point size, float scale, bool reduced, Action<bool, string> check)
    {
        var before = Mana.Scenarios.ExploreTriggersScenario.Snapshot(0);
        using var run = new Journey(profile, size, scale, reduced, 4, "explore", before);
        var mixed = Mana.Scenarios.ExploreTriggersScenario.Snapshot(1);
        run.Present(mixed); run.Tick();
        CheckCurrent(Mana.Scenarios.ExploreTriggersScenario.LandEffect, 4);
        check(run.View.AnnouncementId != null && run.View.AnnouncementBounds == null,
            run.Label + ": exact live Explore announcements remain recorded without a second floating effect body");
        run.Click("stack-workload-overview"); run.Tick();
        var groups = run.View.StackWorkloads;
        CheckGroupGeometry();
        check(groups.SelectMany(group => group.MemberIds).Distinct().Count() == 4
                && groups.Count == 3 && groups.Single(group => group.IsNext).Positions.SequenceEqual([1, 3]),
            run.Label + ": land and nonland Explore effects remain separate even on the same physical Nicanzil");
        foreach (var group in groups.ToArray()) {
            var item = run.View.Match!.Stack.Single(item => item.Id == group.MemberIds[0]);
            var sourceHit = run.Hit("stack-workload:" + item.Id);
            run.Tick(run.Point(sourceHit)); run.Tick(advance: .4);
            var overviewReader = run.View.ReferencePreviewBounds;
            check(overviewReader == null && sourceHit.Intent is MatchIntent.OpenStackWorkload
                    || overviewReader is { } clear && !run.View.SeatReadouts.Concat(run.Frame.Hits
                        .Where(hit => hit.Id is "confirm" or "combat-review" or "stack-resolution-arm" or "stack-resolution-stop")
                        .Select(hit => PanelPaint(hit.Bounds))).Any(clear.Intersects),
                run.Label + ": source hover either fits beside the complete overview or preserves the explicit exact-member reading route without covering protected controls");
            run.Tick(new Point(10, 60)); run.Tick();
            run.Click(sourceHit.Id); run.Tick();
            check(run.View.StackRows.Select(row => row.Id).SequenceEqual(group.MemberIds),
                run.Label + ": each overview source opens only its complete exact current member group");
            var member = run.Hit("stack-workload-member:" + item.Id);
            run.Tick(run.Point(member)); run.Tick(advance: .4);
            var reader = run.View.ReferencePreviewBounds;
            var rules = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
                .Where(text => reader?.Contains(text.X, text.Y) == true && text.Size == CardRulesLayout.RulesFont).ToArray();
            var protectedObjects = run.View.SeatReadouts.Concat(run.Frame.Hits
                .Where(hit => hit.Id is "confirm" or "combat-review" or "stack-resolution-arm" or "stack-resolution-stop")
                .Select(hit => PanelPaint(hit.Bounds))).ToArray();
            run.Record("explore-overview-reader-" + item.Id, new { reader, run.View.PendingReaderReservations,
                run.View.PendingReaderFits, protectedObjects, frame = run.Frame.Observe() });
            check(reader != null && run.View.ReferencePreviewStackId == item.Id
                    && run.View.ReferencePreviewCardId == CardViews.Identity(item.Card!)
                    && Normalize(string.Join(" ", rules.Select(text => text.Value))).Contains(Normalize(item.Text), StringComparison.Ordinal)
                    && rules.All(text => reader.Value.Contains(TextBounds(text, scale))),
                run.Label + ": every source from the three-effect overview opens its exact full rules at the existing reading font");
            check(new Rectangle(0, 0, 1600, run.View.DesignHeight).Contains(reader!.Value)
                    && !protectedObjects.Any(reader.Value.Intersects) && run.Commands.IsEmpty,
                run.Label + ": every complete Explore source reader fits the visible canvas without covering owner facts or current controls");
            run.Tick(new Point(10, 60)); run.Tick();
            run.Click("stack-workload-close"); run.Tick();
            run.Click("stack-workload-overview"); run.Tick();
        }
        var target = run.Hit("stack-workload:explore-land-0");
        run.Click(target.Id); run.Tick();
        check(run.Frame.Hits.Any(hit => hit.Id == "stack-workload-member:explore-land-2") && run.Commands.IsEmpty,
            run.Label + ": the source row opens the exact interleaved members without answering priority");
        run.Click("stack-workload-close"); run.Tick();
        run.Click("stack-workload-overview"); run.Tick();
        check(run.View.StackWorkloadInspectionActive, run.Label + ": explicit queue inspection holds reading only while its current workload exists");
        run.Present(Mana.Scenarios.ExploreTriggersScenario.Snapshot(2)); run.Tick();
        check(!run.View.StackWorkloadInspectionActive, run.Label + ": a workload becoming singleton effects retires stale inspection before pacing");
        CheckCurrent(Mana.Scenarios.ExploreTriggersScenario.CounterEffect, 3);
        run.Present(Mana.Scenarios.ExploreTriggersScenario.Snapshot(3)); run.Tick();
        CheckCurrent("Counter target activated or triggered ability.", 4);
        check(run.View.Match!.Stack[0].Id == "explore-response" && run.Commands.IsEmpty,
            run.Label + ": a new opposing response becomes the actual next effect without a stale source-census ordering");
        run.Present(Mana.Scenarios.ExploreTriggersScenario.Snapshot(4)); run.Tick();
        var required = run.Frame.Hits.SingleOrDefault(hit => hit.Id == "confirm");
        check(run.View.Match!.Decision?.Payload is ConfirmationPayload && required != null
                && run.View.CurrentHit(required) && run.TryPoint(required, out _) && run.Commands.IsEmpty,
            run.Label + ": a required current choice retains its exposed scoped answer without the overview answering it");

        // Dense authored layout coverage uses the same legacy unbound execution
        // form as draw-triggers.v1; it does not invent 110 rules receipts.
        var workload = mixed with { Stack = Enumerable.Range(0, 110).Select(index => mixed.Stack[index % mixed.Stack.Length]
            with { Id = "explore-110-" + index, ExecutionId = "" }).ToImmutableArray(), Executions = null };
        run.Present(workload); run.Tick();
        CheckCurrent(Mana.Scenarios.ExploreTriggersScenario.LandEffect, 110);
        run.Click("stack-workload-overview"); run.Tick();
        CheckGroupGeometry();
        check(run.View.StackWorkloads.SelectMany(group => group.MemberIds).Distinct().Count() == 110 && run.Commands.IsEmpty,
            run.Label + ": all 110 current occurrences remain represented in the readable source census");
        run.Record("explore-readable-current", new { run.View.StackWorkloads, run.View.StackRows,
            announcement = run.View.AnnouncementBounds, frame = run.Frame.Observe(), commands = run.Commands.Count });
        run.Present(Mana.Scenarios.ExploreTriggersScenario.Snapshot(0)); run.Tick();
        check(!run.View.StackWorkloadInspectionActive && run.View.StackRows.Count == 0 && run.Commands.IsEmpty,
            run.Label + ": an empty stack retires the invisible inspection pause and all old member references");

        void CheckCurrent(string effect, int pending)
        {
            run.Record("explore-before-assert-" + pending + "-" + run.View.Match!.Stack[0].Id,
                new { run.View.StudyHudConstrained, run.View.StudyAttentionBounds, run.View.StackWorkloads,
                    run.View.StackRows, announcement = run.View.AnnouncementBounds, frame = run.Frame.Observe() });
            var texts = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().ToArray();
            var top = run.View.StackRows.FirstOrDefault(row => row.Position == 1);
            var body = texts.Where(text => text.Value == effect).ToArray();
            check(run.View.Match!.Stack.Length == pending && top != null && body.Length == 1
                    && top.Bounds.Contains(TextBounds(body[0], scale)),
                run.Label + ": one bounded next-effect surface owns the complete actual top effect at " + pending + " pending");
            CheckGroupGeometry();
            check(!run.View.StudyHudConstrained && run.Commands.IsEmpty,
                run.Label + ": the measured trigger workspace fits without changing decisions or shrinking text");
        }

        void CheckGroupGeometry()
        {
            var state = run.View.Match ?? throw new InvalidOperationException("The current Explore group lost its match.");
            run.Record("explore-group-geometry-" + run.View.Now,
                new { run.View.StackWorkloads, run.View.StackRows, frame = run.Frame.Observe(), commands = run.Commands.Count });
            var texts = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().ToArray();
            foreach (var group in run.View.StackWorkloads) {
                var name = texts.Where(text => text.Value == state.Stack.First(item => item.Id == group.MemberIds[0]).Name
                    && group.Bounds.Contains(text.X, text.Y)).ToArray();
                check(name.Length == 1 && group.Bounds.Contains(TextBounds(name[0], scale)),
                    run.Label + ": the complete source name fits its measured row backdrop");
                var regions = new[] { group.NameBounds, group.ControllerBounds, group.EffectBounds,
                    group.CountBounds, group.PositionBounds }.Where(bounds => !bounds.IsEmpty).ToArray();
                check(regions.All(group.Bounds.Contains)
                        && !regions.Where((first, index) => regions.Skip(index + 1).Any(first.Intersects)).Any(),
                    run.Label + ": source, controller, effect, count and actual position occupy distinct measured regions");
            }
        }
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
            var overview = frame.Hits.SingleOrDefault(hit => hit.Id == "stack-workload-overview");
            bool census = frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value?.StartsWith(run.PendingCount + " pending ·", StringComparison.Ordinal) == true)
                || frame.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => text.Value?.StartsWith(run.PendingCount + " pending ·", StringComparison.Ordinal) == true);
            check(first != null && overview != null && census, run.Label + ": the exact pending count retains an explicit source-overview gesture");
            stack.Add(Paint("stack-source-header", PanelPaint(overview!.Bounds)));
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
            run.View.PendingReaderReservations, run.View.PendingReaderFits,
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
                && run.View.AnnouncementId != null && run.View.AnnouncementBounds == null
                && visibleAnnouncement.Length == 1 && run.Commands.IsEmpty,
            run.Label + ": three exact same-source occurrences retain their order and announcement lifecycle with one current effect body");
        CheckLayout(run, "grouped-current-trigger", check);
        CheckReading(run, check);
    }

    private static void CheckEmptyAnnouncement(string profile, Action<bool, string> check)
    {
        using var run = new Journey(profile, new(1000, 740), 1.3f, true, 1, "empty-announcement");
        run.Present(StackCombatHudScenario.CompletedAnnouncement()); run.Tick();
        var announced = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
            .Where(text => run.View.AnnouncementBounds?.Contains(text.X, text.Y) == true
                && text.Value is { } value && (value == StackCombatHudScenario.TriggerText
                || value.StartsWith("Resolved ·", StringComparison.Ordinal) || value == "Mithril Coat"))
            .Select(text => run.Physical(TextBounds(text, run.Scale))).ToArray();
        var buttons = run.Frame.Hits.Where(hit => hit.Id is "confirm" or "combat-review")
            .Select(hit => run.Physical(PanelPaint(hit.Bounds))).ToArray();
        var combat = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().SingleOrDefault(text => text.Value == "2 attacking · 0 blocked");
        var protectedPaint = buttons.Concat(run.View.SeatReadouts.Select(run.Physical))
            .Concat(combat == null ? [] : new[] { run.Physical(TextBounds(combat, run.Scale)) }).ToArray();
        run.Record("stack-empty-announcement", new { announced, run.View.AnnouncementBounds, protectedPaint, frame = run.Frame.Observe() });
        check(run.View.Match?.Stack.IsEmpty == true && run.View.StackRows.Count == 0 && run.View.AnnouncementId != null
                && run.View.AnnouncementBounds is { } notice && announced.Length == 3
                && announced.All(run.Physical(notice).Contains) && combat != null && run.Commands.IsEmpty,
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
        internal GameSnapshot Present(GameSnapshot state) { engine.Present(state); app.QueueObservation(engine.State); return engine.State; }
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
