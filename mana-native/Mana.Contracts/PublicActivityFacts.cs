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

    private static bool Zone(string? value) => value is "Battlefield" or "Stack" or "Graveyard" or "Exile"
        or "Command" or "Hand" or "Library";
    private static ArgumentException Invalid() => new("Public activity must retain explicit event-time controllers and zone transitions.");
}
