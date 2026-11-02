using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class MatchOutcomeRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var state = State();
        check(PlayerRelations.Between(state, 7, 21) == PlayerRelation.Ally
                && PlayerRelations.Between(state, 7, 13) == PlayerRelation.Opponent
                && PlayerRelations.ForViewer(state, 7) == PlayerRelation.Self,
            "Actual engine teams classify self, allies and opponents independently of seat order, name and elimination");
        var legacy = state with { Players = state.Players.Select(player => player with { TeamId = null }).ToImmutableArray() };
        check(PlayerRelations.ForViewer(legacy, 7) == PlayerRelation.Self
                && PlayerRelations.ForViewer(legacy, 13) == PlayerRelation.Unknown
                && PlayerRelations.Between(state, 99, 13) == PlayerRelation.Unknown
                && PlayerRelations.ForViewer(state with { ViewerId = -1 }, 7) == PlayerRelation.Unknown,
            "Legacy absent teams and nonparticipants remain unknown rather than manufacturing a free-for-all or spectator team");
        var missingTeam = state with { Players = state.Players.SetItem(2, state.Players[2] with { TeamId = null }) };
        check(PlayerRelations.ForViewer(missingTeam, 21) == PlayerRelation.Unknown
                && PlayerRelations.ForViewer(missingTeam, 13) == PlayerRelation.Opponent
                && PlayerRelations.ForViewer(state with { Players = state.Players.Add(state.Players[0]) }, 13) == PlayerRelation.Unknown
                && PlayerRelations.ForViewer(state with { Players = state.Players.SetItem(2, state.Players[2] with { TeamId = -1 }) }, 21) == PlayerRelation.Unknown,
            "Mixed missing or malformed team knowledge cannot turn a possible teammate into a known opponent");

        foreach (string label in new[] { "Victory", "Defeat", "Draw", "Game over" })
            check(MatchOutcomes.ForViewer(state with { Status = "finished", Result = label }) == PlayerMatchOutcome.Unknown,
                "A legacy viewer-local result label never certifies a global rules outcome: " + label);
        var won = state with { Status = "finished", Outcome = new(state.Id, MatchOutcomeKind.Win, [7], [11], "AllOpponentsLost") };
        check(MatchOutcomes.ForPlayer(won, 7) == PlayerMatchOutcome.Win
                && MatchOutcomes.ForPlayer(won, 21) == PlayerMatchOutcome.TeamWin
                && MatchOutcomes.ForPlayer(won, 13) == PlayerMatchOutcome.Loss
                && MatchOutcomes.ForPlayer(won, 99) == PlayerMatchOutcome.Unknown
                && MatchOutcomes.ForViewer(won with { ViewerId = 21 }) == PlayerMatchOutcome.TeamWin,
            "Actual individual winners remain distinct from an eliminated ally's team victory and another team's loss");
        check(won.Outcome!.WinningPlayerIds.SequenceEqual([7])
                && MatchOutcomes.ForViewer(won with { ViewerId = -1 }) == PlayerMatchOutcome.Unknown
                && MatchOutcomes.ForViewer(won with { Result = "Defeat" }) == PlayerMatchOutcome.Win,
            "Outcome projection preserves actual winner membership and does not derive authority from spectator or display labels");
        var draw = won with { Outcome = new(state.Id, MatchOutcomeKind.Draw, [], [], "Draw") };
        check(state.Players.All(player => MatchOutcomes.ForPlayer(draw, player.Id) == PlayerMatchOutcome.Draw)
                && MatchOutcomes.ForPlayer(draw, -1) == PlayerMatchOutcome.Unknown,
            "A global draw supplies an audience perspective without inventing winning players or teams");
        check(MatchOutcomes.ForPlayer(draw, 21) == PlayerMatchOutcome.Draw && draw.Players.Single(player => player.Id == 21).Eliminated
                && draw.Outcome!.WinningPlayerIds.IsEmpty && draw.Outcome.WinningTeamIds.IsEmpty,
            "A global draw does not erase an eliminated participant's earlier loss or certify an individual scoring award");

        foreach (var invalid in new[] {
            won with { Status = "playing" }, won with { Status = "error", Error = "Interrupted" },
            won with { Error = "Interrupted" }, won with { Decision = new("pending", new(), new ConfirmationPayload(new("Yes", true), new("No", true))) },
            won with { Outcome = won.Outcome with { GameId = "other-game" } },
            won with { Outcome = won.Outcome with { WinningPlayerIds = [99] } },
            won with { Outcome = won.Outcome with { WinningTeamIds = [47] } },
            won with { Outcome = won.Outcome with { WinningPlayerIds = [7, 7] } },
            won with { Outcome = won.Outcome with { WinningTeamIds = [11, 11] } },
            won with { Outcome = won.Outcome with { RuleReason = "" } },
            won with { Outcome = won.Outcome with { Kind = (MatchOutcomeKind)999 } },
            won with { Players = won.Players.SetItem(0, won.Players[0] with { TeamId = null }) },
            draw with { Outcome = draw.Outcome! with { WinningPlayerIds = [7] } }
        }) check(MatchOutcomes.ForPlayer(invalid, 7) == PlayerMatchOutcome.Unknown,
            "Unfinished, interrupted, foreign or inconsistent outcome facts never certify match results");
    }

    private static GameSnapshot State() => new("outcome-regression", "playing", null, null, 7, 3, "Main", "MAIN1", 7,
        [Player(7, 11), Player(13, 47), Player(21, 11) with { Eliminated = true }, Player(42, 47)], [], null, null);
    private static Player Player(int id, int team) => new(id, "Same name", 40, false, false, ImmutableDictionary<string, int>.Empty, []) { TeamId = team };
}
