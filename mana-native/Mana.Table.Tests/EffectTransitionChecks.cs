using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
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
        CheckReceiptPlacement(check);
        CheckCounterDrawing(check);
        CheckFamilyCaptionDrawing(check);
        catIds.Clear();
        check(cats.Target.CardIds.Count == 50 && cats.AffectedIds.Count == 50,
            "A composed effect frame freezes occurrence membership independently of later caller mutations");
    }

    private static void CheckFamilyCaptionDrawing(Action<bool, string> check)
    {
        foreach (bool focused in new[] { false, true }) {
            string profile = Path.Combine(Path.GetTempPath(), "ManaTableCounterFamilyChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            new PlayPreferences { ReducedMotion = true, Automatic = false, Sounds = false, TextScale = 1.3f }.Save(profile);
            var baseline = ClutteredChangesScenario.Earthbend(ClutteredChangesScenario.Initial());
            var engine = new InteractionScenarioEngine(); engine.Present(baseline);
            using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
            app.Initialize(); app.SetTableStudy(true);
            if (focused) app.TestPort.OpenBattlefield(1);
            Mana.Table.Presentation.PresentationFrame Tick(double seconds)
            {
                var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.01));
                app.Update(time, new(), new(), new(1600, 900));
                return app.ComposePresentation(time);
            }
            var initial = Tick(10);
            var represented = initial.Cards.Where(card => card.Zone == "Battlefield").Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
            engine.Present(ClutteredChangesScenario.Retreat(baseline)); app.TestPort.Accept(engine.State);
            var frame = Tick(10.9);
            var paintedNames = frame.Drawing.Commands.OfType<DrawCommand.Text>().ToArray();
            var families = frame.Scene!.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards)
                .Select(card => card.StudyFamily).OfType<StudyBattlefieldFamily>().DistinctBy(family => family.Id)
                .Where(family => family.CohortCount > 1 && paintedNames.Any(text => text.Value == family.Name
                    && text.X == family.NameBounds.X + 4 && text.Y == family.NameBounds.Y)).ToArray();
            check(families.Any(family => family.Name == "Forest") && app.TestPort.CounterReceipts.Count > 0
                && app.TestPort.CounterReceipts.All(receipt => families.All(family => !receipt.Bounds.Intersects(family.NameBounds)))
                && represented.SetEquals(frame.Cards.Where(card => card.Zone == "Battlefield").Select(card => card.Id))
                && (!focused || app.TestPort.StudyFocusOwner == 1),
                "The actual 480-permanent " + (focused ? "focused table" : "whole table")
                    + " keeps visible Forest/Plains/Island family captions outside the new counter receipts while retaining the cards and localized increments");
        }
    }

    private static void CheckReceiptPlacement(Action<bool, string> check)
    {
        var face = new Rectangle(300, 250, 100, 120);
        var viewport = new Rectangle(10, 60, 1580, 720);
        var blockedRight = new Rectangle(400, 230, 140, 150);
        var obstacles = new[] { face, blockedRight };
        var receipt = EffectTransitionPresentation.ReceiptBounds(face, new(390, 350), new(85, 28), viewport, obstacles);
        check(!receipt.IsEmpty && viewport.Contains(receipt) && obstacles.All(bounds => !bounds.Intersects(receipt))
            && receipt.Right < face.Left,
            "A counter increment moves beside its actual recipient when the counter side is blocked without covering its current total, name or stats");
        var second = EffectTransitionPresentation.ReceiptBounds(face, new(390, 350), new(85, 28), viewport, [.. obstacles, receipt]);
        check(!second.IsEmpty && !second.Intersects(receipt),
            "Distinct counter-property receipts reserve their space instead of painting over an earlier increment");
        check(EffectTransitionPresentation.ReceiptBounds(face, new(390, 350), new(85, 28), viewport, [viewport]).IsEmpty,
            "A completely crowded recipient keeps its actual card state rather than forcing a transient counter receipt over other permanents");
        var midpointBlock = new Rectangle(440, 240, 200, 120);
        var marker = EffectTransitionPresentation.PendingMarkerBounds(new(300, 300), new(800, 300), new(70, 30),
            viewport, [midpointBlock]);
        check(!marker.IsEmpty && !marker.Intersects(midpointBlock) && viewport.Contains(marker),
            "A blocked arrow midpoint can use another free gap for its pending base-damage marker");
        check(EffectTransitionPresentation.PendingMarkerBounds(new(300, 300), new(800, 300), new(70, 30),
            viewport, [viewport]).IsEmpty,
            "A pending marker never covers cards or answer controls when its whole arrow corridor is occupied");
    }

    private static void CheckCounterDrawing(Action<bool, string> check)
    {
        foreach (bool reduced in new[] { false, true }) {
            string profile = Path.Combine(Path.GetTempPath(), "ManaTableCounterReceiptChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            new PlayPreferences { ReducedMotion = reduced, Automatic = false, Sounds = false }.Save(profile);
            var source = new Card { VisualId = "receipt-source", CombatId = "receipt-source", Name = "Reported counter source", Type = "Enchantment" };
            var recipient = new Card { VisualId = "receipt-target", CombatId = "receipt-target", Name = "Counter recipient", Type = "Artifact Creature",
                Power = 6, Toughness = 7, Counters = ImmutableDictionary<string, int>.Empty.Add("Charge", 4) };
            var baseline = PresentationSmoke.Example() with { Id = "receipt-drawing", Stack = [], Activity = [], Combat = null };
            baseline = baseline with { Players = baseline.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Count = zone.Name == "Battlefield" ? player.Id == 0 ? 2 : 0 : zone.Count,
                Cards = zone.Name == "Battlefield" ? player.Id == 0 ? [source, recipient] : [] : []
            }).ToImmutableArray() }).ToImmutableArray() };
            var engine = new InteractionScenarioEngine(); engine.Present(baseline);
            using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
            app.Initialize(); app.SetTableStudy(true);
            Mana.Table.Presentation.PresentationFrame Tick(double seconds)
            {
                var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.01));
                app.Update(time, new(), new(), new(1600, 900));
                return app.ComposePresentation(time);
            }
            Tick(10);
            var changed = baseline with {
                Players = baseline.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Cards = zone.Cards.Select(card => card.VisualId == recipient.VisualId ? card with { Counters = ImmutableDictionary<string, int>.Empty.Add("Charge", 6) } : card).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray(),
                Activity = [new(1, baseline.Turn, baseline.PhaseKey, "counters", 0, "Two charge counters added.", recipient.VisualId, recipient.Name) {
                    Change = new("counters", "Charge", 4, 6), Cause = new(source.VisualId, source.Name, "Put two charge counters on the artifact.", 0, "ability", "receipt-effect")
                }]
            };
            engine.Present(changed); app.TestPort.Accept(engine.State);
            var frame = Tick(10.9);
            var receipt = frame.Drawing.Commands.OfType<DrawCommand.Text>().SingleOrDefault(text => text.Value == "+2 Charge");
            var painted = app.TestPort.CounterReceipts.SingleOrDefault(item => item.FrontId == recipient.VisualId);
            var card = frame.Cards.Single(card => card.Id == recipient.VisualId && card.Zone == "Battlefield");
            check(receipt != null && painted is { Label: "+2 Charge", Property: "Charge" }
                && painted.AffectedIds.SequenceEqual([recipient.VisualId]) && card.Card is { Power: 6, Toughness: 7 }
                && card.Card.Counters["Charge"] == 6 && frame.Cards.All(entry => !entry.Display.Bounds.Intersects(painted.Bounds))
                && engine.Replies.Count == 0,
                "The actual " + (reduced ? "reduced-motion" : "animated") + " composition paints +2 Charge outside the card while retaining six current counters, authoritative 6/7 and no game action");
            frame = Tick(11.8);
            check(frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "+2 Charge"),
                "The counter increment remains readable after landing instead of existing only as an unlabelled moving spark");
            frame = Tick(15);
            check(!frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "+2 Charge")
                && frame.Cards.Single(card => card.Id == recipient.VisualId && card.Zone == "Battlefield").Card.Counters["Charge"] == 6,
                "The transient increment expires while the authoritative settled counter total remains available");
        }
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
