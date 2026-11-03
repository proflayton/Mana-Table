namespace Mana.Table;

internal enum AiCampaignGoal { RequestedTurns, NaturalOutcome }
internal enum AiCampaignMode { FreeForAll, Cooperative }

internal sealed record AiCampaignLimits(int Matches, int Turns, TimeSpan MatchTime, long PolicySeed = 261027)
{
    public string Profile { get; init; } = "smoke";
    public AiCampaignGoal Goal { get; init; } = AiCampaignGoal.RequestedTurns;
    public AiCampaignMode Mode { get; init; }
    internal static AiCampaignLimits Smoke => new(2, 32, TimeSpan.FromSeconds(45));
    // Manual release evidence. The fast same-JVM CI prefix retains its budget.
    // Four cyclic rotations put every distinct deck in every physical seat.
    internal static AiCampaignLimits Release => new(4, 160, TimeSpan.FromSeconds(180)) {
        Profile = "release", Goal = AiCampaignGoal.NaturalOutcome };
    internal static AiCampaignLimits CooperativeSmoke => Smoke with { Profile = "cooperative-smoke", Mode = AiCampaignMode.Cooperative };
    internal static AiCampaignLimits CooperativeRelease => new(2, 160, TimeSpan.FromSeconds(180)) {
        Profile = "cooperative-release", Goal = AiCampaignGoal.NaturalOutcome, Mode = AiCampaignMode.Cooperative };

    internal static AiCampaignLimits ForProfile(string? profile) => profile switch {
        null or "smoke" => Smoke,
        "release" => Release,
        "cooperative-smoke" => CooperativeSmoke,
        "cooperative-release" => CooperativeRelease,
        _ => throw new ArgumentException("Unknown AI campaign profile: " + profile)
    };
}
