using Mana.Contracts;

namespace Mana.Magic;

/// <summary>The exact builder context in which a printing chooser was opened.</summary>
public sealed record PrintingSelection(string DeckId, long Revision, string Section, CatalogCard Card, bool FromLibrary)
{
    public bool IsCurrent(DeckDetails? deck) => deck is not null && deck.Id == DeckId && deck.Revision == Revision
        && (FromLibrary || deck.Entries.Any(entry => entry.Section == Section && entry.Id == Card.Id));
}
