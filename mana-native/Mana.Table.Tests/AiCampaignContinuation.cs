namespace Mana.Table;

// Accepted commands and earlier development cannot establish that an unfinished
// continuation will terminate. A test cutoff is not a diagnosis of a policy loop.
internal sealed record AiCampaignContinuation(bool Conclusive, string Classification, string Detail)
{
    internal static AiCampaignContinuation Assess(string stopReason, int requestedTurn, int finalTurn, bool naturalOutcome,
        AiCampaignGoal goal = AiCampaignGoal.RequestedTurns)
        => stopReason switch {
            "natural-outcome" when naturalOutcome => new(true, "natural-outcome", "The rules engine reported a natural game outcome."),
            "turn-budget" when goal == AiCampaignGoal.RequestedTurns && finalTurn >= requestedTurn => new(true, "requested-turn-reached", "The campaign reached its requested turn boundary."),
            "turn-budget" when goal == AiCampaignGoal.NaturalOutcome => new(false, "inconclusive-turn-cutoff", "The release campaign reached its turn ceiling without a verified natural outcome."),
            "time-budget" => new(false, "inconclusive-time-cutoff", "The time budget ended before the requested continuation completed; this does not distinguish a slow turn from a policy loop."),
            "mechanical-failure" => new(false, "mechanical-failure", "A mechanical failure prevented the requested continuation."),
            _ => new(false, "inconclusive-unverified-end", "Neither a natural outcome nor the requested turn boundary was established.")
        };

    internal static void CheckRegression(Action<bool, string> check)
    {
        var historical = new { DevelopedSeats = 4, Accepted = 2085, OfferedRoutesUsed = 640, PublicComparisons = 3678 };
        bool coverage = historical.DevelopedSeats >= 3 && historical.Accepted >= 12
            && historical.OfferedRoutesUsed >= 8 && historical.PublicComparisons > 0;
        var stalledCutoff = Assess("time-budget", 32, 12, false);
        check(coverage && !stalledCutoff.Conclusive && stalledCutoff.Classification == "inconclusive-time-cutoff",
            "Historical battlefield development and thousands of accepted legal attempts cannot certify a time-limited unfinished continuation");
        var progressingCutoff = Assess("time-budget", 32, 31, false);
        check(!progressingCutoff.Conclusive && progressingCutoff.Classification == stalledCutoff.Classification,
            "A progressing time cutoff remains inconclusive without silently reducing the requested turn target or accusing the policy of looping");
        check(Assess("natural-outcome", 32, 12, true).Conclusive
                && !Assess("natural-outcome", 32, 12, false).Conclusive,
            "A verified natural outcome completes continuation before the turn target; an unverified outcome label cannot");
        check(Assess("turn-budget", 32, 32, false).Conclusive && !Assess("turn-budget", 32, 31, false).Conclusive,
            "A turn cutoff certifies continuation only when the actual requested turn boundary was reached");
        check(Assess("mechanical-failure", 32, 40, true) is { Conclusive: false, Classification: "mechanical-failure" }
                && !Assess("not-started", 32, 0, false).Conclusive,
            "Mechanical failures and unstarted matches remain separate from conclusive continuation even when unrelated progress counters look sufficient");
        check(!Assess("turn-budget", 160, 160, false, AiCampaignGoal.NaturalOutcome).Conclusive
                && !Assess("time-budget", 160, 159, false, AiCampaignGoal.NaturalOutcome).Conclusive
                && Assess("natural-outcome", 160, 42, true, AiCampaignGoal.NaturalOutcome).Conclusive,
            "The release profile requires a verified natural outcome; neither its time nor turn ceiling can substitute for completion");
        var completed = new Mana.Contracts.GameSnapshot("outcome", "finished", null, "Draw", -1, 42, "", "", null, [], [], null, null);
        var proof = AiCampaignOutcomeProof.Capture(completed, "natural-outcome", null, [], []);
        check(proof.CapturedBeforeCleanup && proof.VerifiedNaturalOutcome
                && !AiCampaignOutcomeProof.Capture(completed, "time-budget", null, [], []).VerifiedNaturalOutcome
                && !AiCampaignOutcomeProof.Capture(completed with { Error = "aborted" }, "natural-outcome", null, [], []).VerifiedNaturalOutcome
                && !AiCampaignOutcomeProof.Capture(completed, "natural-outcome", new("outcome", 0, "unavailable"), [], []).VerifiedNaturalOutcome,
            "A pre-cleanup engine draw can complete release evidence while a forced cutoff, abort or automation fault cannot");
        check(AiCampaignLimits.Smoke is { Matches: 2, Turns: 32, Goal: AiCampaignGoal.RequestedTurns }
                && AiCampaignLimits.Smoke.MatchTime == TimeSpan.FromSeconds(45)
                && AiCampaignLimits.Release is { Matches: 4, Turns: 160, Goal: AiCampaignGoal.NaturalOutcome }
                && AiCampaignLimits.Release.MatchTime == TimeSpan.FromSeconds(180),
            "The manual four-rotation natural-outcome profile does not enlarge the fixed fast-smoke budget");
    }
}
