using System.Collections.Immutable;
using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static class TableSurfaceProjectionChecks
{
    internal static bool InPlacement(Rectangle placement, Pose surface) => surface.Corners
        .Select(TableSurfaceProjection.Unproject).All(point => point.X >= placement.Left - .01f
            && point.X <= placement.Right + .01f && point.Y >= placement.Top - .01f && point.Y <= placement.Bottom + .01f);
    internal static bool Overlap(Pose first, Pose second)
    {
        var left = first.Corners; var right = second.Corners;
        foreach (var corners in new[] { left, right }) for (int index = 0; index < corners.Length; index++) {
            var edge = corners[(index + 1) % corners.Length] - corners[index];
            var axis = new Vector2(-edge.Y, edge.X);
            var a = left.Select(point => Vector2.Dot(point, axis)).ToArray();
            var b = right.Select(point => Vector2.Dot(point, axis)).ToArray();
            if (a.Max() <= b.Min() + .01f || b.Max() <= a.Min() + .01f) return false;
        }
        return true;
    }

    internal static void Run(Action<bool, string> check)
    {
        var viewport = new Rectangle(0, 54, 1600, 724);
        var camera = new BoardView(new(-122, -38), 1.35f);
        foreach (int seat in new[] { 0, 1, 2, 3 }) {
            var placement = PlayerPlaymat.Bounds(seat);
            var surface = TableSurfaceProjection.Surface(placement);
            var corners = new Pose(placement.Center.ToVector2(), placement.Size.ToVector2()).Corners;
            check(surface.Corners.Zip(corners).All(pair => Near(pair.First, TableSurfaceProjection.Project(pair.Second)))
                    && surface.Corners.All(point => Near(TableSurfaceProjection.Project(TableSurfaceProjection.Unproject(point)), point))
                    && viewport.Contains(surface.Bounds),
                $"Seat {seat}: the same physical mat projects corner-for-corner onto the seated table and round-trips without clipping");
            foreach (bool tapped in new[] { false, true }) {
                var flat = new Pose(placement.Center.ToVector2(), new(72, 100),
                    PlayerPlaymat.Facing(seat) + (tapped ? MathF.PI / 2 : 0));
                var card = TableSurfaceProjection.Project(flat);
                var moved = camera.Apply(card);
                var interior = moved.InteriorBounds(1);
                var band = moved.InteriorBand(interior.Top, interior.Bottom, 1);
                check(card.Corners.Zip(flat.Corners).All(pair => Near(pair.First, TableSurfaceProjection.Project(pair.Second)))
                        && moved.Corners.Zip(card.Corners).All(pair => Near(pair.First, camera.Apply(pair.Second)))
                        && InPlacement(placement, card) && moved.Contains(moved.Center.ToPoint())
                        && new[] { interior.Location, new Point(interior.Right, interior.Top),
                            new Point(interior.Right, interior.Bottom), new Point(interior.Left, interior.Bottom) }.All(moved.Contains)
                        && Corners(band).All(moved.Contains)
                        && !moved.Contains(new(moved.Bounds.Left, moved.Bounds.Top)),
                    $"Seat {seat}, tapped {tapped}: cloth, face, upright state text and pointer share one projection followed by one camera transform");
            }
        }
        var stages = ExperienceScenarios.Find("card-play.v1").Create();
        var before = Scene(stages[1].Snapshot);
        var after = Scene(stages[3].Snapshot);
        var forest = before.BattlefieldCard(0, "play-forest")!;
        check(forest.Pose == after.BattlefieldCard(0, forest.Id)!.Pose
                && after.BattlefieldDrawOrder.All(node => node.Pose.IsProjected
                    && ReferenceEquals(node.Card, stages[3].Snapshot.Players.Single(player => player.Id == node.Owner)
                        .Zone("Battlefield").Cards.Single(card => card.VisualId == node.Id))),
            "A spell landing adds its exact current occurrence on the shared plane without moving the already-played Forest or replacing engine cards");
        PaintedCurrentState(check);
        PaintedSmallCounterState(check);
        FallbackIdentityAndState(check);
        FixedRailSupport(check);
    }

    private static void PaintedCurrentState(Action<bool, string> check)
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        var state = baseline with { Players = baseline.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Count = 1, Cards = [new Card { VisualId = "plane-state-" + player.Id, CombatId = "plane-state-" + player.Id,
                    Name = "Current creature", Type = "Creature", Power = 12, Toughness = 13, Tapped = player.Id % 2 == 0,
                    Counters = ImmutableDictionary<string, int>.Empty.Add("Charge", 2) }]
            }).ToImmutableArray()
        }).ToImmutableArray() };
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableSurfaceChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(profile);
        var engine = new InteractionScenarioEngine(); engine.Present(state);
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
        app.Initialize(); app.SetTableStudy(true);
        app.Update(new GameTime(), new(-100, -100, 0, default, default, default, default, default), new(), new(1600, 900));
        var frame = app.ReadPresentation();
        foreach (var node in frame.Scene!.BattlefieldDrawOrder) {
            var card = frame.Cards.Single(item => item.Id == node.Id);
            var stats = app.TestPort.BattlefieldLabels(node.Id).SingleOrDefault(label => label.Kind == "pt");
            var text = frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
                .SingleOrDefault(command => command.Value == "12/13" && command.Bounds == stats?.Bounds);
            int height = text == null ? 0 : new TextMetrics().TextSize("12/13", text.Size, 1000, 1, true).Y;
            check(node.ScanFace && node.Pose.IsProjected && stats is { Text: "12/13" }
                    && stats.Bounds.Bottom == card.Display.Bounds.Bottom - 1 && stats.Bounds.Height == height
                    && Corners(stats.Bounds).All(card.Display.Contains) && text is { Size: >= .375f }
                    && stats.MeasuredWidth <= stats.AvailableWidth && engine.Replies.Count == 0,
                $"Seat {frame.Scene.Seat(node.Owner).Index}: the projected physical card uses the study renderer's upright, wholly contained current 12/13 instead of falling back to a flat-pose-only label path");
        }
    }

    private static void PaintedSmallCounterState(Action<bool, string> check)
    {
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string profile = Path.Combine(Path.GetTempPath(), "ManaTableSurfaceChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(profile);
            var engine = new InteractionScenarioEngine(); engine.Present(FullBattlefieldSmoke.Example("mixed"));
            using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
            app.Initialize(); app.SetTableStudy(true);
            app.Update(new GameTime(), new(-100, -100, 0, default, default, default, default, default), new(), viewport);
            var frame = app.ReadPresentation();
            var card = frame.Cards.Single(card => card.Id == "mixed-0-4");
            var labels = app.TestPort.BattlefieldLabels(card.Id);
            var stats = labels.SingleOrDefault(label => label.Kind == "pt");
            var counter = labels.SingleOrDefault(label => label.Kind == "counter");
            var text = frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
                .SingleOrDefault(command => command.Value == "3/3" && command.Bounds == stats?.Bounds);
            var metrics = new TextMetrics();
            check(stats is { Text: "3/3" } && counter is { Text: "1" }
                    && stats.MeasuredWidth <= stats.AvailableWidth && counter.MeasuredWidth <= counter.AvailableWidth
                    && text is { Size: >= .375f }
                    && stats.Bounds.Height >= metrics.TextSize("3/3", text.Size, 1000, 1, true, 1.3f).Y
                    && counter.Bounds.Height >= metrics.TextSize("1", .46f, 1000, 1, true, 1.3f).Y
                    && Corners(stats.Bounds).All(card.Display.Contains) && Corners(counter.Bounds).All(card.Display.Contains)
                    && !stats.Bounds.Intersects(counter.Bounds) && engine.Replies.Count == 0,
                $"{viewport.X}x{viewport.Y}, 130% text: the actual small tilted Llanowar Elves paints full current 3/3 and one-counter glyph at their measured fonts, wholly on the same card without overlap or a game command");
        }
    }

    private static Point[] Corners(Rectangle bounds) => [bounds.Location, new(bounds.Right, bounds.Top),
        new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];

    private static void FallbackIdentityAndState(Action<bool, string> check)
    {
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float scale in new[] { 1f, 1.3f }) {
            string profile = Path.Combine(Path.GetTempPath(), "ManaTableFallbackChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = scale }.Save(profile);
            var engine = new InteractionScenarioEngine(); engine.Present(TappedResourcesScenario.Create()[1].Snapshot);
            using var run = new PresentationRun(TestTableServices.For(engine), profile, engine.State, viewport);
            run.EnableLiveTableStudy();
            PresentationFrame frame = null!;
            void Tick(Point? logical = null, ButtonState left = ButtonState.Released, Keys? key = null)
            {
                var at = RenderViewport.Fit(viewport, run.View.DesignHeight).ToPhysical((logical ?? new Point(-100, -100)).ToVector2() + new Vector2(.5f));
                frame = run.Tick(TimeSpan.FromSeconds(.02), new((int)at.X, (int)at.Y, 0, left, default, default, default, default), key is { } pressed ? new(pressed) : new());
            }
            Tick(); Tick();
            foreach (string name in new[] { "Serra Angel", "Luminarch Aspirant", "Tireless Tracker" }) {
                var card = frame.Cards.Single(card => card.Zone == "Battlefield" && card.Card.Name == name);
                var labels = run.View.BattlefieldLabels(card.Id);
                var identity = labels.SingleOrDefault(label => label.Kind == "identity");
                var overlays = labels.Where(label => label.Kind != "identity").ToArray();
                var identityPaint = frame.Drawing.Commands.OfType<DrawCommand.Text>()
                    .SingleOrDefault(text => text.Value == name && text.Size is .47f or .67f
                        && new Rectangle(text.X, text.Y, text.Width,
                            new TextMetrics().TextSize(name, text.Size, text.Width, 80, true, scale).Y).Intersects(card.Display.Bounds));
                var paintedGlyphs = frame.Drawing.Commands.OfType<DrawCommand.Circle>()
                    .Where(circle => circle.Radius == 6 && card.Display.Bounds.Contains(circle.Center))
                    .Select(circle => new Rectangle((int)circle.Center.X - 6, (int)circle.Center.Y - 6, 12, 12)).ToArray();
                check((identityPaint == null ? identity == null && name != "Serra Angel"
                        : identity is { } && identity.Text == name && identityPaint is { Size: >= .47f }
                        && identityPaint.X == identity.Bounds.X && identityPaint.Y == identity.Bounds.Y
                        && identity.Bounds.Height == new TextMetrics().TextSize(name, identityPaint.Size, identityPaint.Width, 80, true, scale).Y
                        && Corners(identity.Bounds).All(card.Display.Contains)
                        && overlays.All(label => !label.Bounds.Intersects(identity.Bounds))
                        && paintedGlyphs.All(glyph => !glyph.Intersects(identity.Bounds))),
                    $"{viewport.X}x{viewport.Y}, {scale:P0}, {name}: the actual fallback paints only a complete measured full-font title and keeps every counter/P-T/copy/keyword glyph clear of its footprint");
                check(labels.Any(label => label.Kind == "pt" && label.MeasuredWidth <= label.AvailableWidth)
                        && (name == "Serra Angel" || labels.Any(label => label.Kind == "counter"
                            && label.Text == (name == "Luminarch Aspirant" ? "2" : "1") && label.MeasuredWidth <= label.AvailableWidth)),
                    $"{name}: protecting fallback identity retains its authoritative P/T and actual counter amount");
                var center = card.Display.Center.ToPoint();
                check(run.View.HitAt(center)?.Card is { } target && CardViews.Same(target, card.Card),
                    $"{name}: the actual physical card still owns its scoped reading target");
                Tick(center); Tick(center);
                check(frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == name && text.Size == .7f)
                        && engine.Replies.Count == 0,
                    $"{name}: its real pointer hover supplies the complete source-attached identity even when the overview title cannot fit, without a game command");
                Tick(center, ButtonState.Pressed); Tick(center); Tick(center);
                check(frame.Cards.Single(item => item.Id == card.Id) is { Focused: true, Display.Size.X: >= 300 }
                        && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == name && text.Size == 1.05f)
                        && (name != "Serra Angel" || frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "Flying")
                            && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "Vigilance"))
                        && engine.Replies.Count == 0,
                    $"{name}: clicking the same occurrence opens its full current identity and abilities without choosing or changing engine state");
                Tick(key: Keys.Escape); Tick(); Tick();
                check(!frame.Cards.Single(item => item.Id == card.Id).Focused && engine.Replies.Count == 0,
                    $"{name}: closing reading restores the same physical occurrence and untouched engine decision");
            }
        }
    }

    private static void FixedRailSupport(Action<bool, string> check)
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        var tabletop = PlayerPlaymat.TabletopPlacement;
        var outside = new List<string>();
        bool OnWood(Vector2 screen)
        {
            var point = TableSurfaceProjection.Unproject(screen);
            bool inside = point.X >= tabletop.Left + 8 && point.X <= tabletop.Right - 8
                && point.Y >= tabletop.Top + 8 && point.Y <= tabletop.Bottom - 8;
            if (!inside) outside.Add($"{screen} -> {point}");
            return inside;
        }
        foreach (int count in new[] { 2, 3, 4 }) {
            var state = baseline with { Players = baseline.Players.Take(count).Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name != "Command" ? zone : zone with {
                    Count = 2, Cards = [new Card { VisualId = "wood-leader-" + player.Id }, new Card { VisualId = "wood-partner-" + player.Id }]
                }).ToImmutableArray()
            }).ToImmutableArray() };
            foreach (int viewer in state.Players.Select(player => player.Id)) {
                outside.Clear();
                var scene = Scene(state with { ViewerId = viewer });
                check(scene.Seats.All(seat => seat.Geometry.PlaymatPlacement is { } placement
                        && tabletop.Contains(new Rectangle(placement.X - 16, placement.Y - 16, placement.Width + 32, placement.Height + 32)))
                        && scene.Seats.Where(seat => seat.Index != 1).All(seat => {
                            var geometry = seat.Geometry;
                            return geometry.Commanders.SelectMany(card => (card.Pose with { Size = card.Pose.Size + new Vector2(13) }).Corners).All(OnWood)
                                && geometry.Piles.All(pile => {
                                    var body = PileGeometry.Create(pile.Pose, 100, pile.Compact);
                                    return body.Shadow.Corners.Concat(body.Top.Corners).Concat(body.Layers.SelectMany(layer => layer.Corners))
                                        .Concat(Corners(body.Label).Select(point => point.ToVector2())).All(OnWood);
                                });
                        }),
                    $"{count} players, viewer {viewer}: one fixed rectangular tabletop leaves a modest mat border and supports every side/near commander backing and full pile shadow/layers/label without moving the far seat HUD"
                        + (outside.Count > 0 ? "; outside: " + string.Join(", ", outside) : ""));
            }
        }
    }

    private static TableScene Scene(Mana.Contracts.GameSnapshot state) => TableScene.Build(state,
        new(-100, -100), null, null, false, CardPresentation.IsResource, sharedTableStudy: true);
    private static bool Near(Vector2 first, Vector2 second) => Vector2.DistanceSquared(first, second) < .0001f;
}
