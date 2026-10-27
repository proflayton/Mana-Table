namespace Mana.Table;

internal enum AiCampaignGoal { RequestedTurns, NaturalOutcome }

internal sealed record AiCampaignLimits(int Matches, int Turns, TimeSpan MatchTime, long PolicySeed = 261027)
{
    public string Profile { get; init; } = "smoke";
    public AiCampaignGoal Goal { get; init; } = AiCampaignGoal.RequestedTurns;
    internal static AiCampaignLimits Smoke => new(2, 32, TimeSpan.FromSeconds(45));
    // Manual release evidence. The fast same-JVM CI prefix retains its budget.
    // Four cyclic rotations put every distinct deck in every physical seat.
    internal static AiCampaignLimits Release => new(4, 160, TimeSpan.FromSeconds(180)) {
        Profile = "release", Goal = AiCampaignGoal.NaturalOutcome };
}
