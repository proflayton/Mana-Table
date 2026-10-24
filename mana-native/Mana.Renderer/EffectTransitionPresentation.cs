using System.Globalization;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

public sealed record EffectPermanentGroup(string FrontId, IReadOnlyList<string> CardIds, Pose Pose, Vector2 CounterLanding)
{
    // Focused tiles keep their name, stats and state alongside the artwork.
    public Rectangle ReceiptExclusion { get; init; }
}
public sealed record EffectCounterDelta(string Property, long? PerPermanent, long Total, int AffectedCount)
{
    public string Label => PerPermanent is { } amount ? (amount > 0 ? "+" : "") + amount.ToString(CultureInfo.InvariantCulture) : "Δ";
}
public sealed record EffectSourcePulse(string EffectId, string CardId, Pose Pose, float Strength, float Expansion);
public sealed record EffectTargetImpact(string EffectId, long LastActivityId, EffectPermanentGroup Target,
    IReadOnlyList<string> AffectedIds, EffectCounterDelta Delta, float Strength, bool Landed)
{
    // A representative is not evidence that every copy changed. Preserve that
    // distinction even when equal final public states share one visual family.
    public string Label => Delta.Label + (AffectedIds.Count == Target.CardIds.Count ? "" : $" ·{AffectedIds.Count}/{Target.CardIds.Count}");
}
public sealed record EffectCounterFlight(string EffectId, string FrontId, EffectCounterDelta Delta,
    Vector2 Origin, Vector2 Position, Vector2 Destination, float Progress);
public sealed record EffectTransitionFrame(IReadOnlyList<EffectSourcePulse> Sources,
    IReadOnlyList<EffectTargetImpact> Targets, IReadOnlyList<EffectCounterFlight> Flights)
{
    public static EffectTransitionFrame Empty { get; } = new([], [], []);
}

/// <summary>
/// Derives a short visual explanation from reported public events and current
/// visible anchors. It never changes a counter, invents a source, or retains a
/// card face. Drawing and input remain outside this timeline.
/// </summary>
public static class EffectTransitionPresentation
{
    public const int MaximumFlights = 6;
    private const double SourceLead = .25, HoldSeconds = 2.1;
    private const double MaximumSeconds = 6;
    private const int TravellingGroups = 48;

