using System.Text.Json.Serialization;

namespace Mana.AI;

[JsonConverter(typeof(JsonStringEnumConverter<AiFailureKind>))]
public enum AiFailureKind
{
    CapabilityUnavailable, WorkLimit, TimeLimit, RouteLimit, InvalidPolicyOutput,
    PolicyException, ExecutionUncertain, ObservationNoProgress
}

/// <summary>Private evidence about one owned attempt. Contains no proposal,
/// policy detail, exception text, card face, or executable command authority.</summary>
public sealed record AiFailure
{
    public AiFailureKind Kind { get; }
    public string Code { get; }
    public AiDecisionStamp Stamp { get; }
    public AiTask Task { get; }
    public string EpisodeId { get; }
    public int RemainingWork { get; }
    public TimeSpan Elapsed { get; }

    [JsonConstructor]
    public AiFailure(AiFailureKind kind, string code, AiDecisionStamp stamp, AiTask task,
        string episodeId, int remainingWork, TimeSpan elapsed)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(task) || !ValidCode(code)
            || episodeId == null || remainingWork < 0 || elapsed < TimeSpan.Zero)
            throw new ArgumentException("Failure evidence requires a known kind, bounded code and owned accounting.");
        (Kind, Code, Stamp, Task, EpisodeId, RemainingWork, Elapsed)
            = (kind, code, stamp, task, episodeId, remainingWork, elapsed);
    }

    internal bool Matches(AiDecisionRecord record) => Stamp == record.Stamp && Task == record.Task
        && EpisodeId == record.EpisodeId && RemainingWork == record.RemainingWork && Elapsed == record.Elapsed;

    // Preserve the existing policy-code alphabet. This is private evidence;
    // public Java fault codes keep their separate, unchanged validation.
    internal static bool ValidCode(string? code) => !string.IsNullOrWhiteSpace(code) && code.Length <= 96
        && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
