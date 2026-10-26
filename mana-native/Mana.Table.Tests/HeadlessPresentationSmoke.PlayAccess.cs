using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckPlayAccess(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            foreach (string cardId in new[] { PlayAccessScenario.ExileSpellId, PlayAccessScenario.FlashbackId,
                PlayAccessScenario.BorrowedId, PlayAccessScenario.ExileLandId }) {
                using var run = new PlayAccessRun(profile, "access-play-" + cardId + "-" + size.X, size);
                run.Expose(cardId);
                var original = run.Frame.Cards.Single(card => card.Id == cardId);
                int hand = run.Connection.State.Viewer!.Zone("Hand").Count;
                run.ClickCard(cardId);
                run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                        && run.Frame.Cards.Single(card => card.Id == cardId).Focused
                        && run.Frame.Hits.Any(hit => hit.Id == "source-actions-surface")
                        && run.Connection.State.Viewer!.Zone("Hand").Count == hand,
                    "An adjacent card opens its whole face and current source offer without casting, changing its real zone or inflating the hand at " + size);
                var reader = run.Frame.Cards.Single(card => card.Id == cardId && card.Focused).Display.Bounds;
                var actions = run.Frame.Hits.Last(hit => hit.Id == "source-actions-surface").Bounds;
                check(run.Frame.Hits.Where(hit => hit.Id is "confirm" or "cancel" or "ability-auto-pass-open"
                        || hit.Id.StartsWith("ability-pass:", StringComparison.Ordinal) || hit.Id.StartsWith("ability-undo:", StringComparison.Ordinal))
                        .All(hit => !hit.Bounds.Intersects(reader) && !hit.Bounds.Intersects(actions)),
                    "Live priority and ability-yield controls never overlap the enlarged card or its current cast offers at " + size);
                var current = run.Connection.State;
                var offer = run.Connection.Offers.Last(item => item.SourceCardId == cardId);
                run.Click("read-card-action");
                run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
                check(run.Connection.Commands.Single() is UseOfferedAction used && used.GameId == current.Id
                        && used.DecisionId == current.Decision!.Id && used.Card == offer.SourceCardKey
                        && used.ActionId == offer.Actions.Single().Id
                        && current.PlayAccesses.Single(access => access.CardId == cardId).Routes.All(route => route.Id != used.ActionId)
                        && run.Connection.State.Viewer!.Zone("Hand").Count == hand,
                    "Committing an adjacent card uses the queried opaque current source offer, never the presentation route ID or a fabricated hand action at " + size);
                if (cardId != PlayAccessScenario.ExileLandId) {
                    check(run.Connection.State.Stack.Single().Card?.VisualId == cardId
                            && run.Connection.State.Players.Single(player => player.Id == original.Hit.PlayerId).Zone(original.Zone)
                                .Cards.All(card => card.VisualId != cardId),
                        "The card leaves its actual zone owner for one physical stack occurrence, including another player's exile at " + size);
                }
                trace.Add(new { scenario = "play-access.v1", size, cardId, source = original.Zone,
                    actualOwner = original.Hit.PlayerId, commands = run.Connection.Commands.Count,
                    note = "Production pointer and command check; visual clarity still requires review." });
            }

            foreach (string change in new[] { "new-prompt", "permission-withdrawn", "face-withdrawn" }) {
                using var run = new PlayAccessRun(profile, "access-stale-" + change + "-" + size.X, size);
                run.ClickCard(PlayAccessScenario.ExileSpellId);
                run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
                var old = run.Frame.Hits.Last(hit => hit.Id == "read-card-action");
                var at = run.At(old);
                run.Tick(at, ButtonState.Pressed);
                var state = run.Connection.State;
                var next = state with { Revision = state.Revision + 1,
                    Decision = change == "new-prompt" ? state.Decision! with { Id = state.Decision!.Id + ":replacement" } : state.Decision };
                if (change == "permission-withdrawn") next = next with { PlayAccesses = [] };
                if (change == "face-withdrawn") next = next with { Players = next.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != "Exile" ? zone : zone with {
                        Cards = zone.Cards.Where(card => card.VisualId != PlayAccessScenario.ExileSpellId).ToImmutableArray()
                    }).ToImmutableArray()
                }).ToImmutableArray() };
                run.Publish(next);
                run.Tick(at);
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0 && !run.View.CurrentHit(old),
                    "Replacing a prompt, withdrawing a grant or withdrawing a face between press and release prevents the captured adjacent-card action from reaching the provider at " + size);
                if (change == "face-withdrawn") check(run.Frame.Cards.All(card => card.Id != PlayAccessScenario.ExileSpellId)
                        && run.Frame.Hits.All(hit => hit.Card?.VisualId != PlayAccessScenario.ExileSpellId),
                    "A lost disclosure removes the adjacent face, enlarged reader and all corresponding pointer targets immediately at " + size);
            }

            using (var run = new PlayAccessRun(profile, "access-overflow-" + size.X, size, 5)) {
                var firstHand = run.Frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Pose)).ToArray();
                string last = run.Frame.Scene.PlayAccess.Groups.SelectMany(group => group.Cards).Last().Scene.Id;
                run.Unfold(CardSpreadPlan.AccessId);
                run.FocusOccurrence(last);
                run.ClickCard(last);
                run.Until(() => run.Frame.Cards.Any(card => card.Id == last && card.Focused));
                check(run.Connection.Commands.IsEmpty && run.Connection.State.Viewer!.Zone("Hand").Count == 11
                        && run.Frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Pose)).SequenceEqual(firstHand)
                        && run.Frame.Scene.Spread?.Contains(last) == true,
                    "Unfolding the source packet reaches and reads its last actual card beside eleven in hand without moving hand anchors or casting at " + size);
            }
            using (var run = new PlayAccessRun(profile, "access-off-priority-" + size.X, size, 1)) {
                run.ClickCard(PlayAccessScenario.ExileSpellId);
                run.Until(() => run.Frame.Cards.Any(card => card.Id == PlayAccessScenario.ExileSpellId && card.Focused));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0 && run.Connection.Offers.IsEmpty
                        && run.Frame.Hits.All(hit => hit.Id != "read-card-action")
                        && run.Connection.State.PlayAccesses.Any(access => access.CardId == PlayAccessScenario.ExileSpellId),
                    "An off-priority grant remains readable beside the hand while no cast query, cast control or provider action is created at " + size);
            }
        }
        using (var run = new PlayAccessRun(profile, "access-multiple-routes", new(1540, 980), 4)) {
            string id = PlayAccessScenario.FlashbackId;
            run.Expose(id);
            var start = run.At(run.Frame.Cards.Single(card => card.Id == id).Hit);
            var drop = new Point(900, 470);
            run.Tick(start, ButtonState.Pressed); run.Tick(drop, ButtonState.Pressed);
            run.Until(() => run.Connection.Offers.Any(offer => offer.SourceCardId == id && offer.Actions.Length == 2), drop, ButtonState.Pressed);
            var offer = run.Connection.Offers.Last(item => item.SourceCardId == id);
            run.Tick(drop);
            run.Until(() => run.Frame.Cards.Any(card => card.Id == id && card.Focused)
                && run.Frame.Hits.Any(hit => hit.Id == "source-action:" + offer.Actions[1].Id));
            check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                    && run.Frame.Cards.Count(card => card.Id == id) == 1,
                "Dropping a card with two cast routes opens its single full face and both real offers without choosing a route for the player");
            run.Click("source-action:" + offer.Actions[1].Id);
            run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
            check(run.Connection.Commands.Single() is UseOfferedAction chosen && chosen.ActionId == offer.Actions[1].Id,
                "Choosing the second cast route submits that exact current opaque offer for the shared physical card");
        }
        CheckDelayedPlayAccess(profile, check);
        CheckPlayAccessDrag(profile, check);
        CheckResolvingPlayAccess(profile, check);
        CheckStackPlayAccessFocus(profile, check);
        CheckCollectionPlayAccess(profile, check);
        performance.Add(new { scenario = "play-access.v1", note = "Finite presentation and pointer journeys; not a performance or human-clarity score." });
    }

    private static void CheckCollectionPlayAccess(string profile, Action<bool, string> check)
    {
        foreach (var (stage, collection, size) in new[] {
            (11, CardSpreadPlan.HandId, new Point(1000, 740)),
            (12, CardSpreadPlan.AccessId, new Point(1540, 980)),
            (13, CardSpreadPlan.AccessId, new Point(1000, 740))
        }) {
            using var run = new PlayAccessRun(profile, "physical-collection-" + stage, size, stage);
            var before = run.Connection.State;
            var hand = run.Frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Pose)).ToArray();
            var source = stage == 13 ? run.Frame.Scene.PlayAccess.Groups.Single(group => group.SourceIds.Contains(PlayAccessScenario.ChandraId)) : null;
            string collectionId = source?.Id ?? collection;
            run.Unfold(collectionId);
            var spread = run.Frame.Scene!.Spread!;
            check(spread.View.Scale >= .85f && spread.View == spread.InitialView,
                "Opening a large hand or source packet starts at readable card scale before any explicit overview gesture");
            if (source != null) check(spread.Anchors.Select(card => card.Id).SequenceEqual(source.Cards.Select(item => item.Scene.Id))
                    && spread.Anchors.All(card => card.Zone == source.Zone && card.Owner == source.PlayerId),
                "Unfolding a specific grant keeps only that source packet's exact occurrences and actual owner instead of mixing in other grants or the hand");
            var world = spread.WorldCards.Select(card => (card.Id, card.Pose)).ToArray();
            var originals = spread.Anchors.Select(card => (card.Id, card.Zone, card.Owner)).ToArray();
            var samples = new[] { 0, spread.Cards.Count / 2, spread.Cards.Count - 1 }.Distinct().Select(index => spread.Cards[index].Id).ToArray();
            foreach (string id in samples) {
                run.FocusOccurrence(id);
                var face = run.Frame.Cards.Single(card => card.Id == id);
                check(face.Display.Size.X >= CardSpreadPlan.CardSize.X * .94f
                        && CardSpreadPlan.Viewport.Intersects(face.Display.Bounds)
                        && run.Connection.State == before && run.Connection.Commands.IsEmpty,
                    "An actual middle-click on a first, middle or last overview card focuses the same physical occurrence without choosing or playing it");
                run.ClickCard(id);
                run.Until(() => run.Frame.Cards.Any(card => card.Id == id && card.Focused));
                check(run.Frame.Cards.Count(card => card.Id == id) == 1 && run.Connection.State == before
                        && run.Connection.Commands.IsEmpty && run.View.Selections.Count == 0,
                    "Reading first, middle and last cards in a large unfolded hand or source packet remains noncommitting and keeps one occurrence");
                run.CloseReader();
            }
            if (stage == 11) {
                run.FocusOccurrence(samples[1], keyboard: true);
                check(run.Frame.Cards.Single(card => card.Id == samples[1]).Display.Size.X >= CardSpreadPlan.CardSize.X * .94f
                        && run.Connection.State == before && run.Connection.Commands.IsEmpty && run.View.Selections.Count == 0,
                    "Z focuses the physical card under the pointer from overview without reading, selecting or playing it");
            }
            run.FocusOccurrence(samples[1]);
            var focused = run.Frame.Scene!.Spread!;
            run.Tick(CardSpreadPlan.Viewport.Center, wheelDelta: 120);
            var zoomed = run.Frame.Scene!.Spread!;
            check(zoomed.View.Scale > focused.View.Scale && zoomed.WorldCards.Select(card => (card.Id, card.Pose)).SequenceEqual(world),
                "The wheel zooms the physical collection camera rather than paging or replacing its cards");
            var panStart = CardSpreadPlan.Viewport.Center;
            run.Tick(panStart, middle: ButtonState.Pressed);
            run.Tick(panStart + new Point(88, 36), middle: ButtonState.Pressed);
            run.Tick(panStart + new Point(88, 36));
            check(run.Frame.Scene!.Spread!.View != zoomed.View
                    && run.Frame.Scene.Spread.Anchors.Select(card => (card.Id, card.Zone, card.Owner)).SequenceEqual(originals)
                    && run.Frame.Scene.Hand.Cards.Select(card => (card.Id, card.Pose)).SequenceEqual(hand)
                    && run.Connection.State == before && run.Connection.Commands.IsEmpty,
                "Middle-button panning moves the camera over unchanged world cards, source anchors and the real hand without a game action");

            run.FocusOccurrence(samples[1]);
            var compact = run.Frame.Cards.Single(card => card.Id == samples[1]);
            var start = run.CenterOrAt(compact.Hit);
            run.Tick(start, ButtonState.Pressed); run.Tick(start + new Point(18, 0), ButtonState.Pressed); run.Tick(start + new Point(18, 0));
            check(run.Connection.State == before && run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0,
                "Small movement inside a spread card's original source pose cannot cast, play or choose it");
            run.Unfold(collectionId);
            run.Tick(new(40, 100), ButtonState.Pressed); run.Tick(new(40, 100));
            run.Until(() => run.Frame.Scene?.Spread == null);
            check(run.Frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Pose)).SequenceEqual(hand)
                    && run.Connection.State == before && run.Connection.Commands.IsEmpty,
                "An outside gesture folds the same physical collection back to its source anchors without changing or submitting cards");
            run.Unfold(collectionId); run.Fold();
            check(run.Connection.State == before && run.Connection.Commands.IsEmpty,
                "Escape folds an unfolded collection without advancing priority or a rules decision");
        }
        CheckCollectionDraft(profile, check);
        CheckCollectionExpiryMotion(profile, check);
    }

    private static void CheckCollectionExpiryMotion(string profile, Action<bool, string> check)
    {
        using var run = new PlayAccessRun(profile, "physical-collection-expiry-motion", new(1540, 980), 12, reduced: false);
        run.Unfold(CardSpreadPlan.AccessId);
        const string survivingId = "access-overflow-30";
        run.FocusOccurrence(survivingId);
        var before = run.Connection.State;
        var previous = run.Frame.Cards.Single(card => card.Id == survivingId).Display;
        var revoked = Enumerable.Range(0, 16).Select(index => "access-overflow-" + index.ToString("D2")).ToHashSet();
        var expired = before with { Revision = before.Revision + 1,
            PlayAccesses = before.PlayAccesses.Where(access => !revoked.Contains(access.CardId)).ToImmutableArray() };
        run.Publish(expired);
        var spread = run.Frame.Scene!.Spread!;
        var target = spread.Cards.Single(card => card.Id == survivingId).Pose;
        var first = run.Frame.Cards.Single(card => card.Id == survivingId).Display;
        float travel = Vector2.Distance(previous.Center, target.Center);
        check(travel > 100 && Vector2.Distance(first.Center, previous.Center) < travel * .75f
                && Vector2.Distance(first.Center, target.Center) > travel * .2f
                && run.Frame.Cards.Count(card => card.Id == survivingId) == 1,
            "Permission expiry repacks a camera-focused source collection from the surviving physical card's preceding display instead of snapping to its new world target");
        check(spread.Anchors.All(card => !revoked.Contains(card.Id))
                && spread.WorldCards.All(card => !revoked.Contains(card.Id))
                && run.Frame.Scene.PlayAccess.Groups.SelectMany(group => group.Cards).All(item => !revoked.Contains(item.Scene.Id))
                && run.Frame.Hits.All(hit => hit.Card == null || !revoked.Contains(hit.Card.VisualId))
                && expired.Viewer!.Zone("Exile").Cards.Count(card => revoked.Contains(card.VisualId)) == revoked.Count
                && run.Connection.Commands.IsEmpty && run.Connection.Offers.IsEmpty,
            "The same-prompt expiry immediately removes revoked access faces and pointer authority while keeping their disclosed actual-zone cards and issuing no camera-driven action query");
        run.Until(() => run.SpreadPoseSettled(survivingId));
        check(run.Frame.Cards.Single(card => card.Id == survivingId).Card.VisualId == survivingId
                && run.Connection.State == expired && run.Connection.Commands.IsEmpty,
            "The surviving occurrence completes its normal-motion repacking in the current collection without a game action or stale permission");
    }

    private static void CheckCollectionDraft(string profile, Action<bool, string> check)
    {
        using var run = new PlayAccessRun(profile, "physical-collection-required-draft", new(1000, 740), 11);
        var initial = run.Connection.State;
        var hand = initial.Viewer!.Zone("Hand").Cards;
        var options = hand.Select((card, index) => new Choice(17 + index, card.Name, card) { CardId = card.VisualId }).ToImmutableArray();
        var required = initial with { PriorityPlayerId = null, ActionClass = "choice",
            Decision = new(initial.Decision!.Id + ":hand-choice", new(Message: "Choose two cards from your hand."),
                new OptionsPayload(new(options, new(2, 2)))) };
        run.Publish(required);
        run.Unfold(CardSpreadPlan.HandId);
        int middle = hand.Length / 2, originalIndex = 17 + middle;
        string id = hand[middle].VisualId;
        run.FocusOccurrence(id); run.ClickCard(id);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
        run.Click("read-card-action");
        check(run.View.Selections.SequenceEqual([originalIndex]) && run.Connection.Commands.IsEmpty,
            "The explicit hand-choice gesture stages the original offered index while a large hand remains one current required decision");
        run.FocusOccurrence(hand[^1].VisualId); run.Fold();
        run.Unfold(CardSpreadPlan.HandId); run.FocusOccurrence(hand[0].VisualId);
        check(run.View.Selections.SequenceEqual([originalIndex]) && run.Connection.Commands.IsEmpty
                && run.View.Match!.Decision!.Id == required.Decision!.Id,
            "Overview, focus, fold and reopen preserve the same required-choice draft without confirming it or changing its original index");
        run.FocusOccurrence(id); run.ClickCard(id);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
        var retired = run.Frame.Hits.Last(hit => hit.Id == "read-card-action");
        var press = run.At(retired);
        run.Tick(press, ButtonState.Pressed);
        var withdrawn = required with { Revision = required.Revision + 1,
            Players = required.Players.Select(player => player.Id != required.ViewerId ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                    Cards = zone.Cards.Where(card => card.VisualId != id).ToImmutableArray(), Count = zone.Count - 1
                }).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = required.Decision! with { Payload = new OptionsPayload(new(options.Where(choice => choice.Index != originalIndex).ToImmutableArray(), new(2, 2))) }
        };
        run.Publish(withdrawn); run.Tick(press);
        bool publishedRetired = run.Frame.Hits.Any(hit => hit.Id == retired.Id && hit.Scope == retired.Scope && Equals(hit.Intent, retired.Intent));
        bool activatedRetired = run.View.Activate(retired);
        check(run.View.Selections.All(index => index != originalIndex) && run.Frame.Cards.All(card => card.Id != id)
                && !publishedRetired && !activatedRetired && run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0,
            "Withdrawing a face and its offered choice during a pressed reader gesture removes the old occurrence and draft index without submitting a replacement choice"
                + $" (selected={string.Join(',', run.View.Selections)}, faces={run.Frame.Cards.Count(card => card.Id == id)}, scoped={run.View.CurrentHit(retired)}, published={publishedRetired}, activated={activatedRetired}, commands={run.Connection.Commands.Count}, attempts={run.Connection.Attempts})");
        foreach (var candidate in new[] { hand[0], hand[^1] }) {
            run.Unfold(CardSpreadPlan.HandId); run.FocusOccurrence(candidate.VisualId); run.ClickCard(candidate.VisualId);
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
            run.Click("read-card-action");
        }
        check(run.View.Selections.SequenceEqual([17, 17 + hand.Length - 1]) && run.Connection.Commands.IsEmpty
                && run.Frame.Scene?.Spread?.Id == CardSpreadPlan.HandId,
            "Two explicit hand choices remain a staged draft with their original offered indices while the physical spread is open");
        run.Click("confirm");
        run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
        check(run.Connection.Commands.Single() is ChooseOptions chosen && chosen.DecisionId == withdrawn.Decision!.Id
                && chosen.Choices.SequenceEqual([17, 17 + hand.Length - 1]),
            "The shared current confirmation is reachable from the open hand spread and submits only the two explicitly chosen original indices");
    }

    private static void CheckStackPlayAccessFocus(string profile, Action<bool, string> check)
    {
        using var run = new PlayAccessRun(profile, "access-stack-hover-and-flight", new(1540, 980), reduced: false);
        string id = PlayAccessScenario.ExileSpellId;
        run.ClickCard(id);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
        var route = run.Frame.Hits.Last(hit => hit.Id == "read-card-action");
        var click = run.CenterOrAt(route);
        run.Tick(click, ButtonState.Pressed); run.Tick(click);
        run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1, click);
        var spell = run.Frame.Cards.Single(card => card.Id == id && card.Zone == "Stack");
        bool ControlsClear() => run.Frame.Hits.Where(hit => hit.Id is "confirm" or "cancel" or "ability-auto-pass-open"
                || hit.Id.StartsWith("ability-pass:", StringComparison.Ordinal) || hit.Id.StartsWith("ability-undo:", StringComparison.Ordinal))
            .All(hit => run.Frame.Cards.Where(card => card.Playing).All(card => !hit.Bounds.Intersects(card.Display.Bounds)));
        bool clear = ControlsClear();
        check(spell.Display.Contains(click) || run.View.ReferencePreviewCardId == null && run.View.ReferencePreviewBounds == null,
            "Leaving the pointer at the actual cast-route click does not open an unsolicited stack preview outside the moving physical face");
        // The stack header is an explicit reading target larger than its art.
        // Sample its geometry without advancing the flight into that point.
        var header = run.HeaderOutsideFace(spell);
        run.Tick(header, elapsed: 0);
        clear &= ControlsClear();
        var headerFrame = run.Frame.Cards.Single(card => card.Id == id && card.Zone == "Stack");
        var headerHit = run.View.HitAt(header);
        check(!headerFrame.Display.Contains(header) && run.View.ReferencePreviewCardId == null && run.View.ReferencePreviewBounds == null,
            "A physical spell's enlarged header hit area retains explicit reading without treating empty header space as a hovered card"
                + $" (point={header}, before={spell.Display.Center}/{spell.Display.Size}, after={headerFrame.Display.Center}/{headerFrame.Display.Size}, insideAfter={headerFrame.Display.Contains(header)}, preview={run.View.ReferencePreviewCardId}/{run.View.ReferencePreviewBounds}, hit={headerHit?.Id}/{headerHit?.Zone}/{headerHit?.Intent.GetType().Name})");
        spell = run.Frame.Cards.Single(card => card.Id == id && card.Zone == "Stack");
        run.Tick(spell.Display.Center.ToPoint(), elapsed: 0);
        clear &= ControlsClear();
        check(run.View.ReferencePreviewCardId == id && run.View.ReferencePreviewBounds != null,
            "Hovering the actual displayed physical stack face still previews that exact current spell");

        int movingFrames = 0;
        for (int frame = 0; frame < 55; frame++) {
            var moving = run.Frame.Cards.Where(card => card.Playing).ToArray();
            if (moving.Length == 0) break;
            movingFrames++;
            clear &= ControlsClear();
            run.Tick(new(20, 60));
        }
        check(movingFrames > 0 && clear && run.Connection.Commands.Count == 1,
            "Ordinary priority and yield controls never cover the physical spell anywhere along its cast flight, and hover creates no game command");
    }

    private static void CheckResolvingPlayAccess(string profile, Action<bool, string> check)
    {
        using var run = new PlayAccessRun(profile, "access-resolving-observation", new(1540, 980), reduced: false);
        string id = PlayAccessScenario.ExileSpellId;
        run.Connection.DeferCastObservation = true;
        var old = run.Frame.Cards.Single(card => card.Id == id).Hit;
        var start = run.At(old);
        var drop = new Point(900, 470);
        var held = drop.ToVector2() + new Vector2(0, -72);
        run.Tick(start, ButtonState.Pressed); run.Tick(drop, ButtonState.Pressed);
        run.Until(() => run.Connection.Offers.Any(offer => offer.SourceCardId == id), drop, ButtonState.Pressed);
        run.Tick(drop);
        run.Until(() => run.Connection.Commands.Count == 1 && !run.View.Busy && run.View.Match is { Status: "resolving", Decision: null });
        for (int n = 0; n < 6; n++) run.Tick(drop);
        var waiting = run.Frame.Cards.Single(card => card.Id == id);
        check(!run.View.Busy && waiting.Zone == "Exile" && waiting.Card.Key.Length == 0
                && Vector2.Distance(waiting.Display.Center, held) < 3 && run.View.CardPaintCount(id) == 1
                && !run.View.CurrentHit(old) && run.Frame.Hits.All(hit => hit.Id is not ("confirm" or "cancel" or "read-card-action"))
                && run.Connection.Commands.Count == 1,
            "A prompt-null resolving observation retains only the submitted card's visible held pose after command completion, while old action keys and priority controls are revoked");
        run.Publish(run.Connection.CompleteCastObservation());
        var arriving = run.Frame.Cards.Single(card => card.Id == id);
        check(arriving.Zone == "Stack" && arriving.Playing && Vector2.Distance(arriving.Display.Center, held) < 200
                && run.View.CardPaintCount(id) == 1 && run.Connection.Commands.Count == 1,
            "The eventual authoritative stack occurrence continues from the held pose across the resolving gap without replaying a command or returning to the exile shelf");
    }

    private static void CheckDelayedPlayAccess(string profile, Action<bool, string> check)
    {
        foreach (string result in new[] { "one-route", "two-routes", "new-prompt", "permission-withdrawn", "face-withdrawn", "escape" }) {
            bool multiple = result == "two-routes";
            using var run = new PlayAccessRun(profile, "access-delayed-" + result, new(1540, 980), multiple ? 4 : 0);
            string id = multiple ? PlayAccessScenario.FlashbackId : PlayAccessScenario.ExileSpellId;
            run.Expose(id);
            if (result is "one-route" or "two-routes") {
                run.Unfold(CardSpreadPlan.AccessId);
                run.FocusOccurrence(id);
            }
            run.Connection.DelaySourceReads = true;
            var before = run.Connection.State;
            var source = run.Frame.Cards.Single(card => card.Id == id);
            var start = run.At(source.Hit);
            var drop = result is "one-route" or "two-routes" ? new Point(700, 760) : new Point(900, 470);
            run.Tick(start, ButtonState.Pressed);
            run.Tick(drop, ButtonState.Pressed);
            run.Until(() => run.Connection.DeferredReads.Count == 1, drop, ButtonState.Pressed);
            // Release while the provider task is still incomplete. The exact
            // eventual offer cannot be known or manufactured by the UI yet.
            run.Tick(drop);
            for (int n = 0; n < 4; n++) run.Tick(drop);
            var retained = run.Frame.Cards.Single(card => card.Id == id);
            check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                    && run.Connection.State == before && run.Frame.Cards.Count(card => card.Id == id) == 1
                    && Vector2.Distance(retained.Display.Center, drop.ToVector2() + new Vector2(0, -72)) < 3,
                "Releasing before the source query finishes retains one held public card and sends no speculative command or hand mutation");

            if (result is "new-prompt" or "permission-withdrawn" or "face-withdrawn") {
                var next = before with { Revision = before.Revision + 1,
                    Decision = result == "new-prompt" ? before.Decision! with { Id = before.Decision!.Id + ":replacement" } : before.Decision };
                if (result == "permission-withdrawn") next = next with {
                    PlayAccesses = before.PlayAccesses.Where(access => access.CardId != id).ToImmutableArray()
                };
                if (result == "face-withdrawn") next = next with { Players = before.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != source.Zone ? zone : zone with {
                        Cards = zone.Cards.Where(card => card.VisualId != id).ToImmutableArray()
                    }).ToImmutableArray()
                }).ToImmutableArray() };
                run.Publish(next);
            } else if (result == "escape") {
                run.Tick(drop, keys: new(Keys.Escape)); run.Tick(drop);
            }

            run.Connection.DelaySourceReads = false;
            var offer = run.Connection.CompleteSourceRead();
            if (result == "one-route") {
                run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
                check(run.Connection.Commands.Single() is UseOfferedAction cast && cast.GameId == before.Id
                        && cast.DecisionId == before.Decision!.Id && cast.Card == offer.SourceCardKey
                        && cast.ActionId == offer.Actions.Single().Id && run.Connection.State.Stack.Single().Card?.VisualId == id,
                    "A released play resumes only after its source query supplies the exact current opaque offer, and sends it once");
            } else if (multiple) {
                run.Until(() => run.Frame.Cards.Any(card => card.Id == id && card.Focused)
                    && run.Frame.Hits.Any(hit => hit.Id == "source-action:" + offer.Actions[1].Id));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0 && run.Frame.Cards.Count(card => card.Id == id) == 1,
                    "A delayed response containing two routes opens one full card with both exact offers and never selects the first automatically");
                run.Click("source-action:" + offer.Actions[1].Id);
                run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
                check(run.Connection.Commands.Single() is UseOfferedAction chosen && chosen.ActionId == offer.Actions[1].Id,
                    "The delayed route chooser submits the player's explicitly chosen second opaque offer");
            } else {
                for (int n = 0; n < 12; n++) { run.Tick(drop); Thread.Yield(); }
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                        && run.Frame.Cards.All(card => card.Id != id
                            || Vector2.Distance(card.Display.Center, drop.ToVector2() + new Vector2(0, -72)) > 100),
                    "A new prompt, withdrawn grant, lost face or Escape cancels a released play and its held pose before a late source response can act");
                // The next explicit source read cannot complete until the old
                // in-flight callback has retired. This supplies an observation
                // barrier for the late result without sleeps or private hooks.
                run.Expose(PlayAccessScenario.ExileLandId);
                run.ClickCard(PlayAccessScenario.ExileLandId);
                run.Until(() => run.Frame.Cards.Any(card => card.Id == PlayAccessScenario.ExileLandId && card.Focused)
                    && run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0,
                    "After the cancelled query's callback retires, an unrelated current reader still cannot resurrect or submit the abandoned play");
            }
        }
    }

    private static void CheckPlayAccessDrag(string profile, Action<bool, string> check)
    {
        foreach (bool reduced in new[] { false, true }) {
            using var run = new PlayAccessRun(profile, "access-drag-" + reduced, new(1540, 980), reduced: reduced);
            string id = PlayAccessScenario.ExileSpellId;
            var from = run.Frame.Cards.Single(card => card.Id == id);
            var start = run.At(from.Hit);
            var drop = new Point(900, 470);
            run.Tick(start, ButtonState.Pressed);
            run.Tick(drop, ButtonState.Pressed);
            run.Until(() => run.Connection.Offers.Any(offer => offer.SourceCardId == id), drop, ButtonState.Pressed);
            var offer = run.Connection.Offers.Last(item => item.SourceCardId == id);
            run.Tick(drop);
            run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
            check(run.Connection.Commands.Single() is UseOfferedAction command && command.Card == offer.SourceCardKey
                    && command.ActionId == offer.Actions.Single().Id && run.Connection.State.Stack.Single().Card?.VisualId == id,
                "Dragging a physical exile card directly onto the table waits for and submits exactly its current opaque cast offer");
            var moving = run.Frame.Cards.Single(card => card.Id == id);
            check(moving.Zone == "Stack" && moving.Playing == !reduced && run.View.CardPaintCount(id) == 1
                    && (reduced || Vector2.Distance(moving.Display.Center, drop.ToVector2() + new Vector2(0, -72)) < 260),
                "An adjacent-card cast preserves the held physical occurrence into the stack flight; reduced motion settles directly without a duplicate fan face"
                    + $" (reduced={reduced}, zone={moving.Zone}, playing={moving.Playing}, paints={run.View.CardPaintCount(id)}, pose={moving.Display.Center}, anchor={from.Display.Center}, held={drop.ToVector2() + new Vector2(0, -72)}, frame={run.Frame.Stamp.Revision}, state={run.Connection.State.Revision})");
            run.Until(() => run.Frame.Cards.Single(card => card.Id == id).Playing == false);
            var stack = run.Frame.Cards.Single(card => card.Id == id);
            var next = run.Connection.Pass();
            run.Publish(next);
            moving = run.Frame.Cards.Single(card => card.Id == id);
            check(moving.Zone == "Battlefield" && moving.Playing == !reduced
                    && (reduced || Vector2.Distance(moving.Display.Center, stack.Display.Center) < 200)
                    && run.Frame.Scene!.PlayAccess.Groups.SelectMany(group => group.Cards).All(item => item.Scene.Id != id),
                "Resolution carries the same occurrence from its real stack pose to the battlefield and retires its old adjacent permission");
        }
    }

    private sealed class PlayAccessRun : IDisposable
    {
        private readonly TableApplication app;
        private readonly Point size;
        private double seconds;
        private int wheel;
        internal PlayAccessConnection Connection { get; }
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;

        internal PlayAccessRun(string profile, string name, Point size, int stage = 0, bool reduced = true)
        {
            this.size = size;
            string directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = reduced, Sounds = false, TextScale = 1.3f }.Save(directory);
            Connection = new(stage);
            var setup = new InteractionScenarioEngine();
            app = new(new(setup, setup, Connection, setup.InitializeAsync) { SourceActions = Connection },
                directory, initialState: Connection.State) { Automated = true };
            app.Initialize();
            for (int n = 0; n < (reduced ? 4 : 40); n++) Tick(new(20, 60));
        }

        private Point Physical(Point logical)
        {
            var at = RenderViewport.Fit(size, 900).ToPhysical(logical.ToVector2());
            return new((int)MathF.Ceiling(at.X), (int)MathF.Ceiling(at.Y));
        }
        internal void Tick(Point logical = default, ButtonState left = ButtonState.Released, KeyboardState? keys = null,
            ButtonState middle = ButtonState.Released, int wheelDelta = 0, double elapsed = .05)
        {
            seconds += elapsed; var at = Physical(logical); wheel += wheelDelta;
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(elapsed)),
                new(at.X, at.Y, wheel, left, middle, default, default, default), keys ?? new(), size);
            Frame = app.ReadPresentation();
            if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        }
        internal void Publish(GameSnapshot state) { Connection.Replace(state); View.Accept(state); Tick(new(20, 60)); }
        internal void Until(Func<bool> ready, Point point = default, ButtonState left = ButtonState.Released)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!ready()) {
                if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Play-access presentation did not settle at " + size);
                Tick(point, left); Thread.Yield();
            }
        }
        internal Point At(TableHit hit)
        {
            for (int y = Math.Max(0, hit.Bounds.Top + 2); y < Math.Min(900, hit.Bounds.Bottom - 2); y += 3)
                for (int x = Math.Max(0, hit.Bounds.Left + 2); x < Math.Min(1600, hit.Bounds.Right - 2); x += 3) {
                    var at = new Point(x, y);
                    if (View.HitAt(RenderViewport.Fit(size, 900).ToLogical(Physical(at))) is { } current
                        && current.Id == hit.Id && current.Scope == hit.Scope) return at;
                }
            throw new InvalidOperationException("No exposed play-access pointer target " + hit.Id + " at " + size);
        }
        private bool Matches(TableHit hit, Point logical) => View.HitAt(RenderViewport.Fit(size, 900).ToLogical(Physical(logical))) is { } current
            && current.Id == hit.Id && current.Scope == hit.Scope;
        internal Point CenterOrAt(TableHit hit) => Matches(hit, hit.Bounds.Center) ? hit.Bounds.Center : At(hit);
        internal Point HeaderOutsideFace(PresentedCard card)
        {
            var hit = Frame.Hits.Last(item => item.Id == card.Hit.Id && item.Scope == card.Hit.Scope);
            Point? best = null;
            float distance = -1;
            for (int y = Math.Max(0, hit.Bounds.Top + 3); y < Math.Min(900, hit.Bounds.Bottom - 3); y += 5)
                for (int x = Math.Max(0, hit.Bounds.Left + 3); x < Math.Min(1600, hit.Bounds.Right - 3); x += 5) {
                    var point = new Point(x, y);
                    if (card.Display.Contains(point) || !Matches(hit, point)) continue;
                    float candidate = Math.Min(Vector2.DistanceSquared(point.ToVector2(), card.Display.Center),
                        Vector2.DistanceSquared(point.ToVector2(), card.Rest.Center));
                    if (candidate > distance) { best = point; distance = candidate; }
                }
            return best ?? throw new InvalidOperationException("The physical spell exposes no header-only pointer target at " + size);
        }
        internal void Click(string id) { var at = At(Frame.Hits.Last(hit => hit.Id == id)); Tick(at, ButtonState.Pressed); Tick(at); }
        internal void ClickCard(string id) => Click(Frame.Cards.Single(card => card.Id == id).Hit.Id);
        internal void Expose(string id)
        {
            if (Frame.Cards.Any(card => card.Id == id && Frame.Hits.Any(hit => hit.Id == card.Hit.Id))) {
                try { _ = At(Frame.Cards.Single(card => card.Id == id).Hit); return; }
                catch (InvalidOperationException) { }
            }
            string collection = Connection.State.Viewer!.Zone("Hand").Cards.Any(card => card.VisualId == id)
                ? CardSpreadPlan.HandId : CardSpreadPlan.AccessId;
            Unfold(collection); FocusOccurrence(id);
        }
        internal void CloseReader()
        {
            if (!Frame.Cards.Any(card => card.Focused)) return;
            Tick(new(20, 60), keys: new(Keys.Escape)); Tick(new(20, 60));
            Until(() => Frame.Cards.All(card => !card.Focused));
        }
        internal void Unfold(string collection)
        {
            CloseReader();
            if (Frame.Scene?.Spread?.Id == collection) return;
            Fold();
            Click(collection == CardSpreadPlan.HandId ? "hand-unfold" : collection == CardSpreadPlan.AccessId
                ? "play-access-unfold" : "play-access-source:" + collection);
            Until(() => Frame.Scene?.Spread?.Id == collection);
        }
        internal void Fold()
        {
            CloseReader();
            if (Frame.Scene?.Spread == null) return;
            Tick(new(20, 60), keys: new(Keys.Escape)); Tick(new(20, 60));
            Until(() => Frame.Scene?.Spread == null);
        }
        internal void FocusOccurrence(string id, bool keyboard = false)
        {
            CloseReader();
            if (Frame.Scene?.Spread?.Contains(id) != true) throw new InvalidOperationException("The unfolded collection does not contain " + id);
            Tick(new(800, 400), keys: new(Keys.Home)); Tick(new(800, 400));
            Until(() => SpreadPoseSettled(id));
            var card = Frame.Cards.Single(item => item.Id == id);
            var point = At(card.Hit);
            if (keyboard) { Tick(point, keys: new(Keys.Z)); Tick(point); }
            else { Tick(point, middle: ButtonState.Pressed); Tick(point); }
            Until(() => Frame.Scene!.Spread!.View.Scale >= .95f
                && SpreadPoseSettled(id));
        }
        internal bool SpreadPoseSettled(string id)
        {
            var target = Frame.Scene!.Spread!.Cards.Single(card => card.Id == id).Pose;
            var display = Frame.Cards.Single(card => card.Id == id).Display;
            return Vector2.Distance(display.Center, target.Center) < 1
                && Vector2.Distance(display.Size, target.Size) < 1;
        }
        public void Dispose() => app.Dispose();
    }

    private sealed class PlayAccessConnection : IMatchConnection, ISourceActions
    {
        private readonly ExperienceLabSession lab = new(PlayAccessScenario.Id);
        private GameSnapshot state;
        private int attempts;
        internal GameSnapshot State => Volatile.Read(ref state);
        internal int Attempts => Volatile.Read(ref attempts);
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal ConcurrentQueue<SourceActionSet> Offers { get; } = new();
        internal bool DelaySourceReads { get; set; }
        internal bool DeferCastObservation { get; set; }
        private GameSnapshot? deferredCastObservation;
        internal ConcurrentQueue<(SourceActionSet Offer, TaskCompletionSource<SourceActionSet> Response)> DeferredReads { get; } = new();
        internal PlayAccessConnection(int stage) => state = stage == 0 ? lab.State : lab.Replay(stage)[^1];
        internal void Replace(GameSnapshot next) => Volatile.Write(ref state, next);
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var current = State;
            if (current.Id != gameId || current.Decision is not { } decision || decision.Id != decisionId)
                throw new EngineCommandRejectedException("Stale access query.");
            var card = current.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
                .SingleOrDefault(item => item.Key == sourceCardKey && !item.FaceDown)
                ?? throw new EngineCommandRejectedException("Undisclosed access source.");
            var access = current.PlayAccesses.SingleOrDefault(item => item.CardId == card.VisualId);
            var actions = decision.Payload is PriorityPayload && current.DecisionOwnerId == current.ViewerId && access != null
                ? access.Routes.Where(route => route.CanStart).Select(route => new SourceAction(
                    decisionId + ":" + sourceCardKey + ":access:" + route.Id,
                    route.Kind == PlayAccessKind.PlayLand ? "play" : "cast",
                    route.Detail + (access.Routes.Length > 1 ? " — " + route.SourceName : ""))).ToImmutableArray()
                : [];
            var offered = new SourceActionSet(gameId, decisionId, sourceCardKey, card.VisualId, actions);
            Offers.Enqueue(offered);
            if (DelaySourceReads) {
                var response = new TaskCompletionSource<SourceActionSet>();
                DeferredReads.Enqueue((offered, response));
                return response.Task;
            }
            return Task.FromResult(offered);
        }
        internal SourceActionSet CompleteSourceRead()
        {
            if (!DeferredReads.TryDequeue(out var read)) throw new InvalidOperationException("No controlled source query is awaiting completion.");
            if (!read.Response.TrySetResult(read.Offer)) throw new InvalidOperationException("Controlled source query was already completed.");
            return read.Offer;
        }
        public Task ExecuteAsync(GameCommand command)
        {
            Interlocked.Increment(ref attempts);
            var current = State;
            if (command is ChooseOptions choice) {
                if (!GameCommandPermissions.Allows(current, choice))
                    throw new EngineCommandRejectedException("The hand choices must satisfy this exact current offered selection.");
                Replace(current with { Revision = current.Revision + 1, Decision = null,
                    PriorityPlayerId = null, DecisionOwnerId = null, ActionClass = "resolving" });
                Commands.Enqueue(command);
                return Task.CompletedTask;
            }
            if (command is not UseOfferedAction use || use.GameId != current.Id || current.Decision is not { } decision || use.DecisionId != decision.Id
                || decision.Payload is not PriorityPayload || current.DecisionOwnerId != current.ViewerId || current.PriorityPlayerId != current.ViewerId
                || !Offers.Any(offer => offer.GameId == current.Id && offer.DecisionId == decision.Id
                    && offer.SourceCardKey == use.Card && offer.Actions.Any(action => action.Id == use.ActionId))
                || !current.PlayAccesses.Any(access => current.Players.Single(player => player.Id == access.PlayerId).Zone(access.Zone).Cards
                    .Any(card => card.Key == use.Card && card.VisualId == access.CardId && !card.FaceDown)
                    && access.Routes.Any(route => route.CanStart && use.ActionId == decision.Id + ":" + use.Card + ":access:" + route.Id)))
                throw new EngineCommandRejectedException("Only a current queried access offer can cast in this authored journey.");
            var next = lab.Reply(command);
            if (DeferCastObservation) {
                deferredCastObservation = next with { Revision = current.Revision + 2 };
                Replace(current with {
                    Revision = current.Revision + 1, Status = "resolving", Decision = null,
                    PriorityPlayerId = null, DecisionOwnerId = null, ActionClass = "resolving",
                    Players = current.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                        Cards = zone.Cards.Select(card => card with { Key = "", Selectable = false }).ToImmutableArray()
                    }).ToImmutableArray() }).ToImmutableArray(),
                    PlayAccesses = current.PlayAccesses.Select(access => new PlayAccess(access.PlayerId, access.Zone, access.CardId,
                        access.Routes.Select(route => route with { CanStart = false }).ToImmutableArray())).ToImmutableArray()
                });
            } else Replace(next);
            Commands.Enqueue(command);
            return Task.CompletedTask;
        }
        internal GameSnapshot CompleteCastObservation()
        {
            var next = deferredCastObservation ?? throw new InvalidOperationException("No cast observation is deferred.");
            deferredCastObservation = null;
            return next;
        }
        internal GameSnapshot Pass() => lab.Reply(new PassPriority(lab.State.Id, lab.State.Decision!.Id));
    }
}
