using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Planning;

// Scope validation of read-only resource facts grants no execution permission.
public static class ManaFundingFacts
{
    public static void Validate(GameSnapshot state, ManaFundingContext funding, int? resourceOwnerId = null)
    {
        if (state == null) throw Invalid();
        int ownerId = resourceOwnerId ?? state.ViewerId;
        var owner = state.Players.SingleOrDefault(player => player.Id == ownerId);
        if (owner == null) throw Invalid();
        if (funding == null || !Enum.IsDefined(funding.Knowledge) || funding.Pool == null
            || funding.Pool.Count > 6 || funding.Pool.Any(entry => entry.Value < 0 || entry.Value > 1024
                || entry.Key is not ("W" or "U" or "B" or "R" or "G" or "C"))
            || funding.Sources.IsDefault || funding.Sources.Length > 128
            || funding.Sources.Any(source => source == null)
            || funding.Sources.Select(source => source.SourceCardId).Distinct(StringComparer.Ordinal).Count() != funding.Sources.Length) throw Invalid();
        // Pool entitlement is quantitative: an observation cannot invent
        // floating mana, even though spend applicability remains nominal.
        if (funding.Pool.Any(entry => entry.Value > owner.Mana.GetValueOrDefault(entry.Key))) throw Invalid();
        var ownBattlefield = CardViews.InZones(state).Where(occurrence => occurrence.PlayerId == ownerId
            && occurrence.Zone == "Battlefield").Select(occurrence => CardViews.Identity(occurrence.Card)).ToHashSet(StringComparer.Ordinal);
        foreach (var source in funding.Sources) {
            if (string.IsNullOrWhiteSpace(source.SourceCardId) || !ownBattlefield.Contains(source.SourceCardId)
                || source.Routes.IsDefault || source.Routes.Length > 32) throw Invalid();
            foreach (var route in source.Routes) {
                if (route == null || !Enum.IsDefined(route.Eligibility)) throw Invalid();
                ValidateCosts(route.Costs); ValidateProduction(route.Production);
            }
        }
    }

    public static void ValidateCosts(ImmutableArray<ActionCost> costs)
    {
        if (costs.IsDefault || costs.Length > 128 || costs.Any(cost => cost == null || !Enum.IsDefined(cost.Kind)
            || cost.Amount < 0 || cost.CounterType == null || cost.ManaSymbols.IsDefault || cost.ManaSymbols.Length > 128
            || cost.ManaSymbols.Any(string.IsNullOrWhiteSpace))) throw Invalid();
    }

    public static void ValidateProduction(ManaProduction? production)
    {
        if (production == null || !Enum.IsDefined(production.Knowledge) || production.Alternatives.IsDefault
            || production.Alternatives.Length > 32 || production.Knowledge == SemanticKnowledge.Known && production.Alternatives.IsEmpty) throw Invalid();
        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alternative in production.Alternatives) {
            if (alternative?.Colors == null || alternative.Colors.Count > 6 || alternative.Colors.Any(entry => entry.Value < 0
                || entry.Key is not ("W" or "U" or "B" or "R" or "G" or "C"))
                || alternative.Colors.Values.Sum(value => (long)value) > int.MaxValue) throw Invalid();
            string canonical = string.Join(";", alternative.Colors.Where(entry => entry.Value != 0).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Key + ":" + entry.Value));
            if (!alternatives.Add(canonical)) throw Invalid();
        }
    }

    private static ArgumentException Invalid() => new("Funding facts must be finite and scoped to the current owned battlefield and pool.");
}
