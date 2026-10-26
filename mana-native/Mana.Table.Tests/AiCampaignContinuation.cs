namespace Mana.Table;

// Accepted commands and earlier development cannot establish that an unfinished
// continuation will terminate. A test cutoff is not a diagnosis of a policy loop.
internal sealed record AiCampaignContinuation(bool Conclusive, string Classification, string Detail)
{
    internal static AiCampaignContinuation Assess(string stopReason, int requestedTurn, int finalTurn, bool naturalOutcome)
        => stopReason switch {
            "natural-outcome" when naturalOutcome => new(true, "natural-outcome", "The rules engine reported a natural game outcome."),
            "turn-budget" when finalTurn >= requestedTurn => new(true, "requested-turn-reached", "The campaign reached its requested turn boundary."),
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
    }
}
