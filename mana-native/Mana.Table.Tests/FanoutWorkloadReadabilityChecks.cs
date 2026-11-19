using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Authored pointer input over an actual accepted110-trigger checkpoint. This
// separate observe-only application never resamples the recorded callback replay.
internal static class FanoutWorkloadReadabilityChecks
{
    internal sealed record Result(ImmutableHashSet<string> Names, object Evidence);
    internal static bool AnnouncementVisible(TableApplication.Probe view, PresentationFrame frame,
        StackArrivalPresentation.Announcement announcement)
    {
        if (view.AnnouncementBounds is { } notice)
            return frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == announcement.Name
                && notice.Contains(TextBounds(text, frame.Drawing.TextScale)));
        var top = view.Match?.Stack.FirstOrDefault();
        return top != null && (top.Id == announcement.StackId
                || announcement.ExecutionId.Length > 0 && top.ExecutionId == announcement.ExecutionId)
            && top.Card is { FaceDown: false } source && CardViews.Identity(source) == announcement.SourceId
            && PrimaryVisible(view, frame, top);
    }

    private static bool PrimaryVisible(TableApplication.Probe view, PresentationFrame frame, StackItem top)
    {
        var body = view.StackRows.SingleOrDefault(row => row.Position == 1 && row.Id == top.Id);
        if (body == null) return false;
        var text = frame.Drawing.Commands.OfType<DrawCommand.Text>().ToArray();
        return text.Any(item => item.Value == top.Name && item.Size >= .67f && body.Bounds.Contains(TextBounds(item, frame.Drawing.TextScale)))
            && text.Any(item => item.Value == top.Text && item.Size >= .66f && body.Bounds.Contains(TextBounds(item, frame.Drawing.TextScale)));
    }

    internal static Result Run(string directory, GameSnapshot burst, Point viewport, Action<bool, string> check)
    {
        var expected = burst.Stack.GroupBy(item => CardViews.Identity(item.Card!))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        check(burst.Stack.Length == 110 && expected.Count == 2 && expected.Values.All(items => items.Length == 55
                && items.All(item => item.Card is { FaceDown: false } && item.ControllerId == items[0].ControllerId
                    && item.Name == items[0].Name && item.Text == items[0].Text)),
            "The actual audience checkpoint retains two distinct physical/controller/effect cohorts of55 exact pending occurrences");
        var inspected = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (float scale in new[] { 1f, 1.3f }) {
            string path = Path.Combine(directory, "workload-reading-" + (int)(scale * 100)); Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = false, TextScale = scale, Sounds = false }.Save(path);
            var playback = new ProjectionPlayback(new(1, "actual-fanout-reading", null, [burst]));
            var metadata = new ExperienceLabEngine(new ExperienceLabSession());
            using var run = new PresentationRun(new(metadata, metadata, playback, metadata.InitializeAsync), path, burst, viewport,
                authority: ClientSessionAuthority.ObserveOnly);
            run.EnableLiveTableStudy();
            Point pointer = new(10, 60);
            void Tick(Point? point = null, ButtonState left = ButtonState.Released, Point? size = null)
            {
                pointer = point ?? pointer;
                run.Tick(TimeSpan.FromSeconds(1d / 60), new(pointer.X, pointer.Y, 0, left, default, default, default, default), viewport: size);
            }
            Tick(); Tick();
            var initialDecision = run.View.Match!.Decision;
            var initialFinite = run.View.Match.FiniteStackYield;
            Point At(string id)
            {
                var hit = run.Frame.Hits.Single(item => item.Id == id);
                var mapping = RenderViewport.Fit(run.Viewport, run.View.DesignHeight);
                var area = mapping.ToSurface(hit.Bounds); area.Offset(mapping.Bounds.Location);
                bool Exposed(Point point) => run.View.HitAt(mapping.ToLogical(point)) is { } current
                    && current.Id == hit.Id && current.Scope == hit.Scope;
                if (Exposed(area.Center)) return area.Center;
                for (int y = Math.Max(0, area.Top); y < Math.Min(run.Viewport.Y, area.Bottom); y++)
                for (int x = Math.Max(0, area.Left); x < Math.Min(run.Viewport.X, area.Right); x++)
                    if (Exposed(new(x, y))) return new(x, y);
                throw new InvalidDataException("The actual fanout exposes no physical pointer target for " + id);
            }
            void MoveAway() { Tick(new(10, 60)); Tick(); }
            void Click(string id) { var point = At(id); Tick(point, ButtonState.Pressed); Tick(point); MoveAway(); }
            void Save(string stage) => File.WriteAllText(Path.Combine(path, stage + ".json"), JsonSerializer.Serialize(new {
                audience = "viewer-private", burst.Id, burst.ViewerId, burst.Revision, scale,
                scope = "Authored physical pointer reading over an actual accepted checkpoint; no rules rerun, original pointer capture or human approval.",
                run.View.StackWorkloads, run.View.StackRows, run.View.ReferencePreviewStackId, run.View.ReferencePreviewCardId,
                run.View.ReferencePreviewBounds, run.View.PendingReaderReservations, run.View.SeatReadouts, frame = run.Frame.Observe()
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            void Protected(Rectangle area, string stage)
            {
                var controls = run.Frame.Hits.Where(hit => hit.Id is "confirm" or "combat-review" or "stack-resolution-arm" or "stack-resolution-stop")
                    .Select(hit => { var bounds = hit.Bounds; bounds.Inflate(3, 6); return bounds; });
                check(!controls.Concat(run.View.SeatReadouts).Any(area.Intersects),
                    viewport + "/" + scale + ": " + stage + " preserves protected response/combat controls and owner facts");
            }
            void Geometry(TableApplication.StackWorkloadPaint group)
            {
                var regions = new[] { group.NameBounds, group.ControllerBounds, group.EffectBounds, group.CountBounds, group.PositionBounds };
                check(regions.All(group.Bounds.Contains)
                        && !regions.Where((area, index) => regions.Skip(index + 1).Any(area.Intersects)).Any(),
                    viewport + "/" + scale + ": source/controller/effect/count/position occupy separate bounded regions");
                var name = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().LastOrDefault(text => text.Value == group.Name
                    && group.NameBounds.Contains(text.X, text.Y));
                var controller = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().LastOrDefault(text => text.Value == group.Controller
                    && group.ControllerBounds.Contains(text.X, text.Y));
                var count = run.Frame.Drawing.Commands.OfType<DrawCommand.CenterText>().SingleOrDefault(text => text.Bounds == group.CountBounds
                    && text.Value == "× " + group.MemberIds.Length);
                var metrics = new TextMetrics();
                check(name != null && group.NameBounds.Contains(TextBounds(name, scale)) && name.Size >= .67f
                        && controller != null && group.ControllerBounds.Contains(TextBounds(controller, scale)) && controller.Size >= .48f
                        && count != null && count.Size >= .82f
                        && metrics.TextSize(count.Value!, count.Size, count.Bounds.Width, 1, count.Bold, scale).X <= count.Bounds.Width
                        && metrics.TextSize(count.Value!, count.Size, count.Bounds.Width, 1, count.Bold, scale).Y <= count.Bounds.Height,
                    viewport + "/" + scale + ": complete source/controller and actual cohort count retain their full typography");
                var effect = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().LastOrDefault(text => text.Value == group.EffectPreview
                    && group.EffectBounds.Contains(text.X, text.Y));
                var position = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().LastOrDefault(text => group.PositionBounds.Contains(text.X, text.Y));
                check(effect != null && group.EffectBounds.Contains(TextBounds(effect, scale)) && effect.Size >= .52f
                        && position != null && group.PositionBounds.Contains(TextBounds(position, scale)),
                    viewport + "/" + scale + ": actual effect and stack-position glyphs stay inside their separate regions");
            }
            Save("00-current");
            check(run.View.StackWorkloads is [{ IsNext: true } current] && current.MemberIds.Length == 55
                    && current.MemberIds[0] == burst.Stack[0].Id && PrimaryVisible(run.View, run.Frame, burst.Stack[0]),
                viewport + "/" + scale + ": one actual primary owns its complete current source/effect and55 pending members");
            Geometry(run.View.StackWorkloads[0]); Protected(run.View.StudyAttentionBounds, "current effect/queue");
            foreach (var (source, members) in expected) {
                MoveAway(); Click("stack-workload-overview"); Save("01-overview-" + members[0].Id);
                Tick(size: viewport.X == 1000 ? new(1540, 980) : new(1000, 740)); Tick();
                check(run.View.StackWorkloadInspectionActive && run.View.ZoneName == null,
                    viewport + "/" + scale + ": a changed but fitting viewport recomputes the approved overview without leaving inspection");
                Tick(size: viewport); Tick();
                var groups = run.View.StackWorkloads;
                check(groups.Count == 2 && groups.SelectMany(group => group.MemberIds).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(burst.Stack.Select(item => item.Id)) && groups.Sum(group => group.MemberIds.Length) == 110,
                    viewport + "/" + scale + ": explicit overview retains every one of the110 exact current IDs once");
                foreach (var group in groups) { Geometry(group); Protected(group.Bounds, "overview source"); }
                var cohort = groups.Single(group => group.MemberIds.Contains(members[0].Id));
                check(cohort.MemberIds.ToHashSet(StringComparer.Ordinal).SetEquals(members.Select(item => item.Id))
                        && cohort.Positions.SequenceEqual(burst.Stack.Select((item, index) => (item, index))
                            .Where(pair => CardViews.Identity(pair.item.Card!) == source).Select(pair => pair.index + 1)),
                    viewport + "/" + scale + ": the source cohort preserves its physical identity and absolute interleaved positions");
                var rowId = "stack-workload:" + cohort.MemberIds[0];
                Tick(At(rowId)); Tick(); Save("02-full-reader-" + members[0].Id);
                var reader = run.View.ReferencePreviewBounds;
                var rules = run.Frame.Drawing.Commands.Select(command => command switch {
                    DrawCommand.Text text when text.Size == CardRulesLayout.RulesFont && reader?.Contains(text.X, text.Y) == true => text.Value,
                    DrawCommand.CenterText text when text.Size == CardRulesLayout.RulesFont && reader?.Contains(text.Bounds) == true => text.Value,
                    _ => null
                }).Where(value => value != null);
                check(reader != null && run.View.ReferencePreviewStackId == members[0].Id && run.View.ReferencePreviewCardId == source
                        && Tokens(string.Join(" ", rules)).Contains(Tokens(members[0].Text), StringComparison.Ordinal),
                    viewport + "/" + scale + ": hovering the exact source reads its full actual rules, including the generic payment symbol");
                Protected(reader!.Value, "full actual ability reader");
                MoveAway(); Click(rowId); Save("03-exact-fan-" + members[0].Id);
                check(run.View.StackRows.Count == 55 && run.View.StackRows.Select(row => row.Id).SequenceEqual(members.Select(item => item.Id))
                        && run.View.StackRows.Select(row => row.Position).SequenceEqual(cohort.Positions),
                    viewport + "/" + scale + ": source inspection fans all55 exact members without reordering the engine stack");
                var deeper = members[^1]; Tick(At("stack-workload-member:" + deeper.Id)); Tick();
                check(run.View.ReferencePreviewStackId == deeper.Id && run.View.ReferencePreviewCardId == source,
                    viewport + "/" + scale + ": a deeper member remains an exact source-owned read-only reference");
                Save("04-deeper-reader-" + deeper.Id); MoveAway(); Click("stack-workload-close");
                check(run.View.StackWorkloads is [{ IsNext: true }] && !run.View.StackWorkloadInspectionActive
                        && PrimaryVisible(run.View, run.Frame, burst.Stack[0]),
                    viewport + "/" + scale + ": closing inspection restores the same actual current effect and collapsed queue");
                inspected.Add(members[0].Name);
            }
            check(playback.ObservationRequests == 0 && playback.RejectedCommands == 0
                    && ReferenceEquals(run.View.Match!.Decision, initialDecision) && ReferenceEquals(run.View.Match.FiniteStackYield, initialFinite)
                    && run.View.Match.Stack.Select(item => item.Id).SequenceEqual(burst.Stack.Select(item => item.Id)),
                viewport + "/" + scale + ": all authored reading gestures preserve the actual decision, finite grant and complete stack without provider reads or attempted commands");
            Save("05-returned");
        }
        return new(inspected.ToImmutable(), new {
            version = 1, sourceQueueSize = burst.Stack.Length, textScales = new[] { 1f, 1.3f },
            currentPrimaryValidated = true, protectedControlsValidated = true,
            sources = expected.Select(pair => new {
                sourceId = pair.Key, name = pair.Value[0].Name,
                memberIds = pair.Value.Select(item => item.Id), executionIds = pair.Value.Select(item => item.ExecutionId),
                fullRulesValidated = true, exactFanValidated = true
            })
        });
    }

    private static string Tokens(string text) => string.Join(" ", Regex.Matches(text.Replace("{", "").Replace("}", ""), @"[\p{L}\p{N}_]+|[^\s]")
        .Select(match => match.Value));
    private static Rectangle TextBounds(DrawCommand.Text text, float scale)
    {
        var size = new TextMetrics().TextSize(text.Value ?? "", text.Size, text.Width, text.MaxLines, text.Bold, scale);
        return new(text.X, text.Y, size.X, size.Y);
    }
}
