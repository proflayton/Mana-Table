using Mana.Contracts;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckMatchFailure(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        var board = Example();
        // Older engines can supply a shutdown-induced result alongside error.
        // The native client must still present an interruption, never a loss.
        var interrupted = board with { Status = "error", Result = "Defeat", Error = "Cannot invoke a rules object", Decision = null };
        using (var run = new Runner(profile, "interrupted-match", interrupted, trace, performance)) {
            run.Tick("interrupted-board"); run.Settle("interruption-settled");
            var text = Text(run.Frame);
            check(text.Contains("Game interrupted") && !text.Contains("Defeat") && !text.Any(value => value.Contains("Cannot invoke")),
                "An engine failure overrides a legacy defeat and keeps technical exceptions out of the outcome presentation");
            check(run.Frame.Cards.Any(card => card.Id == "public-ward") && run.Engine.Replies.Count == 0,
                "The last battlefield remains available without submitting a decision after interruption");
            run.ClickCard("public-ward"); run.Settle("inspect-interrupted-board");
            check(run.Frame.Cards.Single(card => card.Id == "public-ward").Focused && run.Engine.Replies.Count == 0,
                "Reading a permanent on an interrupted board remains a local inspection");
        }
        using (var run = new Runner(profile, "completed-defeat", board with { Status = "finished", Result = "Defeat", Decision = null }, trace, performance)) {
            run.Tick("completed-board"); run.Settle("actual-defeat");
            check(Text(run.Frame).Contains("Defeat") && !Text(run.Frame).Contains("Game interrupted"),
                "A real completed defeat retains its rules outcome");
        }
    }
}
