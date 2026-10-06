using Mana.Contracts;

namespace Mana.Magic;

public static class CardPresentation
{
    public static bool IsResource(Card card) => card.Type.Contains("Land", StringComparison.Ordinal);
    public static bool HasCombatStats(Card card) => card.Type.Contains("Creature", StringComparison.Ordinal);
    public static bool HasAbility(Card card, string keyword) => !card.FaceDown && card.CombatKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase);
    public static string[] Abilities(Card card) => card.FaceDown ? [] : card.CombatKeywords.Where(k => !string.IsNullOrWhiteSpace(k))
        .Select(k => k.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
    public static string AbilityName(string keyword) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(keyword);
    public static KeyValuePair<string, int>[] Counters(Card card) => card.Counters.Where(c => c.Value > 0)
        .OrderBy(c => c.Key is "+1/+1" or "-1/-1" ? 0 : 1).ThenBy(c => c.Key, StringComparer.Ordinal).ToArray();
    public static string[] StateLabels(Card card)
    {
        var labels = new List<string> { card.Tapped ? "Tapped" : "Untapped" };
        if (card.Sick && HasCombatStats(card)) labels.Add("Summoning sick");
        if (card.Attacking) labels.Add("Attacking");
        if (card.Blocking) labels.Add("Blocking");
        labels.AddRange(Abilities(card).Select(AbilityName));
        return labels.ToArray();
    }
}
