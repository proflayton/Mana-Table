using System.Collections.Immutable;
using Mana.Contracts;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static partial class CooperativeLobbyInteractionChecks
{
    private static void CheckSetupProblems(string profile, Point viewport, Action<bool, string> check)
    {
        var port = new Port();
        port.HostAsync(5, false).GetAwaiter().GetResult();
        for (int index = 1; index < 5; index++)
            port.SetSeatAsync(port.Lobby.TableId, index, "AI", "ai-a").GetAwaiter().GetResult();
        var roles = new[] { MatchSeatRole.Party, MatchSeatRole.Opposition, MatchSeatRole.Opposition, MatchSeatRole.Party, MatchSeatRole.Party };
        var specification = new MatchSetupSpecification(MatchRulesets.CooperativeCommander, roles.Select((role, index) =>
            new MatchSeatAssignment(index, role == MatchSeatRole.Party ? 0 : 1, role)).ToImmutableArray());
        port.ConfigureMatchAsync(port.Lobby.TableId, port.Lobby.Revision, specification).GetAwaiter().GetResult();
        using var run = new Journey(profile, "setup-problems", viewport, port, check);
        run.View.SetConnectedTable(port.Lobby); run.Tick();
        run.Click("match-setup"); run.Click("match-opposition-size:1");
        int configurations = port.ConfigureCalls;
        var participants = port.Lobby.Seats.Select(seat => seat.ParticipantId).ToArray();
        check(!run.Has("match-setup-apply") && run.Text.Contains("keeps 2 occupied opposition places but allows 1")
                && !run.Text.Contains("Open AI seats"),
            "Reducing an enrolled opposition inside a noncontiguous roster explains the actual retained-side conflict at " + viewport);
        run.Tick(new Point(1130, 831), ButtonState.Pressed); run.Tick();
        check(port.ConfigureCalls == configurations && port.Lobby.Seats.Select(seat => seat.ParticipantId).SequenceEqual(participants),
            "A disabled count change neither remaps the enrolled players nor submits a setup command");
        run.Save("retained-opposition", new { port.ConfigureCalls, participants });

        port.SetSeatAsync(port.Lobby.TableId, 4, "OPEN").GetAwaiter().GetResult();
        port.FriendJoins(4);
        run.Until(() => run.View.Lobby?.Seats[4].Type == "REMOTE", "The rules modal observes a new connected friend without changing its proposed counts");
        run.Click("match-party-size:1");
        var connectedRoster = port.Lobby.Seats.Select(seat => seat.ParticipantId).ToArray();
        check(!run.Has("match-setup-apply") && run.Text.Contains("would remove a connected friend")
                && !run.Text.Contains("Open AI seats"),
            "A count that would truncate a connected friend's place explains that specific protection at " + viewport);
        run.Tick(new Point(1130, 831), ButtonState.Pressed); run.Tick();
        check(port.ConfigureCalls == configurations && port.Lobby.Seats.Select(seat => seat.ParticipantId).SequenceEqual(connectedRoster),
            "The connected friend's enrollment remains intact after a pointer press on the disabled Apply area");
        run.Save("connected-friend-retained", new { port.ConfigureCalls, connectedRoster });
    }
}