    public static EffectTransitionFrame Compose(IReadOnlyList<EffectChangeGroup> effects,
        IReadOnlyList<EffectPermanentGroup> visibleGroups, IReadOnlyDictionary<string, Pose> visibleSources,
        double now, bool reducedMotion, Rectangle protectedControls)
    {
        var byCard = new Dictionary<string, EffectPermanentGroup>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in visibleGroups) {
            var ids = group.CardIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (!ids.Contains(group.FrontId, StringComparer.Ordinal)) continue;
            var frozen = group with { CardIds = Array.AsReadOnly(ids) };
            foreach (string id in ids) if (!byCard.TryAdd(id, frozen)) ambiguous.Add(id);
        }
        foreach (string id in ambiguous) byCard.Remove(id);
        var sources = new List<EffectSourcePulse>();
        var targets = new List<EffectTargetImpact>();
        var flights = new List<EffectCounterFlight>();
        foreach (var effect in effects) {
            var visible = effect.Changes.Where(cue => cue.Change.Kind == "counters" && cue.Change.Before != cue.Change.After
                && now >= cue.Started && now - cue.Started <= MaximumSeconds && byCard.ContainsKey(cue.TargetId)).ToArray();
            if (visible.Length == 0) continue;
            string? sourceId = effect.Cause?.CardId;
            Pose? source = sourceId != null && visibleSources.TryGetValue(sourceId, out var origin) ? origin : null;
            var cohorts = visible.GroupBy(cue => (Front: byCard[cue.TargetId].FrontId, cue.Change.Property, cue.Started))
                .OrderBy(group => group.Min(cue => cue.ActivityId)).ToArray();
            double flightSeconds = cohorts.Length <= MaximumFlights ? .5 : .30;
            double first = effect.Changes.Min(cue => cue.Started), age = now - first;
            bool visibleImpact = false;
            for (int index = 0; index < cohorts.Length; index++) {
                var cohort = cohorts[index];
                var anchor = byCard[cohort.First().TargetId];
                var amounts = cohort.GroupBy(cue => cue.TargetId).Select(group => new {
                    Id = group.Key, Amount = group.Sum(cue => (long)cue.Change.After - cue.Change.Before)
                }).Where(item => item.Amount != 0).ToArray();
                if (amounts.Length == 0) continue;
                var delta = new EffectCounterDelta(cohort.Key.Property,
                    amounts.All(item => item.Amount == amounts[0].Amount) ? amounts[0].Amount : null,
                    amounts.Sum(item => item.Amount), amounts.Length);
                double sinceEvent = now - cohort.Key.Started;
                bool travelling = source != null && !reducedMotion && index < TravellingGroups;
                double launch = travelling ? SourceLead + index / MaximumFlights * flightSeconds : 0;
                double landing = travelling ? launch + flightSeconds : 0;
                double afterLanding = sinceEvent - landing;
                if (afterLanding > HoldSeconds) continue;
                visibleImpact = true;
                float strength = reducedMotion ? .85f : afterLanding < 0 ? .20f : (float)Math.Clamp(1 - afterLanding / HoldSeconds, 0, 1);
                targets.Add(new(effect.Id, cohort.Max(cue => cue.ActivityId), anchor,
                    Array.AsReadOnly(amounts.Select(item => item.Id).ToArray()), delta, strength, afterLanding >= 0));
                if (!travelling || sinceEvent < launch || sinceEvent >= landing || source is not { } start) continue;
                float progress = (float)((sinceEvent - launch) / flightSeconds);
                // Small chips travel in short waves. There is no permanent web
                // of arrows across the field, even when eighty copies changed.
                var position = FlightPoint(start.Center, anchor.CounterLanding, progress, protectedControls);
                if (position is { } point) flights.Add(new(effect.Id, anchor.FrontId, delta,
                    start.Center, point, anchor.CounterLanding, progress));
            }
            if (visibleImpact && source is { } sourcePose && sourceId != null && age >= 0 && age <= MaximumSeconds) {
                float pulse = reducedMotion ? .85f : (float)Math.Clamp(1 - age / .55, .22, 1);
                sources.Add(new(effect.Id, sourceId, sourcePose, pulse, reducedMotion ? 4 : (float)Math.Clamp(age / .55, 0, 1) * 14));
            }
        }
        return new(Array.AsReadOnly(sources.ToArray()), Array.AsReadOnly(targets.ToArray()),
            Array.AsReadOnly(flights.Take(MaximumFlights).ToArray()));
    }

    /// <summary>Place a brief state receipt beside its recipient, never over a face or control.</summary>
    public static Rectangle ReceiptBounds(Rectangle recipient, Vector2 anchor, Point size,
        Rectangle viewport, IReadOnlyList<Rectangle> occupied)
    {
        if (recipient.IsEmpty || size.X <= 0 || size.Y <= 0) return Rectangle.Empty;
        var candidates = new List<Rectangle>();
        foreach (int gap in new[] { 6, 18, 30 }) {
            int y = (int)anchor.Y - size.Y / 2, x = (int)anchor.X - size.X / 2;
            candidates.Add(new(recipient.Right + gap, y, size.X, size.Y));
            candidates.Add(new(recipient.Left - gap - size.X, y, size.X, size.Y));
            candidates.Add(new(x, recipient.Bottom + gap, size.X, size.Y));
            candidates.Add(new(x, recipient.Top - gap - size.Y, size.X, size.Y));
        }
        // A seat-facing source can put its local anchor beside a neighboring
        // lane. Try the rest of that recipient's perimeter before suppressing
        // the result; never move it over a card or into an unrelated UI region.
        foreach (int gap in new[] { 6, 18, 30 }) {
            foreach (int y in new[] { recipient.Center.Y - size.Y / 2, recipient.Top, recipient.Bottom - size.Y }) {
                candidates.Add(new(recipient.Right + gap, y, size.X, size.Y));
                candidates.Add(new(recipient.Left - gap - size.X, y, size.X, size.Y));
            }
            foreach (int x in new[] { recipient.Center.X - size.X / 2, recipient.Left, recipient.Right - size.X }) {
                candidates.Add(new(x, recipient.Bottom + gap, size.X, size.Y));
                candidates.Add(new(x, recipient.Top - gap - size.Y, size.X, size.Y));
            }
        }
        return candidates.FirstOrDefault(candidate => viewport.Contains(candidate)
            && !candidate.Intersects(recipient) && occupied.All(bounds => !candidate.Intersects(bounds)));
    }

    /// <summary>A pending amount follows its arrow through a free gap instead of disappearing at a blocked midpoint.</summary>
    public static Rectangle PendingMarkerBounds(Vector2 start, Vector2 target, Point size,
        Rectangle viewport, IReadOnlyList<Rectangle> occupied)
    {
        if (size.X <= 0 || size.Y <= 0) return Rectangle.Empty;
        var direction = target - start;
        var normal = direction.LengthSquared() > 0 ? Vector2.Normalize(new(-direction.Y, direction.X)) : Vector2.UnitX;
        foreach (float fraction in new[] { .55f, .4f, .7f, .25f, .85f }) {
            var center = Vector2.Lerp(start, target, fraction);
            foreach (float offset in new[] { 0f, size.Y * .75f + 5, -size.Y * .75f - 5 }) {
                var point = center + normal * offset;
                var candidate = new Rectangle((int)point.X - size.X / 2, (int)point.Y - size.Y / 2, size.X, size.Y);
                if (viewport.Contains(candidate) && occupied.All(bounds => !candidate.Intersects(bounds))) return candidate;
            }
        }
        return Rectangle.Empty;
    }

    private static Vector2? FlightPoint(Vector2 start, Vector2 finish, float progress, Rectangle controls)
    {
        var avoid = controls;
        if (!avoid.IsEmpty) avoid.Inflate(18, 18);
        if (!avoid.IsEmpty && (Contains(avoid, start) || Contains(avoid, finish))) return null;
        float eased = progress * progress * (3 - 2 * progress);
        if (avoid.IsEmpty || !Crosses(start, finish, avoid)) return Vector2.Lerp(start, finish, eased);
        // Current table controls occupy the lower-right corner. A two-segment
        // route around their upper-left corner stays outside both instructions
        // and buttons; a single bowed curve could cut back across that corner.
        var corner = new Vector2(avoid.Left - 2, avoid.Top - 2);
        float first = Vector2.Distance(start, corner), second = Vector2.Distance(corner, finish);
        float split = first / Math.Max(1, first + second);
        return eased < split ? Vector2.Lerp(start, corner, eased / Math.Max(.001f, split))
            : Vector2.Lerp(corner, finish, (eased - split) / Math.Max(.001f, 1 - split));
    }
    private static bool Contains(Rectangle area, Vector2 point) => point.X >= area.Left && point.X <= area.Right
        && point.Y >= area.Top && point.Y <= area.Bottom;
    private static bool Crosses(Vector2 start, Vector2 end, Rectangle area)
    {
        float low = 0, high = 1;
        var direction = end - start;
        return Clip(-direction.X, start.X - area.Left) && Clip(direction.X, area.Right - start.X)
            && Clip(-direction.Y, start.Y - area.Top) && Clip(direction.Y, area.Bottom - start.Y);
        bool Clip(float p, float q)
        {
            if (Math.Abs(p) < .001f) return q >= 0;
            float ratio = q / p;
            if (p < 0) { if (ratio > high) return false; low = Math.Max(low, ratio); }
            else { if (ratio < low) return false; high = Math.Min(high, ratio); }
            return true;
        }
    }
}
