using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class SeatZoneChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        PhysicalOwnership(check);
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
            PointerJourney(profile, size, check);
    }

    private static void PhysicalOwnership(Action<bool, string> check)
    {
        foreach (int count in new[] { 2, 3, 4 })
        foreach (int viewer in Enumerable.Range(0, count)) {
            var state = State(count, viewer);
            var scene = Scene(state);
            foreach (var seat in scene.Seats) {
                var geometry = seat.Geometry;
                var station = geometry.ZoneStation;
                string context = $"{count} players, viewer {viewer}, owner {seat.PlayerId}";
                check(station != null && station.PlayerId == seat.PlayerId
                        && geometry.PlaymatPlacement!.Value.Contains(station.Placement)
                        && station.Piles.SequenceEqual(geometry.Piles)
                        && station.Commanders.SequenceEqual(geometry.Commanders),
                    context + ": one current seat owns the complete personal station and the scene uses its exact pile/commander placements");
                if (station == null) continue;
                var owner = state.Players.Single(player => player.Id == seat.PlayerId);
                var faces = station.Commanders.Select(card => card.Pose).Concat(station.Piles.Select(pile => pile.Pose)).ToArray();
                check(station.Piles.Select(pile => pile.Zone).SequenceEqual(new[] { "Library", "Graveyard", "Exile" })
                        && station.Commanders.Count == 2
                        && station.Commanders.All(card => card.Owner == seat.PlayerId && card.Zone == "Command"
                            && card.TableAnchored && owner.Zone("Command").Cards.Any(face => ReferenceEquals(face, card.Card)))
                        && station.Piles.All(pile => pile.TableAnchored && pile.Caption is { Width: > 0, Height: > 0 })
                        && faces.All(face => face.IsProjected && Math.Abs(face.Angle - PlayerPlaymat.Facing(seat.Index, count == 2)) < .001f
                            && TableSurfaceProjectionChecks.InPlacement(station.Placement, face)),
                    context + ": distinct piles and both authorized commanders lie within their own personal area and retain physical seat facing, including upright duel faces");
                check(faces.All(face => scene.Seats.Where(other => other.PlayerId != seat.PlayerId)
                        .All(other => !TableSurfaceProjectionChecks.Overlap(face, other.Geometry.PlaymatSurface!.Value)))
                        && station.Piles.Select(pile => pile.Caption!.Value).Append(station.CommandLabel)
                            .All(caption => scene.Seats.Where(other => other.PlayerId != seat.PlayerId)
                                .All(other => !TableSurfaceProjectionChecks.Overlap(new Pose(caption.Center.ToVector2(), caption.Size.ToVector2()),
                                    other.Geometry.PlaymatSurface!.Value))),
                    context + ": no station card or upright zone caption borrows another player's mat");
                var exposed = station.Commanders.Select(card => (Id: card.Id, card.Pose))
                    .Concat(station.Piles.Select(pile => (Id: pile.Zone, Pose: PileGeometry.Create(pile, 100).Top))).ToArray();
                check(exposed.All(face => exposed.Where(other => other.Id != face.Id)
                        .All(other => !TableSurfaceProjectionChecks.Overlap(face.Pose, other.Pose))),
                    context + ": the actual exposed tops of full hundred-card stacks remain separate from both commanders and every other zone");
            }

            var emptied = state with { Players = state.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name is "Command" or "Graveyard" or "Exile"
                    ? zone with { Count = 0, Cards = [], TopCard = null } : zone).ToImmutableArray()
            }).ToImmutableArray() };
            var quiet = Scene(emptied);
            check(scene.Seats.All(seat => quiet.Seat(seat.PlayerId).Geometry.ZoneStation!.Placement == seat.Geometry.ZoneStation!.Placement
                    && quiet.Seat(seat.PlayerId).Geometry.Piles.SequenceEqual(seat.Geometry.Piles)
                    && quiet.Seat(seat.PlayerId).Geometry.Commanders.Count == 0)
                    && quiet.BattlefieldDrawOrder.Select(card => (card.Id, card.Pose))
                        .SequenceEqual(scene.BattlefieldDrawOrder.Select(card => (card.Id, card.Pose))),
                $"{count} players, viewer {viewer}: empty public zones do not move anyone's personal area, pile anchors or existing battlefield occurrences");
        }
    }

    private static void PointerJourney(string profile, Point size, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "seat-owned-zones-" + size.X);
        Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
        var engine = new InteractionScenarioEngine(); engine.Present(State(4, 0));
        using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
        run.EnableLiveTableStudy();
        PresentationFrame frame = null!;
        int scroll = 0;
        void Tick(Point? point = null, ButtonState left = ButtonState.Released, Keys? key = null, int? wheel = null)
        {
            if (wheel is { } value) scroll = value;
            var physical = RenderViewport.Fit(size, run.View.DesignHeight).ToPhysical(
                (point ?? new Point(-100, -100)).ToVector2() + new Vector2(.5f));
            frame = run.Tick(TimeSpan.FromSeconds(.05), new((int)physical.X, (int)physical.Y, scroll, left, default, default, default, default),
                key is { } pressed ? new(pressed) : new());
        }
        void Click(Point point) { Tick(point, ButtonState.Pressed); Tick(point); Tick(point); }
        void Close() { Tick(key: Keys.Escape); Tick(); Tick(); }
        Tick(); Tick();
        var before = engine.State;
        var metrics = new TextMetrics();
        Rectangle TextBounds(DrawCommand.Text text) => new(text.X, text.Y, text.Width,
            metrics.TextSize(text.Value ?? "", text.Size, text.Width, text.MaxLines, text.Bold, 1.3f).Y);
        bool Foreground(DrawCommand.Text text) => text.Color.R != 0 || text.Color.G != 0 || text.Color.B != 0;
        foreach (var seat in frame.Scene!.Seats)
        foreach (var pile in seat.Geometry.Piles) {
            var caption = frame.BoardView.Apply(pile.Caption!.Value);
            float font = .43f * frame.BoardView.Scale;
            int width = metrics.TextWidth(pile.Zone, font, textScale: 1.3f);
            int height = metrics.TextSize(pile.Zone, font, width + 2, 1, textScale: 1.3f).Y;
            var candidates = frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.Value == pile.Zone).ToArray();
            var paint = candidates.SingleOrDefault(text => text.Size == font
                && text.X == caption.Center.X - width / 2 && text.Y == caption.Center.Y - height / 2);
            bool readable = paint != null && paint.Width >= width
                    && caption.Contains(new Rectangle(paint.X, paint.Y, width,
                        metrics.TextSize(pile.Zone, paint.Size, paint.Width, 1, textScale: 1.3f).Y))
                    && frame.Drawing.Commands.OfType<DrawCommand.CenterText>().All(text => text.Value != pile.Zone || text.Bounds != caption);
            if (!readable) {
                string failure = Path.Combine(path, $"caption-{seat.PlayerId}-{pile.Zone}-failure.json");
                if (!File.Exists(failure)) using (var stream = new FileStream(failure, FileMode.CreateNew, FileAccess.Write))
                    JsonSerializer.Serialize(stream, new { size, seat.PlayerId, pile.Zone, caption, font, width, height, paint,
                        candidates, camera = frame.BoardView }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
            }
            check(readable,
                $"{size.X}x{size.Y}, owner {seat.PlayerId}: actual full {pile.Zone} caption paints its measured 130% font within its reserved area rather than silently shrinking");
            var body = PileGeometry.Create(pile, before.Players.Single(player => player.Id == seat.PlayerId).Zone(pile.Zone).Count);
            string quantity = before.Players.Single(player => player.Id == seat.PlayerId).Zone(pile.Zone).Count.ToString();
            var countArea = frame.BoardView.Apply(body.Count);
            var quantityPaint = frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.Value == quantity
                && text.Size == .6f * frame.BoardView.Scale && Foreground(text) && TextBounds(text).Intersects(countArea)).ToArray();
            var top = frame.Cards.FirstOrDefault(card => card.Zone == pile.Zone && card.Hit.PlayerId == seat.PlayerId);
            var names = top == null ? [] : frame.Drawing.Commands.OfType<DrawCommand.Text>()
                .Where(text => text.Value == top.Card.Name && text.Size < .6f && TextBounds(text).Intersects(top.Display.Bounds)).ToArray();
            check(quantityPaint.Length == 1 && quantityPaint[0].Width >= metrics.TextWidth(quantity, quantityPaint[0].Size, true, 1.3f)
                    && names.All(name => !TextBounds(name).Intersects(TextBounds(quantityPaint[0]))),
                $"Owner {seat.PlayerId} {pile.Zone}: its actual quantity remains complete and does not overwrite fallback identity");
        }
        foreach (var stationCard in frame.Cards.Where(card => card.Request.TableAnchored).ToArray()) {
            var texts = frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => Foreground(text)
                && TextBounds(text).Intersects(stationCard.Display.Bounds)).ToArray();
            check(texts.All(text => text.Value != stationCard.Card.ManaCost || stationCard.Card.ManaCost.Length == 0)
                    && texts.All(text => text.Value != stationCard.Card.Type || stationCard.Card.Type.Length == 0),
                $"{stationCard.Card.Name}: the actual missing-art station face has no truncated mana or type fragment");
            if (stationCard.Zone == "Command") {
                string tax = "+" + stationCard.Card.CommanderTax;
                var stamps = texts.Where(text => text.Value == tax && text.Size == .48f * frame.BoardView.Scale).ToArray();
                var names = texts.Where(text => text.Value == stationCard.Card.Name).ToArray();
                check(stamps.Length == 1 && names.All(name => !TextBounds(name).Intersects(TextBounds(stamps[0])))
                        && frame.Cards.Where(other => other.Zone == "Command" && other.Id != stationCard.Id)
                            .All(other => !TextBounds(stamps[0]).Intersects(other.Display.Bounds)),
                    $"{stationCard.Card.Name}: its actual +{stationCard.Card.CommanderTax} tax stamp stays clear of identity and the other partner face");
            }
        }
        var hoverCards = frame.Cards.Where(card => card.Request.TableAnchored && !card.Card.FaceDown).ToArray();
        foreach (var stationCard in hoverCards) {
            Tick(stationCard.Display.Center.ToPoint()); Tick(stationCard.Display.Center.ToPoint());
            check(frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == stationCard.Card.Name && text.Size == .7f)
                    && engine.Replies.Count == 0 && ReferenceEquals(engine.State, before),
                $"{stationCard.Card.Name}: hovering the exact commander or public top supplies its whole current source-attached name without committing");
        }
        Tick(); Tick();
        foreach (var seat in frame.Scene!.Seats.ToArray()) {
            foreach (string zone in new[] { "Library", "Graveyard", "Exile", "Command" }) {
                var hit = frame.Hits.Single(item => item.Id == $"zone:{seat.PlayerId}:{zone}");
                Point at = zone == "Command" ? hit.Bounds.Center
                    : frame.BoardView.Apply(seat.Geometry.Piles.Single(pile => pile.Zone == zone).Caption!.Value).Center;
                var actual = run.View.HitAt(at);
                check(actual?.Intent is MatchIntent.OpenZone open && open.PlayerId == seat.PlayerId && open.Zone == zone,
                    $"{size.X}x{size.Y}, 130% text: the actual {zone} caption opens owner {seat.PlayerId}, including adjacent left/viewer stations");
                Click(at);
                if (zone is "Graveyard" or "Exile") {
                    var target = frame.Scene!.Spread?.Target as CardCollectionTarget.PublicZone;
                    check(target?.PlayerId == seat.PlayerId && target.ZoneName == zone
                            && frame.Scene.Spread!.WorldCards.All(card => card.Owner == seat.PlayerId && card.Zone == zone && !card.Card.FaceDown)
                            && engine.Replies.Count == 0 && ReferenceEquals(engine.State, before),
                        $"Owner {seat.PlayerId} {zone}: the physical caption expands only its current disclosed collection without answering the engine");
                } else {
                    check(run.View.ZoneName == zone && engine.Replies.Count == 0 && ReferenceEquals(engine.State, before),
                        $"Owner {seat.PlayerId} {zone}: inspection retains its existing zone route and current engine decision");
                }
                Close();
                check(run.View.ZoneName == null && frame.Scene!.Spread == null
                        && frame.Scene.Seat(seat.PlayerId).Geometry.ZoneStation!.Placement == seat.Geometry.ZoneStation!.Placement
                        && engine.Replies.Count == 0,
                    $"Owner {seat.PlayerId} {zone}: closing inspection returns to the same station without a gameplay command");
            }
        }
        foreach (var commander in frame.Scene!.Seats.SelectMany(seat => seat.Geometry.Commanders).ToArray()) {
            var at = frame.Cards.Single(card => card.Id == commander.Id && card.Zone == "Command").Display.Center.ToPoint();
            check(run.View.HitAt(at)?.Card is { } actual && CardViews.Same(actual, commander.Card),
                $"{size.X}x{size.Y}: each tiny partner-commander face remains its own current reading target");
            Click(at);
            check(frame.Cards.Single(card => card.Id == commander.Id && card.Zone == "Command") is { Focused: true, Display.Size.X: >= 300 }
                    && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == commander.Card.Name && text.Size == 1.05f)
                    && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value is { } value
                        && value.StartsWith("COMMANDER TAX", StringComparison.Ordinal) && value.EndsWith("+" + commander.Card.CommanderTax, StringComparison.Ordinal))
                    && engine.Replies.Count == 0 && ReferenceEquals(engine.State, before),
                $"{commander.Card.Name}: opening either partner reads the complete current identity and tax without casting or answering priority");
            Close();
            check(frame.Cards.Single(card => card.Id == commander.Id && card.Zone == "Command") is { Focused: false }
                    && frame.Scene!.Commanders(commander.Owner).Single(card => card.Id == commander.Id).Pose == commander.Pose,
                $"{commander.Card.Name}: closing its reader returns the same physical commander to its own mat");
        }
        check(frame.Cards.All(card => card.Id != "zone-hidden-exile-2")
                && frame.Drawing.Commands.All(command => command switch {
                    DrawCommand.Text text => text.Value != "Unavailable exile identity",
                    DrawCommand.CenterText text => text.Value != "Unavailable exile identity", _ => true })
                && frame.Scene!.Seats.All(seat => seat.Geometry.Piles.All(pile => pile.Zone != "Library"
                    || !frame.Cards.Any(card => card.Zone == "Library" && card.Hit.PlayerId == seat.PlayerId))),
            $"{size.X}x{size.Y}: grouping station cards never materializes a concealed exile face or an undisclosed library top");

        var anchors = frame.Scene!.Seats.SelectMany(seat => seat.Geometry.Piles.Select(pile =>
            (seat.PlayerId, pile.Zone, pile.Pose))).ToArray();
        Tick(key: Keys.W); Tick(); Tick();
        var boardClip = new Rectangle(0, 54, 1600, 724);
        var moved = frame.Scene!.Seats.SelectMany(seat => seat.Geometry.Piles.Select(pile => {
            var caption = frame.BoardView.Apply(pile.Caption!.Value);
            var actual = run.View.HitAt(caption.Center);
            var hit = frame.Hits.Single(item => item.Id == $"zone:{seat.PlayerId}:{pile.Zone}");
            return new { seat.PlayerId, pile.Zone, originalCaption = pile.Caption.Value, caption, at = caption.Center,
                actualId = actual?.Id, actualIntent = actual?.Intent.ToString(), actualBounds = actual?.Bounds,
                sameOwner = actual?.Intent is MatchIntent.OpenZone open && open.PlayerId == seat.PlayerId && open.Zone == pile.Zone,
                registeredBounds = hit.Bounds, registeredContains = hit.Contains(caption.Center) };
        })).ToArray();
        string motionFailure = Path.Combine(path, "camera-station-caption-first.json");
        if (!File.Exists(motionFailure)) using (var stream = new FileStream(motionFailure, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, new { size, boardClip, camera = frame.BoardView, run.View.DesignHeight, frame.Stamp.Scope,
                sequence = "close all zone and commander readers; W for 50ms; release for two 50ms frames", moved },
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        check(!frame.BoardView.IsIdentity && anchors.All(anchor => frame.Scene!.Seat(anchor.PlayerId).Geometry.Piles
                    .Single(pile => pile.Zone == anchor.Zone).Pose == anchor.Pose)
                && moved.Any(item => boardClip.Contains(item.at)) && moved.Any(item => !boardClip.Contains(item.at))
                && moved.All(item => boardClip.Contains(item.at) ? item.sameOwner && item.registeredContains
                    : !item.sameOwner && !item.registeredContains) && engine.Replies.Count == 0,
            $"{size.X}x{size.Y}: keyboard movement carries visible station hits with the mat and clips off-board captions so they cannot open any zone, without repacking or gameplay commands");
        Tick(key: Keys.Home); Tick(); Tick();
        check(frame.BoardView.IsIdentity && frame.Scene!.Seats.All(seat => seat.Geometry.Piles.All(pile => {
                    var at = pile.Caption!.Value.Center;
                    return boardClip.Contains(at) && run.View.HitAt(at)?.Intent is MatchIntent.OpenZone open
                        && open.PlayerId == seat.PlayerId && open.Zone == pile.Zone;
                })) && anchors.All(anchor => frame.Scene!.Seat(anchor.PlayerId).Geometry.Piles
                    .Single(pile => pile.Zone == anchor.Zone).Pose == anchor.Pose) && engine.Replies.Count == 0,
            $"{size.X}x{size.Y}: Home restores every owner's three reachable caption hits at their original physical anchors, including the local station clipped by the preceding pan");
        var commanderAnchors = frame.Scene!.Seats.SelectMany(seat => seat.Geometry.Commanders
            .Select(card => (Owner: seat.PlayerId, card.Id, card.Pose))).ToArray();
        foreach (int owner in before.Players.Select(player => player.Id)) {
            run.View.OpenBattlefield(owner); Tick(); Tick();
            var focusedSeat = frame.Scene!.Seat(owner);
            var focusedCaptions = focusedSeat.Geometry.Piles.Select(pile => {
                var bounds = frame.BoardView.Apply(pile.Caption!.Value);
                var actual = run.View.HitAt(bounds.Center);
                return new { owner, pile.Zone, bounds, actualId = actual?.Id, actualIntent = actual?.Intent.ToString(),
                    reachable = boardClip.Contains(bounds.Center) && actual?.Intent is MatchIntent.OpenZone open
                        && open.PlayerId == owner && open.Zone == pile.Zone };
            }).ToArray();
            string focusEvidence = Path.Combine(path, "focus-station-owner-" + owner + "-first.json");
            if (!File.Exists(focusEvidence)) using (var stream = new FileStream(focusEvidence, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, new { size, owner, focusOwner = run.View.StudyFocusOwner,
                    camera = frame.BoardView, boardClip, captions = focusedCaptions },
                    new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
            check(run.View.StudyFocusOwner == owner && !frame.BoardView.IsIdentity && focusedCaptions.All(caption => caption.reachable),
                $"{size.X}x{size.Y}, 130% text: automatic battlefield focus keeps owner {owner}'s three station captions reachable at their actual camera positions");
            foreach (string zone in new[] { "Library", "Graveyard", "Exile" }) {
                var pile = frame.Scene!.Seat(owner).Geometry.Piles.Single(pile => pile.Zone == zone);
                Click(frame.BoardView.Apply(pile.Caption!.Value).Center);
                bool correctZone = zone == "Library" ? run.View.ZoneName == zone
                    : frame.Scene!.Spread?.Target is CardCollectionTarget.PublicZone target && target.PlayerId == owner && target.ZoneName == zone;
                check(correctZone && engine.Replies.Count == 0 && ReferenceEquals(engine.State, before),
                    $"Focused owner {owner} {zone}: clicking the actual caption opens only that owner's existing inspection route without a gameplay command");
                Close();
            }
            foreach (var commander in frame.Scene!.Commanders(owner).ToArray()) {
                var face = frame.Cards.Single(card => card.Id == commander.Id && card.Zone == "Command");
                var actual = run.View.HitAt(face.Display.Center.ToPoint());
                check(actual?.PlayerId == owner && actual.Card is { } current && CardViews.Same(current, commander.Card),
                    $"Focused owner {owner}: each physical partner face remains that owner's exact current commander reading target");
                Click(face.Display.Center.ToPoint());
                check(frame.Cards.Single(card => card.Id == commander.Id && card.Zone == "Command") is { Focused: true }
                        && engine.Replies.Count == 0 && ReferenceEquals(engine.State, before),
                    $"Focused owner {owner}: commander reading enlarges its current source without casting or answering the pending decision");
                Close();
            }
            check(anchors.All(anchor => frame.Scene!.Seat(anchor.PlayerId).Geometry.Piles.Single(pile => pile.Zone == anchor.Zone).Pose == anchor.Pose)
                    && commanderAnchors.All(anchor => frame.Scene!.Commanders(anchor.Owner).Single(card => card.Id == anchor.Id).Pose == anchor.Pose)
                    && engine.Replies.Count == 0 && ReferenceEquals(engine.State, before),
                $"Focused owner {owner}: inspection preserves all players' physical pile/commander anchors and the exact accepted game state");
            Tick(key: Keys.Home); Tick(); Tick();
            check(frame.BoardView.IsIdentity && run.View.StudyFocusOwner == null && engine.Replies.Count == 0,
                $"Focused owner {owner}: Home restores the shared table after zone and commander inspection");
        }
        Tick(new Point(800, 400), wheel: -1200); Tick(); Tick();
        var small = frame.Cards.Where(card => card.Request.TableAnchored && !card.Card.FaceDown).ToArray();
        check(frame.BoardView.Scale == .45f && small.Length == hoverCards.Length && small.All(card =>
                frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => Foreground(text) && TextBounds(text).Intersects(card.Display.Bounds))
                    .All(text => text.Value != card.Card.ManaCost || card.Card.ManaCost.Length == 0)
                && frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => Foreground(text) && TextBounds(text).Intersects(card.Display.Bounds))
                    .All(text => text.Value != card.Card.Type || card.Card.Type.Length == 0)) && engine.Replies.Count == 0,
            $"{size.X}x{size.Y}: the actual minimum camera zoom retains all station occurrences and suppresses partial mana/type text even when no identity can fit");
    }

    private static TableScene Scene(GameSnapshot state) => TableScene.Build(state, new(-100, -100), null, null,
        false, CardPresentation.IsResource, sharedTableStudy: true);

    private static GameSnapshot State(int count, int viewer)
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        Card Face(string id, string name) => new() { VisualId = id, CombatId = id, Name = name, Type = "Creature", Power = 2, Toughness = 2 };
        return baseline with { ViewerId = viewer, DecisionOwnerId = viewer, PriorityPlayerId = viewer,
            Players = baseline.Players.Take(count).Select(player => player with {
                Priority = player.Id == viewer,
                Zones = player.Zones.Select(zone => zone.Name switch {
                    "Hand" => zone with { Cards = [], Count = 0 },
                    "Command" => zone with { Count = 2, Cards = [Face("zone-leader-" + player.Id, "Toothy, Imaginary Friend") with { CommanderTax = 2, ManaCost = "{3}{U}" },
                        Face("zone-partner-" + player.Id, "Pir, Imaginative Rascal") with { CommanderTax = 4, ManaCost = "{2}{G}" }] },
                    "Library" => zone with { Count = 78, Cards = [], TopCard = null },
                    "Exile" => zone with { Count = 1, Cards = [player.Id == 2
                        ? Face("zone-hidden-exile-2", "Unavailable exile identity") with { FaceDown = true }
                        : Face("zone-exile-" + player.Id, "Disclosed exile card")], TopCard = null },
                    _ => zone
                }).ToImmutableArray()
            }).ToImmutableArray() };
    }
}
