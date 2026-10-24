using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

// Composition supplies explicit ports. The game neither discovers provider
// interfaces nor owns the provider's lifetime.
public sealed record TableServices(IEngineCatalog Catalog, ITableSetup Tables, IMatchConnection Match, Func<Task> Initialize)
{
    public ISourceActions? SourceActions { get; init; }
    public ICardPrintings? Printings { get; init; }
    public IDeckTokens? Tokens { get; init; }
    public IDeckLibrary? Library { get; init; }
    public IDeckWorkshop? Workshop { get; init; }
    public IDeckAnalysis? Analysis { get; init; }
    public ITableSeatSetup? Seats { get; init; }
    public ITableTempo? Tempo { get; init; }
    public ITableSocial? Social { get; init; }
    public IDeckVersionHistory? Versions { get; init; }
    public ExperienceLabSession? Lab { get; init; }
}
