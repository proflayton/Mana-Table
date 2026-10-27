using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Mana.Contracts;

namespace Mana.Magic;

// Printed/nominal requirements and an actual unpaid bill have different
// authority. This helper only arranges already typed rules symbols for reading.
public static class ManaCostPresentation
{
    public static ImmutableArray<string> Nominal(ImmutableArray<ActionCost> costs)
    {
        var symbols = costs.Where(cost => cost.Kind == ActionCostKind.Mana).SelectMany(cost => cost.ManaSymbols);
        long generic = costs.Where(cost => cost.Kind == ActionCostKind.Mana && cost.Amount > 0).Sum(cost => (long)cost.Amount!.Value);
        return Arrange(symbols, generic);
    }

    public static ImmutableArray<string> Unpaid(ImmutableArray<string> symbols) => Arrange(symbols, 0);
    public static ImmutableArray<string> Printed(string cost)
    {
        if (string.IsNullOrWhiteSpace(cost) || cost == "no cost") return [];
        var braced = Regex.Matches(cost, @"\{([^{}]+)\}");
        return braced.Count > 0 ? braced.Select(part => part.Value).ToImmutableArray()
            : cost.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => "{" + part + "}").ToImmutableArray();
    }

    private static ImmutableArray<string> Arrange(IEnumerable<string> symbols, long generic)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (string symbol in symbols) {
            string value = symbol.Trim('{', '}');
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int amount) && amount >= 0) generic += amount;
            else result.Add("{" + value + "}");
        }
        if (generic > 0) result.Insert(0, "{" + generic.ToString(CultureInfo.InvariantCulture) + "}");
        return result.ToImmutable();
    }

    public static bool CanPlanNominal(ManaCostPreview preview) => preview.ManaCostKnowledge != SemanticKnowledge.Unsupported
        && !preview.Costs.Any(cost => cost.Kind == ActionCostKind.Mana && cost.Amount == null);

    public static string? ReservedSource(ImmutableArray<ActionCost> costs, string sourceId) =>
        costs.Any(cost => cost.FromSource && cost.Amount != 0
            && cost.Kind is ActionCostKind.Tap or ActionCostKind.Sacrifice or ActionCostKind.Discard or ActionCostKind.Exile) ? sourceId : null;
}
