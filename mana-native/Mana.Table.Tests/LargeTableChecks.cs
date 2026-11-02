using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static class LargeTableChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        Geometry(check);
        ObserverAndAttachments(check);
        Relationships(check);
        PresentationDetails(profile, check);
        FarHands(profile, check);
        DrawTransfers(profile, check);
        foreach (int count in new[] { 5, 6 })
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) Journey(profile, count, size, check);
    }

    private static void FarHands(string profile, Action<bool, string> check)
    {
        foreach (int count in new[] { 6, 7 }) {
            string path = Path.Combine(profile, "painted-far-hands-" + count);
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(path);
            var state = WithFarMana(WithRemoteHandCount(State(6, 0), count));
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, new(1000, 740));
            run.EnableLiveTableStudy();
            var mouse = new MouseState(-100, -100, 0, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released);
            run.Tick(TimeSpan.FromSeconds(.05), mouse, new());
            var frame = run.Tick(TimeSpan.FromSeconds(.05), mouse, new());
            var painted = frame.Scene!.Seats.Where(seat => seat.Geometry.Frame.IsFar).Select(seat => {
                var hand = seat.Geometry.Hand;
                var backs = frame.Drawing.Commands.OfType<DrawCommand.Surface>().Where(command => command.Texture == null
                    && command.Tint == new Color(27, 41, 49) && !command.Pose.IsProjected && command.Pose.Size == new Vector2(34, 48)
                    && Math.Abs(command.Pose.Center.X - hand.X) < 90 && Math.Abs(command.Pose.Center.Y - hand.Y) < 20)
                    .Select(command => command.Pose).ToArray();
                return new { seat.PlayerId, hand, backs, mana = run.View.ManaPoolBadge(seat.PlayerId)!.Parts };
            }).ToArray();
            File.WriteAllText(Path.Combine(path, "actual-backs-and-mana-first.json"), JsonSerializer.Serialize(painted,
                new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            check(painted.Length == 3 && painted.All(seat => seat.backs.Length == count
                    && seat.backs.All(back => seat.mana.All(part => !back.Bounds.Intersects(part.Bounds)))),
                $"Every far player keeps all {count} actually painted card backs clear of the actual 32/1000-mana symbol and number bounds");
            check(painted.SelectMany(seat => seat.backs).All(back => run.View.ReadoutPlans.Values
                    .SelectMany(plan => plan.Pieces).All(piece => !back.Bounds.Intersects(piece))),
                "The retained far card backs also leave every accepted owner name and land summary readable");
            check(run.View.ReadoutPlans.Values.All(plan => Math.Abs(plan.Header.Bounds.Center.X
                    - frame.Scene.Seat(plan.PlayerId).Geometry.Hero.X) < 40),
                "The full far hand and multi-digit mana cannot displace an owner's complete identity into another player's region");
        }
    }

    private static void PresentationDetails(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "six-seat-physical-details");
        Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(path);
        var state = State(6, 0);
        state = state with { Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => {
                if (zone.Name != "Battlefield") return zone;
                var host = zone.Cards[0] with { Type = "Artifact", Power = null, Toughness = null };
                var aura = new Card { VisualId = "detail-aura-" + player.Id, CombatId = "detail-aura-" + player.Id,
                    Name = "Authored Aura", Type = "Enchantment — Aura", AttachedTo = new(host.VisualId, host.Name, host.Type) };
                host = host with { Attachments = [new(aura.VisualId, aura.Name, aura.Type)] };
                return zone with { Cards = [host, .. zone.Cards.Skip(1), aura], Count = zone.Count + 1 };
            }).ToImmutableArray()
        }).ToImmutableArray() };
        var engine = new InteractionScenarioEngine(); engine.Present(state);
        using var run = new PresentationRun(TestTableServices.For(engine), path, state, new(1000, 740));
        run.EnableLiveTableStudy();
        var mouse = new MouseState(-100, -100, 0, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released);
        run.Tick(TimeSpan.FromSeconds(.05), mouse, new());
        run.Tick(TimeSpan.FromSeconds(.05), mouse, new());
        var painted = new List<object>();
        foreach (var player in state.Players.Where(player => player.Id != state.ViewerId)) {
            var active = state with { Revision = player.Id, ActivePlayerId = player.Id };
            engine.Present(active); run.QueueObservation(active);
            var frame = run.Tick(TimeSpan.FromSeconds(.05), mouse, new());
            var physical = frame.Scene!.Seat(player.Id).Geometry;
            var glyphCenter = physical.Hero + new Vector2(0, -50);
            var glyph = frame.Drawing.Commands.OfType<DrawCommand.Circle>().Any(circle => circle.Radius == 15
                && Vector2.Distance(circle.Center, glyphCenter) < .1f);
            check(glyph == !physical.Frame.IsFar,
                $"Owner {player.Id}: the active phase marker uses the actual near table edge while every far seat keeps the established clear portrait rail");
            var host = player.Zone("Battlefield").Cards[0];
            var node = frame.Scene.BattlefieldCard(player.Id, host.VisualId)!;
            var pose = frame.BoardView.Apply(node.Pose);
            var badge = run.View.AttachmentBadge(host.CombatId);
            check(badge != null && pose.Bounds.Contains(badge.Bounds)
                    && (!physical.Frame.IsFar || !badge.Compact || badge.Bounds.Top <= pose.Bounds.Top + 3),
                $"Owner {player.Id}: the actual attached-artifact status stays inside its card and uses the far-facing exposed edge for every far physical place");
            painted.Add(new { player.Id, physical.Frame, glyph, glyphCenter, pose, badge });
        }
        File.WriteAllText(Path.Combine(path, "actual-painted-details-first.json"), JsonSerializer.Serialize(painted,
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
    }

    private static void DrawTransfers(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "six-seat-draw-transfers");
        Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = false, Sounds = false }.Save(path);
        var original = State(6, 0);
        var engine = new InteractionScenarioEngine(); engine.Present(original);
        using var run = new PresentationRun(TestTableServices.For(engine), path, original, new(1000, 740));
        run.EnableLiveTableStudy();
        var mouse = new MouseState(-100, -100, 0, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released);
        run.Tick(TimeSpan.FromSeconds(.05), mouse, new());
        run.Tick(TimeSpan.FromSeconds(1), mouse, new());
        var drawn = original with { Revision = 1, Activity = [
            new(1, original.Turn, original.PhaseKey, "draw", 4, "Ash drew a card.", null, null) { Count = 1 },
            new(2, original.Turn, original.PhaseKey, "draw", 5, "Vale drew a card.", null, null) { Count = 1 }
        ] };
        engine.Present(drawn);
        var accepted = engine.State;
        run.QueueObservation(accepted);
        run.Tick(TimeSpan.FromSeconds(.05), mouse, new());
        var frame = run.Tick(TimeSpan.FromSeconds(.15), mouse, new());
        var flying = frame.Drawing.Commands.OfType<DrawCommand.Surface>().Where(command => command.Texture == null
            && command.Tint == new Color(27, 41, 49) && command.Pose.Elevation > 0).Select(command => command.Pose).ToArray();
        var expected = new[] { 4, 5 }.Select(owner => {
            var geometry = frame.Scene!.Seat(owner).Geometry;
            var origin = frame.BoardView.Apply(PileGeometry.Create(geometry.Piles.Single(pile => pile.Zone == "Library"),
                drawn.Players.Single(player => player.Id == owner).Zone("Library").Count).Top);
            int count = Math.Min(7, drawn.Players.Single(player => player.Id == owner).Zone("Hand").Count);
            var destination = TableLayout.HiddenHand(geometry.Index, Math.Max(0, count - 1), count);
            destination = destination with { Center = destination.Center + geometry.Hand - TableLayout.SeatHand(geometry.Index) };
            return new { owner, origin, destination, expected = TablePresence.DrawPose(origin, destination, .15) };
        }).ToArray();
        using (var stream = File.Create(Path.Combine(path, "actual-painted-transfers-first.json")))
            JsonSerializer.Serialize(stream, new { frame.BoardView, flying, expected, accepted.Revision,
                publicationUnchanged = ReferenceEquals(engine.State, accepted), gameplayCommands = engine.Replies.Count },
                new JsonSerializerOptions { IncludeFields = true, WriteIndented = true });
        check(flying.Length == 2 && expected.All(item => flying.Any(pose => Vector2.Distance(pose.Center, item.expected.Center) < .1f
                && Vector2.Distance(pose.Size, item.expected.Size) < .1f)),
            "Actual fifth/sixth-seat anonymous draw backs travel from their own camera-mapped libraries to their own hidden hand anchors");
        check(ReferenceEquals(engine.State, accepted) && engine.Replies.Count == 0,
            "The extra-seat draw animation consumes public events without hidden card identity, gameplay commands or authoritative-state replacement");
    }

    private static void Relationships(Action<bool, string> check)
    {
        foreach (int viewer in Enumerable.Range(0, 6)) {
            var state = State(6, viewer);
            foreach (var other in state.Players) {
                string expected = other.Id == viewer ? "" : other.TeamId == state.Viewer!.TeamId ? "ALLY" : "OPPONENT";
                check(SeatReadoutPlan.RelationshipLabel(state, other.Id) == expected,
                    $"Viewer {viewer}/player {other.Id}: the relationship cue follows actual team membership, not mat color or physical side");
            }
            var unknown = state with { Players = state.Players.Select(player => player.Id != viewer ? player : player with { TeamId = null }).ToImmutableArray() };
            check(unknown.Players.All(player => SeatReadoutPlan.RelationshipLabel(unknown, player.Id).Length == 0),
                "Missing team metadata never invents an ally or opponent label");
        }
        var ffa = State(4, 0) with { Players = State(4, 0).Players.Select(player => player with { TeamId = player.Id }).ToImmutableArray() };
        check(ffa.Players.All(player => SeatReadoutPlan.RelationshipLabel(ffa, player.Id).Length == 0),
            "A legacy free-for-all without a cooperative encounter keeps its existing identity header unchanged");
        var observer = State(6, 0) with { ViewerId = -1 };
        check(observer.Players.All(player => SeatReadoutPlan.RelationshipLabel(observer, player.Id).Length == 0),
            "An independent observer sees no invented self-relative relationship labels");
    }

    private static void ObserverAndAttachments(Action<bool, string> check)
    {
        foreach (int count in new[] { 5, 6 }) {
            var publicState = State(count, 0) with { ViewerId = -1 };
            var publicScene = Scene(publicState);
            check(publicScene.Seats.Count == count && publicScene.Seats.Select(seat => seat.Index).Distinct().Count() == count
                    && publicScene.Seats.All(seat => !seat.Geometry.Frame.IsLocal && !seat.Geometry.Frame.LocalApron)
                    && publicScene.Hand.Total == 0,
                "An independent public observer sees all physical seats without inventing a local player or private held hand");
        }
        var state = State(6, 0);
        var host = state.Players[0].Zone("Battlefield").Cards.First(card => card.Type.Contains("Creature", StringComparison.Ordinal));
        var attached = state.Players[5].Zone("Battlefield").Cards[0] with {
            Name = "Authored foreign Aura", Type = "Enchantment — Aura", AttachedTo = new(host.VisualId, host.Name, host.Type)
        };
        host = host with { Attachments = [new(attached.VisualId, attached.Name, attached.Type)] };
        state = state with { Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => card.VisualId == host.VisualId ? host
                    : card.VisualId == attached.VisualId ? attached : card).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() };
        var scene = Scene(state);
        var aura = scene.BattlefieldCard(5, attached.VisualId);
        check(aura?.Attachment is { RootOwner: 0 } relation && relation.RootId == host.VisualId && aura.Owner == 5
                && TableSurfaceProjectionChecks.Overlap(aura.Pose, scene.Seat(0).Geometry.PlaymatSurface!.Value),
            "The sixth seat's foreign Aura follows its exact host while retaining its real controller and individual reading identity");
    }

    private static void Geometry(Action<bool, string> check)
    {
        foreach (int count in new[] { 2, 3, 4, 5, 6 })
        foreach (int viewer in Enumerable.Range(0, count)) {
            var state = State(count, viewer);
            var scene = Scene(state);
            var home = TableViewGeometry.Overview(scene);
            check(scene.Seats.Count == count && scene.Seats.Select(seat => seat.PlayerId).Order()
                    .SequenceEqual(state.Players.Select(player => player.Id).Order()),
                $"{count} seats/viewer {viewer}: every registered player has a complete scene seat, including eliminated spectators");
            foreach (var seat in scene.Seats) {
                var geometry = seat.Geometry;
                var frame = geometry.Frame;
                var mat = geometry.PlaymatPlacement!.Value;
                check(mat.Width * mat.Height == 512 * 288 && scene.TabletopPlacement.Contains(mat)
                        && scene.Seats.Where(other => other.PlayerId != seat.PlayerId)
                            .All(other => !mat.Intersects(other.Geometry.PlaymatPlacement!.Value)),
                    $"{count} seats/viewer {viewer}/owner {seat.PlayerId}: one unchanged canonical mat occupies a distinct physical place");
                if (count <= 4) {
                    var approvedMat = seat.Index switch { 0 => new Rectangle(224, 192, 288, 512),
                        1 => new Rectangle(544, 160, 512, 288), 2 => new Rectangle(1088, 192, 288, 512),
                        _ => new Rectangle(544, 516, 512, 288) };
                    var approvedHero = seat.Index switch { 0 => new Vector2(102, 330), 1 => new Vector2(800, 108),
                        2 => new Vector2(1498, 330), _ => new Vector2(800, 744) };
                    float approvedFacing = count == 2 ? 0 : seat.Index switch { 0 => MathF.PI / 2,
                        1 => MathF.PI, 2 => -MathF.PI / 2, _ => 0 };
                    check(mat == approvedMat && geometry.Hero == approvedHero && home.IsIdentity && frame.Facing == approvedFacing,
                        "Approved two/three/four-player mat transforms, facing and overview remain unchanged");
                } else {
                    check(mat.Contains(geometry.ZoneStation!.Placement) && !frame.LocalApron
                            && frame.QuarterTurns is 0 or 2,
                        $"Owner {seat.PlayerId}: personal zones stay on that player's mat rather than crossing the new neighboring seat");
                    foreach (var pile in geometry.Piles) {
                        var top = PileGeometry.Create(pile, 100).Top;
                        check(scene.Seats.Where(other => other.PlayerId != seat.PlayerId)
                                .All(other => !TableSurfaceProjectionChecks.Overlap(top, other.Geometry.PlaymatSurface!.Value)),
                            $"Owner {seat.PlayerId} {pile.Zone}: the actual top of a full stack never borrows another player's mat");
                    }
                    if (!frame.IsLocal) {
                        var corners = home.Apply(geometry.PlaymatSurface!.Value).Corners;
                        var edge = frame.IsFar ? (corners[0] + corners[1]) / 2 : (corners[2] + corners[3]) / 2;
                        float distance = frame.IsFar ? edge.Y - frame.Hero.Y : frame.Hero.Y - edge.Y;
                        check(Math.Abs(frame.Hero.X - edge.X) < 1 && distance is >= 60 and <= 146,
                            "Each remote identity anchor remains directly beside its own actual home-painted mat edge, rather than on a detached screen rail");
                    }
                }
                check(frame.IsLocal == (seat.PlayerId == viewer) && (!frame.IsLocal
                        || geometry.Hero == TableLayout.Hero(3) && geometry.Hand == TableLayout.LocalHand),
                    "The local life portrait and hand remain at the approved central viewpoint for every viewer");
                check(scene.Battlefield(seat.PlayerId).SelectMany(cluster => cluster.Cards).All(card => card.ScanFace
                        && Math.Abs(card.Pose.Size.X / card.Pose.Size.Y - 488f / 680) < .0001f),
                    "Every permanent retains the same full portrait printing silhouette, including lands and support cards");
            }
            if (count <= 4) continue;
            var changed = state with { Players = state.Players.Select(player => player with {
                Eliminated = true, Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = [.. zone.Cards, .. Enumerable.Range(0, 100).Select(index => new Card {
                        VisualId = $"extra-{player.Id}-{index}", Name = "Forest", Type = "Basic Land — Forest", Tapped = index % 2 == 0
                    })], Count = zone.Count + 100
                }).ToImmutableArray()
            }).ToImmutableArray() };
            var after = Scene(changed);
            check(TableViewGeometry.Overview(after) == home && scene.TabletopPlacement == after.TabletopPlacement
                    && scene.Seats.All(seat => seat.Geometry.Frame == after.Seat(seat.PlayerId).Geometry.Frame
                        && seat.Geometry.Piles.Select(pile => pile.Pose).SequenceEqual(after.Piles(seat.PlayerId).Select(pile => pile.Pose))),
                "Elimination, a hundred new permanents and tap changes cannot repack the seats or silently reframe the camera");
        }
    }

    private static void Journey(string profile, int count, Point size, Action<bool, string> check)
    {
        string path = Path.Combine(profile, $"large-table-{count}-{size.X}");
        Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(path);
        var engine = new InteractionScenarioEngine(); engine.Present(State(count, 0));
        using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
        run.EnableLiveTableStudy();
        var placements = new Dictionary<int, object>();
        run.View.SetReadoutPlacer((plans, reserved, viewport, occupied) => {
            var preferred = plans[0];
            var result = SeatReadoutPlan.Place(plans, reserved, viewport, occupied);
            placements[preferred.PlayerId] = new { preferred.PlayerId, preferred.Header, preferred.Bounds, viewport,
                collisions = preferred.Pieces.SelectMany(piece => occupied.Where(obstacle => obstacle.Intersects(piece))
                    .Select(obstacle => new { piece, obstacle })),
                placed = result is SeatReadoutPlacement.Placed placed ? placed.Plan.Header : null };
            return result;
        });
        PresentationFrame frame = null!;
        void Tick(Point? point = null, ButtonState left = ButtonState.Released, Keys? key = null)
        {
            var physical = RenderViewport.Fit(size, run.View.DesignHeight).ToPhysical((point ?? new(-100, -100)).ToVector2() + new Vector2(.5f));
            frame = run.Tick(TimeSpan.FromSeconds(.05), new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default),
                key is { } pressed ? new(pressed) : new());
        }
        void Click(Point at) { Tick(at, ButtonState.Pressed); Tick(at); Tick(at); }
        Tick(); Tick();
        var accepted = engine.State;
        var scene = frame.Scene!;
        var home = TableViewGeometry.Overview(scene);
        File.WriteAllText(Path.Combine(path, "identity-placement-first.json"), JsonSerializer.Serialize(placements,
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        check(run.View.TableStudyEnabled && scene.Seats.Count == count && frame.BoardView == home
                && frame.Hits.Count(hit => hit.Id.StartsWith("player:", StringComparison.Ordinal)) == count,
            $"{count} players at {size.X}: actual application paints every player and starts at the same larger physical table");
        var wood = frame.Drawing.Commands.OfType<DrawCommand.Polygon>().Single(command => command.Color == new Color(49, 40, 33));
        var paintedCorners = wood.Points.Select(home.Apply).ToArray();
        var paintedBounds = new Rectangle((int)paintedCorners.Min(point => point.X), (int)paintedCorners.Min(point => point.Y),
            (int)MathF.Ceiling(paintedCorners.Max(point => point.X) - paintedCorners.Min(point => point.X)),
            (int)MathF.Ceiling(paintedCorners.Max(point => point.Y) - paintedCorners.Min(point => point.Y)));
        check(paintedBounds.Width >= 1500 && paintedBounds.Top is >= 230 and <= 300
                && paintedBounds.Bottom is >= 640 and < 720,
            "The actual painted larger table fills the usable arena width and reaches the near seat, rather than shrinking between detached HUD rails");
        check(run.View.ReadoutPlans.Count == count - 1 && accepted.Players.Where(player => player.Id != accepted.ViewerId)
                .All(player => {
                    string relationship = SeatReadoutPlan.RelationshipLabel(accepted, player.Id);
                    var lands = PublicResources.Lands(player);
                    string facts = (relationship.Length == 0 ? "" : relationship + " \u00b7 ") + lands.Total
                        + (lands.Total == 1 ? " land" : " lands") + $" \u00b7 {lands.Untapped} untapped \u00b7 {lands.Tapped} tapped";
                    return run.View.ReadoutPlans.TryGetValue(player.Id, out var plan) && plan.Header.Text == player.Name
                        && frame.Drawing.Commands.OfType<DrawCommand.Text>().Count(text => text.Value == plan.Header.Text) == 1
                        && plan.Text.Any(text => text.Role == SeatReadoutTextRole.ResourceSummary && text.Text == facts
                            && frame.Drawing.Commands.OfType<DrawCommand.Text>().Count(command => command.Value == facts
                                && command.X == text.Bounds.X && command.Y == text.Bounds.Y) == 1);
                }),
            "All opponents have a distinct complete name/land/tap readout; the fifth and sixth seats are not omitted");
        check(run.View.ReadoutPlans.Values.SelectMany(plan => plan.Pieces).All(piece =>
                scene.Hand.Cards.All(card => !piece.Intersects(card.Pose.Bounds))
                && !piece.Intersects(run.View.DecisionPanelBounds)),
            "The complete remote identity and land summaries remain clear of the actual local hand and priority action area");
        check(run.View.ReadoutPlans.Values.All(plan => Math.Abs(plan.Header.Bounds.Center.X - scene.Seat(plan.PlayerId).Geometry.Hero.X) < 40),
            "Every complete owner header remains beneath its own physical portrait instead of migrating to a different player's edge");
        check(frame.Hits.Any(hit => hit.Id == "confirm") && frame.Hits.Any(hit => hit.Id == "quiet-priority-arm"),
            "The large-table journey checks a real-shaped local main-phase priority offer and its full action controls");
        var local = scene.Seat(accepted.ViewerId).Geometry;
        var localLands = local.LandReadoutArea!.Value;
        File.WriteAllText(Path.Combine(path, "local-summary-first.json"), JsonSerializer.Serialize(new { localLands,
            local.Hero, hand = scene.Hand.Cards.Select(card => card.Pose.Bounds), phases = local.TurnControls.PhaseBounds,
            readouts = run.View.ReadoutPlans.Values.Select(plan => plan.Bounds),
            actual = frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.X == localLands.X && text.Y == localLands.Y),
            expected = PublicResources.Lands(accepted.Viewer!).Total + " lands" },
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        check(localLands.Right < local.Hero.X && local.Hero.X - localLands.Center.X <= 200
                && scene.Hand.Cards.All(card => !localLands.Intersects(card.Pose.Bounds))
                && local.TurnControls.PhaseBounds.All(bounds => !localLands.Intersects(bounds))
                && run.View.ReadoutPlans.Values.All(plan => !localLands.Intersects(plan.Bounds))
                && frame.Drawing.Commands.OfType<DrawCommand.Text>().Count(text => text.X == localLands.X
                    && text.Y == localLands.Y && text.Value == PublicResources.Lands(accepted.Viewer!).Total + " lands") == 1,
            "Your single painted land summary stays beside the central portrait, clear of the phase wheel and hand, rather than borrowing the near-left ally's apron");
        foreach (var player in accepted.Players) {
            var portrait = frame.Hits.Single(hit => hit.Id == "player:" + player.Id);
            check(run.View.HitAt(portrait.Bounds.Center)?.Id == portrait.Id,
                $"{count} players: owner {player.Id}'s actual life portrait remains reachable without another player's hit covering it");
        }
        using (var stream = File.Create(Path.Combine(path, "overview.json")))
            JsonSerializer.Serialize(stream, new { count, size, frame.BoardView, paintedBounds,
                seats = scene.Seats.Select(seat => new { seat.PlayerId, seat.Geometry.Frame, seat.Geometry.PlaymatPlacement,
                    seat.Geometry.Hero, seat.Geometry.Piles, commanders = seat.Geometry.Commanders.Select(card => new { card.Id, card.Pose }) }),
                readouts = run.View.ReadoutPlans }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true });
        foreach (int owner in accepted.Players.Where(player => player.Id != accepted.ViewerId).Select(player => player.Id)) {
            var focus = frame.Hits.Single(hit => hit.Id == "focus:" + owner);
            Click(focus.Bounds.Center);
            check(run.View.StudyFocusOwner == owner && frame.BoardView.Scale > home.Scale
                    && frame.Scene!.Seats.Count == count && frame.Scene.Seats.All(seat => seat.Geometry.Frame == scene.Seat(seat.PlayerId).Geometry.Frame),
                $"Owner {owner}: clicking that player's complete header zooms into the same physical setup without hiding or rebuilding other seats");
            foreach (var pile in frame.Scene!.Piles(owner)) {
                var caption = frame.BoardView.Apply(pile.Caption!.Value);
                var actual = run.View.HitAt(caption.Center);
                check(actual?.Intent is MatchIntent.OpenZone open && open.PlayerId == owner && open.Zone == pile.Zone,
                    $"Focused owner {owner}: the actual {pile.Zone} caption still names that owner's read-only zone");
            }
            Tick(key: Keys.Home); Tick(); Tick();
            check(frame.BoardView == home && run.View.StudyFocusOwner == null,
                "Home returns to the configured larger table, rather than the four-seat identity camera");
        }
        Tick(key: Keys.D); Tick();
        check(frame.BoardView != home && frame.Scene!.Seats.All(seat => seat.Geometry.Frame == scene.Seat(seat.PlayerId).Geometry.Frame),
            "Keyboard panning moves the larger table in one continuous space and leaves seat ownership intact");
        Tick(key: Keys.Home); Tick(); Tick();
        check(frame.BoardView == home && engine.Replies.Count == 0 && ReferenceEquals(engine.State, accepted),
            "Overview/focus/pan/return consumes no rules command and never refreshes or replaces the accepted game state");
    }

    private static TableScene Scene(GameSnapshot state) => TableScene.Build(state, new(-100, -100), null, null,
        false, CardPresentation.IsResource, sharedTableStudy: true);

    internal static GameSnapshot State(int count, int viewer)
    {
        var source = FourPlayerTableScenario.Create()[0].Snapshot;
        var names = new[] { "You", "Rowan", "Mira", "Jules", "Ash", "Vale" };
        var players = Enumerable.Range(0, count).Select(index => {
            var player = source.Players[index % source.Players.Length];
            Card Face(Card card, string zone, int position) => card with {
                Key = "", VisualId = $"large-{index}-{zone}-{position}", CombatId = $"large-{index}-{zone}-{position}",
                Selectable = false, Highlighted = false, AttachedTo = null, Attachments = []
            };
            return player with { Id = index, Name = names[index], Priority = index == viewer, TeamId = index < 4 ? 0 : 1,
                Mana = ImmutableDictionary<string, int>.Empty.Add("W", 1).Add("U", 2).Add("B", 3).Add("R", 4).Add("G", 5).Add("C", 6),
                Zones = player.Zones.Select(zone => zone with {
                    Cards = zone.Name == "Hand" ? index != viewer ? []
                        : source.Viewer!.Zone("Hand").Cards.Select((card, position) => Face(card, zone.Name, position)).ToImmutableArray()
                        : zone.Cards.Select((card, position) => Face(card, zone.Name, position)).ToImmutableArray(),
                    Count = zone.Name == "Hand" && index == viewer ? source.Viewer!.Zone("Hand").Count : zone.Count,
                    TopCard = null
                }).ToImmutableArray() };
        }).ToImmutableArray();
        return source with { Id = "large-table-" + count, ViewerId = viewer, Players = players,
            Decision = count > 4 ? new("authored-large-priority", new DecisionContext(),
                new PriorityPayload(new("Pass priority", true), QuietYieldToken: "authored-quiet-window")) : null,
            DecisionOwnerId = count > 4 ? viewer : null, PriorityPlayerId = viewer, ActivePlayerId = viewer,
            ActionClass = count > 4 ? "priority" : "waiting" };
    }

    internal static GameSnapshot WithRemoteHandCount(GameSnapshot state, int count) => state with {
        Players = state.Players.Select(player => player.Id == state.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = count } : zone).ToImmutableArray()
        }).ToImmutableArray()
    };

    internal static GameSnapshot WithFarMana(GameSnapshot state) => state with {
        Players = state.Players.Select(player => TableSeatFrame.For(TableSeatFrame.DisplayIndex(state, player.Id), state.Players.Length).IsFar
            ? player with { Mana = player.Mana.ToImmutableDictionary(pair => pair.Key, pair => pair.Key is "W" or "G" ? 32 : 1000) }
            : player).ToImmutableArray()
    };
}
