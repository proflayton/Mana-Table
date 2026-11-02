using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class MatchResultPresentationRegression
{
    public static void Run(Action<bool, string> check)
    {
        Player Player(int id, int team, bool eliminated = false) => new(id, "Player " + id, eliminated ? 0 : 40,
            false, eliminated, ImmutableDictionary<string, int>.Empty, []) { TeamId = team };
        var state = new GameSnapshot("result-game", "finished", null, "Defeat", 0, 12, "Cleanup", "CLEANUP", 2,
            [Player(0, 10), Player(1, 10, true), Player(2, 20, true)], [], null, null) {
            Outcome = new("result-game", MatchOutcomeKind.Win, [0], [10], "AllOpposingTeamsLost")
        };
        check(MatchResultPresentation.For(state) is { Title: "Victory", Victory: true, Interrupted: false },
            "Typed individual victory overrides contradictory legacy display text");
        check(MatchResultPresentation.PlayerLabel(state, state.Players[0]) == "Winner",
            "Result rows identify an actual rules winner");
        check(MatchResultPresentation.For(state with { ViewerId = 1 }) is { Title: "Team victory", Banner: "TEAM VICTORY", Victory: true },
            "An eliminated ally can see the team's victory without becoming an individual winner");
        check(MatchResultPresentation.PlayerLabel(state, state.Players[1]) == "Team won (eliminated)"
            && state.Outcome!.WinningPlayerIds.SequenceEqual([0]),
            "Result rows preserve individual elimination alongside a winning team");
        check(MatchResultPresentation.For(state with { ViewerId = 2, Result = "Victory" }) is { Title: "Defeat", Victory: false },
            "An opposing seat cannot inherit a victory from presentation text");
        check(MatchResultPresentation.For(state with { ViewerId = -1 }) is { Title: "Game finished", Victory: false },
            "Spectator result labels do not pretend the observer won");
        var draw = state with { Outcome = new(state.Id, MatchOutcomeKind.Draw, [], [], "Draw"), Result = "Victory" };
        check(MatchResultPresentation.For(draw) is { Title: "Draw", Banner: "DRAW", Victory: false },
            "Authoritative draws have their own result presentation");
        check(MatchResultPresentation.PlayerLabel(draw, draw.Players[0]) == "Draw"
            && MatchResultPresentation.PlayerLabel(draw, draw.Players[1]) == "Eliminated",
            "A draw does not erase an earlier individual elimination");
        check(MatchResultPresentation.For(state with { Status = "error", Error = "Engine stopped" })
            is { Title: "Game interrupted", Victory: false, Interrupted: true },
            "An engine failure cannot be displayed as a typed or legacy victory");
        check(MatchResultPresentation.PlayerLabel(state with { Status = "error" }, state.Players[0]) == "Not completed",
            "Interrupted result rows do not announce winners");
        check(MatchResultPresentation.For(state with { Error = "Stopped", Outcome = null, Result = "Victory" })
            is { Victory: false, Interrupted: true },
            "A nonempty engine error cannot inherit a legacy victory even with a finished status");
        check(MatchResultPresentation.PlayerLabel(state, state.Players[2] with { Eliminated = false }) == "Defeated",
            "A certified losing participant is defeated even when an alternate win did not mark it eliminated");
        check(MatchResultPresentation.For(state with { Status = "playing", Outcome = null, Result = "Victory" })
            is { Title: "Game in progress", Victory: false },
            "A running match's stray display text cannot announce a result");
        check(MatchResultPresentation.For(state with { Outcome = null, Result = "Victory" })
            is { Title: "Victory", Victory: true },
            "Legacy providers retain their result label without inventing typed outcome facts");
        check(MatchResultPresentation.For(state with { Outcome = state.Outcome with { GameId = "another-game" }, Result = "Victory" })
            is { Title: "Game finished", Victory: false },
            "An invalid present outcome cannot fall back to a legacy victory");
        check(MatchResultPresentation.PlayerLabel(state with { Outcome = null }, state.Players[2], timedOut: true) == "Timed out",
            "Legacy timeout labels remain available without certifying a match result");
    }
}
