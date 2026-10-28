using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

public enum AiPlanPurpose { Develop, HoldInteraction, Defend, Respond }
public enum AiReservationPurpose { Interaction, FollowUp }

// A retained intention references entitled occurrences and a semantic route,
// never the action handles that authorized one old input. Funding is a witness
// to preserve, not permission to spend it or a prediction that a spell resolves.
public sealed record AiResourceReservation(string SourceCardId, string StableRouteId,
    ImmutableArray<string> ManaSymbols, int GenericMana)
{
    public AiReservationPurpose Purpose { get; init; }
    public string SourceZone { get; init; } = "";
    public ImmutableArray<string> ManaSourceIds { get; init; } = [];
    public ImmutableDictionary<string, int> PoolMana { get; init; } = ImmutableDictionary<string, int>.Empty;
}

public sealed record AiTurnPlan(string GameId, int SeatId, int Turn, long Version,
    AiPlanPurpose Purpose, ImmutableArray<AiResourceReservation> Reservations,
    ImmutableArray<string> RetainedBlockerIds)
{
    public AiDecisionStamp CreatedFrom { get; init; }
}

// Optimistic, immutable memory change. Only the owning session can retain it,
// after a valid current answer receives an accepted execution receipt.
public sealed record AiPlanTransition(long ExpectedVersion, AiTurnPlan? Next);

internal static class AiPlanValidation
{
    // Strategic memory is deliberately small. It is not a second action catalog
    // or an unbounded cache of cards disclosed by older observations.
    private const int MaxReservations = 16, MaxBlockers = 64, MaxSources = 64;
    private static readonly HashSet<string> Colors = new(["W", "U", "B", "R", "G", "C"], StringComparer.Ordinal);

    internal static bool IsTransitionValid(AiPlanTransition? change, long version, AiDecisionFrame frame, AiTurnPlan? retained)
        => change == null || version < long.MaxValue && change.ExpectedVersion == version
            && (change.Next == null || change.Next.Version == version + 1
                && change.Next.CreatedFrom == frame.Stamp && CanRetain(change.Next, version + 1, frame)
                && change.Next.Reservations.All(reservation => retained?.Reservations.Any(prior => SameReservation(prior, reservation)) == true
                    || MatchesOfferedReservation(reservation, frame)));

    private static bool MatchesOfferedReservation(AiResourceReservation reservation, AiDecisionFrame frame)
    {
        var source = frame.SourceActions.FirstOrDefault(set => set.SourceCardId == reservation.SourceCardId);
        if (source == null) return false;
        foreach (var offered in source.Actions) {
            var action = frame.Domain.Actions.FirstOrDefault(action => action.ActionId == offered.Id && action.StableId == reservation.StableRouteId);
            if (action == null) continue;
            var costs = action.Costs.Where(cost => cost.Kind == ActionCostKind.Mana).ToArray();
            if (costs.Any(cost => cost.Amount == null)) continue;
            int tax = reservation.SourceZone == "Command" && offered.Kind == "cast"
                ? CardViews.FindZoneOccurrence(frame.State, reservation.SourceCardId)!.Value.Card.CommanderTax : 0;
            long generic = costs.Sum(cost => (long)cost.Amount!.Value) + tax;
            if (generic != reservation.GenericMana || !costs.SelectMany(cost => cost.ManaSymbols).SequenceEqual(reservation.ManaSymbols)) continue;
            // Witness identities originate in this observation's mana catalog.
            // This is not a proof of modified costs, spending applicability or
            // a joint payment, which still belong to the rules input/planner.
            return reservation.ManaSourceIds.All(id => frame.SourceActions.Any(set => set.SourceCardId == id
                && set.Actions.Any(route => frame.Domain.Actions.Any(facts => facts.ActionId == route.Id && facts.ManaAbility
                    && facts.Affordability != ActionAffordability.Unavailable && facts.Production.Knowledge == SemanticKnowledge.Known
                    && !facts.Production.Alternatives.IsEmpty))));
        }
        return false;
    }

