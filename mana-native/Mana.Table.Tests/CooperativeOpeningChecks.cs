using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

internal static class CooperativeOpeningChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var players = new[] { 7, 42, 99 }.Select(id => new Player(id, "Player " + id, 40, false, false,
            ImmutableDictionary<string, int>.Empty, [new("Hand", 0, [], null), new("Battlefield", 0, [], null)])).ToImmutableArray();
        var decision = new Decision("pregame-choice", new(), new EntitySelectionPayload(new("Continue", false),
            new("Cancel", false), [99, 7, 42], new(1, 1)));
        var state = new GameSnapshot("pregame-game", "playing", null, null, 7, 0, "Pregame", "PREGAME", null,
            players, [], null, decision) { DecisionOwnerId = 7 };
        var answer = CooperativeEncounterEngineJourney.StartingPlayerAnswer(state) as SelectPlayer;
        check(answer is { Player: 7 } && answer.GameId == state.Id && answer.DecisionId == decision.Id,
            "The exact pregame one-player choice selects our offered rules ID, rather than the first option, display name or seat index");
        check(CooperativeEncounterEngineJourney.StartingPlayerAnswer(state, answer) == null,
            "A submitted starting-player choice is not toggled or submitted again while the same controller decision reconciles");
        var ready = state with { Decision = decision with { Payload = ((EntitySelectionPayload)decision.Payload) with { Complete = new("Continue", true) } } };
        check(CooperativeEncounterEngineJourney.StartingPlayerAnswer(ready, answer) is CompleteSelection complete
                && complete.GameId == state.Id && complete.DecisionId == decision.Id,
            "A pregame controller that still requires confirmation receives it only after our same scoped player selection");
        check(CooperativeEncounterEngineJourney.StartingPlayerAnswer(ready) == null
                && CooperativeEncounterEngineJourney.StartingPlayerAnswer(ready, answer! with { GameId = "old-game" }) == null
                && CooperativeEncounterEngineJourney.StartingPlayerAnswer(ready, answer! with { DecisionId = "old-decision" }) == null,
            "Missing or foreign selection receipts never authorize confirmation of a pregame decision");
        var payload = (EntitySelectionPayload)decision.Payload;
        var changed = new[] {
            state with { Turn = 1 }, state with { PhaseKey = "MAIN1" }, state with { Status = "starting" },
            state with { DecisionOwnerId = 42 },
            state with { Decision = decision with { Context = new(Source: new("spell-source")) } },
            state with { Decision = decision with { Payload = payload with { Range = null } } },
            state with { Decision = decision with { Payload = payload with { Range = new(0, 1) } } },
            state with { Decision = decision with { Payload = payload with { Range = new(1, 2) } } },
            state with { Decision = decision with { Payload = payload with { Players = [7, 42] } } },
            state with { Decision = decision with { Payload = payload with { Players = [7, 42, 101] } } },
            state with { Decision = decision with { Payload = payload with { Players = [7, 7, 42] } } },
            state with { Decision = decision with { Payload = new ConfirmationPayload(new("Yes", true)) } },
            state with { Players = players.SetItem(1, players[1] with { Zones = [new("Hand", 1, [], null)] }) },
            state with { Players = players.SetItem(1, players[1] with { Zones = [new("Hand", 0, [], null),
                new("Battlefield", 1, [new Card { VisualId = "selectable-card", Selectable = true }], null)] }) }
        };
        check(changed.All(other => CooperativeEncounterEngineJourney.StartingPlayerAnswer(other) == null),
            "Gameplay, card/mixed choices, unknown or nonexact ranges, changed authority and malformed player lists cannot enter the starting-player driver");
    }
}
