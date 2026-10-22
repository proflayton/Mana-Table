using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal static class PublicChangeFeedbackChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var initial = PresentationSmoke.Example();
        var targets = initial.Viewer!.Zone("Battlefield").Cards.Where(card => card.Type.Contains("Creature")).Take(2).ToArray();
        var feedback = new PublicChangeFeedback();
        var cause = new PublicEffectCause("enchantment", "Felidar Retreat", "Put a counter on each creature. They gain vigilance.", initial.ViewerId, "trigger", "first-trigger");
        ActivityEntry Event(long id, Card target, int before, int after, PublicEffectCause? source) => new(id, initial.Turn, initial.PhaseKey,
            "counters", initial.ViewerId, "Public counter change", target.VisualId, target.Name) {
                Change = new("counters", "+1/+1", before, after), Cause = source
            };
        feedback.Observe(null, initial, 0);
        var changed = initial with { Activity = [Event(1, targets[0], 2, 3, cause), Event(2, targets[1], 4, 5, cause)] };
        feedback.Observe(initial, changed, 1);
        check(feedback.Groups.Count == 1 && feedback.Latest!.Changes.Count == 2
            && feedback.Latest.Summary == "Added 1 +1/+1 counter on each of 2 permanents",
            "One engine-issued trigger groups its exact per-target counter changes and distinguishes each amount from the total");
        feedback.Observe(changed, changed, 2);
        check(feedback.Latest!.Changes.Count == 2 && feedback.Latest.Updated == 1,
            "Repeated observation neither duplicates counter changes nor restarts their visual timing");
        var again = changed with { Activity = [.. changed.Activity, Event(3, targets[0], 3, 4, cause with { Id = "second-trigger" })] };
        feedback.Observe(changed, again, 3);
        check(feedback.Groups.Count == 2 && feedback.Latest!.Changes.Count == 1,
            "Repeated triggers with the same card name and text stay separate when their engine cause identities differ");
        var unreported = again with { Activity = [.. again.Activity, Event(4, targets[0], 4, 3, null), Event(5, targets[1], 5, 4, null)] };
        feedback.Observe(again, unreported, 4);
        check(feedback.Groups.Count == 4 && feedback.Groups.TakeLast(2).All(group => group.Cause == null && group.Changes.Count == 1),
            "Unknown sources remain unknown rather than being attributed to a nearby resolution or matching card name");
        var hidden = unreported with { Players = unreported.Players.Select(player => player.Id != initial.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Where(card => card.VisualId != targets[0].VisualId).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        feedback.Observe(unreported, hidden, 5);
        check(feedback.Groups.First().Changes.Count == 2 && feedback.Groups.First().Summary.Contains("each of 2 permanents"),
            "A later departure does not rewrite the affected count of a completed public effect");
        feedback.Observe(hidden, hidden with { Id = "rematch" }, 6);
        check(feedback.Groups.Count == 0, "A rematch establishes a clean counter-feedback baseline");
        var large = new PublicChangeFeedback();
        large.Observe(null, initial, 0);
        large.Observe(initial, initial with { Activity = [Event(1, targets[0], 0, 1_500_000_000, cause), Event(2, targets[1], 0, 1_500_000_000, cause)] }, 1);
        check(large.Latest!.Summary.Contains("1500000000") && large.Latest.Changes.Count == 2,
            "Large valid per-card counter totals do not overflow when one effect changes multiple permanents");
    }
}
