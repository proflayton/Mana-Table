using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Regression checks for public attribution, counts and animation geometry.
// Visual clarity is reviewed in actual windows; these do not establish it.
internal static class EffectTransitionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var cause = new PublicEffectCause("retreat", "Felidar Retreat", "Put a counter on each creature.", 0, "trigger", "landfall-1");
        var catIds = Enumerable.Range(0, 50).Select(index => "cat-" + index).ToList();
        var groups = new List<EffectPermanentGroup> {
            new("cat-49", catIds, new(new(400, 300), new(26, 34)), new(440, 340))
        };
        groups.AddRange(Enumerable.Range(0, 32).Select(index => new EffectPermanentGroup("other-" + index,
            new[] { "other-" + index }, new(new(600 + index % 8 * 50, 200 + index / 8 * 60), new(26, 34)), new(620 + index % 8 * 50, 230 + index / 8 * 60))));
        var sources = new Dictionary<string, Pose> { ["retreat"] = new(new(180, 400), new(26, 34)) };
        CounterChangeCue Cue(long id, string target, int before = 0, int after = 1) => new(id, target, "Public permanent",
            new("counters", "+1/+1", before, after), cause, 10);
        var effect = new EffectChangeGroup("cause:landfall-1", cause,
            groups.SelectMany(group => group.CardIds).Select((id, index) => Cue(index + 1, id)).ToArray(), 10);
        var controls = new Rectangle(1240, 550, 350, 345);
        EffectTransitionFrame Frame(double time, bool reduced = false) => EffectTransitionPresentation.Compose([effect], groups, sources, time, reduced, controls);
        var still = Frame(10.2, true);
        check(still.Sources.Single().CardId == "retreat" && still.Flights.Count == 0
            && still.Targets.SelectMany(target => target.AffectedIds).Distinct().Count() == 82,
            "Reduced motion keeps the reported source and every affected public occurrence without travelling chips");
        var cats = still.Targets.Single(target => target.Target.FrontId == "cat-49");
        check(cats.Delta.PerPermanent == 1 && cats.Delta.Total == 50 && cats.Label == "+1" && cats.AffectedIds.Count == 50,
            "Fifty matching copies show a per-copy +1 rather than incorrectly displaying +50 on one token");
        check(Enumerable.Range(1, 30).Select(index => Frame(10 + index * .05)).All(frame => frame.Flights.Count <= EffectTransitionPresentation.MaximumFlights)
            && Frame(10.5).Flights.Count > 0 && Frame(10.8).Targets.Any(target => target.Landed),
            "Motion has a bounded wave of counter chips followed by landing impacts");
        var repeated = effect with { Updated = 1000 };
        check(EffectTransitionPresentation.Compose([repeated], groups, sources, 15, false, controls) is { Sources.Count: 0, Targets.Count: 0, Flights.Count: 0 },
            "Reobserving a finished cause cannot replay its events by changing a group update timestamp");
        var missingSource = EffectTransitionPresentation.Compose([effect], groups, new Dictionary<string, Pose>(), 10.2, false, controls);
        check(missingSource.Sources.Count == 0 && missingSource.Flights.Count == 0 && missingSource.Targets.Count == 33,
            "A source that is no longer public has localized target feedback without a retained or invented source face");
        var sameName = EffectTransitionPresentation.Compose([effect], groups,
            new Dictionary<string, Pose> { ["Felidar Retreat"] = sources["retreat"] }, 10.2, false, controls);
        check(sameName.Sources.Count == 0 && sameName.Flights.Count == 0,
            "A matching name never substitutes for the engine-issued source identity");
        var hidden = effect with { Changes = new[] { Cue(100, "hidden-hand-card") } };
        check(EffectTransitionPresentation.Compose([hidden], groups, sources, 10.2, false, controls) is { Sources.Count: 0, Targets.Count: 0, Flights.Count: 0 },
            "A change without a currently visible public target cannot reveal a location or animate a private card");
        var partial = effect with { Changes = new[] { Cue(101, "cat-0", 20, 21), Cue(102, "cat-1", 19, 21) } };
        var partialCue = EffectTransitionPresentation.Compose([partial], groups, sources, 10.2, true, controls).Targets.Single();
        check(partialCue.Delta.PerPermanent == null && partialCue.Delta.Total == 3 && partialCue.Label == "Δ ·2/50"
            && partialCue.AffectedIds.SequenceEqual(new[] { "cat-0", "cat-1" }),
            "Nonuniform changes retain their exact affected subset and never assert one invented per-copy amount");
        var removed = effect with { Changes = new[] { Cue(103, "other-0", 4, 2) } };
        check(EffectTransitionPresentation.Compose([removed], groups, sources, 10.2, true, controls).Targets.Single() is { Label: "-2", Delta.Total: -2 },
            "Counter removal preserves its signed amount rather than turning it into a gain");
        CheckProtectedRoute(check, cause);
        catIds.Clear();
        check(cats.Target.CardIds.Count == 50 && cats.AffectedIds.Count == 50,
            "A composed effect frame freezes occurrence membership independently of later caller mutations");
    }

    private static void CheckProtectedRoute(Action<bool, string> check, PublicEffectCause cause)
    {
        var controls = new Rectangle(1240, 550, 350, 345);
        var source = new Dictionary<string, Pose> { ["retreat"] = new(new(1500, 500), new(26, 34)) };
        var group = new EffectPermanentGroup("target", new[] { "target" }, new(new(1200, 750), new(26, 34)), new(1200, 750));
        var effect = new EffectChangeGroup("route", cause,
            new[] { new CounterChangeCue(1, "target", "Public target", new("counters", "Charge", 0, 1), cause, 10) }, 10);
        var points = Enumerable.Range(0, 400).SelectMany(index => EffectTransitionPresentation.Compose([effect], [group], source,
            10 + index / 100.0, false, controls).Flights.Select(flight => flight.Position)).ToArray();
        check(points.Length > 0 && points.All(point => !controls.Contains((int)point.X, (int)point.Y)),
            "Counter-flight geometry routes around the priority instructions and buttons instead of crossing their corner");
    }
}
