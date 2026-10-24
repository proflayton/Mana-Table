using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

// Tests compose explicit ports at the boundary, just as the shipping host does.
// The caller retains the provider lifetime; the UI owns none of these services.
internal static class TestTableServices
{
    internal static TableServices For(ICardEngine engine) => new(engine, engine, engine, engine.InitializeAsync) {
        SourceActions = engine as ISourceActions,
        Printings = engine as ICardPrintings,
        Tokens = engine as IDeckTokens,
        Library = engine as IDeckLibrary,
        Workshop = engine as IDeckWorkshop,
        Analysis = engine as IDeckAnalysis,
        Seats = engine as ITableSeatSetup,
        Tempo = engine as ITableTempo,
        Social = engine as ITableSocial,
        Versions = engine as IDeckVersionHistory,
        Lab = (engine as ExperienceLabEngine)?.Session
    };
}
