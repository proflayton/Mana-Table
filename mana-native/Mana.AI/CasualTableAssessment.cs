using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

/// <summary>Public tactical estimates, never legality or a forecast of a resolved effect.</summary>
public sealed record CasualThreat(int PlayerId, double Board, double Resources, double Pressure, double RecentFocus)
{
    public double Threat => Board + Resources + Pressure;
}

public static class CasualTableAssessment
{
    private static readonly ImmutableArray<string> ValuedKeywords = ["flying", "deathtouch", "lifelink", "double strike"];
    public static ImmutableArray<CasualThreat> Assess(GameSnapshot state, AiSeatHistory history)
    {
        var result = ImmutableArray.CreateBuilder<CasualThreat>();
        int round = Math.Max(1, state.Players.Count(player => !player.Eliminated));
        foreach (var player in state.Players.Where(player => player.Id != state.ViewerId && !player.Eliminated)) {
            double board = player.Zone("Battlefield").Cards.Where(card => !card.PhasedOut).Sum(CardValue);
            double resources = player.Zone("Hand").Count * .25 + player.Zone("Battlefield").Cards.Count(IsLand) * .4
                + player.Mana.Values.Sum(value => Math.Max(0d, value)) * .25;
            double pressure = 0, focus = 0;
            foreach (var entry in history.Events) {
                int age = state.Turn - entry.Turn;
                if (age < 0 || age > round * 2 || entry.Impact is not { Kind: "damage", Amount: > 0 } impact) continue;
                int? actor = entry.CauseControllerId ?? entry.ActorId;
                double recent = Math.Min(8, impact.Amount) / (1d + age / (double)round);
                if (actor == player.Id && impact.PlayerId == state.ViewerId) pressure += recent;
                if (actor == state.ViewerId && impact.PlayerId == player.Id) focus += recent;
            }
            if (state.Combat != null)
                foreach (var attack in state.Combat.Attackers.Where(attack => attack.DefendingPlayerId == state.ViewerId)) {
                    var occurrence = CardViews.FindZoneOccurrence(state, attack.CardId);
                    if (occurrence?.PlayerId == player.Id) pressure += Math.Max(0, occurrence.Value.Card.Power ?? 0) * 1.5;
                }
            result.Add(new(player.Id, board, resources, Math.Min(16, pressure), Math.Min(6, focus)));
        }
        return result.ToImmutable();
    }

    // Life total is deliberately absent. An exposed player is not necessarily
    // the player preventing the rest of the table from participating.
    internal static double OpponentScore(AiDecisionRequest request, int playerId)
    {
        var threats = Assess(request.Frame.State, request.History);
        var threat = threats.FirstOrDefault(value => value.PlayerId == playerId);
        if (threat == null) return playerId == request.Frame.State.ViewerId ? -1000 : -100;
        double maximum = threats.IsEmpty ? 0 : threats.Max(value => value.Threat);
        // Avoid repetitive pressure only among genuinely comparable positions;
        // a clear threat retains its advantage. This is not target rotation.
        double focus = maximum - threat.Threat <= Math.Max(2, maximum * .15) ? threat.RecentFocus * .4 : 0;
        return threat.Threat - focus + Tie(request.Seed, playerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static double CardValue(Card card)
    {
        if (card.FaceDown) return 1;
        if (IsLand(card)) return .5;
        return .75 + Math.Max(0, card.Power ?? 0) + Math.Max(0, card.Toughness ?? 0) * .35
            + ValuedKeywords.Count(keyword => HasKeyword(card, keyword)) * .7;
    }
    internal static bool IsLand(Card card) => card.Type.Split([' ', '—', '-'], StringSplitOptions.RemoveEmptyEntries).Contains("Land");
    internal static bool IsCreature(Card card) => card.Type.Split([' ', '—', '-'], StringSplitOptions.RemoveEmptyEntries).Contains("Creature");
    internal static bool HasKeyword(Card card, string key) => card.States is { } states
        ? states.Any(state => state.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && state.Value != 0)
        : card.CombatKeywords.Contains(key, StringComparer.OrdinalIgnoreCase);
    internal static double Tie(long seed, string key)
    {
        // Stable across runtimes/processes. No randomized string.GetHashCode or
        // dependence on provider enumeration order or opponent names.
        return (Hash(seed, key) & 0xffff) / 65536d;
    }
    internal static string IdentityCode(string identity) => Hash(0, identity).ToString("x16");
    private static ulong Hash(long seed, string key)
    {
        ulong hash = unchecked((ulong)seed) ^ 14695981039346656037UL;
        foreach (char value in key) hash = (hash ^ value) * 1099511628211UL;
        hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
        return hash;
    }
}
