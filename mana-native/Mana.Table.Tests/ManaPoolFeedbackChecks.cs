using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal static class ManaPoolFeedbackChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var original = PresentationSmoke.Example();
        int player = original.ViewerId;
        GameSnapshot Pool(long revision, params (string Symbol, int Amount)[] parts) => original with {
            Revision = revision, Players = original.Players.Select(p => p.Id == player
                ? p with { Mana = parts.ToDictionary(part => part.Symbol, part => part.Amount) }
                : p with { Mana = [] }).ToArray()
        };
        var presence = new ManaPoolPresence();
        var empty = Pool(1);
        var green = Pool(2, ("G", 2));
        presence.Observe(null, green, 0);
        check(presence.ForPlayer(player, 0).Count == 0, "Joining a table with existing mana does not invent a gain animation");
        presence.Clear();
        presence.Observe(null, empty, 0);
        presence.Observe(empty, green, 1);
        check(presence.For(player, "G", 1) is { Before: 0, After: 2, Delta: 2, Started: 1 },
            "An authoritative color increase immediately exposes the exact new amount and a gain cue");
        presence.Observe(green, green, 1.3);
        presence.Observe(green, empty, 1.4);
        check(presence.For(player, "G", 1.4)?.Started == 1,
            "Repeated and stale revisions neither replay a gain nor replace it with an old pool");
        var one = Pool(3, ("G", 1));
        presence.Observe(green, one, 1.5);
        check(presence.For(player, "G", 1.5) is { Before: 2, After: 1, Delta: -1 },
            "A partial loss preserves the authoritative remainder without guessing why mana left");
        var zero = Pool(4);
        presence.Observe(one, zero, 2);
        check(presence.For(player, "G", 2) is { Before: 1, After: 0, Delta: -1 }
            && presence.For(player, "G", 2.55)?.Progress(2.55) is > .49 and < .51
            && presence.For(player, "G", 3.11) == null,
            "A fully emptied color has a bounded fade cue that expires without another poll");
        presence.Observe(zero, Pool(5, ("G", 2)), 4);
        var converted = Pool(6, ("C", 2));
        presence.Observe(green, converted, 5);
        check(presence.For(player, "G", 5) is { Before: 2, After: 0 }
            && presence.For(player, "C", 5) is { Before: 0, After: 2 },
            "Converting mana animates both colors even when the pool's total stays equal");
        var nextPhase = converted with { Revision = 7, PhaseKey = "COMBAT_BEGIN" };
        presence.Observe(converted, nextPhase, 5.2);
        check(presence.For(player, "C", 5.2)?.Started == 5,
            "A step transition does not empty retained mana or replay its animation");
        converted.Viewer!.Mana["C"] = 999;
        presence.Observe(nextPhase, Pool(8, ("C", 2)), 5.3);
        check(presence.For(player, "C", 5.3) is { After: 2, Started: 5 },
            "Stored public amounts are copied so later snapshot mutation cannot change comparison history");
        var eliminated = Pool(9, ("C", 2));
        eliminated = eliminated with { Players = eliminated.Players.Select(p => p.Id == player ? p with { Eliminated = true } : p).ToArray() };
        presence.Observe(nextPhase, eliminated, 5.4);
        check(presence.ForPlayer(player, 5.4).Count == 0, "An eliminated seat cannot retain mana cues");
        presence.Observe(eliminated, Pool(10, ("U", 4)) with { Id = "new-table" }, 6);
        check(presence.ForPlayer(player, 6).Count == 0, "Changing tables resets mana history without a false gain");
        presence.Clear();
        presence.Observe(empty, green, 7);
        check(presence.For(player, "G", 7)?.Delta == 2, "A first transition with a valid prior snapshot still animates the actual gain");
        presence.Observe(green, green with { Status = "finished", Revision = 3 }, 7.1);
        check(presence.ForPlayer(player, 7.1).Count == 0, "Finishing a game retires transient mana cues");
        var cue = new ManaPoolCue(player, "C", 0, 1, 8);
        check(cue.Progress(7) == 0 && cue.Progress(10) == 1, "Mana cue progress is bounded for rendering and reduced-motion alternatives");
    }
}
