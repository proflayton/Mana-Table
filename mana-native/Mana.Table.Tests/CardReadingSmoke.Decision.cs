using Mana.Contracts;
using Mana.Scenarios;
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
        GameSnapshot? previousState = null, abilityState = null;
        string sourceGame = "", sourceDecision = "", sourceId = "", sourcePhase = "";
        bool SourceSettled()
        {
            if (!FreshOwnCard(sourceId, "Battlefield", card => card.Power == 6 && card.Toughness == 6)) return false;
            var entry = view.Frame!.Cards.Single(card => card.Id == sourceId && card.Zone == "Battlefield");
            var target = entry.Focused ? entry.ReaderTarget ?? entry.Rest : entry.Rest;
            return Vector2.Distance(entry.Display.Center, target.Center) < 2 && Vector2.Distance(entry.Display.Size, target.Size) < 2;
        }
        Step(0, () => {
            previousState = scenario.State;
            var lab = new ExperienceLabSession("card-reading.v1");
            lab.Replay(3);
            abilityState = lab.State with { Id = previousState.Id };
            Publish(abilityState); Move(new(10, 70));
            replies = scenario.Replies.Count;
            var prompt = scenario.State.Decision!;
            sourceGame = scenario.State.Id; sourceDecision = prompt.Id;
            sourceId = prompt.SourceCardId; sourcePhase = scenario.State.PhaseKey;
        });
        Prepared(() => SourceSettled()
            && view.Hits.Any(hit => hit.Id == "choice:0")
            && !view.Hits.Any(hit => hit.Id is "close-overlay" or "choicesfilter" or "choicesdone"), 3, () => {
            Check(view.TextScale == 1.3f && scenario.Replies.Count == replies && view.Selections.Count == 0
                && view.Frame!.Cards.Single(card => card.Id == sourceId && card.Zone == "Battlefield").Card is { Power: 6, Toughness: 6 } source
                && source.VisualId == sourceId && source.Counters.GetValueOrDefault("+1/+1") == 2,
                "At130% inline ability actions preserve their actual current battlefield source without a modal, duplicate preview or submitted command");
            view.CaptureWindow(Path.Combine(view.Profile, "19-inline-ability-actions-window.png"));
            MoveCard(sourceId);
        }, "The inline action and actual source card must be physically reachable before inspection"); RightClick();
        Prepared(() => view.Overlay == "inspect", 2, () => {
            Check(view.Match is { Decision: { } decision } current && current.Id == sourceGame && decision.Id == sourceDecision
                && decision.SourceCardId == sourceId && view.Selections.Count == 0 && scenario.Replies.Count == replies,
                "Right-clicking the actual source preserves the unanswered inline ability decision without choosing or activating an option");
            MoveHit("close-overlay");
        }, "The actual source card's right-click must open its details"); Click();
        Prepared(() => view.Overlay.Length == 0 && view.Hits.Any(hit => hit.Id == "choice-cancel")
            && view.Hits.Any(hit => hit.Id == "choice:0"), 2, () => {
            Check(view.Match is { Decision: { } decision } current && current.Id == sourceGame && decision.Id == sourceDecision
                && decision.SourceCardId == sourceId && view.Selections.Count == 0 && scenario.Replies.Count == replies,
                "Closing source details restores the same direct ability action and optional cancellation without sending a command");
            MoveHit("choice-cancel");
        }, "Source inspection must return to the original inline optional ability action"); Click();
        Prepared(() => !view.Busy && scenario.Replies.Count == replies + 1, 2, () => {
            Check(scenario.Replies[^1] is { Action: ReplyAction.Choose, Choices.Length: 0 } reply
                && reply.GameId == sourceGame && reply.DecisionId == sourceDecision
                && view.Match is { } current && current.Id == sourceGame && current.PhaseKey == sourcePhase
                && current.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == sourceId) is { Tapped: false, Power: 6, Toughness: 6 },
                "Optional Cancel sends exactly one empty choice in the original scope and leaves its current source and phase intact");
            Publish(abilityState!); Move(new(10, 70));
            sourceDecision = scenario.State.Decision!.Id;
        }, "The inline ability cancellation must reconcile its one scoped empty choice");
        Prepared(() => !view.Busy && view.Match?.Decision?.Id == sourceDecision && view.Hits.Any(hit => hit.Id == "choice:0")
            && !view.Hits.Any(hit => hit.Id == "choicesdone"), 2, () => MoveHit("choice:0"),
            "The fresh canonical ability prompt must expose its direct original option before activation"); Click();
        Prepared(() => !view.Busy && scenario.Replies.Count == replies + 2, 2, () => {
            Check(scenario.Replies[^1] is { Action: ReplyAction.Choose } reply && reply.Choices!.SequenceEqual([0])
                && reply.GameId == sourceGame && reply.DecisionId == sourceDecision,
                "One inline ability action submits exactly original index zero in the fresh scope without an additional confirmation or simulated mana result");
            Publish(previousState!); Move(new(10, 70));
        }, "The inline ability action must reconcile its one exact scoped command");
        Prepared(() => previousState is { } previous && view.Match is { } current
            && current.Id == previous.Id && current.Revision == scenario.State.Revision, 2, () => { },
            "The prior controlled snapshot must be accepted before the following player-state journey");
    }
}
