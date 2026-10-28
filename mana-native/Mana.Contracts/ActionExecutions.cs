using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

[JsonConverter(typeof(ExecutionEnumConverter))]
public enum ExecutionKind { Spell, Activation, Trigger, Mana, Rules }
[JsonConverter(typeof(ExecutionEnumConverter))]
public enum ExecutionStep { Preparation, Choice, Cost, Stack, Resolution, Complete }
[JsonConverter(typeof(ExecutionEnumConverter))]
public enum ExecutionOutcome { Resolved, Declined, Cancelled, Countered, Failed }
[JsonConverter(typeof(ExecutionEnumConverter))]
public enum ExecutionRuleKind { Replacement, StateBased }
[JsonConverter(typeof(ExecutionEnumConverter))]
public enum ExecutionRuleResult { NotReplaced, Replaced, Prevented, Updated, Skipped }
public sealed class ExecutionEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type == typeof(ExecutionKind) || type == typeof(ExecutionStep) || type == typeof(ExecutionOutcome)
        || type == typeof(ExecutionRuleKind) || type == typeof(ExecutionRuleResult);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) => type == typeof(ExecutionKind)
        ? new NamedValue<ExecutionKind>() : type == typeof(ExecutionStep) ? new NamedValue<ExecutionStep>()
        : type == typeof(ExecutionOutcome) ? new NamedValue<ExecutionOutcome>()
        : type == typeof(ExecutionRuleKind) ? new NamedValue<ExecutionRuleKind>()
        : type == typeof(ExecutionRuleResult) ? new NamedValue<ExecutionRuleResult>() : throw new NotSupportedException();
    private sealed class NamedValue<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly Dictionary<string, T> Values = Enum.GetValues<T>().ToDictionary(
            value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()), StringComparer.Ordinal);
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value)
                ? value : throw new JsonException("Unknown rules execution value.");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException("Unknown rules execution value.");
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
    }
}

// Rules-owned historical identities. These never authorize an answer, a source
// action, or inspection of a face which the current observation does not reveal.
public sealed record ExecutionOccurrence([property: JsonRequired] string Id, [property: JsonRequired] string BudgetRootId,
    [property: JsonRequired] ExecutionKind Kind, [property: JsonRequired] ExecutionStep Step)
{
    public string? ParentId { get; init; }
    public int? ActorPlayerId { get; init; }
    public string? SourceCardId { get; init; }
    public string? SourceName { get; init; }
    public ExecutionOutcome? Outcome { get; init; }
    public ExecutionRuleKind? RuleKind { get; init; }
    public ExecutionRuleResult? RuleResult { get; init; }
}
public sealed record ExecutionTransition([property: JsonRequired] long Sequence, [property: JsonRequired] string ExecutionId,
    [property: JsonRequired] ExecutionStep Step)
{
    public ExecutionOutcome? Outcome { get; init; }
    public ExecutionRuleResult? RuleResult { get; init; }
}
// Bounds belong to the provider's retained stream. A viewer can receive a sparse
// subset: omitted private transitions are not missing transport observations.
public sealed record ExecutionFrame([property: JsonRequired] long FirstSequence, [property: JsonRequired] long LastSequence,
    [property: JsonRequired] ImmutableArray<ExecutionOccurrence> Occurrences,
    [property: JsonRequired] ImmutableArray<ExecutionTransition> Transitions);

public static class ExecutionFrames
{
    public static ExecutionOccurrence? Find(GameSnapshot state, string? id) => string.IsNullOrEmpty(id)
        ? null : state.Executions?.Occurrences.FirstOrDefault(item => item.Id == id);

    // An occurrence's terminal result does not close copies or other children
    // which still own decisions under its allowance.
    public static ImmutableHashSet<string> LiveBudgetRoots(GameSnapshot state) => state.Executions?.Occurrences
        .Where(item => item.Step != ExecutionStep.Complete).Select(item => item.BudgetRootId)
        .ToImmutableHashSet(StringComparer.Ordinal) ?? [];

