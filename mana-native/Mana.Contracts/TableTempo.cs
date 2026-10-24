using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Optional table policy, separate from rules commands and the provider lifetime.
public interface ITableTempo
{
    bool SupportsTempo => true;
    Task<Lobby> ConfigureTempoAsync(string tableId, TempoPreset preset, TempoExpiry expiry);
    Task SetReadyAsync(string tableId, bool ready, string tempoRevision);
    Task PresentedAsync(string gameId, string clockId);
}
[JsonConverter(typeof(JsonStringEnumConverter<TempoPreset>))]
public enum TempoPreset { Off, Relaxed, Standard, Fast }
[JsonConverter(typeof(JsonStringEnumConverter<TempoExpiry>))]
public enum TempoExpiry { Pass, Loss, Warn }
[JsonConverter(typeof(JsonStringEnumConverter<TempoPhase>))]
public enum TempoPhase { Preparing, Thinking, Grace, Expired }
public sealed record TempoSettings(TempoPreset Preset, TempoExpiry Expiry, int Seconds, int GraceSeconds, string Revision)
{
    public bool Enabled => Preset != TempoPreset.Off;
}
public sealed record DecisionClock(int PlayerId, string Id, TempoPhase Phase, long RemainingMs, long LimitMs, int Strikes);
public sealed record DecisionTempoFrame(TempoSettings Settings, ImmutableArray<DecisionClock> Clocks)
{
    public ImmutableArray<int> TimedOutPlayers { get; init; } = [];
}
