using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Exact current output bundles for one independently authorized source action.
// Presence does not authorize a different source, action, or additional cost.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuickMana([property: JsonRequired] ImmutableArray<ManaProductionAlternative> Outputs)
{
    public bool IsValid()
    {
        if (Outputs.IsDefaultOrEmpty || Outputs.Length > 32) return false;
        for (int index = 0; index < Outputs.Length; index++) {
            var output = Outputs[index];
            if (output?.Colors == null || !new ManaOutputChoice(output.Colors).IsValid()) return false;
            for (int previous = 0; previous < index; previous++) {
                var known = Outputs[previous].Colors;
                if (known.Count == output.Colors.Count && known.All(pair =>
                    output.Colors.TryGetValue(pair.Key, out int count) && count == pair.Value)) return false;
            }
        }
        return true;
    }
}
