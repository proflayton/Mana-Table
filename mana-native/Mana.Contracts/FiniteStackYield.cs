using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

[JsonConverter(typeof(FiniteStackYieldStatusConverter))]
public enum FiniteStackYieldStatus { Idle, Active, Completed, Revoked }
public sealed class FiniteStackYieldStatusConverter : JsonConverter<FiniteStackYieldStatus>
{
    public override FiniteStackYieldStatus Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Enum.TryParse<FiniteStackYieldStatus>(reader.GetString(), out var value)
            && Enum.IsDefined(value) && reader.GetString() == value.ToString() ? value : throw new JsonException("Unknown finite yield status.");
    public override void Write(Utf8JsonWriter writer, FiniteStackYieldStatus value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown finite yield status.");
        writer.WriteStringValue(value.ToString());
    }
}
public sealed record FiniteStackYieldState([property: JsonRequired] string GrantId, [property: JsonRequired] FiniteStackYieldStatus Status,
    [property: JsonRequired] ImmutableArray<string> ExecutionIds, [property: JsonRequired] ImmutableArray<string> RemainingExecutionIds,
    [property: JsonRequired] long StackActionVersion, [property: JsonRequired] long ResolutionChoiceVersion, string ReasonCode = "")
{
    public int AutomaticPasses { get; init; }
}

// Explicit consent for one currently observed queue, never a default strategy.
// Every later priority pass still belongs to this seat's actual rules input.
public sealed record GrantFiniteStackYield(string GameId, string DecisionId, string GrantId,
    ImmutableArray<string> ExecutionIds, long StackActionVersion, long ResolutionChoiceVersion)
    : DecisionCommand(GameId, DecisionId);
public sealed record RevokeFiniteStackYield(string GameId, string GrantId) : GameCommand(GameId);

public static class FiniteStackYieldFacts
{
    public const int MaximumMembers = 256;
    public static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    public static bool Members(ImmutableArray<string> ids) => !ids.IsDefault && ids.Length is > 0 and <= MaximumMembers
        && ids.All(Id) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;

    public static bool CanGrant(GameSnapshot state, GrantFiniteStackYield grant)
        => Members(grant.ExecutionIds) && state.FiniteStackYield is { Status: not FiniteStackYieldStatus.Active }
            && state.Status == "playing" && state.StackActionVersion == grant.StackActionVersion
            && state.ResolutionChoiceVersion == grant.ResolutionChoiceVersion && state.Stack.Length == grant.ExecutionIds.Length
            && state.Stack.Select(item => item.ExecutionId).Distinct(StringComparer.Ordinal).Count() == state.Stack.Length
            && state.Stack.All(item => grant.ExecutionIds.Contains(item.ExecutionId)
                && ExecutionFrames.Find(state, item.ExecutionId) != null);

    public static void Validate(GameSnapshot state)
    {
        if (state.FiniteStackYield is not { } yield) return;
        if (!Enum.IsDefined(yield.Status) || yield.ExecutionIds.IsDefault || yield.RemainingExecutionIds.IsDefault
            || yield.StackActionVersion < 0 || yield.ResolutionChoiceVersion < 0 || yield.AutomaticPasses < 0 || yield.ReasonCode == null
            || yield.ReasonCode.Length > 128 || yield.ReasonCode.Any(char.IsControl)
            || yield.Status == FiniteStackYieldStatus.Idle
                && (yield.GrantId != "" || !yield.ExecutionIds.IsEmpty || !yield.RemainingExecutionIds.IsEmpty)
            || yield.Status != FiniteStackYieldStatus.Idle && (!Id(yield.GrantId) || !Members(yield.ExecutionIds))
            || yield.RemainingExecutionIds.Any(id => !yield.ExecutionIds.Contains(id))
            || yield.RemainingExecutionIds.Distinct(StringComparer.Ordinal).Count() != yield.RemainingExecutionIds.Length
            || yield.Status == FiniteStackYieldStatus.Active && yield.RemainingExecutionIds.IsEmpty
            || yield.Status == FiniteStackYieldStatus.Completed && !yield.RemainingExecutionIds.IsEmpty)
            throw new ArgumentException("A finite stack yield must preserve one bounded seat-owned grant and its original members.", nameof(state));
    }
}
