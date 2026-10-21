using Mana.Contracts;

namespace Mana.Magic;

public sealed record TuningQuestion(string DeckId, string VersionId, string Question);

/// <summary>Turns a review finding into a question to observe in a real test.</summary>
public static class DeckTuning
{
    public static TuningQuestion FromFinding(AnalysisReport report, AnalysisFinding finding)
    {
        string question = finding.Id switch {
            "land-consistency" => $"Can I make my first {report.Metrics.LandGoal} land drops on time? Record the opening lands and each turn's land drop; observe colors, tapped lands and ramp in play.",
            "conditional-draw" => "Can I find more cards before my draw engine is working? Record the turn, setup and payment for each card-access source.",
            "expensive-support" => "Do my expensive spells contribute soon enough? Record when cards costing five or more are cast, or remain stranded in hand.",
            _ => "Test: " + finding.Title + ". Record the cards involved, the turn, and whether this situation helps or blocks the deck's plan."
        };
        const string caveat = " This checks a review estimate; record your observations rather than treating it as a measured result.";
        int limit = 500 - caveat.Length - 1;
        if (question.Length > limit) question = question[..(char.IsHighSurrogate(question[limit - 1]) ? limit - 1 : limit)] + "…";
        question += caveat;
        return new(report.DeckId, report.Fingerprint, question);
    }

    public static bool Matches(TuningQuestion question, WorkshopState? workshop) => workshop is not null
        && question.DeckId == workshop.DeckId && question.VersionId == workshop.VersionId;
}
