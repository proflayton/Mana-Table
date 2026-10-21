using Mana.Contracts;

namespace Mana.Magic;

public static class CardPresentation
{
    public static ArtRequest? Artwork(Card card)
    {
        // Effect names can collide with unrelated printed cards. Their public
        // rules text remains readable without a name-based artwork lookup.
        if (card.FaceDown || card.GamePieceType.Equals("effect", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(card.Name)) return null;
        return new(string.IsNullOrWhiteSpace(card.ArtName) ? card.Name : card.ArtName,
            card.ArtFace == "back" ? "back" : "front");
    }

    public static bool IsEquipment(Card card) => !card.FaceDown && card.Type.Contains("Equipment", StringComparison.Ordinal);
    public static string[] AttachmentLabels(Card card)
    {
        var labels = new List<string>();
        if (card.AttachedTo is { } target) labels.Add((IsEquipment(card) ? "Equipped to: " : "Attached to: ") + target.Name);
        labels.AddRange(card.Attachments.Select(a => (a.Type.Contains("Equipment", StringComparison.Ordinal) ? "Equipped with: " : "Attached: ") + a.Name));
        return labels.ToArray();
    }
    public static string AttachmentBadge(Card card)
    {
        if (card.AttachedTo != null) return IsEquipment(card) ? "EQUIPPED" : "ATTACHED";
        int equipment = card.Attachments.Count(a => a.Type.Contains("Equipment", StringComparison.Ordinal));
        if (equipment > 0) return equipment > 1 ? "EQUIPPED ×" + equipment : "EQUIPPED";
        return card.Attachments.Length > 0 ? "ATTACHED" : "";
    }
    public static int CommanderTax(Card card) => card.FaceDown ? 0 : Math.Max(0, card.CommanderTax);
    public static string CommanderTaxDetails(Card card) => CommanderTax(card) is > 0 and var tax
        ? $"Commander tax: +{tax} generic mana on the next cast from the command zone. Each previous cast from that zone adds 2 mana." : "";
    public static bool IsResource(Card card) => card.Type.Contains("Land", StringComparison.Ordinal) && !HasCombatStats(card);
    public static bool HasCombatStats(Card card) => card.Type.Contains("Creature", StringComparison.Ordinal);
    // A masked battlefield object can still have authoritative public characteristics.
    // Hidden-zone faces have no projected type/keywords; this never reconstructs identity.
    public static bool HasAbility(Card card, string keyword) => (!card.FaceDown || card.Type.Length > 0) && card.CombatKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase);
    public static string[] Abilities(Card card) => card.FaceDown && card.Type.Length == 0 ? [] : card.CombatKeywords.Where(k => !string.IsNullOrWhiteSpace(k))
        .Select(k => k.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
    public static string AbilityName(string keyword) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(keyword);
    public static KeyValuePair<string, int>[] Counters(Card card) => card.Counters.Where(c => c.Value > 0)
        .OrderBy(c => c.Key is "+1/+1" or "-1/-1" ? 0 : 1).ThenBy(c => c.Key, StringComparer.Ordinal).ToArray();
    public static string[] StateLabels(Card card)
    {
        var labels = new List<string> { card.Tapped ? "Tapped" : "Untapped" };
        if (card.PhasedOut) labels.Insert(0, "Phased out");
        if (card.Sick && HasCombatStats(card)) labels.Add("Summoning sick");
        if (card.Attacking) labels.Add("Attacking");
        if (card.Blocking) labels.Add("Blocking");
        labels.AddRange(AttachmentLabels(card));
        labels.AddRange(Abilities(card).Select(AbilityName));
        return labels.ToArray();
    }
}
