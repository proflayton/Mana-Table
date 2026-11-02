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
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static class LargeTablePlayAccessChecks
{
    internal static GameSnapshot CaptureState() => WithAccess(LargeTableChecks.State(6, 0));

    private static GameSnapshot WithAccess(GameSnapshot state)
    {
        var access = PlayAccessScenario.Create()[0].Snapshot;
        return state with { PlayAccesses = access.PlayAccesses,
            Players = state.Players.Select(player => player with {
                Name = player.Id == state.Players[^1].Id ? "Explorers of the Deep · Opponent" : player.Name,
                Zones = player.Zones.Select(zone => player.Id <= 1 && (zone.Name is "Exile" or "Graveyard")
                    ? access.Players.Single(owner => owner.Id == player.Id).Zone(zone.Name)
                    : player.Id != state.ViewerId && zone.Name == "Hand" ? zone with { Count = 7 } : zone).ToImmutableArray()
            }).ToImmutableArray() };
    }

    internal static void Run(string profile, Action<bool, string> check)
    {
        var legacy = PlayAccessScenario.Create()[0].Snapshot;
        var original = PlayAccessPlan.Build(legacy);
        foreach (int count in new[] { 2, 3, 4 }) {
            var plan = PlayAccessPlan.Build(legacy with { Players = legacy.Players.Take(count).ToImmutableArray() });
            check(plan.PreferredPriorityArea(new(1100, 708, 360, 70)) == new Rectangle(1250, 628, 328, 70)
                    && plan.SourceRail == PlayAccessPlan.Fan
                    && plan.Groups.SelectMany(group => group.Cards).Select(card => card.Scene.Pose)
                        .SequenceEqual(original.Groups.SelectMany(group => group.Cards).Select(card => card.Scene.Pose)),
                $"{count} seats retain their accepted adjacent-card faces, source rail and priority position");
        }
        foreach (int count in new[] { 5, 6 })
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string path = Path.Combine(profile, $"large-access-{count}-{size.X}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(path);
            var engine = new InteractionScenarioEngine(); engine.Present(WithAccess(LargeTableChecks.State(count, 0)));
            using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
            var placements = new Dictionary<int, object>();
            run.View.SetReadoutPlacer((plans, reserved, viewport, occupied) => {
                var placed = SeatReadoutPlan.Place(plans, reserved, viewport, occupied);
                placements[plans[0].PlayerId] = new { name = engine.State.Players.Single(player => player.Id == plans[0].PlayerId).Name,
                    viewport, arrangements = plans.Select(plan => new { plan.Header, plan.Text, plan.States, plan.Bounds,
                        collisions = plan.Pieces.SelectMany(piece => occupied.Where(obstacle => obstacle.Intersects(piece))
                            .Select(obstacle => new { piece, obstacle })) }),
                    result = placed is SeatReadoutPlacement.Placed ? "placed" : "no-fit",
                    accepted = (placed as SeatReadoutPlacement.Placed)?.Plan, occupied };
                return placed;
            });
            run.EnableLiveTableStudy();
            PresentationFrame frame = null!;
            void Tick(Point? point = null, ButtonState left = ButtonState.Released) {
                var physical = RenderViewport.Fit(size, 900).ToPhysical((point ?? new(-100, -100)).ToVector2() + new Vector2(.5f));
                frame = run.Tick(TimeSpan.FromSeconds(.1), new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default), new());
            }
            for (int step = 0; step < 5; step++) Tick();
            var accepted = engine.State;
            var scene = frame.Scene!;
            var plan = scene.PlayAccess;
            var cards = plan.Groups.SelectMany(group => group.Cards).ToArray();
            var sourceHits = frame.Hits.Where(hit => hit.Id.StartsWith("play-access-source:", StringComparison.Ordinal)
                || hit.Id == "play-access-unfold").ToArray();
            var sourceCaption = frame.Drawing.Commands.OfType<DrawCommand.Text>().Single(text => text.X == plan.UnfoldBounds.X + 4
                && text.Y == plan.UnfoldBounds.Y + 4);
            var captionSize = new TextMetrics().TextSize(sourceCaption.Value!, sourceCaption.Size, sourceCaption.Width, 80,
                sourceCaption.Bold, run.View.TextScale);
            var ownerFurniture = run.View.ReadoutPlans.Values.SelectMany(readout => readout.Pieces)
                .Concat(frame.Hits.Where(hit => hit.Id.StartsWith("player:", StringComparison.Ordinal)).Select(hit => hit.Bounds))
                .Concat(frame.Hits.Where(hit => hit.Id.StartsWith("hand-zone:", StringComparison.Ordinal)).Select(hit => hit.Bounds))
                .Concat(frame.Drawing.Commands.OfType<DrawCommand.CenterText>().Where(text => text.Value?.EndsWith(" in hand", StringComparison.Ordinal) == true)
                    .Select(text => text.Bounds))
                .Concat(accepted.Players.SelectMany(player => run.View.ManaPoolBadge(player.Id)?.Parts.Select(part => part.Bounds) ?? []))
                .ToArray();
            var priority = run.View.DecisionPanelBounds;
            var solids = ownerFurniture.Concat(scene.Hand.Cards.Select(card => card.Pose.Bounds))
                .Concat(cards.Select(card => card.Scene.Pose.Bounds)).Concat(sourceHits.Select(hit => hit.Bounds))
                .Concat(scene.BattlefieldDrawOrder.Select(card => frame.BoardView.Apply(card.Pose).Bounds))
                .Concat(scene.Seats.SelectMany(seat => seat.Geometry.Piles.Select(pile => pile.TableAnchored
                    ? frame.BoardView.Apply(PileGeometry.Create(pile, accepted.Players.Single(player => player.Id == seat.PlayerId).Zone(pile.Zone).Count).Reservation)
                    : PileGeometry.Create(pile, accepted.Players.Single(player => player.Id == seat.PlayerId).Zone(pile.Zone).Count).Reservation)))
                .ToArray();
            File.WriteAllText(Path.Combine(path, "access-priority-first.json"), JsonSerializer.Serialize(new {
                count, size, priority, sourceHits = sourceHits.Select(hit => new { hit.Id, hit.Bounds }),
                placements, acceptedReadouts = run.View.ReadoutPlans,
                cards = cards.Select(card => new { card.Scene.Id, card.Scene.Pose }), ownerFurniture,
                sourceCaption, captionSize,
                priorityCollisions = solids.Where(bounds => bounds.Intersects(priority)),
                fanCollisions = cards.SelectMany(card => ownerFurniture.Where(bounds => bounds.Intersects(card.Scene.Pose.Bounds))),
                sourceCollisions = sourceHits.SelectMany(hit => ownerFurniture.Where(bounds => bounds.Intersects(hit.Bounds)))
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            check(accepted.Players.Where(player => player.Id != accepted.ViewerId).All(player =>
                    run.View.ReadoutPlans.TryGetValue(player.Id, out var readout)
                    && readout.Header.Text.Contains(player.Name, StringComparison.Ordinal)
                    && frame.Drawing.Commands.OfType<DrawCommand.Text>().Count(text => text.Value == readout.Header.Text) == 1),
                "The actual accepted and painted owner headers preserve every full player name, including the long near-right identity beside the external fan");
            check(!priority.IsEmpty && new Rectangle(0, 54, 1600, 846).Contains(priority)
                    && frame.Hits.Any(hit => hit.Id == "confirm") && frame.Hits.Any(hit => hit.Id == "quiet-priority-arm")
                    && solids.All(bounds => !bounds.Intersects(priority)),
                $"{count} seats at {size.X}: real priority controls stay clear of every owner, hand, battlefield and external-card source packet");
            check(cards.Length == 4 && plan.Groups.Count == 4 && cards.All(card => card.Scene.Pose.Size == new Vector2(137, 192))
                    && cards.All(card => ownerFurniture.All(bounds => !bounds.Intersects(card.Scene.Pose.Bounds)))
                    && sourceHits.Length == 5 && sourceHits.All(hit => ownerFurniture.All(bounds => !bounds.Intersects(hit.Bounds)))
                    && captionSize.Y + 4 <= plan.UnfoldBounds.Height
                    && sourceHits.All(hit => run.View.HitAt(hit.Bounds.Center)?.Id == hit.Id),
                "The four actual source packets retain recognizable full-size card faces and independently reachable source/unfold gestures clear of owner identities and mana");
            var unfold = sourceHits.Single(hit => hit.Id == "play-access-unfold");
            Tick(unfold.Bounds.Center, ButtonState.Pressed); Tick(unfold.Bounds.Center); Tick();
            check(frame.Scene!.Spread is { Target: CardCollectionTarget.AccessCollection } spread
                    && spread.WorldCards.Select(card => card.Id).Order().SequenceEqual(cards.Select(card => card.Scene.Id).Order())
                    && spread.Anchors.All(anchor => anchor.Pose == cards.Single(card => card.Scene.Id == anchor.Id).Scene.Pose)
                    && spread.WorldCards.All(card => card.Entrance == cards.Single(source => source.Scene.Id == card.Id).Scene.Pose.Center)
                    && engine.Replies.Count == 0 && ReferenceEquals(engine.State, accepted),
                "The actual unfold gesture exposes every granted physical occurrence without sending a rules command or changing the hand");
        }
    }
}
