using System.Collections.Immutable;
using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

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
                check(card.Corners.Zip(flat.Corners).All(pair => Near(pair.First, TableSurfaceProjection.Project(pair.Second)))
                        && moved.Corners.Zip(card.Corners).All(pair => Near(pair.First, camera.Apply(pair.Second)))
                        && InPlacement(placement, card) && moved.Contains(moved.Center.ToPoint())
                        && new[] { interior.Location, new Point(interior.Right, interior.Top),
                            new Point(interior.Right, interior.Bottom), new Point(interior.Left, interior.Bottom) }.All(moved.Contains)
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
            var safe = card.Display.InteriorBounds(1);
            int title = Math.Clamp((int)(safe.Height * .14f), 0, 18);
            safe = new(safe.X, safe.Y + title, safe.Width, Math.Max(0, safe.Height - title));
            int height = Math.Min(22, safe.Height / 3);
            check(node.ScanFace && node.Pose.IsProjected && stats is { Text: "12/13" }
                    && stats.Bounds.Bottom == safe.Bottom && stats.Bounds.Height == height && safe.Contains(stats.Bounds)
                    && stats.MeasuredWidth <= stats.AvailableWidth && engine.Replies.Count == 0,
                $"Seat {frame.Scene.Seat(node.Owner).Index}: the projected physical card uses the study renderer's upright, wholly contained current 12/13 instead of falling back to a flat-pose-only label path");
        }
    }

    private static TableScene Scene(Mana.Contracts.GameSnapshot state) => TableScene.Build(state,
        new(-100, -100), null, null, false, CardPresentation.IsResource, sharedTableStudy: true);
    private static bool Near(Vector2 first, Vector2 second) => Vector2.DistanceSquared(first, second) < .0001f;
}
