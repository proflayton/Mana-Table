using Mana.Table.Interaction;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class MatchPresentationLifecycleChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var original = PresentationSmoke.Example();
        var initial = original with { Revision = 1, Combat = null, Stack = [], PriorityPlayerId = original.ViewerId,
            DecisionOwnerId = original.ViewerId, ActionClass = "priority", Notices = [],
            Players = original.Players.Select(player => player with { Mana = ImmutableDictionary<string, int>.Empty }).ToImmutableArray(),
            Activity = [new(1, original.Turn, original.PhaseKey, "resolved", original.ViewerId, "Retained history", null, null) { Outcome = "resolved" }]
        };
        var owner = new MatchPresentationLifecycle();
        var baseline = owner.Observe(null, initial, 0, BoardView.Identity);
        check(baseline.ScopeChanged && owner.LastResolution == null && owner.Changes.Count == 0 && owner.Impacts.Count == 0
            && owner.PriorityTransition == null && !owner.Ready(0),
            "Joining baselines all presentation consumers together without replaying retained history and retains the opening pacing beat");

        var card = initial.Viewer!.Zone("Battlefield").Cards.First(card => !card.FaceDown);
        var context = new CardPresentationContext(initial, "match:priority", false, false, false, false);
        owner.Cards.BeginFrame(context, null, .016f, true);
        var entry = owner.Cards.Prepare(new(card.VisualId, card, new(new(500, 450), new(100, 140)),
            "Battlefield", initial.ViewerId, "card:" + card.Key, UiIntent.None));
        owner.Cards.EndFrame(); owner.Cards.Toggle(entry.Hit, context);
        var changed = initial with { Revision = 2, PriorityPlayerId = 1, DecisionOwnerId = 1, Decision = null, Notices = ["New notice"],
            Players = initial.Players.Select(player => player.Id != initial.ViewerId ? player : player with {
                Life = player.Life - 2, Mana = ImmutableDictionary<string, int>.Empty.Add("G", 2),
                Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with {
                    Count = zone.Count - 1, Cards = zone.Cards.Where(item => item.VisualId != card.VisualId).ToImmutableArray()
                } : zone.Name == "Graveyard" ? zone with { Count = zone.Count + 1, Cards = [.. zone.Cards, card] } : zone).ToImmutableArray()
            }).ToImmutableArray(), Activity = [.. initial.Activity,
                new(2, initial.Turn, initial.PhaseKey, "counters", initial.ViewerId, "Reported counters", card.VisualId, card.Name) { Change = new("counters", "+1/+1", 0, 2) },
                new(3, initial.Turn, initial.PhaseKey, "resolved", initial.ViewerId, "New outcome", null, null) { Outcome = "resolved" },
                new(4, initial.Turn, initial.PhaseKey, "life", initial.ViewerId, "Reported life loss", null, null) { Impact = new("life", -2, PlayerId: initial.ViewerId) }
            ]
        };
        var transition = owner.Observe(initial, changed, 2, BoardView.Identity);
        check(!transition.ScopeChanged && transition is { LifeLosses: 1, Notice: "New notice" }
            && owner.LastResolution is { Title: "New outcome", Started: 2 } && owner.Changes.Count == 1 && owner.Impacts.Count == 1
            && owner.PriorityTransition is { ResponderChanged: true, ResponderId: 1 }
            && owner.ManaCue(initial.ViewerId, "G", 2) is { Before: 0, After: 2 }
            && owner.Departures.Single().Card.VisualId == card.VisualId,
            "One accepted observation preserves reported outcomes, counters, actual life, mana and priority alongside a public departure from its displayed pose");
        owner.Observe(changed, changed, 2.1, BoardView.Identity);
        check(owner.LastResolution?.Started == 2 && owner.LatestChange?.Updated == 2
            && owner.ManaCue(initial.ViewerId, "G", 2.1)?.Started == 2 && owner.Impacts.Single().Started == 2,
            "Polling the same accepted feed cannot replay cues or restart their timers");

        var switched = changed with { ViewerId = 1, Revision = 3 };
        var switchedTransition = owner.Observe(changed, switched, 2.2, BoardView.Identity);
        check(switchedTransition is { ScopeChanged: true, LifeLosses: 0, Notice: null }
            && owner.LastResolution == null && owner.Changes.Count == 0 && owner.Impacts.Count == 0
            && owner.SeatCues.Count == 0 && owner.CardCues.Count == 0 && owner.PriorityTransition == null
            && owner.ManaCues(initial.ViewerId, 2.2).Count == 0 && owner.Departures.Count == 0
            && owner.Cards.Cards.Count == 0 && !owner.Cards.HasPinnedCard && !owner.Cards.TryGetPose(card.VisualId, out _),
            "A same-game viewer change immediately revokes every prior-seat feedback consumer, reader and displayed card pose before another frame");
        owner.Observe(switched, switched, 2.3, BoardView.Identity);
        check(owner.LastResolution == null && owner.Changes.Count == 0 && owner.ManaCues(initial.ViewerId, 2.3).Count == 0,
            "The new viewer's repeated retained history remains a baseline without invented mana or priority transitions");
        owner.Reset();
        check(owner.Ready(0) && owner.Cards.Cards.Count == 0 && owner.Departures.Count == 0,
            "Explicit presentation reset clears pacing and retained public faces together");
        var reattached = owner.Observe(switched, switched, 3, BoardView.Identity);
        check(reattached.ScopeChanged && owner.LastResolution == null && owner.Changes.Count == 0,
            "Reattaching after a reset establishes a fresh baseline even with the same accepted match snapshot");
        check(owner.Observe(switched, null, 3.1, BoardView.Identity).ScopeChanged && owner.Ready(0)
            && owner.Observe(null, switched with { Id = "presentation-rematch" }, 4, BoardView.Identity).ScopeChanged,
            "Leaving and rematching use the same scope revocation lifecycle");
    }
}
