using Mana.Contracts;

namespace Mana.Magic;

// The engine supplies eligibility. Names, printed rules and card types never
// create an alternate action in the client.
public static class HandPlacements
{
    public static PriorityAbility[] AllOptions(GameSnapshot state) => (state.Viewer?.Zone("Hand").Cards ?? [])
        .SelectMany(card => Options(state, card)).DistinctBy(a => a.Id).ToArray();

    public static PriorityAbility[] Options(GameSnapshot state, Card clicked)
    {
        if (state.Status != "playing" || state.Decision is not { Kind: "input", Intent: DecisionIntent.Priority } decision
            || decision.Id.Length == 0 || state.Viewer is not { } viewer) return [];
        var card = viewer.Zone("Hand").Cards.FirstOrDefault(c => CardViews.Same(c, clicked));
        if (card is not { FaceDown: false, Key.Length: > 0 }) return [];
        return decision.PriorityAbilities.Where(a => a.Id.Length > 0 && a.HandPlacementLimit > 0
            && a.EligibleHandKeys.Contains(card.Key, StringComparer.Ordinal)
            && viewer.Zone("Battlefield").Cards.Any(source => source.Key == a.SourceCardKey && !source.FaceDown && !source.PhasedOut))
            .DistinctBy(a => a.Id).ToArray();
    }

    public static DecisionReply? Start(GameSnapshot state, Card card, string abilityId)
    {
        var current = state.Viewer?.Zone("Hand").Cards.FirstOrDefault(c => CardViews.Same(c, card));
        return current != null && Options(state, current).Any(a => a.Id == abilityId)
            ? new(state.Id, state.Decision!.Id, ReplyAction.UseHandPlacement, Card: current.Key, AbilityId: abilityId) : null;
    }

    public static string Availability(GameSnapshot state, Card card)
    {
        int count = Options(state, card).Sum(a => a.HandPlacementLimit);
        return count == 0 ? "" : count + (count == 1 ? " placement available" : " placements available");
    }

    public static string LandPlayState(Player player) => player.LandPlaysUsed is not { } used ? ""
        : player.LandPlayLimit is { } limit ? $"Land plays: {Math.Max(0, used)}/{Math.Max(0, limit)} used"
        : $"Land plays: {Math.Max(0, used)} used · unlimited";
}
