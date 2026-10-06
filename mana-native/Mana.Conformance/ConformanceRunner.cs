using System.Text.Json.Nodes;

namespace Mana.Conformance;

// Test-only contract. Full rules state and randomness must never be sent to a player connection.
public sealed record Scenario(string Id, string Ruleset, JsonObject Setup, SemanticCommand[] Commands, RandomChoice[] Randomness, int Players = 4);
public sealed record SemanticCommand(int Seat, string Kind, JsonObject Arguments);
public sealed record RandomChoice(string Purpose, string[] OrderedOutcomes);
public sealed record Checkpoint(string Boundary, JsonObject Rules, IReadOnlyDictionary<int, JsonObject> SeatViews, RandomChoice[] ConsumedRandomness);
public interface IConformanceEngine : IAsyncDisposable
{
    string Name { get; }
    // Instance aliases come from the scenario's deck occurrences and token-creation
    // events, never card names or backend handles. A seed alone is insufficient.
    Task<Checkpoint> StartAsync(Scenario scenario, CancellationToken cancellationToken);
    Task<Checkpoint> ApplyAsync(SemanticCommand command, CancellationToken cancellationToken);
}
public sealed record Difference(string Path, string? Reference, string? Candidate);
public sealed record Comparison(int CommandIndex, string Boundary, Difference[] Differences);
public sealed record ConformanceReport(string Scenario, string Reference, string Candidate, Comparison[] Checkpoints)
{
    public bool Passed => Checkpoints.Length > 0 && Checkpoints.All(c => c.Differences.Length == 0);
}

public static class ConformanceRunner
{
    public static async Task<ConformanceReport> RunAsync(Scenario scenario, IConformanceEngine reference, IConformanceEngine candidate, CancellationToken token = default)
    {
        var comparisons = new List<Comparison>();
        var first = Freeze(await reference.StartAsync(scenario, token));
        var second = Freeze(await candidate.StartAsync(scenario, token));
        Compare(-1, first, second);
        for (int i = 0; i < scenario.Commands.Length && comparisons[^1].Differences.Length == 0; i++) {
            token.ThrowIfCancellationRequested();
            first = Freeze(await reference.ApplyAsync(scenario.Commands[i], token));
            second = Freeze(await candidate.ApplyAsync(scenario.Commands[i], token));
            Compare(i, first, second);
        }
        return new(scenario.Id, reference.Name, candidate.Name, comparisons.ToArray());
        void Compare(int index, Checkpoint a, Checkpoint b)
        {
            // Empty or partial oracle data is a configuration failure, not a pass.
            if (a.Rules.Count == 0 || b.Rules.Count == 0 || !a.SeatViews.Keys.Order().SequenceEqual(Enumerable.Range(0, scenario.Players))
                || !b.SeatViews.Keys.Order().SequenceEqual(Enumerable.Range(0, scenario.Players)))
                throw new InvalidOperationException("Conformance requires authoritative rules state and every seat's view");
            var differences = new List<Difference>();
            if (a.Boundary != b.Boundary) differences.Add(new("$.boundary", a.Boundary, b.Boundary));
            Diff(a.Rules, b.Rules, "$.rules", differences);
            foreach (int seat in a.SeatViews.Keys.Union(b.SeatViews.Keys).Order())
                Diff(a.SeatViews.GetValueOrDefault(seat), b.SeatViews.GetValueOrDefault(seat), $"$.seats[{seat}]", differences);
            Diff(System.Text.Json.JsonSerializer.SerializeToNode(a.ConsumedRandomness), System.Text.Json.JsonSerializer.SerializeToNode(b.ConsumedRandomness), "$.randomness", differences);
            comparisons.Add(new(index, a.Boundary, differences.ToArray()));
        }
    }
    private static Checkpoint Freeze(Checkpoint state) => state with {
        Rules = (JsonObject)state.Rules.DeepClone(),
        SeatViews = state.SeatViews.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone()),
        ConsumedRandomness = state.ConsumedRandomness.Select(c => c with { OrderedOutcomes = c.OrderedOutcomes.ToArray() }).ToArray()
    };
    public static Difference[] CompareJson(JsonNode? reference, JsonNode? candidate)
    {
        var result = new List<Difference>(); Diff(reference, candidate, "$", result); return result.ToArray();
    }
    private static void Diff(JsonNode? a, JsonNode? b, string path, List<Difference> result)
    {
        if (JsonNode.DeepEquals(a, b)) return;
        if (a is JsonObject left && b is JsonObject right) {
            foreach (string key in left.Select(p => p.Key).Union(right.Select(p => p.Key)).Order()) {
                if (!left.ContainsKey(key) || !right.ContainsKey(key)) result.Add(new(path + "." + key, left.ContainsKey(key) ? left[key]?.ToJsonString() ?? "null" : "<missing>", right.ContainsKey(key) ? right[key]?.ToJsonString() ?? "null" : "<missing>"));
                else Diff(left[key], right[key], path + "." + key, result);
            }
        } else if (a is JsonArray aa && b is JsonArray bb && aa.Count == bb.Count) {
            for (int i = 0; i < aa.Count; i++) Diff(aa[i], bb[i], path + $"[{i}]", result);
        } else result.Add(new(path, a?.ToJsonString(), b?.ToJsonString()));
    }
}
