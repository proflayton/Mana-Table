using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;

namespace Mana.Table;

internal static class PlayAccessPresentationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var stages = PlayAccessScenario.Create();
        var initial = stages[0].Snapshot;
        var scene = Build(initial);
        var without = Build(initial with { PlayAccesses = [] });
        check(scene.Hand.Total == 7 && scene.Hand.Cards.Select(card => (card.Id, card.Pose))
                .SequenceEqual(without.Hand.Cards.Select(card => (card.Id, card.Pose)))
                && initial.Viewer!.Zone("Hand").Count == 7,
            "Additional-zone access preserves the actual seven-card hand, hand slots and hand count");
        var all = scene.PlayAccess.Groups.SelectMany(group => group.Cards).ToArray();
        check(all.Any(item => item.Scene.Id == PlayAccessScenario.BorrowedId && item.Scene.Owner == 1 && item.Scene.Zone == "Exile")
                && all.All(item => item.Scene.Card == initial.Players.Single(player => player.Id == item.Scene.Owner)
                    .Zone(item.Scene.Zone).Cards.Single(card => card.VisualId == item.Scene.Id))
                && all.All(item => item.Scene.Id != PlayAccessScenario.PreparedId),
            "The adjacent fan resolves exact disclosed physical occurrences, including an opponent's exile, without copying a prepared virtual spell into the hand");
        var chandra = scene.PlayAccess.Groups.Single(group => group.Cards.Any(item => item.Scene.Id == PlayAccessScenario.ExileSpellId));
        var iteration = scene.PlayAccess.Groups.Single(group => group.Cards.Any(item => item.Scene.Id == PlayAccessScenario.ExileLandId));
        check(chandra.Id != iteration.Id && chandra.SourceIds.Contains(PlayAccessScenario.ChandraId)
                && iteration.SourceIds.Contains(PlayAccessScenario.IterationId),
            "Different exile grants keep their source relationship instead of merging into an undifferentiated extra hand");
        foreach (int stage in new[] { 1, 2 }) {
            var retained = Build(stages[stage].Snapshot);
            check(retained.PlayAccess.Groups.SelectMany(group => group.Cards).Select(item => item.Scene.Id).Order()
                    .SequenceEqual(all.Select(item => item.Scene.Id).Order())
                    && retained.PlayAccess.Groups.SelectMany(group => group.Cards).All(item => item.Access.Routes.All(route => !route.CanStart)),
                "Off-priority and required-choice observations retain disclosed permissions without promoting them into current initiation authority");
        }
        var duplicate = Build(stages[4].Snapshot);
        check(duplicate.PlayAccess.Groups.SelectMany(group => group.Cards).Count(item => item.Scene.Id == PlayAccessScenario.FlashbackId) == 1
                && duplicate.PlayAccess.Groups.SelectMany(group => group.Cards).Single(item => item.Scene.Id == PlayAccessScenario.FlashbackId).Access.Routes.Length == 2,
            "Two current cast routes describe one physical graveyard card and retain both routes for source-action discovery");
        var revoked = Build(stages[3].Snapshot);
        check(revoked.PlayAccess.Groups.SelectMany(group => group.Cards).All(item => item.Scene.Id != PlayAccessScenario.ExileSpellId)
                && stages[3].Snapshot.Viewer!.Zone("Exile").Cards.Any(card => card.VisualId == PlayAccessScenario.ExileSpellId),
            "Expiring a play grant removes the adjacent occurrence while its ordinary public exile face remains available in the actual zone");
        var hidden = initial with { Players = initial.Players.Select(player => player.Id != 1 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Exile" ? zone : zone with { Cards = [], Count = 1 }).ToImmutableArray()
        }).ToImmutableArray() };
        check(Build(hidden).PlayAccess.Groups.SelectMany(group => group.Cards).All(item => item.Scene.Id != PlayAccessScenario.BorrowedId),
            "A retained reference cannot recreate an opponent-owned face after the current snapshot withdraws its disclosure");
        foreach (int stage in new[] { 5, 11, 12, 13 }) {
            var state = stages[stage].Snapshot;
            var folded = Build(state);
            foreach (var collection in new[] { CardCollectionTarget.Hand, CardCollectionTarget.Access }) {
                var spread = Build(state, new(collection)).Spread!;
                var originals = collection == CardCollectionTarget.Hand ? folded.Hand.Cards
                    : folded.PlayAccess.Groups.SelectMany(group => group.Cards).Select(item => item.Scene).ToArray();
                check(spread.Anchors.Select(card => (card.Id, card.Pose, card.Zone, card.Owner))
                        .SequenceEqual(originals.Select(card => (card.Id, card.Pose, card.Zone, card.Owner)))
                        && spread.WorldCards.Select(card => (card.Id, card.Card, card.Zone, card.Owner))
                            .SequenceEqual(originals.Select(card => (card.Id, card.Card, card.Zone, card.Owner)))
                        && folded.Hand.Cards.Select(card => card.Id).SequenceEqual(state.Viewer!.Zone("Hand").Cards.Select(card => card.VisualId)),
                    "Unfolding preserves every original physical occurrence, real zone, owner and tucked anchor while the complete hand has no page boundary");
                check(spread.View == spread.InitialView && spread.View.Scale >= .85f
                        && (spread.Overview.Scale >= .85f || spread.View.Scale > spread.Overview.Scale),
                    "A large collection opens at reading distance; fitting its entire extent is an explicit overview camera gesture");
                foreach (int index in new[] { 0, spread.Cards.Count / 2, spread.Cards.Count - 1 }.Distinct()) {
                    var target = spread.WorldCards[index];
                    var focused = Build(state, new(collection, spread.Focus(target.Id))).Spread!;
                    var face = focused.Cards.Single(card => card.Id == target.Id);
                    check(CardSpreadPlan.Viewport.Contains(face.Pose.Bounds) && face.Pose.Size.X >= CardSpreadPlan.CardSize.X * .95f
                            && focused.WorldCards.Select(card => (card.Id, card.Pose)).SequenceEqual(spread.WorldCards.Select(card => (card.Id, card.Pose)))
                            && focused.Anchors.Select(card => (card.Id, card.Pose)).SequenceEqual(spread.Anchors.Select(card => (card.Id, card.Pose))),
                        "Focusing a first, middle or last card changes only the physical collection camera and exposes its intact current face without moving its source anchor");
                }
            }
        }

        CheckPublicZones(stages[14].Snapshot, check);
        CheckAuthoredJourneys(check);
    }

    private static TableScene Build(GameSnapshot state, CardSpreadRequest? spread = null) => TableScene.Build(state, new(-100, -100),
        null, null, false, CardPresentation.IsResource, spread: spread);

    private static void CheckPublicZones(GameSnapshot state, Action<bool, string> check)
    {
        foreach (var target in new[] {
            new CardCollectionTarget.PublicZone(0, PublicInspectionZone.Graveyard),
            new CardCollectionTarget.PublicZone(0, PublicInspectionZone.Exile),
            new CardCollectionTarget.PublicZone(1, PublicInspectionZone.Exile)
        }) {
            var current = state.Players.Single(player => player.Id == target.PlayerId).Zone(target.Zone.ToString());
            var disclosed = current.Cards.Where(card => !card.FaceDown).ToArray();
            var scene = Build(state, new(target));
            var spread = scene.Spread!;
            check(spread.Target == target
                    && spread.WorldCards.Select(card => card.Id).Order().SequenceEqual(disclosed.Select(card => card.VisualId).Order())
                    && spread.WorldCards.All(card => card.Owner == target.PlayerId && card.Zone == target.Zone.ToString()
                        && current.Cards.Contains(card.Card) && !card.Card.FaceDown)
                    && spread.WorldCards.Select(card => card.Id).Distinct().Count() == disclosed.Length
                    && spread.WorldCards.Any(card => !card.Card.Selectable),
                "An actual public pile unfolds all its disclosed current occurrences, including unplayable cards, with their real zone and owner and no masked face");
            check(scene.Hand.Cards.Select(card => card.Id).SequenceEqual(state.Viewer!.Zone("Hand").Cards.Select(card => card.VisualId))
                    && scene.PlayAccess.Groups.SelectMany(group => group.Cards).Select(item => item.Scene.Id).Order()
                        .SequenceEqual(state.PlayAccesses.Select(access => access.CardId).Order()),
                "Inspecting a public zone neither turns its ordinary cards into play permissions nor changes the private hand or existing grants");
            string newest = disclosed[^1].VisualId;
            check(spread.Cards.Single(card => card.Id == newest).Pose.Bounds.Intersects(spread.Area)
                    && spread.View.Scale >= .85f,
                "A large public pile opens at its newest disclosed face at reading distance instead of beginning at its oldest history");
        }
        foreach (var target in new[] {
            new CardCollectionTarget.PublicZone(2, PublicInspectionZone.Exile),
            new CardCollectionTarget.PublicZone(2, PublicInspectionZone.Graveyard)
        }) {
            var spread = Build(state, new(target)).Spread;
            check(spread is { } empty && empty.Target == target && empty.WorldCards.Count == 0 && empty.Anchors.Count == 0,
                "An empty or count-only public zone retains its inspection context without fabricating concealed card faces or source handles");
        }
    }

    private static void CheckAuthoredJourneys(Action<bool, string> check)
    {
        foreach (var (id, owner, source, destination) in new[] {
            (PlayAccessScenario.ExileSpellId, 0, "Exile", "Battlefield"),
            (PlayAccessScenario.FlashbackId, 0, "Graveyard", "Exile"),
            (PlayAccessScenario.BorrowedId, 1, "Exile", "Battlefield")
        }) {
            var lab = new ExperienceLabSession(PlayAccessScenario.Id);
            var before = lab.State;
            var card = before.Players.Single(player => player.Id == owner).Zone(source).Cards.Single(item => item.VisualId == id);
            var offers = lab.SourceActions(before.Id, before.Decision!.Id, card.Key);
            check(lab.State == before && offers.Actions.Length == 1,
                "Reading an authored out-of-hand source obtains its current offer without casting or changing any zone");
            var command = new UseOfferedAction(before.Id, before.Decision.Id, card.Key, offers.Actions.Single().Id);
            lab.Reply(command);
            check(lab.State.Stack.Single().Card?.VisualId == id
                    && lab.State.Players.Single(player => player.Id == owner).Zone(source).Cards.All(item => item.VisualId != id)
                    && lab.State.PlayAccesses.All(access => access.CardId != id)
                    && lab.State.Viewer!.Zone("Hand").Count == before.Viewer!.Zone("Hand").Count,
                "An exact current offer moves the same physical occurrence from its actual owner and zone to the stack without changing the hand");
            bool stale = false;
            try { lab.Reply(command); } catch (EngineCommandRejectedException) { stale = true; }
            check(stale, "The prior source offer cannot be replayed after the cast creates a fresh response scope");
            lab.Reply(new PassPriority(lab.State.Id, lab.State.Decision!.Id));
            check(lab.State.Stack.IsEmpty && lab.State.Viewer!.Zone(destination).Cards.Any(item => item.VisualId == id)
                    && lab.State.PlayAccesses.All(access => access.CardId != id),
                "The authored stack result preserves physical identity and uses the explicit battlefield or flashback-exile destination without reviving its grant");
        }
        var landLab = new ExperienceLabSession(PlayAccessScenario.Id);
        var land = landLab.State.Viewer!.Zone("Exile").Cards.Single(card => card.VisualId == PlayAccessScenario.ExileLandId);
        var offer = landLab.SourceActions(landLab.State.Id, landLab.State.Decision!.Id, land.Key).Actions.Single();
        landLab.Reply(new UseOfferedAction(landLab.State.Id, landLab.State.Decision!.Id, land.Key, offer.Id));
        check(landLab.State.Stack.IsEmpty && landLab.State.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == land.VisualId)
                && landLab.State.Viewer.Zone("Hand").Count == 7,
            "The exact exiled-land offer enters the battlefield directly without fabricating a hand card or spell on the stack");
        var multi = new ExperienceLabSession(PlayAccessScenario.Id);
        multi.Replay(4);
        var twice = multi.State.Viewer!.Zone("Graveyard").Cards.Single(card => card.VisualId == PlayAccessScenario.FlashbackId);
        var routes = multi.SourceActions(multi.State.Id, multi.State.Decision!.Id, twice.Key);
        check(routes.Actions.Length == 2 && routes.Actions.Select(action => action.Id).Distinct().Count() == 2,
            "Multiple grants retain distinct opaque offers for one source rather than arbitrarily selecting a cast route");
        multi.AdvanceTo(5);
        bool invalidated = false;
        try { multi.Reply(new UseOfferedAction(routes.GameId, routes.DecisionId, routes.SourceCardKey, routes.Actions[1].Id)); }
        catch (EngineCommandRejectedException) { invalidated = true; }
        check(invalidated, "A source route captured before an external observation is rejected by the current lab response scope");
    }
}
