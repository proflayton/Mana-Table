using Mana.Contracts;
using System.Collections.Immutable;
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
            Check(scenario.Replies[^1] is PlayCard reply && reply.Card == key
                    && reply.GameId == gameId && reply.DecisionId == decisionId
                    && view.Match!.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == id) && view.HoverPreview == null,
                "Dropping above the old narrow play lane submits exactly the grabbed card in its original scope");
            Move(new(10, 70));
        }, "The real hand drag must submit one exact scoped play and finish");
    }

    private void QueueCleanupHandReadingChecks()
    {
        int before = 0;
        string game = "", decision = "";
        int[] keepSlots = [34, 0, 17, 3, 28, 9, 22];
        Step(0, () => {
            before = scenario.Replies.Count;
            var state = FocusedSeed(PresentationSmoke.Example());
            var originals = state.Viewer!.Zone("Hand").Cards;
            var hand = Enumerable.Range(0, 35).Select(index => originals[index is 0 or 17 or 34 ? 0 : index % originals.Length]
                with { VisualId = "cleanup-reading-" + index, Key = "cleanup-reading-key-" + index }).ToImmutableArray();
            var offered = hand.Select((card, index) => new Choice(71 + index * 5, card.Name,
                card with { Key = "", Selectable = false, Highlighted = false }) { CardId = card.VisualId }).ToImmutableArray();
            Publish(state with { PriorityPlayerId = null, DecisionOwnerId = state.ViewerId, ActionClass = "choice", Stack = [], Combat = null,
                Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
                    MaxHandSize = 7, Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Count = 35, Cards = hand }).ToImmutableArray()
                }).ToImmutableArray(), Decision = new("cleanup-reading", new(), new OptionsPayload(new(offered, new(28, 28))) {
                    HandReduction = new(state.ViewerId, 35, 7)
                })
            });
            game = scenario.State.Id; decision = scenario.State.Decision!.Id;
            Move(new(20, 60));
        });
        Prepared(() => view.Frame?.Scene?.Spread is { WorldCards.Count: 35, View.Scale: >= .95f } plan
            && view.DisplayedPose("cleanup-reading-0") is { } pose
            && Microsoft.Xna.Framework.Vector2.Distance(pose.Center, plan.Cards[0].Pose.Center) < 2, 2, () => {
            Check(view.Frame!.Scene!.Spread!.WorldCards.Select(card => card.Id).Distinct().Count() == 35
                && view.Selections.Count == 0 && scenario.Replies.Count == before,
                "A 35-card cleanup opens recognizable physical cards at reading distance with no discarded cards yet");
            view.CaptureWindow(Path.Combine(view.Profile, "19-cleanup-hand-spread-window.png"));
        }, "The cleanup hand spread must open before its real pointer journey");
        foreach (int slot in keepSlots) {
            string id = "cleanup-reading-" + slot;
            Step(0, () => MoveHit("card-spread-overview")); Click();
            Prepared(() => view.Frame?.Scene?.Spread is { } plan && plan.View == plan.Overview
                && view.DisplayedPose(id) is { } pose && Microsoft.Xna.Framework.Vector2.Distance(pose.Center,
                    plan.Cards.Single(card => card.Id == id).Pose.Center) < 2, 2, () => {
                MoveHit(view.Frame!.Cards.Single(card => card.Id == id).Hit.Id);
            }, "The whole hand overview must expose the same exact cleanup occurrence before focusing");
            Step(.025, () => mouse = new(mouse.X, mouse.Y, 0, ButtonState.Released, ButtonState.Pressed,
                ButtonState.Released, ButtonState.Released, ButtonState.Released));
            Step(.04, () => Buttons());
            Prepared(() => view.Frame?.Scene?.Spread is { View.Scale: >= .95f } plan && view.DisplayedPose(id) is { } pose
                && Microsoft.Xna.Framework.Vector2.Distance(pose.Center, plan.Cards.Single(card => card.Id == id).Pose.Center) < 2,
                2, () => MoveHit(view.Frame!.Cards.Single(card => card.Id == id).Hit.Id),
                "The cleanup camera must settle on the physical card before opening its reader"); Click();
            Prepared(() => ReadingReady(id) && view.Hits.Any(hit => hit.Id == "read-card-action"), 2, () => {
                Check(scenario.Replies.Count == before && !view.Selections.Contains(71 + slot * 5),
                    "Reading a cleanup candidate preserves the staged hand and sends no discard command");
                if (slot == keepSlots[0]) view.CaptureWindow(Path.Combine(view.Profile, "20-cleanup-card-reader-window.png"));
                MoveHit("read-card-action");
            }, "The cleanup candidate's explicit Keep card reader action must be reachable"); Click();
            Prepared(() => view.HoverPreview == null && view.Selections.Contains(71 + slot * 5), 2, () =>
                Check(scenario.Replies.Count == before, "Keep card stages its exact physical copy without committing cleanup"),
                "The keeper gesture must settle before moving to another card");
        }
        Step(0, () => MoveHit("card-spread-overview")); Click();
        Prepared(() => view.Frame?.Scene?.Spread is { } plan && plan.View == plan.Overview && view.Hits.Any(hit => hit.Id == "confirm")
            && view.DisplayedPose("cleanup-reading-0") is { } pose
            && Microsoft.Xna.Framework.Vector2.Distance(pose.Center, plan.Cards[0].Pose.Center) < 2, 2, () => {
            Check(view.Selections.SequenceEqual(keepSlots.Select(slot => 71 + slot * 5)) && scenario.Replies.Count == before,
                "All seven keepers remain staged for one final 28-card discard, including repeated-name physical copies");
            view.CaptureWindow(Path.Combine(view.Profile, "21-cleanup-ready-discard-window.png"));
            MoveHit("confirm");
        }, "The cleanup confirmation must remain reachable from the full-hand overview"); Click();
        Prepared(() => !view.Busy && scenario.Replies.Count == before + 1, 2, () => {
            var expected = Enumerable.Range(0, 35).Where(slot => !keepSlots.Contains(slot)).Select(slot => 71 + slot * 5);
            Check(scenario.Replies[^1] is ChooseOptions reply && reply.GameId == game && reply.DecisionId == decision
                && reply.Choices.SequenceEqual(expected),
                "One final physical cleanup confirmation submits exactly the 28 discarded indices and excludes all seven keepers");
        }, "The cleanup journey must finish with one exact discard batch");
    }
}
