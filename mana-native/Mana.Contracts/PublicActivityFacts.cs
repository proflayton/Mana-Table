namespace Mana.Contracts;

// Historical occurrences can have left the current board or changed controller.
// Validate copied event-time facts without granting current card authority or
// trying to recover an absent source from its current owner or nearby execution.
public static class PublicActivityFacts
{
    public static void Validate(GameSnapshot state)
    {
        if (state.Activity.IsDefault || state.Activity.Any(entry => entry == null)) throw Invalid();
        var players = state.Players.Select(player => player.Id).ToHashSet();
        foreach (var entry in state.Activity) {
            ValidateEntry(entry);
            if (entry.Impact is not { } impact) continue;
            if (impact.SourceControllerId is { } source && !players.Contains(source)
                || impact.RecipientControllerId is { } recipient && !players.Contains(recipient)) throw Invalid();
            if (impact.FromZone != null || impact.ToZone != null) {
                if (impact.Kind != "zone" || impact.Amount != 1 || !Zone(impact.FromZone) || !Zone(impact.ToZone)
                    || impact.FromZone == impact.ToZone) throw Invalid();
            }
            else if (impact.Kind == "zone") throw Invalid();
        }
    }

    // Shared by live publications and archived history. This validates the
    // reported shape only; an execution ancestry consumer proves causality.
    public static void ValidateEntry(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.HistoricSpell.HasValue && entry.Kind != "cast") throw Invalid();
        if (entry.Kind == "tapped" || entry.Change is { Kind: "state", Property: "tapped" }) {
            if (entry.Kind != "tapped" || entry.Change is not { Kind: "state", Property: "tapped" } change
                || change.Before is not (0 or 1) || change.After is not (0 or 1)
                || change.Before == change.After) throw Invalid();
        }
    }

    private static bool Zone(string? value) => value is "Battlefield" or "Stack" or "Graveyard" or "Exile"
        or "Command" or "Hand" or "Library";
    private static ArgumentException Invalid() => new("Public activity must retain explicit event-time controllers and zone transitions.");
}
