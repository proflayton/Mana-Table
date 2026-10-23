namespace Mana.Table.Interaction;

internal enum InteractionScopeKind { Current, Home, Updates, Activity, Experience, Chat, Playback, PhaseStop, Source }

/// <summary>
/// A published interaction is data. It cannot retain executable application
/// callbacks; its owning flow resolves and validates the intent when consumed.
/// Control names identify geometry and diagnostics, never action authority.
/// </summary>
internal abstract record UiIntent
{
    private protected UiIntent() { }
    internal virtual InteractionScopeKind ScopeKind => InteractionScopeKind.Current;
    internal static UiIntent None { get; } = new NoInteraction();
    internal sealed record NoInteraction : UiIntent;
    internal sealed record Block(InteractionScopeKind Kind) : UiIntent
    {
        internal override InteractionScopeKind ScopeKind => Kind;
    }
}
