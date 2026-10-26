using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class RoleLaneAvoidanceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var stages = ExperienceScenarios.Find("stack-priority.v1").Create();
        var protectedStack = new Rectangle(545, 285, 435, 265);
        foreach (var stage in stages.Where(stage => stage.Snapshot.Stack.Length > 0)) {
            foreach (int viewer in stage.Snapshot.Players.Select(player => player.Id)) {
                var state = stage.Snapshot with { ViewerId = viewer };
                var scene = Build(state);
                var cards = scene.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards).ToArray();
                check(cards.All(card => !card.Pose.Bounds.Intersects(protectedStack)),
                    $"Ordinary {stage.Name} battlefield faces stay full-size and outside the central stack for viewer {viewer}");
                check(cards.All(card => {
                    var player = state.Players.Single(player => player.Id == card.Owner);
                    int seat = card.Owner == viewer ? 3 : TableLayout.SeatIndex(state, card.Owner);
                    var original = BattlefieldLayout.Build(seat, card.Owner, player.Zone("Battlefield").Cards, CardPresentation.IsResource)
                        .SelectMany(cluster => cluster.Cards).Single(node => node.Id == card.Id);
                    return (original.Pose with { Center = card.Pose.Center }) == card.Pose;
                }) && cards.Length == state.Players.Sum(player => player.Zone("Battlefield").Cards.Length),
                    $"Stack avoidance retains every occurrence and exact face projection for viewer {viewer} in {stage.Name}");
            }
        }

        var initial = stages[0].Snapshot;
        var noStack = initial with { Stack = [], Decision = initial.Decision! with { Context = initial.Decision!.Context with { Source = new DecisionSource((initial.Decision!.Context.Source?.CardId ?? ""), null, initial.Decision!.Context.Source?.ControllerId, (initial.Decision!.Context.Source?.Zone ?? "")) } } };
        var withoutStack = Build(noStack);
        var left = withoutStack.Seat(1);
        var command = left.Geometry.CommandLink;
        var commandBounds = new Rectangle((int)command.X - 56, (int)command.Y - 3, 112, 30);
        var rawRing = BattlefieldLayout.Build(left.Index, left.PlayerId, noStack.Players.Single(player => player.Id == left.PlayerId)
            .Zone("Battlefield").Cards, CardPresentation.IsResource).SelectMany(cluster => cluster.Cards).Single(card => card.Id == "stack-ring");
        var placedRing = left.BattlefieldCard("stack-ring")!;
        check(rawRing.Pose.Bounds.Intersects(commandBounds) && !placedRing.Pose.Bounds.Intersects(commandBounds)
                && (rawRing.Pose with { Center = placedRing.Pose.Center }) == placedRing.Pose,
            "Without a stack, ordinary full-size Sol Ring still moves clear of its pre-existing Command-link overlap");

        // Removing the stack alone does not make the authored table clear:
        // its left Sol Ring overlaps Command and its Bear reaches the far
        // library's count strip. Keep only the already-clear local Sage when
        // checking that the pass leaves genuinely unobstructed lanes alone.
        var unobstructed = noStack with { Players = noStack.Players.Select(player => player.Id == noStack.ViewerId ? player
            : player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone
                : zone with { Count = 0, Cards = [] }).ToImmutableArray() }).ToImmutableArray() };
        var clearScene = Build(unobstructed);
        check(clearScene.Seats.All(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards).All(card =>
                BattlefieldLayout.Build(seat.Index, seat.PlayerId, unobstructed.Players.Single(player => player.Id == seat.PlayerId)
                    .Zone("Battlefield").Cards, CardPresentation.IsResource).SelectMany(cluster => cluster.Cards)
                    .Single(original => original.Id == card.Id).Pose == card.Pose)),
            "An unobstructed ordinary table keeps its original role-lane coordinates");

        var copies = Enumerable.Range(0, 3).Select(index => new Card {
            VisualId = "ordinary-copy-" + index, Name = "Cat", Type = "Creature — Cat", Power = 2, Toughness = 2
        }).ToArray();
        var withCopies = initial with { Players = initial.Players.Select(player => player.Id != initial.ViewerId ? player
            : player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone
                : zone with { Count = copies.Length, Cards = copies.ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        var fan = Build(withCopies).Battlefield(initial.ViewerId).SelectMany(cluster => cluster.Cards).Where(card => card.Card.Name == "Cat").ToArray();
        var rawFan = BattlefieldLayout.Build(3, initial.ViewerId, copies, CardPresentation.IsResource).SelectMany(cluster => cluster.Cards).ToArray();
        check(fan.Length == copies.Length && fan.All(card => !card.Pose.Bounds.Intersects(protectedStack))
                && fan.Select(card => card.Pose.Center - rawFan.Single(raw => raw.Id == card.Id).Pose.Center).Distinct().Count() == 1
                && fan.Count(card => card.GroupFront) == 1 && fan.Single(card => card.GroupFront).GroupCount == copies.Length,
            "Moving an obstructed identical-copy family preserves its exact fan and single shared public-state front");
    }

    private static TableScene Build(GameSnapshot state) => TableScene.Build(state, new(-100, -100), null, null,
        false, CardPresentation.IsResource);
}
