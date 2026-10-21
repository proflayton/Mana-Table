using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;

namespace Mana.Table;

internal static class FocusedBattlefieldGroupingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var initial = ClutteredChangesScenario.Initial();
        var earthbend = ClutteredChangesScenario.Earthbend(initial);
        var history = new PublicChangeFeedback();
        history.Observe(null, initial, 0);
        history.Observe(initial, earthbend, 1);
        var forests = Forests(earthbend);
        var withoutHistory = FocusedBattlefieldLayout.Group(forests, CardPresentation.IsResource);
        var afterEarthbend = FocusedBattlefieldLayout.Group(forests, CardPresentation.IsResource, history.Groups);
        check(withoutHistory is { Count: 1 } && withoutHistory[0].Count == 2
                && afterEarthbend.Count == 2 && afterEarthbend.All(group => group.Count == 1)
                && afterEarthbend.Select(group => group.Id).SequenceEqual(forests.Select(CardViews.Identity))
                && afterEarthbend.All(group => ReferenceEquals(group.Front, forests.Single(card => CardViews.Identity(card) == group.Id))),
            "The reported earthbend target retains its own opaque identity beside an otherwise-equal unchanged Forest, using current projected cards");

        // Both Forests receive the same later effect and again have equal
        // current characteristics. Their earlier distinct event membership
        // remains relevant; an elapsed interval must not fold them together.
        var retreat = ClutteredChangesScenario.Retreat(earthbend);
        history.Observe(earthbend, retreat, 100);
        var laterForests = Forests(retreat);
        var afterRetreat = FocusedBattlefieldLayout.Group(laterForests, CardPresentation.IsResource, history.Groups);
        check(FocusedBattlefieldLayout.Group(laterForests, CardPresentation.IsResource).Count == 1
                && afterRetreat.Count == 2 && afterRetreat.All(group => group.Count == 1)
                && afterRetreat.Select(group => group.Id).SequenceEqual(afterEarthbend.Select(group => group.Id)),
            "A later shared counter effect preserves the Forests' separate reported histories without a timed regroup or replacement identity");

        var cats = retreat.Viewer!.Zone("Battlefield").Cards.Where(card => card.VisualId.StartsWith("crowd-0-cat-", StringComparison.Ordinal)).ToArray();
        var together = FocusedBattlefieldLayout.Group(cats, CardPresentation.IsResource, history.Groups);
        check(together is { Count: 1 } && together[0].Count == 50
                && together[0].Cards.Select(CardViews.Identity).SequenceEqual(cats.Select(CardViews.Identity)),
            "Identical tokens affected together remain one complete fifty-copy family rather than being separated by individual IDs");
        check(FocusedBattlefieldLayout.Group([], CardPresentation.IsResource, history.Groups).Count == 0,
            "Retained reported targets never reconstruct a departed or no-longer-projected battlefield face");
    }

    private static Card[] Forests(GameSnapshot state) => state.Viewer!.Zone("Battlefield").Cards
        .Where(card => card.VisualId is "crowd-0-land-9" or ClutteredChangesScenario.AnimatedLandId).ToArray();
}