    private static bool SameReservation(AiResourceReservation first, AiResourceReservation second)
        => first.SourceCardId == second.SourceCardId && first.StableRouteId == second.StableRouteId && first.Purpose == second.Purpose
            && first.SourceZone == second.SourceZone && first.GenericMana == second.GenericMana
            && first.ManaSymbols.SequenceEqual(second.ManaSymbols) && first.ManaSourceIds.SequenceEqual(second.ManaSourceIds)
            && first.PoolMana.Count == second.PoolMana.Count
            && first.PoolMana.All(pair => second.PoolMana.TryGetValue(pair.Key, out int amount) && amount == pair.Value);

    internal static bool CanRetain(AiTurnPlan plan, long version, AiDecisionFrame frame)
    {
        var state = frame.State;
        var created = plan.CreatedFrom;
        if (version <= 0 || plan.Version != version || plan.GameId != state.Id || plan.SeatId != state.ViewerId
            || plan.Turn != state.Turn || !Enum.IsDefined(plan.Purpose)
            || created.GameId != state.Id || created.SeatId != state.ViewerId || !Identifier(created.DecisionId)
            || created.Revision < 0 || created.Revision > state.Revision
            || created.BoardRevision < 0 || created.BoardRevision > state.BoardRevision || created.OfferVersion < 0
            || created.Revision == frame.Stamp.Revision && created.OfferVersion > frame.Stamp.OfferVersion
            || plan.Reservations.IsDefault || plan.Reservations.Length > MaxReservations
            || plan.RetainedBlockerIds.IsDefault || plan.RetainedBlockerIds.Length > MaxBlockers
            || plan.RetainedBlockerIds.Any(id => !Identifier(id))
            || plan.RetainedBlockerIds.Distinct(StringComparer.Ordinal).Count() != plan.RetainedBlockerIds.Length) return false;
        var responses = new HashSet<(string Source, string Route)>();
        var funding = new HashSet<string>(StringComparer.Ordinal);
        var pool = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var reservation in plan.Reservations) {
            if (reservation == null || !Enum.IsDefined(reservation.Purpose) || !Identifier(reservation.SourceCardId) || !Identifier(reservation.StableRouteId)
                || !Identifier(reservation.SourceZone) || !responses.Add((reservation.SourceCardId, reservation.StableRouteId))
                || reservation.GenericMana is < 0 or > 1024 || reservation.ManaSymbols.IsDefault || reservation.ManaSymbols.Length > 64
                || reservation.ManaSymbols.Any(symbol => string.IsNullOrWhiteSpace(symbol) || symbol.Length > 32)
                || reservation.ManaSourceIds.IsDefault || reservation.ManaSourceIds.Length > MaxSources
                || reservation.PoolMana == null || reservation.PoolMana.Count > Colors.Count
                || !OwnedOccurrence(state, reservation.SourceCardId, reservation.SourceZone, out var response)
                || response.FaceDown || response.PhasedOut) return false;
            foreach (string id in reservation.ManaSourceIds) {
                if (!Identifier(id) || !funding.Add(id) || !AvailablePermanent(state, id, out _)) return false;
            }
            foreach (var amount in reservation.PoolMana) {
                if (!Colors.Contains(amount.Key) || amount.Value is < 1 or > 1024) return false;
                pool[amount.Key] = pool.GetValueOrDefault(amount.Key) + amount.Value;
            }
        }
        if (funding.Count > MaxSources || pool.Any(pair => pair.Value > state.Viewer!.Mana.GetValueOrDefault(pair.Key))) return false;
        foreach (string id in plan.RetainedBlockerIds)
            if (!AvailablePermanent(state, id, out var blocker) || !blocker.Type.Contains("Creature", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool AvailablePermanent(GameSnapshot state, string id, out Card card)
        => OwnedOccurrence(state, id, "Battlefield", out card) && !card.Tapped && !card.PhasedOut && !card.FaceDown;

    private static bool OwnedOccurrence(GameSnapshot state, string id, string zone, out Card card)
    {
        var occurrence = CardViews.FindZoneOccurrence(state, id);
        card = occurrence?.Card!;
        return occurrence is { } found && found.PlayerId == state.ViewerId && found.Zone == zone;
    }

    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
}
