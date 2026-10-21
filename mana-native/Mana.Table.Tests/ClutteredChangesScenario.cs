using Mana.Contracts;

namespace Mana.Table;

// Production lab and acceptance checks use the same canonical fixture data.
internal static class ClutteredChangesScenario
{
    internal const string TophName = Mana.Scenarios.ClutteredChangesScenario.TophName;
    internal const string RetreatName = Mana.Scenarios.ClutteredChangesScenario.RetreatName;
    internal const string AnimatedLandId = Mana.Scenarios.ClutteredChangesScenario.AnimatedLandId;
    internal const string ActionCardId = Mana.Scenarios.ClutteredChangesScenario.ActionCardId;
    internal static string TophId(int seat) => Mana.Scenarios.ClutteredChangesScenario.TophId(seat);
    internal static string RetreatId(int seat) => Mana.Scenarios.ClutteredChangesScenario.RetreatId(seat);
    internal static GameSnapshot Initial() => Mana.Scenarios.ClutteredChangesScenario.Initial();
    internal static GameSnapshot Earthbend(GameSnapshot state) => Mana.Scenarios.ClutteredChangesScenario.Earthbend(state);
    internal static GameSnapshot Retreat(GameSnapshot state) => Mana.Scenarios.ClutteredChangesScenario.Retreat(state);
}
