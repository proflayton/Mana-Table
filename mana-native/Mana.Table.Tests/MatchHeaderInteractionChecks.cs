using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class MatchHeaderInteractionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckPlaymatReadouts(profile, check);
        CheckPlaymatResourceCamera(profile, check);
        CheckSidePlaymatReadouts(profile, check);
        CheckManaReadingLateLayers(profile, check);
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float scale in new[] { 1f, 1.3f }) {
            string path = Path.Combine(profile, $"match-header-{size.X}-{scale}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = scale }.Save(path);
            var stages = FourPlayerTableScenario.Create();
            var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
            var services = TestTableServices.For(engine) with { Social = new HeaderConversation() };
            using var app = new TableApplication(services, path, initialState: engine.State) { Automated = true };
            app.Initialize(); app.EnableLiveTableStudy();
            var view = app.TestPort;
            var lobby = new Lobby("joined", "Playing", null, false, null, true, "", null, [], [], "header-table");
            view.SetConnectedTable(lobby); view.PresentLobby(lobby, true);
            double time = 0;
            PresentationFrame Tick(Point? point = null, ButtonState button = ButtonState.Released)
            {
                time += .016;
                var at = point ?? new(-100, -100);
                app.Update(new(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(.016)),
                    new(at.X, at.Y, 0, button, default, default, default, default), new(), size);
                return app.ReadPresentation();
            }
            var frame = Tick();
            var chat = frame.Hits.Single(hit => hit.Id == "chat-toggle").Bounds;
            engine.Present(stages[2].Snapshot with { States = [new("day-night", "Day", "status", null, null, "It is day.", "day-night")] });
            view.Accept(engine.State); frame = Tick();
            var controls = frame.Hits.Where(hit => hit.Bounds.Top >= 0 && hit.Bounds.Bottom <= 54).ToArray();
            check(controls.Any(hit => hit.Id == "recent-changes") && controls.Any(hit => hit.Id == "table-states")
                    && controls.Single(hit => hit.Id == "chat-toggle").Bounds == chat
                    && controls.All(first => controls.All(second => first.Id == second.Id || !first.Bounds.Intersects(second.Bounds))),
                "The real connected header keeps chat, recent changes, table states and navigation in separate stable slots at both window sizes and text scales");
            Point Physical(string id)
            {
                var hit = frame.Hits.Single(hit => hit.Id == id);
                var viewport = RenderViewport.Fit(size, frame.DesignHeight);
                var pixel = viewport.ToPhysical(hit.Bounds.Center.ToVector2());
                var physical = new Point((int)MathF.Round(pixel.X), (int)MathF.Round(pixel.Y));
                check(view.HitAt(viewport.ToLogical(physical))?.Id == id,
                    "The visible header control owns its actual physical pointer target: " + id);
                return physical;
            }
            foreach (var control in controls) Physical(control.Id);
            void Click(string id) { var at = Physical(id); Tick(at, ButtonState.Pressed); frame = Tick(at); }
            Click("chat-toggle");
            check(view.ChatOpen, "Table chat opens from its own visible header button");
            Click("chat-toggle"); Click("recent-changes");
            check(!view.ChatOpen && view.Overlay == "changes" && engine.Replies.Count == 0,
                "Recent changes opens independently of chat without submitting a game action");
        }
    }

    private static void CheckPlaymatResourceCamera(string profile, Action<bool, string> check)
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        const int topId = 2;
        var viewport = new Rectangle(0, 54, 1600, 724);
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float textScale in new[] { 1f, 1.3f })
        foreach (int count in new[] { 3, 4 }) {
            var state = baseline with {
                Players = baseline.Players.Take(count).Select(player => player.Id == baseline.ViewerId ? player : player with {
                    Mana = new[] { "W", "U", "B", "R", "G", "C" }.ToImmutableDictionary(symbol => symbol, _ => 1),
                    States = [new("counter:energy", "Energy", "counter", player.Id, null, "Public energy counters."),
                        new("counter:poison", "Poison", "counter", player.Id + 1, null, "Public poison counters.")]
                }).ToImmutableArray(),
                Combat = new([new(baseline.Viewer!.Zone("Battlefield").Cards[0].VisualId, topId, [], [])], [], null)
            };
            string path = Path.Combine(profile, $"playmat-camera-resources-{count}-{size.X}-{textScale}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = textScale }.Save(path);
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
            run.EnableLiveTableStudy();
            var view = run.View;
            PresentationFrame frame = null!;
            PresentationFrame Tick(double seconds = .02, Keys? key = null, Point? logical = null, int wheel = 0)
            {
                var pointer = logical is { } point
                    ? RenderViewport.Fit(size, view.DesignHeight).ToPhysical(point.ToVector2()).ToPoint() : new Point(-100, -100);
                return frame = run.Tick(TimeSpan.FromSeconds(seconds),
                    new(pointer.X, pointer.Y, wheel, default, default, default, default, default),
                    key is { } held ? new(held) : new());
            }
            void Home() { Tick(key: Keys.Home); Tick(); }
            Tick(); Tick();
            var original = view.Match;
            var canonical = frame.Scene!.BattlefieldDrawOrder.ToDictionary(card => card.Id, card => card.Pose);
            bool IsAuxHit(string id, int owner) => id == "player-counters:" + owner
                || id.StartsWith("player-state:" + owner + ":", StringComparison.Ordinal) || id == "player-state-more:" + owner;
            var observed = frame.Observe().Text;
            var groups = view.WorldReadouts.ToDictionary(pair => pair.Key, pair => new {
                World = pair.Value, Badge = view.CounterBadge(pair.Key)!,
                Hits = frame.Hits.Where(hit => IsAuxHit(hit.Id, pair.Key)).ToArray(),
                Text = observed.Where(text => text.Bounds.Height > 0 && pair.Value.Canonical.Any(bounds =>
                    bounds.Intersects(new Rectangle(text.Bounds.X, text.Bounds.Y, text.Bounds.Width, text.Bounds.Height)))).ToArray(),
                Name = frame.Hits.Single(hit => hit.Id == "focus:" + pair.Key).Bounds,
                Portrait = frame.Hits.Single(hit => hit.Id == "player:" + pair.Key).Bounds,
                Mana = view.ManaPoolBadge(pair.Key)!.Bounds
            });
            var fixedReadouts = view.SeatReadouts.Where(bounds => !groups.Values.Any(group =>
                group.World.Canonical.Any(piece => bounds.Contains(piece)))).ToArray();
            check(groups.Count == count - 1 && groups.All(pair => pair.Value.World.Visible
                    && pair.Value.Badge.Chips.Length == 2 && pair.Value.Hits.Length == 3 && pair.Value.Text.Length >= 4),
                $"{count} seats, {size.X}px {textScale:P0}: every opponent starts with complete visible world counters and land readouts, distinct from fixed identity and mana");

            void CheckView(string journey)
            {
                var camera = view.BoardView;
                Rectangle Visible(Rectangle world) => Rectangle.Intersect(camera.Apply(world), viewport);
                check(ReferenceEquals(view.Match, original) && engine.Replies.Count == 0
                        && frame.Scene!.BattlefieldDrawOrder.All(card => canonical[card.Id] == card.Pose),
                    journey + ": camera navigation retains the exact authoritative snapshot, canonical occurrences and zero game commands");
                var textNow = frame.Observe().Text;
                foreach (var (owner, group) in groups) {
                    var current = view.WorldReadouts[owner];
                    var projected = group.World.Canonical.Select(Visible).Where(bounds => !bounds.IsEmpty).ToArray();
                    bool blocked = projected.Any(bounds => FixedReadoutObstruction(frame, view, state, fixedReadouts, bounds));
                    bool expected = projected.Length > 0 && !blocked;
                    check(current.Canonical.SequenceEqual(group.World.Canonical) && current.Visible == expected
                            && current.Displayed.SequenceEqual(expected ? projected : []),
                        journey + $": opponent {owner}'s complete world group is projected once and shown exactly when actual fixed paint leaves it clear"
                            + $" (expected={expected}, visible={current.Visible}, pieces={projected.Length}, blocked={blocked})");
                    var badge = view.CounterBadge(owner);
                    check(expected ? badge != null && badge.Bounds == Visible(group.Badge.Bounds)
                            && projected.All(piece => view.SeatReadouts.Any(bounds => bounds.Contains(piece)))
                        : badge == null && projected.All(piece => !view.SeatReadouts.Contains(piece)),
                        journey + ": counter and measured-readout probes follow the same complete visibility decision as paint");
                    check(frame.Hits.Single(hit => hit.Id == "focus:" + owner).Bounds == group.Name
                            && frame.Hits.Single(hit => hit.Id == "player:" + owner).Bounds == group.Portrait
                            && view.ManaPoolBadge(owner)!.Bounds == group.Mana,
                        journey + ": world resources preserve fixed portraits, player names and mana controls");
                    foreach (var world in group.Hits) {
                        var bounds = Visible(world.Bounds);
                        var hit = frame.Hits.SingleOrDefault(candidate => candidate.Id == world.Id);
                        var chipCenters = group.Badge.Chips.Select(chip => camera.Apply(chip.Bounds.Center)).ToArray();
                        var samples = chipCenters.Append(bounds.Center).Append(new(bounds.Center.X, 53)).ToArray();
                        check(!expected || bounds.IsEmpty ? hit == null : hit != null && hit.Bounds == bounds
                                && hit.Scope == world.Scope && hit.Intent == world.Intent && hit.Shape != null
                                && viewport.Contains(hit.Bounds) && samples.All(point => hit.Contains(point)
                                    == (viewport.Contains(point) && bounds.Contains(point) && world.Contains(camera.Inverse(point)))),
                            journey + ": actual visible resource controls retain their scope and inverse pointer geometry; hidden groups leave no hit: " + world.Id);
                        if (expected && hit != null && world.Id == "player-counters:" + owner) {
                            var paintedCenters = chipCenters.Where(point => viewport.Contains(point) && bounds.Contains(point)).ToArray();
                            check(paintedCenters.All(point => world.Contains(camera.Inverse(point)) && hit.Contains(point)),
                                journey + ": the aggregate counter control accepts each actual visible painted chip");
                            var gapCandidates = group.Badge.Chips.SelectMany(chip => new[] {
                                new Point(chip.Bounds.Right + 1, chip.Bounds.Center.Y),
                                new Point(chip.Bounds.Center.X, chip.Bounds.Bottom + 1)
                            }).Append(world.Bounds.Center);
                            var gaps = gapCandidates.Where(point => world.Bounds.Contains(point)
                                    && !group.Badge.Chips.Any(chip => chip.Bounds.Contains(point)))
                                .Select(camera.Apply).Where(point => bounds.Contains(point) && viewport.Contains(point)
                                    && !group.Badge.Chips.Any(chip => chip.Bounds.Contains(camera.Inverse(point)))).ToArray();
                            if (gaps.Length > 0) check(gaps.All(point => !world.Contains(camera.Inverse(point)) && !hit.Contains(point)),
                                journey + ": blank space between painted counter rows remains noninteractive through the inverse camera shape");
                        }
                    }
                    foreach (var text in group.Text) {
                        var bounds = Visible(new(text.Bounds.X, text.Bounds.Y, text.Bounds.Width, text.Bounds.Height));
                        bool painted = textNow.Any(item => item.Value == text.Value && item.Bounds == ObservedBounds.From(bounds)
                            && MathF.Abs(item.Size - text.Size * camera.Scale) < .001f);
                        check(painted == (expected && !bounds.IsEmpty),
                            journey + ": observed resource text agrees with the complete group's actual clipped paint: " + text.Value);
                    }
                }
            }
            foreach (int focus in frame.Scene!.Seats.Where(seat => seat.PlayerId != state.ViewerId).Select(seat => seat.PlayerId).ToArray()) {
                view.OpenBattlefield(focus); Tick(); Tick(.7);
                check(!view.BoardView.IsIdentity, "Each actual opponent focus moves the camera over its existing mat");
                CheckView($"{count} seats, {size.X}px {textScale:P0}, focus {focus}");
                Home(); CheckView("Home restores every opponent's clear overview readout group");
                check(view.BoardView.IsIdentity && groups.Keys.All(owner => view.WorldReadouts[owner].Visible),
                    "Home restores all overview groups rather than retaining an earlier camera's occlusion");
            }
            if (count == 3 && textScale == 1.3f) {
                // Same physical gesture as the reported 1000px GPU capture:
                // three bounded W steps, then four wheel notches at the table.
                Tick(.05, Keys.W); Tick(.05, Keys.W); Tick(.05, Keys.W); Tick();
                Tick(logical: new(800, 420), wheel: 480); Tick(logical: new(800, 420), wheel: 480);
                var commander = frame.Cards.First(card => card.Zone == "Command" && card.Request.Owner == state.ViewerId);
                var body = new CardPaintGeometry(commander.Display);
                bool actualCollision = groups[topId].World.Canonical.Select(bounds => Rectangle.Intersect(view.BoardView.Apply(bounds), viewport))
                    .Any(bounds => !bounds.IsEmpty && PoseOverlaps(body.Backing, bounds));
                check(view.BoardView.Scale > 1.9f && actualCollision && !view.WorldReadouts[topId].Visible,
                    $"{size.X}px: zooming the three-player table reproduces a real fixed-commander obstruction and suppresses the whole top group");
                CheckView("Three-player zoom keeps fixed objects clear and retires the complete obstructed resource group");
                Home(); CheckView("Home restores the previously obstructed three-player resource group");
                check(view.WorldReadouts[topId].Visible && view.CounterBadge(topId) is { Chips.Length: 2 },
                    "The same top counters and land totals return together after Home, without changing or replaying game state");
            }
        }
    }

    private static bool FixedReadoutObstruction(PresentationFrame frame, TableApplication.Probe view,
        GameSnapshot state, IReadOnlyList<Rectangle> fixedReadouts, Rectangle bounds)
    {
        if (fixedReadouts.Any(item => item.Intersects(bounds))) return true;
        foreach (var card in frame.Cards.Where(card => !BoardView.AffectsZone(card.Zone))) {
            var body = new CardPaintGeometry(card.Display);
            if (PoseOverlaps(body.Backing, bounds) || PoseOverlaps(body.Shadow, bounds)) return true;
        }
        foreach (var seat in frame.Scene!.Seats) {
            var player = state.Players.Single(player => player.Id == seat.PlayerId);
            if (frame.Hits.SingleOrDefault(hit => hit.Id == "focus:" + player.Id)?.Bounds.Intersects(bounds) == true
                    || view.ManaPoolBadge(player.Id)?.Parts.Any(part => part.Bounds.Intersects(bounds)) == true) return true;
            if (seat.Geometry.Commanders.Any(card => PoseOverlaps(card.Pose with { Size = card.Pose.Size + new Vector2(13) }, bounds))) return true;
            var command = seat.Geometry.CommandLink;
            if (new Rectangle((int)command.X - 52, (int)command.Y, 104, 24).Intersects(bounds)) return true;
            foreach (var pile in seat.Geometry.Piles) {
                var paint = PileGeometry.Create(pile.Pose, player.Zone(pile.Zone).Count, pile.Compact);
                if (paint.Label.Intersects(bounds) || !paint.Count.IsEmpty && paint.Count.Intersects(bounds)
                        || PoseOverlaps(paint.Top, bounds) || PoseOverlaps(paint.Shadow, bounds)
                        || paint.Layers.Any(layer => PoseOverlaps(layer, bounds))) return true;
            }
            float radius = player.Id == state.ViewerId ? 40 : 34;
            foreach (var circle in new[] { (seat.Geometry.Hero, radius + 5), (seat.Geometry.Hero + new Vector2(0, 6), radius + 7) }) {
                var nearest = Vector2.Clamp(circle.Item1, new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Bottom));
                if (Vector2.DistanceSquared(nearest, circle.Item1) < circle.Item2 * circle.Item2) return true;
            }
            if (player.Id == state.ViewerId) continue;
            int count = Math.Min(7, player.Zone("Hand").Count);
            for (int index = 0; index < count; index++) {
                var back = TableLayout.HiddenHand(seat.Index, index, count);
                back = back with { Center = back.Center + seat.Geometry.Hand - TableLayout.SeatHand(seat.Index) };
                if (PoseOverlaps(back, bounds) || PoseOverlaps(back with { Center = back.Center + new Vector2(3, 6) }, bounds)) return true;
            }
        }
        return false;
    }

    private static bool PoseOverlaps(Pose pose, Rectangle bounds)
    {
        if (!pose.Bounds.Intersects(bounds)) return false;
        var face = pose.Corners;
        Vector2[] rectangle = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        foreach (var polygon in new[] { face, rectangle }) {
            for (int index = 0; index < polygon.Length; index++) {
                var edge = polygon[(index + 1) % polygon.Length] - polygon[index];
                var axis = new Vector2(-edge.Y, edge.X);
                float faceMin = face.Min(point => Vector2.Dot(point, axis)), faceMax = face.Max(point => Vector2.Dot(point, axis));
                float rectMin = rectangle.Min(point => Vector2.Dot(point, axis)), rectMax = rectangle.Max(point => Vector2.Dot(point, axis));
                if (faceMax <= rectMin || rectMax <= faceMin) return false;
            }
        }
        return true;
    }

    private static void CheckSidePlaymatReadouts(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float scale in new[] { 1f, 1.3f })
        foreach (string counterKind in new[] { "Poison", "Experience" }) {
            string path = Path.Combine(profile, $"playmat-side-readouts-{size.X}-{scale}-{counterKind}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = scale }.Save(path);
            var baseline = TappedResourcesScenario.Create()[3].Snapshot;
            var state = baseline with { Players = baseline.Players.Select(player => player.Id == baseline.ViewerId ? player : player with {
                Mana = new[] { "W", "U", "B", "R", "G", "C" }.ToImmutableDictionary(symbol => symbol, _ => 1),
                States = [new("counter:energy", "Energy", "counter", 2, null, "Two public energy counters."),
                    new("counter:second", counterKind, "counter", 3, null, "Three public counters.")],
                Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = 7 }
                    : zone.Name != "Battlefield" ? zone : zone with { Count = zone.Count + 2, Cards = [.. zone.Cards,
                        new Card { VisualId = "sidebar-ring-" + player.Id, Name = "Sol Ring", Type = "Artifact" },
                        new Card { VisualId = "sidebar-study-" + player.Id, Name = "Rhystic Study", Type = "Enchantment" }] }).ToImmutableArray()
            }).ToImmutableArray() };
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
            run.EnableLiveTableStudy();
            run.Tick(TimeSpan.FromSeconds(.02), new(-100, -100, 0, default, default, default, default, default));
            var frame = run.Tick(TimeSpan.FromSeconds(.02), new(-100, -100, 0, default, default, default, default, default));
            var view = run.View;
            foreach (var side in frame.Scene!.Seats.Where(seat => seat.Index is 0 or 2)) {
                var badge = view.CounterBadge(side.PlayerId)!;
                var pool = view.ManaPoolBadge(side.PlayerId)!;
                var name = frame.Hits.Single(hit => hit.Id == "focus:" + side.PlayerId);
                int landX = side.Index == 0 ? 8 : 1448;
                var readouts = view.SeatReadouts.Where(bounds => bounds == name.Bounds || bounds == badge.Bounds
                    || bounds.X == landX && bounds.Y >= 425).ToArray();
                var obstacles = frame.Cards.Select(card => (Kind: "card:" + card.Id, Bounds: card.Display.Bounds)).ToList();
                obstacles.Add(("painted life ring", new((int)side.Geometry.Hero.X - 39, (int)side.Geometry.Hero.Y - 39, 78, 78)));
                for (int index = 0; index < 7; index++) {
                    var back = TableLayout.HiddenHand(side.Index, index, 7);
                    back = back with { Center = back.Center + side.Geometry.Hand - TableLayout.SeatHand(side.Index) + new Vector2(3, 6) };
                    obstacles.Add(("held hand shadow:" + index, back.Bounds));
                }
                var manaCollision = pool.Parts.SelectMany(part => obstacles.Where(obstacle => obstacle.Bounds.Intersects(part.Bounds))
                    .Select(obstacle => $"mana {part.Key} {part.Bounds} intersects {obstacle.Kind} {obstacle.Bounds}"))
                    .Concat(pool.Parts.SelectMany(part => readouts.Where(readout => readout.Intersects(part.Bounds))
                        .Select(readout => $"mana {part.Key} {part.Bounds} intersects readout {readout}"))).FirstOrDefault();
                check(pool.Parts.Length == 6 && manaCollision == null
                        && pool.Parts.All(part => view.HitAt(part.Bounds.Center)?.Id == "mana-pool:" + side.PlayerId + ":" + part.Key),
                    $"Side {side.Index}, {size.X}px {scale:P0}: six full-size mana pips clear actual permanents, held cards, life and readouts while retaining their own inspection targets; " + manaCollision);
                var collisions = readouts.SelectMany(readout => obstacles.Where(obstacle => obstacle.Bounds.Intersects(readout))
                    .Select(obstacle => $"readout {readout} intersects {obstacle.Kind} {obstacle.Bounds}")).Take(4).ToArray();
                check(badge.Chips.Length == 2 && badge.Chips.All(chip => chip.MeasuredWidth <= chip.AvailableWidth)
                        && badge.Chips.All(chip => frame.Drawing.Commands.OfType<DrawCommand.Text>()
                            .Any(text => text.Value == chip.Text && text.Size == .6f
                                && badge.Bounds.Contains(new Point(text.X, text.Y))))
                        && readouts.Length >= 4 && collisions.Length == 0,
                    $"Side {side.Index}, {size.X}px {scale:P0}: full-font Energy/{counterKind} and measured tapped/untapped totals clear physical support cards, held hands, life and commanders; "
                    + string.Join("; ", collisions));
            }
        }
    }

    private static void CheckPlaymatReadouts(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float scale in new[] { 1f, 1.3f })
        foreach (int count in new[] { 2, 3, 4 }) {
            string path = Path.Combine(profile, $"playmat-readouts-{count}-{size.X}-{scale}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = scale }.Save(path);
            var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
            var state = baseline with { Players = baseline.Players.Take(count).ToImmutableArray() };
            int topId = TableLayout.Opponents(state)[count == 2 ? 0 : 1].Id;
            state = state with {
                Players = state.Players.Select(player => player.Id != topId ? player : player with {
                    Name = "Top player",
                    Mana = new[] { "W", "U", "B", "R", "G", "C" }.ToImmutableDictionary(symbol => symbol, _ => 1),
                    States = [new("counter:energy", "Energy", "counter", 2, null, "Two public energy counters."),
                        new("counter:poison", "Poison", "counter", 3, null, "Three public poison counters.")],
                    Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = 7 }
                        : zone.Name != "Command" ? zone : zone with { Count = 2,
                            Cards = [zone.Cards[0], zone.Cards[0] with { VisualId = "readout-partner-" + player.Id }] }).ToImmutableArray()
                }).ToImmutableArray(),
                Combat = new([new(state.Viewer!.Zone("Battlefield").Cards[0].VisualId, topId, [], [])], [], null)
            };
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
            run.EnableLiveTableStudy();
            run.Tick(TimeSpan.FromSeconds(.02), new(-100, -100, 0, default, default, default, default, default));
            var frame = run.Tick(TimeSpan.FromSeconds(.02), new(-100, -100, 0, default, default, default, default, default));
            var view = run.View;
            var top = frame.Scene!.Seat(topId);
            var name = frame.Hits.Single(hit => hit.Id == "focus:" + topId);
            var badge = view.CounterBadge(topId)!;
            var pool = view.ManaPoolBadge(topId)!;
            check(top.Geometry.Commanders.Count == 2 && pool.Parts.Length == 6 && badge.Chips.Length == 2
                    && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "1 INCOMING")
                    && badge.Chips.All(chip => chip.MeasuredWidth <= chip.AvailableWidth),
                $"{count}-seat {size.X}px {scale:P0}: the actual top HUD includes both commanders, six mana colors, two complete counter types and incoming combat");
            var readouts = view.SeatReadouts.Where(bounds => bounds == name.Bounds
                || bounds.X == badge.Bounds.X && bounds.Y >= badge.Bounds.Y
                || count == 2 && bounds.X == name.Bounds.X).ToArray();
            var obstacles = new List<(string Kind, Rectangle Bounds)>();
            obstacles.AddRange(frame.Cards.Select(card => ("card:" + card.Id, card.Display.Bounds)));
            foreach (var seat in frame.Scene.Seats) {
                var player = state.Players.Single(player => player.Id == seat.PlayerId);
                obstacles.AddRange(seat.Geometry.Piles.Select(pile => {
                    var painted = PileGeometry.Create(pile.Pose, player.Zone(pile.Zone).Count, pile.Compact);
                    return ("pile:" + player.Id + ":" + pile.Zone, Rectangle.Union(painted.HitBounds, painted.Shadow.Bounds));
                }));
            }
            obstacles.AddRange(frame.Hits.Where(hit => hit.Bounds.Top >= 0 && hit.Bounds.Bottom <= 54)
                .Select(hit => ("header:" + hit.Id, hit.Bounds)));
            obstacles.Add(("central response area", count == 2 ? DuelTableStudy.ActionArea : SharedTableStudy.ActionArea));
            obstacles.AddRange(pool.Parts.Select(part => ("mana:" + part.Key, part.Bounds)));
            foreach (var other in frame.Scene.Seats.Where(seat => seat.PlayerId != topId))
                obstacles.Add(("other mat:" + other.PlayerId, other.Geometry.PlaymatBounds!.Value));
            for (int index = 0; index < 7; index++) {
                var back = TableLayout.HiddenHand(top.Index, index, 7);
                back = back with { Center = back.Center + top.Geometry.Hand - TableLayout.SeatHand(top.Index) + new Vector2(3, 6) };
                obstacles.Add(("held hand shadow:" + index, back.Bounds));
            }
            var overlap = readouts.SelectMany(readout => obstacles.Where(obstacle => obstacle.Bounds.Intersects(readout))
                .Select(obstacle => new { Readout = readout, Obstacle = obstacle })).FirstOrDefault();
            check(readouts.Length >= 5 && overlap == null,
                $"{count}-seat {size.X}px {scale:P0}: measured top identity, land counts, public states and incoming status clear actual cards, piles, hands, mana, headers, response space and other mats"
                + (overlap == null ? "" : $"; readout {overlap.Readout} intersects {overlap.Obstacle.Kind} {overlap.Obstacle.Bounds}"));
            check(engine.Replies.Count == 0 && view.HitAt(top.Geometry.Hero.ToPoint())?.Id == "player:" + topId
                    && view.HitAt(name.Bounds.Center)?.Id == name.Id,
                $"{count}-seat {size.X}px {scale:P0}: readable names retain inspection while the portrait keeps its real player target, without game commands");
        }
    }

    private static void CheckManaReadingLateLayers(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "mana-reading-connected-late-layers"); Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
        var engine = new InteractionScenarioEngine();
        var initial = ManaPaymentScenario.Snapshot(0) with { DecisionOwnerId = 0, PriorityPlayerId = 0 };
        initial = initial with { Players = initial.Players.Select(player => player.Id is 0 or 1 ? player with {
            Mana = ImmutableDictionary<string, int>.Empty.Add("C", player.Id == 0 ? 4 : 9)
        } : player).ToImmutableArray() };
        engine.Present(initial);
        engine.SourceActionRead = (game, decision, key) => new(game, decision, key, ManaPaymentScenario.Spell,
            [new("current-cast", "cast", "Cast Faerie Vandal")]) { Revision = engine.State.Revision };
        var services = TestTableServices.For(engine) with { Social = new HeaderConversation() };
        var size = new Point(1000, 740);
        using var run = new PresentationRun(services, path, engine.State, size);
        var view = run.View;
        var lobby = new Lobby("joined", "Playing", null, false, null, true, "", null, [], [], "header-table");
        view.SetConnectedTable(lobby); view.PresentLobby(lobby, true);
        PresentationFrame Tick(Point? point = null, ButtonState button = ButtonState.Released,
            Microsoft.Xna.Framework.Input.KeyboardState? keys = null) => run.Tick(TimeSpan.FromSeconds(1d / 60),
                new((point ?? new(-100, -100)).X, (point ?? new(-100, -100)).Y, 0, button, default, default, default, default), keys ?? new());
        var frame = Tick(); Tick(); frame = Tick();
        void Click(string id) {
            var hit = frame.Hits.Single(hit => hit.Id == id);
            var pixel = RenderViewport.Fit(size, frame.DesignHeight).ToPhysical(hit.Bounds.Center.ToVector2());
            var point = new Point((int)MathF.Round(pixel.X), (int)MathF.Round(pixel.Y));
            Tick(point, ButtonState.Pressed); frame = Tick(point);
        }
        var chat = frame.Hits.Single(hit => hit.Id == "chat-toggle");
        Click(frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell).Hit.Id);
        for (int index = 0; index < 120 && !frame.Hits.Any(hit => hit.Id == "read-card-action"); index++) { frame = Tick(); Thread.Yield(); }
        check(frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused) && view.ManaJourney != null,
            "The real connected-social variant starts with a current pinned hand reader and bill");
        var bill = view.ManaJourney!;
        string pinnedKey = frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell && card.Focused).Card.Key;
        Click("chat-toggle"); view.TypeText("Unsent table draft"); frame = Tick();
        check(view.ChatOpen && view.ChatDraft == "Unsent table draft" && frame.Hits.Any(hit => hit.Id == "chat-background"),
            "The connected continuation opens the real chat drawer and enters an unsent draft before mana inspection");
        // The left seat's public pip remains exposed beside the open drawer.
        Click("mana-pool:1:C"); frame = Tick();
        var reading = frame.Observe();
        bool lateTogglePainted = frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
            .Any(text => text.Value == "Table chat" && chat.Bounds.Contains(text.Bounds));
        bool drawerRetired = !view.ChatOpen && view.ChatDraft == "Unsent table draft"
            && !frame.Hits.Any(hit => hit.Id == "chat-background");
        bool readerAndBillRetained = view.ManaPoolDetail is { Total: 9 }
            && frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Card.Key == pinnedKey && card.Focused)
            && view.ManaJourney is { } current && current.SourceId == bill.SourceId && current.Symbols.SequenceEqual(bill.Symbols);
        bool isolated = ManaPaymentSmoke.PoolReadingIsolated(view);
        File.WriteAllText(Path.Combine(path, "mana-reading-chat-checkpoint.json"), System.Text.Json.JsonSerializer.Serialize(new {
            stage = "after-real-remote-pip-before-chat-activation-probe", reading, state = view.Match, view.Overlay,
            view.ChatOpen, view.ChatDraft, view.SocialSending, pinnedKey, beforeReading = bill, currentJourney = view.ManaJourney,
            detail = view.ManaPoolDetail is { } detail ? detail with { Parts = detail.Parts.ToArray() } : null,
            sources = view.ManaSources.ToArray(), colors = view.ManaColors.ToArray(),
            cards = frame.Cards.Select(card => new { card.Id, card.Zone, card.Card.Key, card.Focused,
                bounds = card.Display.Bounds, card.ReaderTarget }).ToArray(),
            hits = frame.Hits.Select(hit => new { hit.Id, hit.Bounds, intent = hit.Intent.GetType().Name }).ToArray(),
            chat = new { chat.Id, chat.Bounds, intent = chat.Intent.GetType().Name,
                resolvedIntent = view.HitAt(chat.Bounds.Center)?.Intent.GetType().Name },
            lateTogglePainted, drawerRetired, readerAndBillRetained, isolated,
            poolReadingClearsContext = ManaPaymentSmoke.PoolReadingClearsContext(view, 1), replies = engine.Replies.Count,
            focusEvidence = "Private focus is not exposed; subsequent real typing and Enter check retired focus without inspecting implementation state."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        check(lateTogglePainted, "The real late chat toggle remains painted with its complete centered label inside its original button bounds");
        check(drawerRetired, "Opening the exposed mana pip retires the chat drawer while preserving its unsent draft");
        check(readerAndBillRetained, "The remote C9 inspection preserves the exact current pinned hand occurrence and bill");
        check(isolated, "The completed connected mana-reading frame isolates the late chat toggle after Social paints it");
        check(!view.Activate(chat) && view.HitAt(chat.Bounds.Center)?.Intent is Mana.Table.Interaction.UiIntent.NoInteraction
            && engine.Replies.Count == 0,
            "An old chat hit and its current coordinate cannot reopen the drawer or submit commands through mana reading");
        view.TypeText(" must not append"); frame = Tick(keys: new(Microsoft.Xna.Framework.Input.Keys.Enter)); frame = Tick();
        check(view.ChatDraft == "Unsent table draft" && !view.ChatOpen && !view.SocialSending && engine.Replies.Count == 0,
            "Passive mana reading cannot capture typing or send the retired chat draft through Enter");
        Click("close-overlay"); frame = Tick();
        view.TypeText(" still must not append");
        check(frame.Hits.Any(hit => hit.Id == "chat-toggle") && frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused)
            && !view.ChatOpen && view.ChatDraft == "Unsent table draft",
            "Closing mana reading returns the actual chat toggle and valid pin without restoring the drawer or its text focus");
        Click("chat-toggle"); check(view.ChatOpen && view.ChatDraft == "Unsent table draft",
            "A new deliberate chat click opens the preserved unsent draft after mana reading");
        Click("chat-toggle"); Click("mana-pool:0:C");
        frame = Tick(keys: new(Microsoft.Xna.Framework.Input.Keys.F1)); frame = Tick();
        var guide = frame.Observe();
        check(view.TableGuideOpen && frame.Hits.Any(hit => hit.Id == "experience-guide-close")
            && frame.Hits.Any(hit => hit.Id == "experience-guide-done") && !frame.Hits.Any(hit => hit.Id == "mana-reading-surface"),
            "A real F1 guide takeover retains its own late close/back controls instead of being erased by mana input isolation");
        Click("experience-guide-done"); frame = Tick();
        check(!view.TableGuideOpen && view.Overlay.Length == 0 && view.ManaPoolDetail == null
            && !frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused) && engine.Replies.Count == 0,
            "Back to it closes the higher guide without restoring the cleared mana reader, pinned card or old action");
        File.WriteAllText(Path.Combine(path, "mana-reading-late-layers.json"), System.Text.Json.JsonSerializer.Serialize(new {
            reading, guide, afterGuide = frame.Observe(), replies = engine.Replies.Count,
            scope = "Candidate-only actual connected Social and guide composition; authored current facts, not a rules or human UX claim."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    private sealed class HeaderConversation : ITableSocial
    {
        public Task<TableConversation> ConversationAsync(string tableId) => Task.FromResult(new TableConversation(tableId, 0, 0, [], []));
        public Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) => ConversationAsync(tableId);
        public Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => ConversationAsync(tableId);
    }
}
