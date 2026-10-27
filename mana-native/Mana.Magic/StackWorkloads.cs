using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Magic;

public sealed record StackWorkloadMember(int Position, StackItem Item);
public sealed record StackWorkload(string Key, ImmutableArray<StackWorkloadMember> Members)
{
    public StackWorkloadMember First => Members[0];
    public bool IsNext => First.Position == 1;
}

// A census of the CURRENT public workload, never a reordered rules stack or a
// forecast of results. Repeated sources may occur at interleaved positions.
public static class StackWorkloads
{
    public static ImmutableArray<StackWorkload> From(ImmutableArray<StackItem> stack)
    {
        var groups = new List<(string Key, List<StackWorkloadMember> Members)>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < stack.Length; index++) {
            var item = stack[index];
            string source = item.Card is { FaceDown: false } card ? CardViews.Identity(card) : "";
            // Spells remain separate physical objects. A legacy/anonymous
            // ability without reported identity cannot establish a cohort.
            string key = item.Kind == StackActionKind.TriggeredAbility && source.Length > 0
                ? JsonSerializer.Serialize(new { source, item.ControllerId, item.Kind, item.Name, item.Text, item.Targets, item.Effect })
                : JsonSerializer.Serialize(new { entry = item.Id, index });
            if (!indices.TryGetValue(key, out int group)) {
                group = groups.Count; indices.Add(key, group); groups.Add((key, []));
            }
            groups[group].Members.Add(new(index + 1, item));
        }
        return groups.Select(group => new StackWorkload(group.Key, group.Members.ToImmutableArray())).ToImmutableArray();
    }
}
