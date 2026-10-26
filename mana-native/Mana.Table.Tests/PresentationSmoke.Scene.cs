using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class PresentationSceneChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckSeatPlans(check);
        CheckPreparation(check);
        CheckHand(check);
        CheckUnsupportedSeat(check);
    }

    private static TableScene Build(GameSnapshot state, Func<Card, bool>? resource = null,
        Func<Card, int, bool, BattlefieldLabelMetrics>? measure = null) => TableScene.Build(state,
            new(-100, -100), null, null, false, resource ?? CardPresentation.IsResource, measure);

    private static GameSnapshot Field(GameSnapshot state, Func<Player, Card[]> cards) => state with {
        Players = state.Players.Select(player => {
            var field = cards(player);
            return player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone
                : zone with { Cards = field.ToImmutableArray(), Count = field.Length }).ToImmutableArray() };
        }).ToImmutableArray()
    };

    private static void CheckSeatPlans(Action<bool, string> check)
    {
        var state = PresentationSmoke.CommanderTaxExample() with { ViewerId = 2 };
        var scene = Build(state);
        check(scene.Seats.Select(seat => (seat.PlayerId, seat.Index)).SequenceEqual(state.Players.Select(player =>
                (player.Id, player.Id == state.ViewerId ? 3 : TableLayout.SeatIndex(state, player.Id)))),
            "The prepared seat plans preserve source order and rotate geometry around the current viewer");
        check(scene.Seats.All(seat => ReferenceEquals(seat.Geometry.Commanders, scene.Commanders(seat.PlayerId))
                && ReferenceEquals(seat.Geometry.Piles, scene.Piles(seat.PlayerId))
                && ReferenceEquals(seat.Battlefield, scene.Battlefield(seat.PlayerId))),
            "Compatibility scene views reuse the same prepared commander, pile and battlefield collections");
        check(scene.Seats.All(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards)
                .All(node => ReferenceEquals(node, scene.BattlefieldCard(seat.PlayerId, node.Id))
                    && ReferenceEquals(node, seat.BattlefieldCard(node.Id))))
                && scene.BattlefieldCard(-1, "absent") == null
                && scene.Seats.All(seat => seat.BattlefieldCard("absent") == null),
            "Occurrence lookup returns the exact prepared node for its owner and safely rejects missing owners or identities");
        check(scene.Seats.All(seat => seat.IsDense == seat.Battlefield.SelectMany(cluster => cluster.Cards).Any(node => node.Dense)
                && seat.Cohorts.SelectMany(cohort => cohort.MemberIds).Count()
                    == state.Players.Single(player => player.Id == seat.PlayerId).Zone("Battlefield").Cards.Length),
            "Seat density and prepared cohort membership describe the complete public battlefield");
        check(scene.Seats.All(seat => seat.Cohorts.All(cohort => cohort.Front.GroupFront
                && cohort.MemberIds.Contains(cohort.Front.Id)
                && cohort.MemberIds.All(id => ReferenceEquals(cohort, seat.Cohort(id)) && ReferenceEquals(cohort, scene.Cohort(id))))
                && seat.Cohort("absent") == null) && scene.Cohort("absent") == null,
            "Every prepared public occurrence resolves to its exact painted front and immutable cohort rather than a name-matched copy");

        var denseState = Field(state, player => Enumerable.Range(0, 9).Select(index => new Card {
            VisualId = $"obstacle-{player.Id}-{index}", Name = "Creature " + index,
            Type = "Creature", Power = index, Toughness = 1
        }).ToArray());
        var geometry = SeatGeometry.Build(denseState);
        var context = BattlefieldLayoutContext.Build(denseState, null, geometry);
        check(context.Bounded && geometry.All(seat => seat.Commanders.All(commander => {
                var bounds = commander.Pose.Bounds;
                bounds.Inflate(7, 12);
                return context.Obstacles.Contains(bounds);
            }) && seat.Piles.All(pile => {
                var displayed = PileGeometry.Create(pile.Pose,
                    denseState.Players.Single(player => player.Id == seat.PlayerId).Zone(pile.Zone).Count, pile.Compact);
                return context.Obstacles.Any(bounds => bounds.Contains(displayed.Top.Bounds)
                    && bounds.Contains(displayed.Label) && (displayed.Count.IsEmpty || bounds.Contains(displayed.Count)));
            })),
            "Crowded battlefield reservations derive from the exact commander and pile geometry exposed by the scene");
    }

    private static void CheckPreparation(Action<bool, string> check)
    {
        foreach (int count in new[] { 5, 12 }) {
            var state = Field(PresentationSmoke.Example(), player => Enumerable.Range(0, count).Select(index => new Card {
                VisualId = $"prepared-{player.Id}-{index}", Name = "Card " + index,
                Type = index % 3 == 0 ? "Creature" : index % 3 == 1 ? "Basic Land" : "Artifact",
                Power = index % 3 == 0 ? 2 : null, Toughness = index % 3 == 0 ? 2 : null
            }).ToArray());
            var classified = new Dictionary<Card, int>(ReferenceEqualityComparer.Instance);
            var measured = new Dictionary<Card, int>(ReferenceEqualityComparer.Instance);
            bool Resource(Card card)
            {
                classified[card] = classified.GetValueOrDefault(card) + 1;
                return CardPresentation.IsResource(card);
            }
            BattlefieldLabelMetrics Measure(Card card, int copies, bool miniature)
            {
                measured[card] = measured.GetValueOrDefault(card) + 1;
                return new(137, 23);
            }
            var scene = Build(state, Resource, Measure);
            var cards = state.Players.SelectMany(player => player.Zone("Battlefield").Cards).ToArray();
            check(classified.Count == cards.Count(card => !card.Type.Contains("Creature", StringComparison.Ordinal))
                    && classified.Values.All(calls => calls == 1),
                $"A {count}-card field classifies each noncreature occurrence once while creatures retain their combat role");
            check(count == 5 ? measured.Count == 0 && scene.Seats.All(seat => !seat.IsDense)
                    : measured.Count == cards.Length && measured.Values.All(calls => calls == 1)
                        && scene.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards)
                            .All(node => node.Dense && node.DenseLabels.Width == 137 && node.DenseRowHeight == 23),
                $"A {count}-card field keeps the coordinated layout mode and consumes each real label measurement once");
        }
    }

    private static void CheckHand(Action<bool, string> check)
    {
        var state = PresentationSmoke.Example();
        var hand = Enumerable.Range(0, 24).Select(index => new Card {
            VisualId = "hand-" + index, Name = "Hand card " + index, Type = "Artifact"
        }).ToArray();
        state = state with { Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Count = hand.Length, Cards = hand.ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        var whole = TableScene.Build(state, new(-100, -100), null, null, false, CardPresentation.IsResource).Hand;
        check(whole is { Total: 24, Raised: null } && whole.Cards.Length == 24
                && whole.Cards.Select(node => node.Id).ToHashSet().SetEquals(hand.Select(CardViews.Identity))
                && !whole.UnfoldBounds.IsEmpty,
            "A large hand retains every physical occurrence and an explicit way to unfold it instead of paging away cards");
        var dragging = TableScene.Build(state, new(-100, -100), null, "hand-13", false, CardPresentation.IsResource).Hand;
        var anchors = whole.Cards.ToDictionary(node => node.Id);
        check(dragging.Total == 24 && dragging.Cards.Length == 23 && dragging.Cards.All(node => node.Id != "hand-13"
                && node.Pose == anchors[node.Id].Pose && node.EntranceDelay == anchors[node.Id].EntranceDelay),
            "Lifting a card from a large hand preserves every other occurrence's anchored pose and entrance timing");
    }

    private static void CheckUnsupportedSeat(Action<bool, string> check)
    {
        var state = PresentationSmoke.Example();
        var extra = new[] { 4, 5 }.Select(id => state.Players[1] with { Id = id, Name = "Extra " + id }).ToArray();
        state = Field(state with { Players = [.. state.Players, .. extra] }, player => Enumerable.Range(0, player.Id == 5 ? 9 : 1)
            .Select(index => new Card { VisualId = $"extra-{player.Id}-{index}", Name = "Creature " + index, Type = "Creature", Power = 2, Toughness = 2 }).ToArray());
        var scene = Build(state);
        check(scene.Seats.Count == 5 && scene.Seats.All(seat => seat.PlayerId != 5 && seat.IsDense)
                && scene.BattlefieldCard(5, "extra-5-0") == null,
            "A seat outside the four-seat viewport retains its prior influence on coordinated layout without adding visible nodes");
    }
}
