using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed partial class CardReadingSmoke
{
    private void QueueHandReadingChecks()
    {
        const string id = "hand-0";
        int replies = 0;
        string key = "", gameId = "", decisionId = "";
        Step(0, () => {
            Publish(FocusedSeed(PresentationSmoke.Example()));
            replies = scenario.Replies.Count;
            MoveCard(id);
        });
        Step(.28, () => {
            Check(view.Hovered?.VisualId == id && view.HoverPreview == null && view.DisplayedPose(id) is { Size.X: <= 150 }
                && scenario.Replies.Count == replies, "Hover highlights the stationary hand occurrence without moving its grab target or submitting a play");
        });
        Click();
        Prepared(() => view.HoverPreview != null && view.DisplayedPose(id) is { Size.X: >= 400 }
            && view.Hits.Any(hit => hit.Id == "read-card-action"), 2, () => {
            Check(view.Hits.Any(hit => hit.Id == "read-card-action") && scenario.Replies.Count == replies,
                "One deliberate click opens a reader with a separate play control and no automatic cast");
            view.CaptureWindow(Path.Combine(view.Profile, "17-explicit-hand-reader-window.png"));
            Move(new(10, 70));
        }, "The explicit hand reader must finish opening");
        Step(.2, () => {
            Check(view.HoverPreview != null && view.DisplayedPose(id) is { Size.X: >= 400 },
                "A clicked hand reader remains pinned when the pointer leaves instead of running away");
            var card = view.Match!.Viewer!.Zone("Hand").Cards.Single(card => card.VisualId == id);
            key = card.Key; gameId = view.Match.Id; decisionId = view.Match.Decision!.Id;
            MoveCard(id);
            Check(view.HitAt(SmokePointer.Table(view, new(mouse.X, mouse.Y)))?.Card?.VisualId == id,
                "The original fan slot remains the exact card's usable grab target while its reader is pinned");
        });
        Step(.025, () => Buttons(ButtonState.Pressed));
        Step(.05, () => Move(new(800, 360), ButtonState.Pressed));
        Step(.08, () => {
            Check(view.HoverPreview == null && view.DisplayedPose(id) is { Size.X: < 200 },
                "Beginning a real drag collapses the reader and keeps the held card attached to the pointer");
            Buttons();
        });
        Prepared(() => !view.Busy && scenario.Replies.Count == replies + 1, 2, () => {
            Check(scenario.Replies[^1] is { Action: ReplyAction.SelectCard } reply && reply.Card == key
                    && reply.GameId == gameId && reply.DecisionId == decisionId
                    && view.Match!.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == id) && view.HoverPreview == null,
                "Dropping above the old narrow play lane submits exactly the grabbed card in its original scope");
            Move(new(10, 70));
        }, "The real hand drag must submit one exact scoped play and finish");
    }
}
