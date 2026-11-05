using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Captured at the rules announcement, never resolved through a current card or
// stack view. These historical identities cannot authorize inspection/actions.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoryAnnouncedTarget([property: JsonRequired] string Kind,
    [property: JsonRequired] int GroupIndex, [property: JsonRequired] int TargetIndex)
{
    public string? CardId { get; init; }
    public string? StackId { get; init; }
    public int? PlayerId { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Zone { get; init; }
    public int? ControllerId { get; init; }
    public int? OwnerId { get; init; }
    public int? AllocatedAmount { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoryAnnouncement([property: JsonRequired] string Kind,
    [property: JsonRequired] string StackId, [property: JsonRequired] string TargetKnowledge,
    [property: JsonRequired] ImmutableArray<HistoryAnnouncedTarget> Targets)
{
    public string? CauseExecutionId { get; init; }
    public int? AnnouncedX { get; init; }
}

public static class HistoryAnnouncementFacts
{
    public const int MaximumTargets = 4096;
    public static void Validate(HistoryAnnouncement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind is not ("announced" or "retargeted") || !Text(value.StackId, 256)
            || value.TargetKnowledge is not ("Known" or "Unknown") || value.Targets.IsDefault
            || value.Targets.Length > MaximumTargets || value.AnnouncedX < 0
            || value.CauseExecutionId != null && !Text(value.CauseExecutionId, 256)) throw Invalid();
        var slots = new HashSet<(int Group, int Target)>();
        foreach (var target in value.Targets) {
            if (target == null || target.Kind is not ("card" or "player" or "stack")
                || target.GroupIndex is < 0 or >= MaximumTargets || target.TargetIndex is < 0 or >= MaximumTargets
                || !slots.Add((target.GroupIndex, target.TargetIndex))
                || target.PlayerId < 0 || target.ControllerId < 0 || target.OwnerId < 0 || target.AllocatedAmount < 0
                || !Optional(target.CardId, 256) || !Optional(target.StackId, 256)
                || !Optional(target.Name, 1024) || !Optional(target.Type, 1024)
                || target.Zone != null && target.Zone is not ("Battlefield" or "Stack" or "Graveyard" or "Exile" or "Command" or "Hand" or "Library")) throw Invalid();
            if (target.Kind == "player" && (target.PlayerId == null || target.CardId != null || target.StackId != null
                    || target.Type != null || target.Zone != null || target.ControllerId != null || target.OwnerId != null)
                || target.Kind == "card" && (target.PlayerId != null || target.StackId != null)
                || target.Kind == "stack" && (target.PlayerId != null || target.StackId == null
                    || target.Zone != null && target.Zone != "Stack")) throw Invalid();
        }
    }
    private static bool Optional(string? value, int maximum) => value == null || Text(value, maximum);
    private static bool Text(string value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
    private static ArgumentException Invalid() => new("Announced targets require bounded event-time facts without current card authority.");
}
