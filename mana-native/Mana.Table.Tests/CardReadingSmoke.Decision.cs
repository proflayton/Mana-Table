using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class CardReadingSmoke
{
    private void QueueDecisionReadabilityChecks()
    {
        const string purpose = "Choose target creature you control to receive two +1/+1 counters. Then choose target creature an opponent controls. Only highlighted creatures are legal targets.";
        string expanded = string.Join("\n\n", Enumerable.Repeat(purpose, 5)) + "\n\nFINAL TARGET RULE: both creatures must remain legal.";
        string firstHelpPage = "";
        int replies = 0;
        void PublishPurpose(string message) => Publish(scenario.State with {
            Decision = new() { Kind = "input", Intent = DecisionIntent.Selection, Message = message, OkEnabled = true }
        });
        bool SafeText() => view.TextScale == 1.3f && view.DecisionTextBounds is { Width: > 0, Height: > 0 } text
            && view.DecisionPanelBounds.Contains(text) && text.Bottom < 799
            && !view.Hits.Any(hit => hit.Id == "read-card-action" && hit.Bounds.Intersects(text));
        Step(0, () => {
            replies = scenario.Replies.Count;
            Move(new(10, 70)); PublishPurpose(purpose);
        });
        Prepared(() => view.DecisionText == purpose && view.DecisionTextBounds.Height > 80, 2, () => {
            Check(SafeText() && !view.DecisionTextOverflow && view.Match!.Decision!.Message == purpose,
                "At130% a multi-line engine purpose grows above the fixed action row with its complete current text inside the decision panel");
            PublishPurpose(expanded);
        }, "The complete engine selection purpose must paint before checking its layout");
        Prepared(() => view.DecisionText == expanded && view.DecisionTextOverflow && view.Hits.Any(hit => hit.Id == "decision-read-all"), 2, () => {
            using var reference = new Mana.Renderer.Canvas(view.GraphicsDevice) { TextScale = view.TextScale };
            int eightLines = reference.TextSize(string.Join("\n", Enumerable.Repeat("Ag", 8)), .75f, 295, 80).Y;
            Check(view.DecisionTextBounds.Height <= eightLines,
                "The overflow preview occupies no more than eight actual rendered text lines at the active accessibility size");
            Check(SafeText() && view.Match!.Decision!.Message == expanded && scenario.Replies.Count == replies,
                "An overlong purpose keeps the exact engine message and a bounded visible text area rather than covering action controls");
            var text = view.DecisionTextBounds;
            bool exposed = true;
            for (int x = text.Left + 5; x < text.Right - 5; x += 48)
                for (int y = text.Top + 5; y < text.Bottom - 5; y += 24)
                    exposed &= view.HitAt(new(x, y))?.Id == "instructions";
            Check(exposed, "The displayed instruction retains its own help target above underlying cards and reader controls");
            var readAll = view.Hits.Single(hit => hit.Id == "decision-read-all");
            Check(view.DecisionPanelBounds.Contains(readAll.Bounds)
                && view.HitAt(SmokePointer.Table(view, SmokePointer.Hit(view, readAll)))?.Id == readAll.Id,
                "Overflow exposes a physically reachable Read all control within the current decision panel");
            view.CaptureWindow(Path.Combine(view.Profile, "18-decision-readability-window.png"));
            MoveHit("decision-read-all");
        }, "A purpose beyond eight actual text lines must expose Read all"); Click();
        Prepared(() => view.Overlay == "help", 2, () => {
            Check(view.Match!.Decision!.Message == expanded && scenario.Replies.Count == replies
                && view.Hits.Any(hit => hit.Id == "help-next") && view.HelpPageText.Length > 0,
                "Read all exposes measured pages for the full current decision without submitting or changing the game choice");
            firstHelpPage = view.HelpPageText;
            MoveHit("help-next");
        }, "Read all must open the actual decision-help overlay with reachable remaining text"); Click();
        Prepared(() => view.Overlay == "help" && view.HelpPageText.Length > 0 && view.HelpPageText != firstHelpPage, 2, () => {
            using var reference = new Mana.Renderer.Canvas(view.GraphicsDevice) { TextScale = view.TextScale };
            Check((firstHelpPage + view.HelpPageText).StartsWith(expanded, StringComparison.Ordinal)
                && view.HelpPageText.Contains("FINAL TARGET RULE", StringComparison.Ordinal)
                && reference.TextSize(firstHelpPage, .93f, 1155, 80).Y <= 500
                && reference.TextSize(view.HelpPageText, .93f, 1155, 80).Y <= 500 && scenario.Replies.Count == replies,
                "A real More click reaches the complete engine-message tail without omitted text, overflowing pages or a game reply");
            Check(new[] { "close-overlay", "help-prev" }.All(id => {
                var hit = view.Hits.Single(candidate => candidate.Id == id);
                return view.HitAt(SmokePointer.Table(view, SmokePointer.Hit(view, hit)))?.Id == id;
            }), "Help navigation remains physically reachable below the measured text area");
            MoveHit("close-overlay");
        }, "More must paint the remaining decision explanation before checking its content"); Click();
    }
}