    public static void Validate(GameSnapshot state)
    {
        var frame = state.Executions;
        if (state.Decision is { ExecutionId: null } || state.Stack.Any(item => item.ExecutionId == null)
            || state.Activity.Any(item => item.ExecutionId == null)) throw Invalid();
        bool anyReference = state.Decision is { ExecutionId.Length: > 0 } || state.Stack.Any(item => item.ExecutionId.Length > 0)
            || state.Activity.Any(item => item.ExecutionId.Length > 0);
        if (frame == null) {
            if (anyReference || state.Decision?.ExecutionStep != null || state.Activity.Any(item => item.ExecutionStep != null)) throw Invalid();
            return;
        }
        if (frame.FirstSequence < 1 || frame.LastSequence < 0 || frame.FirstSequence - 1 > frame.LastSequence
            || frame.Occurrences.IsDefault || frame.Transitions.IsDefault
            || frame.Occurrences.Any(item => item == null || !Id(item.Id) || !Id(item.BudgetRootId)
                || item.ParentId != null && !Id(item.ParentId) || item.ParentId == item.Id
                || !Enum.IsDefined(item.Kind) || !Step(item.Step, item.Outcome)
                || item.RuleKind is { } rule && (item.Kind != ExecutionKind.Rules || !Enum.IsDefined(rule))
                || !RuleResult(item.RuleKind, item.Step, item.RuleResult)
                || item.ActorPlayerId is { } actor && !state.Players.Any(player => player.Id == actor)
                || item.SourceCardId != null && !Id(item.SourceCardId) || item.SourceName != null && string.IsNullOrWhiteSpace(item.SourceName))
            || frame.Occurrences.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != frame.Occurrences.Length)
            throw Invalid();
        var occurrences = frame.Occurrences.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var occurrence in frame.Occurrences) {
            if (!occurrences.TryGetValue(occurrence.BudgetRootId, out var root) || root.BudgetRootId != root.Id
                || occurrence.ParentId is { } parent && !occurrences.ContainsKey(parent)) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal) { occurrence.Id };
            var current = occurrence;
            while (current.ParentId is { } parentId) {
                if (!seen.Add(parentId)) throw Invalid();
                current = occurrences[parentId];
            }
        }
        long previous = frame.FirstSequence - 1;
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var latest = new Dictionary<string, ExecutionTransition>(StringComparer.Ordinal);
        foreach (var transition in frame.Transitions) {
            if (transition == null || transition.Sequence <= previous || transition.Sequence > frame.LastSequence
                || !occurrences.ContainsKey(transition.ExecutionId) || !Step(transition.Step, transition.Outcome)
                || completed.Contains(transition.ExecutionId)) throw Invalid();
            if (!RuleResult(occurrences[transition.ExecutionId].RuleKind, transition.Step, transition.RuleResult)) throw Invalid();
            if (transition.Step == ExecutionStep.Complete) completed.Add(transition.ExecutionId);
            latest[transition.ExecutionId] = transition;
            previous = transition.Sequence;
        }
        foreach (var (id, transition) in latest) {
            var occurrence = occurrences[id];
            if (occurrence.Step != transition.Step || occurrence.Outcome != transition.Outcome
                || occurrence.RuleResult != transition.RuleResult) throw Invalid();
        }
        if (state.Decision is { } decision) Reference(decision.ExecutionId, decision.ExecutionStep, current: true);
        foreach (var item in state.Stack) {
            if (item.ExecutionId.Length > 0 && !occurrences.ContainsKey(item.ExecutionId)) throw Invalid();
        }
        foreach (var entry in state.Activity) Reference(entry.ExecutionId, entry.ExecutionStep, current: false);

        void Reference(string id, ExecutionStep? step, bool current)
        {
            if (string.IsNullOrEmpty(id)) { if (step != null) throw Invalid(); return; }
            if (!Id(id) || !occurrences.TryGetValue(id, out var execution) || step == null || !Enum.IsDefined(step.Value)
                || current && execution.Step != step || current && step == ExecutionStep.Complete) throw Invalid();
        }
    }

    private static bool Id(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Any(char.IsControl);
    private static bool Step(ExecutionStep step, ExecutionOutcome? outcome) => Enum.IsDefined(step)
        && (step == ExecutionStep.Complete ? outcome != null && Enum.IsDefined(outcome.Value) : outcome == null);
    private static bool RuleResult(ExecutionRuleKind? kind, ExecutionStep step, ExecutionRuleResult? result)
        => result == null || kind == ExecutionRuleKind.Replacement && step == ExecutionStep.Complete && Enum.IsDefined(result.Value);
    private static ArgumentException Invalid() => new("Execution history must contain exact ordered, disclosed rules occurrences and references.");
}
