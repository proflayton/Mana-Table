using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// A read-only, audience-scoped stream. Historical identities never authorize
// commands, source queries, or inspection of a currently concealed occurrence.
public interface IMatchHistory
{
    bool SupportsHistory => true;
    Task<HistoryBatch> HistoryAsync(string gameId, long afterSequence, int limit = 128);
}

// Host-only historical evidence for one genuinely owned automated audience.
// This port is not supplied to policies or remote seats and grants no commands.
public interface IAutomatedMatchHistory
{
    bool SupportsAutomatedHistory => false;
    Task<HistoryBatch> AutomatedHistoryAsync(string gameId, int seatId, long afterSequence, int limit = 128)
        => throw new NotSupportedException("This provider does not expose automated-seat history.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoryBatch([property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string GameId, [property: JsonRequired] int ViewerId,
    [property: JsonRequired] long FirstSequence, [property: JsonRequired] long LastSequence,
    [property: JsonRequired] long NextSequence, [property: JsonRequired] bool HasMore,
    [property: JsonRequired] ImmutableArray<HistoryRecord> Records);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoryRecord([property: JsonRequired] long Sequence, [property: JsonRequired] int Turn,
    [property: JsonRequired] string PhaseKey, [property: JsonRequired] string Exposure)
{
    public string? ExecutionId { get; init; }
    public string? ParentId { get; init; }
    public string? SourceCardId { get; init; }
    public string? SourceName { get; init; }
    public ActivityEntry? Activity { get; init; }
    public HistoryExecution? Execution { get; init; }
    public HistoryPayment? Payment { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoryExecution([property: JsonRequired] ExecutionOccurrence Occurrence,
    ExecutionTransition? Transition = null);

// Null means not reported. An empty collection means a known empty result.
// Production and consumption are separate committed facts, never pool diffs.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoryPayment([property: JsonRequired] string Kind, [property: JsonRequired] int PayerId,
    [property: JsonRequired] string Origin)
{
    public string? ProducerExecutionId { get; init; }
    public string? ProducerMode { get; init; }
    public ImmutableArray<ManaBillPart>? BillBefore { get; init; }
    public ImmutableArray<ManaBillPart>? BillAfter { get; init; }
    public ImmutableDictionary<string, int>? Produced { get; init; }
    public ImmutableDictionary<string, int>? Spent { get; init; }
    public ImmutableDictionary<string, int>? PoolAfter { get; init; }
    public ImmutableDictionary<string, int>? SourceCounters { get; init; }
    public string? RestoredExecutionId { get; init; }
}

public static class HistoryFacts
{
    public const int SchemaVersion = 1;
    public const int MaximumBatchRecords = 128;
    public const int MaximumRecordBytes = 64 * 1024;
    public const int MaximumBatchBytes = 512 * 1024;
    // Measure the existing typed JSON using the provider's compact UTF-8 scalar
    // escaping. Native ASCII/surrogate escaping must not inflate emoji or BMP
    // text into a false over-budget record. This does not write or parse JSON.
    public static int RecordBytes(HistoryRecord record) => JsonBytes(JsonSerializer.SerializeToElement(record));
    private static int JsonBytes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) {
            int bytes = 2, members = 0;
            foreach (var member in value.EnumerateObject()) {
                if (members++ > 0) bytes++;
                bytes += StringBytes(member.Name) + 1 + JsonBytes(member.Value);
            }
            return bytes;
        }
        if (value.ValueKind == JsonValueKind.Array) {
            int bytes = 2, members = 0;
            foreach (var member in value.EnumerateArray()) {
                if (members++ > 0) bytes++;
                bytes += JsonBytes(member);
            }
            return bytes;
        }
        return value.ValueKind switch {
            JsonValueKind.String => StringBytes(value.GetString()!), JsonValueKind.Number => value.GetRawText().Length,
            JsonValueKind.True or JsonValueKind.Null => 4, JsonValueKind.False => 5, _ => throw Invalid()
        };
    }
    private static int StringBytes(string value)
    {
        int bytes = Encoding.UTF8.GetByteCount(value) + 2;
        foreach (char character in value)
            bytes += character switch {
                '"' or '\\' or '\b' or '\t' or '\n' or '\f' or '\r' => 1,
                '<' or '>' or '&' or '=' or '\'' => 5,
                '\u2028' or '\u2029' => 3,
                < ' ' => 5, _ => 0
            };
        return bytes;
    }
    public static void Validate(HistoryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.SchemaVersion != SchemaVersion || !Key(batch.GameId) || batch.ViewerId < 0
            || batch.FirstSequence < 1 || batch.LastSequence < 0 || batch.FirstSequence - 1 > batch.LastSequence
            || batch.NextSequence < batch.FirstSequence - 1 || batch.NextSequence > batch.LastSequence
            || batch.HasMore != (batch.NextSequence < batch.LastSequence)
            || batch.Records.IsDefault || batch.Records.Length > MaximumBatchRecords) throw Invalid();
        long previous = batch.FirstSequence - 1, bytes = 0;
        foreach (var record in batch.Records) {
            if (record == null || record.Sequence <= previous || record.Sequence > batch.NextSequence
                || record.Turn < 0 || !Text(record.PhaseKey, 64) || record.Exposure is not ("public" or "seatPrivate")
                || !OptionalKey(record.ExecutionId) || !OptionalKey(record.ParentId) || !OptionalKey(record.SourceCardId)
                || record.SourceName != null && !Text(record.SourceName, 1024)
                || (record.Activity == null ? 0 : 1) + (record.Execution == null ? 0 : 1) + (record.Payment == null ? 0 : 1) != 1)
                throw Invalid();
            previous = record.Sequence;
            if (record.Execution is { } execution) Validate(execution, record);
            if (record.Activity is { } activity) {
                PublicActivityFacts.ValidateEntry(activity);
                if (activity.Id < 1 || activity.Turn != record.Turn || activity.PhaseKey != record.PhaseKey
                    || !Text(activity.Kind, 64) || !Bounded(activity.Message, 16384) || !Bounded(activity.Detail, 16384)
                    || !Bounded(activity.Outcome, 256) || activity.Count < 0 || !OptionalKey(activity.CardId)
                    || activity.CardName != null && !Text(activity.CardName, 1024)
                    || activity.ExecutionId.Length > 0 && !Key(activity.ExecutionId)
                    || record.ExecutionId != null && activity.ExecutionId.Length > 0 && record.ExecutionId != activity.ExecutionId
                    || activity.ExecutionStep is { } step && !Enum.IsDefined(step)) throw Invalid();
            }
            if (record.Payment is { } payment) {
                Validate(payment);
                if (record.Exposure == "seatPrivate" && payment.PayerId != batch.ViewerId) throw Invalid();
            }
            int recordBytes = RecordBytes(record);
            bytes += recordBytes;
            if (recordBytes > MaximumRecordBytes || bytes > MaximumBatchBytes) throw Invalid();
        }
    }
    private static void Validate(HistoryExecution execution, HistoryRecord record)
    {
        var occurrence = execution.Occurrence;
        if (occurrence == null || !Key(occurrence.Id) || !Key(occurrence.BudgetRootId) || !OptionalKey(occurrence.ParentId)
            || occurrence.ParentId == occurrence.Id || !Enum.IsDefined(occurrence.Kind) || !Enum.IsDefined(occurrence.Step)
            || occurrence.Outcome is { } outcome && !Enum.IsDefined(outcome)
            || (occurrence.Step == ExecutionStep.Complete) != (occurrence.Outcome != null)
            || occurrence.RuleKind is { } rule && (!Enum.IsDefined(rule) || occurrence.Kind != ExecutionKind.Rules)
            || occurrence.RuleResult is { } result && !Enum.IsDefined(result)
            || !OptionalKey(occurrence.SourceCardId) || occurrence.SourceName != null && !Text(occurrence.SourceName, 1024)
            || record.ExecutionId != null && record.ExecutionId != occurrence.Id
            || record.ParentId != null && record.ParentId != occurrence.ParentId) throw Invalid();
        if (execution.Transition is { } transition && (transition.Sequence < 1 || transition.ExecutionId != occurrence.Id
            || !Enum.IsDefined(transition.Step) || transition.Outcome is { } terminal && !Enum.IsDefined(terminal)
            || transition.RuleResult is { } transitionResult && !Enum.IsDefined(transitionResult)
            || (transition.Step == ExecutionStep.Complete) != (transition.Outcome != null)
            || transition.Step != occurrence.Step || transition.Outcome != occurrence.Outcome)) throw Invalid();
    }
    private static void Validate(HistoryPayment payment)
    {
        if (payment.Kind is not ("opened" or "produced" or "spent" or "completed" or "cancelled" or "restored")
            || payment.PayerId < 0 || payment.Origin is not ("manual" or "auto" or "unknown")
            || !OptionalKey(payment.ProducerExecutionId) || !OptionalKey(payment.RestoredExecutionId)
            || payment.ProducerMode != null && (string.IsNullOrWhiteSpace(payment.ProducerMode)
                || !Bounded(payment.ProducerMode, 16384))) throw Invalid();
        Parts(payment.BillBefore); Parts(payment.BillAfter);
        Mana(payment.Produced); Mana(payment.Spent); Mana(payment.PoolAfter);
        if (payment.SourceCounters is { } counters && (counters.Count > 128
            || counters.Any(item => !Text(item.Key, 128) || item.Value < 0))) throw Invalid();
    }
    private static void Parts(ImmutableArray<ManaBillPart>? parts)
    {
        if (parts is not { } values) return;
        if (values.IsDefault || values.Length > 128 || values.Any(part => part == null || part.Count < 1
            || !Text(part.Symbol, 64) || part.Symbol.Length < 3 || part.Symbol[0] != '{' || part.Symbol[^1] != '}')
            || values.Select(part => part.Symbol).Distinct(StringComparer.Ordinal).Count() != values.Length) throw Invalid();
    }
    private static void Mana(ImmutableDictionary<string, int>? mana)
    {
        if (mana != null && (mana.Count > 6 || mana.Any(item => item.Key is not ("W" or "U" or "B" or "R" or "G" or "C")
            || item.Value < 0))) throw Invalid();
    }
    private static bool OptionalKey(string? value) => value == null || Key(value);
    private static bool Key(string? value) => Text(value, 256);
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && Bounded(value, maximum) && !value.Any(char.IsControl);
    private static bool Bounded(string? value, int maximum) => value != null && value.Length <= maximum;
    private static ArgumentException Invalid() => new("Match history must retain bounded typed facts, source cursors and audience ownership.");
}
