using Mana.Contracts;

namespace Mana.AI;

// These are current public, nominal damage contributions, not resolved damage.
// Keeping the source is essential: separate commanders never share a bill.
internal sealed record AiCombatDamage(Card Attacker, long Amount);
// This covers nominal life/commander channels only. Other effects are ALWAYS
// outside coverage, including when both nominal channels are understood.
internal readonly record struct AiNominalCombatRisk(int KnownDefeats, long LethalPressure, bool UnknownNominalChannels);
internal enum AiCombatSurvivalRank { None, ReducesModeledDefeat, AvoidsModeledDefeat }

/// <summary>
/// One bounded public combat estimate shared by declaration and retention.
/// It does not simulate prevention, replacements, future actions or poison.
/// Unknown coverage is never a certificate that the player will survive.
/// </summary>
internal sealed class AiCombatSurvival
{
    private const int MaximumSources = 256, MaximumCommanders = 64;
    private readonly Player defender;
    private readonly int? commanderLimit;
    private readonly Dictionary<string, int> prior = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> commanders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiCombatDamage> sources = new(StringComparer.Ordinal);
    private long lifeDamage;
    private bool lifeKnown = true, commanderKnown = true, unknown;

    internal AiCombatSurvival(GameSnapshot state, Player defender, IReadOnlyList<AiCombatDamage> incoming)
    {
        this.defender = defender;
        commanderLimit = state.CommanderCombat?.DamageLimit;
        commanderKnown = state.CommanderCombat != null;
        unknown = !commanderKnown || !state.States.IsEmpty || !defender.States.IsEmpty
            || defender.Counters.Any(counter => counter.Value > 0);
        if (incoming.Count > MaximumSources || commanderLimit is <= 0) {
            lifeKnown = commanderKnown = false; unknown = true; return;
        }
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        if (defender.CommanderDamage.IsDefault || defender.CommanderDamage.Length > MaximumCommanders) {
            commanderKnown = false; unknown = true;
        }
        else if (commanderLimit != null) foreach (var row in defender.CommanderDamage) {
            // A legacy or partial row cannot establish zero prior damage.
            if (row.CommanderId.Length == 0 || row.Damage < 0 || ambiguous.Contains(row.CommanderId)
                || !prior.TryAdd(row.CommanderId, row.Damage)) {
                prior.Remove(row.CommanderId); ambiguous.Add(row.CommanderId);
                commanderKnown = false; unknown = true;
            }
        }
        foreach (var contribution in incoming) {
            var card = contribution.Attacker;
            if (contribution.Amount < 0 || CardViews.Identity(card).Length == 0
                || !sources.TryAdd(CardViews.Identity(card), contribution)) {
                lifeKnown = commanderKnown = false; unknown = true; continue;
            }
            // Infect does not turn its damage into ordinary life loss. Other
            // poison and replacement outcomes remain an explicitly unknown
            // channel even when a separate commander threat is understood.
            if (CasualTableAssessment.HasKeyword(card, "infect")
                || CasualTableAssessment.HasKeyword(card, "assign combat damage using toughness")) {
                lifeKnown = false; unknown = true;
            }
            else lifeDamage += contribution.Amount;
            if (card.FaceDown || card.Power == null) { lifeKnown = false; unknown = true; }
            // The exchange model is deliberately narrower than all keywords.
            // Do not describe its nominal result as all-channel safety.
            if (card.CombatKeywords.Any(keyword => !OrdinaryTraits.Contains(keyword))
                || card.States is { } states && states.Any(shown => shown.Kind == "ability"
                    && shown.Value != 0 && !OrdinaryTraits.Contains(shown.Key))) unknown = true;
            if (commanderLimit == null || card.CommanderId.Length == 0) continue;
            if (!prior.ContainsKey(card.CommanderId) || card.FaceDown) { commanderKnown = false; unknown = true; }
            commanders[card.CommanderId] = commanders.GetValueOrDefault(card.CommanderId) + contribution.Amount;
        }
    }

    private static readonly HashSet<string> OrdinaryTraits = new([
        "flying", "reach", "haste", "vigilance", "first strike", "double strike", "trample", "deathtouch",
        "indestructible", "lifelink", "hexproof", "ward", "shroud", "flash", "prowess", "partner", "partner with"
    ], StringComparer.OrdinalIgnoreCase);

    internal AiNominalCombatRisk Risk => Measure(null, 0);
    internal bool ModelsLife => lifeKnown;

    internal AiNominalCombatRisk AfterBlock(Card attacker, long damageToDefender)
    {
        if (!sources.TryGetValue(CardViews.Identity(attacker), out var original) || damageToDefender < 0
            || damageToDefender > original.Amount) return Risk with { UnknownNominalChannels = true };
        return Measure(original, original.Amount - damageToDefender);
    }

    private AiNominalCombatRisk Measure(AiCombatDamage? reduced, long prevented)
    {
        int defeats = 0; long pressure = 0;
        long life = lifeDamage - (reduced == null ? 0 : prevented);
        if (lifeKnown && life > 0 && life >= defender.Life) { defeats++; pressure += life - defender.Life + 1; }
        if (commanderLimit is { } limit) foreach (var group in commanders) {
            if (!prior.TryGetValue(group.Key, out int previous)) continue;
            long total = previous + group.Value - (reduced?.Attacker.CommanderId == group.Key ? prevented : 0);
            if (total >= limit) { defeats++; pressure += total - limit + 1; }
        }
        return new(defeats, pressure, unknown || !lifeKnown || !commanderKnown);
    }

    internal static AiCombatSurvivalRank Improvement(AiNominalCombatRisk before, AiNominalCombatRisk after)
        => before.KnownDefeats == 0 || after.LethalPressure >= before.LethalPressure ? AiCombatSurvivalRank.None
            : after.KnownDefeats == 0 ? AiCombatSurvivalRank.AvoidsModeledDefeat : AiCombatSurvivalRank.ReducesModeledDefeat;

    internal static AiCombatSurvival? Current(GameSnapshot state, Player defender)
    {
        var incoming = new List<AiCombatDamage>();
        foreach (var attack in state.Combat!.Attackers.Where(attack => attack.HasDefender && attack.DefendingPlayerId == defender.Id
            && (attack.Defender == null || attack.Defender.Kind == "player"))) {
            if (incoming.Count >= MaximumSources) return null;
            var attacker = CardViews.FindZoneOccurrence(state, attack.CardId)?.Card;
            if (attacker?.Power == null) return null;
            long damage = 0;
            if (attack.BlockerIds.IsEmpty) {
                if (!attack.Blocked || CasualTableAssessment.HasKeyword(attacker, "trample"))
                    damage = CasualTableAssessment.UnblockedDamage(attacker);
            }
            else if (CasualTableAssessment.HasKeyword(attacker, "trample")) {
                // Ordering/assignment across multiple blockers is unsupported,
                // never an invented zero contribution from this attacker.
                if (attack.BlockerIds.Length != 1) return null;
                var blocker = CardViews.FindZoneOccurrence(state, attack.BlockerIds[0])?.Card;
                if (blocker?.Power == null || blocker.Toughness == null || attacker.Toughness == null) return null;
                damage = CasualTableAssessment.Exchange(attacker, blocker).DamageToDefender;
            }
            incoming.Add(new(attacker, damage));
        }
        return new(state, defender, incoming);
    }
}
