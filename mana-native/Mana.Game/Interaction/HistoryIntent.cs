namespace Mana.Table.Interaction;

// Historical selection is read-only and survives a new live decision. These
// intents contain archive/occurrence identities, never live command handles.
internal abstract record HistoryIntent : UiIntent
{
    internal override InteractionScopeKind ScopeKind => InteractionScopeKind.History;
    internal sealed record Open(bool Saved = false) : HistoryIntent;
    internal sealed record Close : HistoryIntent;
    internal sealed record SavedMatches : HistoryIntent;
    internal sealed record LiveMatch : HistoryIntent;
    internal sealed record OpenArchive(string Id) : HistoryIntent;
    internal sealed record Move(int Delta) : HistoryIntent;
    internal sealed record Newest : HistoryIntent;
    internal sealed record Details(int Delta) : HistoryIntent;
    internal sealed record Archives(int Delta) : HistoryIntent;
    internal sealed record TogglePrivateReport : HistoryIntent;
    internal sealed record SaveReport : HistoryIntent;
    internal sealed record ReadSource : HistoryIntent;
    internal sealed record BackFromCard : HistoryIntent;
    internal sealed record Retry : HistoryIntent;
}
