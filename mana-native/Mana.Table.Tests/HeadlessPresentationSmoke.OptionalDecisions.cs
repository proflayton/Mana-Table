using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Scenarios;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckOptionalDecisions(string profile, Action<bool, string> check,
        List<object> trace, List<object> performance)
    {
        var stages = OptionalDecisionScenario.Create();
        Player ExtraPlayer(int id) => new(id, "Seat " + (id + 1), 40, false, false,
            ImmutableDictionary<string, int>.Empty, [new("Library", 80, [], null), new("Hand", 7, [], null),
                new("Battlefield", 0, [], null), new("Command", 0, [], null)]);
        var targets = new[] { stages[0].Snapshot, stages[1].Snapshot, stages[0].Snapshot with {
            Players = [.. stages[0].Snapshot.Players, ExtraPlayer(4), ExtraPlayer(5)],
            Decision = stages[0].Snapshot.Decision! with { Payload = ((TargetSelectionPayload)stages[0].Snapshot.Decision!.Payload) with { Players = [0, 1, 2, 3, 4, 5] } }
        } };
        foreach (var (state, stage) in targets.Select((state, index) => (state, index))) {
            using var run = new Runner(profile, "library-target-" + stage, state, trace, performance);
            var replies = new List<GameCommand>();
            run.Engine.ReplyOverride = command => {
                replies.Add(command); run.Engine.Present(run.Engine.State with { Decision = null });
                return Task.CompletedTask;
            };
            run.Settle("library-target");
            var current = run.View.Match!;
            check(replies.Count == 0 && ((TargetSelectionPayload)current.Decision!.Payload).Players.All(id =>
                    run.Frame.Hits.Any(hit => hit.Id == "recipient:" + id)
                    && (id >= 4 || run.Frame.Hits.Any(hit => hit.Id == "recipient-pile:" + id))),
                "Every engine-eligible library owner remains reachable and even a sole real player target waits for an explicit gesture");
            int targetPlayer = stage == 2 ? 5 : 0;
            string gesture = stage == 2 ? "recipient:5" : "recipient-pile:0";
            var target = run.Hit(gesture);
            var point = run.PointFor(target);
            run.Input(point, left: ButtonState.Pressed);
            run.Present(run.Engine.State);
            run.Input(point);
            check(replies.Count == 0 && !run.View.CurrentHit(target),
                "A library target retired between press and release cannot answer a replacement decision");
            run.Settle("fresh-library-target");
            string prompt = run.View.Match!.Decision!.Id;
            run.ClickHit(gesture); run.Until(() => !run.View.Busy && replies.Count == 1, "choose-library-owner");
            check(replies.Single() is SelectPlayer selected && selected.Player == targetPlayer && selected.DecisionId == prompt,
                "The direct library gesture sends the original scoped player target and does not accept the later optional search");
        }
        foreach (int stage in new[] { 2, 3, 4, 5, 6 })
        foreach (bool accept in new[] { true, false }) {
            using var run = new Runner(profile, "optional-reply-" + stage + "-" + accept,
                stages[stage].Snapshot, trace, performance);
            var replies = new List<GameCommand>();
            run.Engine.ReplyOverride = command => {
                replies.Add(command); run.Engine.Present(run.Engine.State with { Decision = null });
                return Task.CompletedTask;
            };
            run.Settle("optional-reply");
            var yes = run.Hit("confirm"); var no = run.Hit("cancel");
            check(yes.Bounds.Width == no.Bounds.Width && yes.Bounds.Height == no.Bounds.Height
                    && !yes.Bounds.Intersects(no.Bounds),
                "Optional search, ordinary confirmation and Keep/Mulligan offer separate peer-sized pointer targets");
            _ = run.PointFor(yes); _ = run.PointFor(no);
            check(replies.Count == 0 && run.View.Match!.Players.All(player => player.Zone("Library").Cards.IsEmpty),
                "Presenting an optional offer neither answers it nor discloses hidden library contents");
            var decision = run.View.Match!.Decision!;
            if (decision.Payload is ConfirmationPayload && decision.Context.Source?.Card is { } source) {
                run.ClickCard(source.VisualId); run.Settle("read-optional-source");
                check(replies.Count == 0 && run.View.Match.Decision!.Id == decision.Id
                        && run.Frame.Cards.Any(card => card.Id == source.VisualId && card.Focused)
                        && run.Frame.Hits.Any(hit => hit.Id == "confirm") && run.Frame.Hits.Any(hit => hit.Id == "cancel"),
                    "Reading the optional effect's disclosed source neither accepts nor declines the offer and retains both answers");
                run.ClickCard(source.VisualId); run.Settle("close-source-reader");
            }
            if (decision.Payload is OpeningHandPayload) {
                var handCard = run.View.Match.Viewer!.Zone("Hand").Cards.First();
                run.ClickCard(handCard.VisualId); run.Settle("read-opening-hand");
                check(replies.Count == 0 && run.View.Match.Decision!.Id == decision.Id
                        && run.Frame.Hits.Any(hit => hit.Id == "confirm") && run.Frame.Hits.Any(hit => hit.Id == "cancel"),
                    "Reading an opening-hand card keeps both alternatives available without keeping, redrawing or bottoming a card");
                // Return to the unchanged hand before exercising the chosen reply.
                run.ClickCard(handCard.VisualId); run.Settle("close-opening-reader");
            }
            string prompt = run.View.Match!.Decision!.Id;
            run.ClickHit(accept ? "confirm" : "cancel");
            run.Until(() => !run.View.Busy && replies.Count == 1, "optional-answer");
            check(replies.Single() is DecisionCommand answer && answer.DecisionId == prompt
                    && (decision.Payload is OpeningHandPayload
                        ? accept ? answer is KeepOpeningHand : answer is MulliganOpeningHand
                        : accept ? answer is AcceptConfirmation : answer is DeclineConfirmation),
                "The explicit optional alternative submits exactly its semantic answer for the current deciding seat and prompt");
        }
        var offer = stages[2].Snapshot;
        using (var run = new Runner(profile, "optional-with-real-stack", offer with {
            Stack = [new("Another pending trigger", "An actual remaining stack item.", true, offer.Decision!.Context.Source?.Card) {
                Id = "optional-remaining-trigger", ControllerId = 0
            }]
        }, trace, performance)) {
            run.Settle("remaining-stack-during-offer");
            check(run.View.StackRows.Any(row => row.Id == "optional-remaining-trigger")
                    && run.Frame.Hits.Any(hit => hit.Id == "confirm") && run.Frame.Hits.Any(hit => hit.Id == "cancel"),
                "Using the existing source for an optional decision retains actual remaining stack items and both current answers");
        }
    }
}
