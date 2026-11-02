using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

[JsonConverter(typeof(MatchOutcomeKindConverter))]
public enum MatchOutcomeKind { Win, Draw }

public sealed class MatchOutcomeKindConverter : JsonConverter<MatchOutcomeKind>
{
    public override MatchOutcomeKind Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() switch {
            "win" => MatchOutcomeKind.Win,
            "draw" => MatchOutcomeKind.Draw,
            _ => throw new JsonException("Unknown match outcome kind.")
        } : throw new JsonException("Unknown match outcome kind.");

    public override void Write(Utf8JsonWriter writer, MatchOutcomeKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch {
            MatchOutcomeKind.Win => "win",
            MatchOutcomeKind.Draw => "draw",
            _ => throw new JsonException("Unknown match outcome kind.")
        });
}

// Winners are the actual engine outcome participants. An eliminated teammate is
// not added to WinningPlayerIds merely because its team won.
public sealed record MatchOutcome([property: JsonRequired] string GameId,
    [property: JsonRequired] MatchOutcomeKind Kind,
    [property: JsonRequired] ImmutableArray<int> WinningPlayerIds,
    [property: JsonRequired] ImmutableArray<int> WinningTeamIds,
    [property: JsonRequired] string RuleReason);

public static class MatchOutcomeFacts
{
    public static void Validate(GameSnapshot state)
    {
        if (state.Outcome is not { } outcome) return;
        if (state.Status != "finished" || !string.IsNullOrEmpty(state.Error) || state.Decision != null || state.Players.IsDefaultOrEmpty
            || state.Players.Any(player => player == null || player.Id < 0 || player.TeamId is < 0)
            || state.Players.Select(player => player.Id).Distinct().Count() != state.Players.Length) throw Invalid();
        Validate(outcome, state.Id, state.Players.ToImmutableDictionary(player => player.Id, player => player.TeamId));
    }

    // Validates identities in a retained terminal receipt without manufacturing
    // a live GameSnapshot. The caller still owns terminal/durability provenance.
    public static void Validate(MatchOutcome outcome, string gameId, ImmutableDictionary<int, int?> participantTeams)
    {
        if (string.IsNullOrWhiteSpace(gameId) || outcome.GameId != gameId || !Enum.IsDefined(outcome.Kind)
            || string.IsNullOrWhiteSpace(outcome.RuleReason) || participantTeams.IsEmpty
            || participantTeams.Any(pair => pair.Key < 0 || pair.Value is < 0)
            || outcome.WinningPlayerIds.IsDefault || outcome.WinningTeamIds.IsDefault
            || outcome.WinningPlayerIds.Distinct().Count() != outcome.WinningPlayerIds.Length
            || outcome.WinningTeamIds.Distinct().Count() != outcome.WinningTeamIds.Length)
            throw Invalid();
        if (outcome.Kind == MatchOutcomeKind.Draw) {
            if (!outcome.WinningPlayerIds.IsEmpty || !outcome.WinningTeamIds.IsEmpty) throw Invalid();
            return;
        }
        if (outcome.WinningPlayerIds.IsEmpty || outcome.WinningTeamIds.IsEmpty) throw Invalid();
        var teams = new HashSet<int>();
        foreach (int id in outcome.WinningPlayerIds) {
            if (!participantTeams.TryGetValue(id, out var value) || value is not { } team) throw Invalid();
            teams.Add(team);
        }
        if (!teams.SetEquals(outcome.WinningTeamIds)) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Invalid rules match outcome.");
}
