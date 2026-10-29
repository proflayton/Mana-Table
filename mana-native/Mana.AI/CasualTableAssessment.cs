using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

/// <summary>Public tactical estimates, never legality or a forecast of a resolved effect.</summary>
public sealed record CasualThreat(int PlayerId, double Board, double Resources, double Pressure, double RecentFocus)
{
    public double Threat => Board + Resources + Pressure;
}

// A bounded tactical estimate using the currently visible body and effective
// keywords. Prevention, replacement, protection and future actions are not
// simulated; this never establishes combat legality or a resolved outcome.
internal sealed record CasualCombatExchange(bool AttackerDies, bool BlockerDies, long DamageToDefender);

public static class CasualTableAssessment
{
    public static ImmutableArray<CasualThreat> Assess(GameSnapshot state, AiSeatHistory history)
    {
        var result = ImmutableArray.CreateBuilder<CasualThreat>();
        int round = Math.Max(1, state.Players.Count(player => !player.Eliminated));
        foreach (var player in state.Players.Where(player => player.Id != state.ViewerId && !player.Eliminated)) {
            double board = player.Zone("Battlefield").Cards.Where(card => !card.PhasedOut).Sum(CardValue);
            double resources = player.Zone("Hand").Count * .25 + player.Zone("Battlefield").Cards.Count(IsLand) * .4
                + player.Mana.Values.Sum(value => Math.Max(0d, value)) * .25;
            double pressure = 0, focus = 0;
            foreach (var entry in history.ImpactEvents ?? history.Events) {
                int age = state.Turn - entry.Turn;
                if (age < 0 || age > round * 2 || entry.Impact is not { } impact) continue;
                // PlayerId on ordinary activity is often the affected seat. It
                // cannot stand in for the source controller. Historical source
                // cards need not exist on this board or still have that owner.
                int? recipient;
                int? actor;
                double harm;
                if (impact is { Kind: "damage", Amount: > 0 }) {
                    // A resolving ability keeps its producing controller even
                    // if the physical source was stolen after it was stacked.
                    // Physical control alone establishes combat attribution;
                    // an unknown noncombat ability remains unknown.
                    actor = entry.CauseControllerId ?? (impact.Combat == true ? impact.SourceControllerId : null);
                    recipient = impact.PlayerId ?? impact.RecipientControllerId;
                    harm = Math.Min(8, impact.Amount) * (impact.PlayerId == null ? .5 : 1);
                }
                else if (impact is { Kind: "zone", Amount: 1, FromZone: "Battlefield",
                    ToZone: "Graveyard" or "Exile" or "Hand" or "Library" }
                    && entry.ExecutionStep == ExecutionStep.Resolution) {
                    // A committed departure is one lost public resource. Do not
                    // price it by looking up a later card incarnation, and do
                    // not blame another player for a cost or an unknown step.
                    actor = entry.CauseControllerId; recipient = impact.RecipientControllerId; harm = 2;
                }
                else continue;
                double recent = harm / (1d + age / (double)round);
                if (actor == player.Id && recipient == state.ViewerId) pressure += recent;
                if (actor == state.ViewerId && recipient == player.Id) focus += recent;
            }
            if (state.Combat != null)
                foreach (var attack in state.Combat.Attackers.Where(attack => attack.HasDefender && attack.DefendingPlayerId == state.ViewerId)) {
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
        if (IsLand(card) && !IsCreature(card)) return .5;
        double power = Math.Max(0, card.Power ?? 0);
        return .75 + power + Math.Max(0, card.Toughness ?? 0) * .35
            + (HasKeyword(card, "flying") ? .8 : 0) + (HasKeyword(card, "reach") ? .3 : 0)
            + (HasKeyword(card, "deathtouch") ? 1.2 : 0) + (HasKeyword(card, "lifelink") ? .6 : 0)
            + (HasKeyword(card, "double strike") ? 1 + power * .65 : HasKeyword(card, "first strike") ? .7 : 0)
            + (HasKeyword(card, "indestructible") ? 2 : 0) + (HasKeyword(card, "vigilance") ? .5 : 0)
            + (HasKeyword(card, "trample") ? .5 : 0);
    }

    // Losing a resource and threatening with it are different evaluations. In
    // particular, preserve the next few land drops without hoarding excess
    // lands forever. Only the acting seat's disclosed hand is consulted.
    internal static double OpportunityCost(GameSnapshot state, Card card, string zone)
    {
        double value = CardValue(card);
        if (!IsLand(card) || state.Viewer is not { } viewer) return value;
        int developed = viewer.Zone("Battlefield").Cards.Count(item => IsLand(item) && !item.PhasedOut);
        int held = viewer.Zone("Hand").Cards.Count(IsLand);
        if (zone == "Hand") {
            if (developed < 3 && held <= 3 - developed) return value + 12;
            if (developed < 5 && held <= 1) return value + 6;
        }
        else if (zone == "Battlefield") {
            if (developed <= 3) return value + 8;
            if (developed <= 5) return value + 3;
        }
        return value;
    }

    internal static long UnblockedDamage(Card attacker) => Math.Max(0L, attacker.Power ?? 0)
        * (HasKeyword(attacker, "double strike") ? 2 : 1);

    internal static long? IncomingDamage(GameSnapshot state, int playerId)
    {
        long damage = 0;
        foreach (var attack in state.Combat!.Attackers.Where(attack => attack.HasDefender && attack.DefendingPlayerId == playerId
            && (attack.Defender == null || attack.Defender.Kind == "player"))) {
            var attacker = CardViews.FindZoneOccurrence(state, attack.CardId)?.Card;
            if (attacker?.Power == null) return null;
            if (attack.BlockerIds.IsEmpty) {
                if (!attack.Blocked || HasKeyword(attacker, "trample")) damage += UnblockedDamage(attacker);
            } else if (HasKeyword(attacker, "trample")) {
                // Reuse the ordinary one-blocker exchange for staged blocks.
                // Multiple-blocker assignment/order requires a richer model;
                // missing knowledge must not become a forecast of zero damage.
                if (attack.BlockerIds.Length != 1) return null;
                var blocker = CardViews.FindZoneOccurrence(state, attack.BlockerIds[0])?.Card;
                if (blocker?.Power == null || blocker.Toughness == null || attacker.Toughness == null) return null;
                damage += Exchange(attacker, blocker).DamageToDefender;
            }
        }
        return damage;
    }

    internal static CasualCombatExchange Exchange(Card attacker, Card blocker)
    {
        long attackerDamage = Math.Max(0L, attacker.Damage), blockerDamage = Math.Max(0L, blocker.Damage), defenderDamage = 0;
        bool attackerTouch = false, blockerTouch = false;
        bool attackerDead = Dead(attacker, attackerDamage, false), blockerDead = Dead(blocker, blockerDamage, false);
        bool attackerFirst = HasKeyword(attacker, "first strike") || HasKeyword(attacker, "double strike");
        bool blockerFirst = HasKeyword(blocker, "first strike") || HasKeyword(blocker, "double strike");
        if (attackerFirst || blockerFirst) Step(attackerFirst, blockerFirst);
        Step(!attackerFirst || HasKeyword(attacker, "double strike"), !blockerFirst || HasKeyword(blocker, "double strike"));
        return new(attackerDead, blockerDead, defenderDamage);

        void Step(bool attackerStrikes, bool blockerStrikes)
        {
            long toBlocker = 0, toAttacker = 0;
            if (!attackerDead && attackerStrikes) {
                long power = Math.Max(0L, attacker.Power ?? 0);
                if (HasKeyword(attacker, "trample")) {
                    long lethal = blockerDead ? 0 : HasKeyword(attacker, "deathtouch") ? 1
                        : Math.Max(0L, (long)(blocker.Toughness ?? 0) - blockerDamage);
                    toBlocker = Math.Min(power, lethal);
                    defenderDamage += power - toBlocker;
                }
                else if (!blockerDead) toBlocker = power;
            }
            if (!blockerDead && !attackerDead && blockerStrikes) toAttacker = Math.Max(0L, blocker.Power ?? 0);
            // Both damage assignments use the same pre-step survival state.
            blockerDamage += toBlocker; attackerDamage += toAttacker;
            blockerTouch |= toBlocker > 0 && HasKeyword(attacker, "deathtouch");
            attackerTouch |= toAttacker > 0 && HasKeyword(blocker, "deathtouch");
            attackerDead |= Dead(attacker, attackerDamage, attackerTouch);
            blockerDead |= Dead(blocker, blockerDamage, blockerTouch);
        }
        static bool Dead(Card card, long damage, bool touch) => (card.Toughness ?? 0) <= 0
            || !HasKeyword(card, "indestructible") && (touch || damage >= (card.Toughness ?? 0));
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
