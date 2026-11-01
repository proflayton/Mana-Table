using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckChoiceEligibility(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        var pool = new Card { VisualId = "search-pool", Name = "Breeding Pool", Type = "Land — Forest Island" };
        var forest = new Card { VisualId = "search-forest", Name = "Forest", Type = "Basic Land — Forest" };
        var wastes = new Card { VisualId = "search-wastes", Name = "Wastes", Type = "Basic Land", Text = "{T}: Add {C}." };
        var ring = new Card { VisualId = "search-ring", Name = "Sol Ring", Type = "Artifact" };
        var selection = new ChoiceSelection([new(17, forest.Name, forest), new(42, wastes.Name, wastes)], new(0, 2),
            [new(null, pool.Name, pool), new(null, ring.Name, ring), new(17, forest.Name, forest), new(42, wastes.Name, wastes)]);
        var state = Example() with { Decision = new("basic-land-search", new DecisionContext(Title: "Search your library",
            Message: "Choose up to two basic land cards."), new OptionsPayload(selection)) };
        using var run = new Runner(profile, "condition-filtered-card-choices", state, trace, performance);
        run.Tick("eligible-cards-first");
        string[] Faces() => run.Frame.Cards.Where(card => card.Zone == "Choice").Select(card => card.Card.VisualId).ToArray();
        check(Faces().ToHashSet().SetEquals(new[] { forest.VisualId, wastes.VisualId })
                && run.View.Selections.Count == 0 && run.Engine.Replies.Count == 0,
            "A basic-land search opens with only the engine's eligible Forest and Wastes, without choosing either one");
        run.ClickHit("choice:42");
        check(run.View.Selections.SequenceEqual([42]) && run.Engine.Replies.Count == 0,
            "Filtering retains original sparse option indexes and stages selection without resolving the search");
        var original = run.Frame.Cards.Single(card => card.Card.VisualId == wastes.VisualId);
        check(original.ChoiceSlot == 3, "A filtered library face retains its original disclosure slot for reading");
        var read = run.Hit("choice-read:3");
        check(read.Intent is MatchIntent.ReadChoice { Slot: 3 }
                && !read.Bounds.Intersects(original.Hit.Bounds) && run.TryPointFor(read, out _),
            "A selected gallery card exposes a separate reachable Read card gesture bound to its original disclosure slot");
        string prompt = run.View.Match!.Decision!.Id;
        run.ClickHit(read.Id); run.Settle("read-selected-matching-card");
        check(run.View.Overlay == "inspect" && Text(run.Frame).Contains(wastes.Name) && Text(run.Frame).Any(text => text.Contains(wastes.Text))
                && run.View.Selections.SequenceEqual([42]) && run.View.Match?.Decision?.Id == prompt && run.Engine.Replies.Count == 0,
            "The visible Read card action shows the selected card's complete current rules without changing the choice or its sparse index");
        run.ClickHit("close-overlay");
        check(run.View.Overlay.Length == 0 && Faces().ToHashSet().SetEquals(new[] { forest.VisualId, wastes.VisualId })
                && run.Frame.Cards.Single(card => card.Card.VisualId == wastes.VisualId).Id == original.Id
                && run.View.Selections.SequenceEqual([42]) && run.Engine.Replies.Count == 0,
            "Closing explicit card inspection restores the same matching-only gallery and selected original slot");
        run.ClickHit("choices-disclosure");
        check(Faces().ToHashSet().SetEquals(new[] { pool.VisualId, ring.VisualId, forest.VisualId, wastes.VisualId })
                && run.View.Selections.SequenceEqual([42]) && run.Engine.Replies.Count == 0
                && run.Frame.Cards.Single(card => card.Card.VisualId == wastes.VisualId).Id == original.Id,
            "Show all cards reveals only the already disclosed context and preserves staged choices and reader identity");
        check(run.Hit("choice-read:0").Intent is MatchIntent.ReadChoice { Slot: 0 }
                && !run.Frame.Hits.Any(hit => hit.Id == "choice:0"),
            "An already disclosed ineligible card has its own Read card action without acquiring a choice handle");
        run.ClickHit("choice-read:0");
        run.Settle("read-ineligible-card");
        check(run.View.Overlay == "inspect" && Text(run.Frame).Contains(pool.Name)
                && run.View.Selections.SequenceEqual([42]) && run.Engine.Replies.Count == 0,
            "A disclosed nonmatching card remains readable without acquiring a selection handle");
        run.ClickHit("close-overlay");
        run.ClickHit("choices-disclosure");
        check(Faces().ToHashSet().SetEquals(new[] { forest.VisualId, wastes.VisualId })
                && run.Frame.Cards.All(card => !card.Focused) && run.View.Selections.SequenceEqual([42]),
            "Returning to matching cards closes the old reader and keeps the actual staged choice");
        run.ClickHit("choices-disclosure");
        var old = run.Hit("choices-disclosure");
        var point = run.PointFor(old); run.Input(point, left: ButtonState.Pressed);
        run.Present(state); run.Input(point);
        check(!run.View.CurrentHit(old) && Faces().ToHashSet().SetEquals(new[] { forest.VisualId, wastes.VisualId })
                && run.View.Selections.Count == 0 && run.Engine.Replies.Count == 0,
            "A new choice scope restores the eligibility default and rejects a stale filter click");
        var unusual = new ChoiceSelection([new(93, pool.Name, pool)], new(0, 1),
            [new(93, pool.Name, pool), new(null, forest.Name, forest)]);
        run.Present(state with { Decision = state.Decision! with { Payload = new OrderingPayload(unusual),
            Context = new(Message: "Choose an order.") } }); run.Tick("engine-condition-authority");
        check(Faces().SequenceEqual([pool.VisualId]) && run.Frame.Hits.Any(hit => hit.Id == "choice:93"),
            "Other selection and ordering conditions use the engine's offered choices rather than inferring basic-land rules from names or text");
        var empty = new ChoiceSelection([], new(0, 0), [new(null, pool.Name, pool), new(null, ring.Name, ring)]);
        run.Present(state with { Decision = state.Decision! with { Payload = new OptionsPayload(empty) } }); run.Tick("no-eligible-cards");
        check(Faces().Length == 0 && Text(run.Frame).Contains("No cards match this choice.")
                && run.Frame.Hits.Any(hit => hit.Id == "choice-cancel") && run.Engine.Replies.Count == 0,
            "No eligible cards leaves an empty matching view and the explicit Choose none gesture instead of showing invalid candidates or answering automatically");
        run.Present(state with { Decision = state.Decision! with { Payload = new RevealPayload(empty.Disclosures) } }); run.Tick("read-only-reveal");
        check(Faces().ToHashSet().SetEquals(new[] { pool.VisualId, ring.VisualId })
                && run.Frame.Hits.All(hit => hit.Id != "choices-disclosure") && run.Engine.Replies.Count == 0,
            "A read-only reveal continues to show every disclosed card even though none has a choice index");
        CheckChoiceReadDrafts(run, state, check);
    }

    private static void CheckChoiceReadDrafts(Runner run, GameSnapshot state, Action<bool, string> check)
    {
        var choices = Enumerable.Range(0, 12).Select(slot => {
            var card = new Card { Name = slot is 7 or 8 ? "Repeated Relic" : "Relic choice " + slot,
                Type = "Artifact", ManaCost = "{2}", Text = $"Rules for original disclosure slot {slot}. {{T}}: Add {{C}}." };
            return new Choice(101 + slot * 3, "Relic choice " + slot, card);
        }).ToImmutableArray();
        var selection = new ChoiceSelection(choices, new(0, 3),
            choices.Select(choice => new LibraryChoice(choice.Index, choice.Label, choice.Card!)).ToImmutableArray());
        var options = state with { Decision = state.Decision! with { Context = new(Title: "Choose cards", Message: "Put selected cards on the bottom of your library."),
            Payload = new OptionsPayload(selection) } };
        run.Present(options); run.Tick("larger-gallery-read-entry");
        run.ClickHit("choicesfilter"); run.View.TypeText("Relic"); run.Tick("filter-before-reading");
        run.ClickHit("choicesnext"); run.ClickHit("choice:128");
        int[] OriginalSlots() => run.Frame.Cards.Where(card => card.Zone == "Choice").Select(card => card.ChoiceSlot).ToArray();
        check(OriginalSlots().SequenceEqual([7, 8, 9, 10, 11]) && run.View.Selections.SequenceEqual([128])
                && Text(run.Frame).Contains("Relic") && run.Engine.Replies.Count == 0,
            "A typed filter and second gallery page retain their original slots and staged card before reading");
        string currentPrompt = run.View.Match!.Decision!.Id;
        foreach (int slot in new[] { 7, 8 }) {
            var expected = choices[slot].Card!;
            check(run.Hit("choice-read:" + slot).Intent is MatchIntent.ReadChoice read && read.Slot == slot,
                "A same-named card on a later page retains its own exact disclosure slot for inspection");
            run.ClickHit("choice-read:" + slot); run.Settle("read-same-named-slot-" + slot);
            check(run.View.Overlay == "inspect" && Text(run.Frame).Contains(expected.Name)
                    && Text(run.Frame).Any(text => text.Contains(expected.Text))
                    && !Text(run.Frame).Any(text => text.Contains(choices[slot == 7 ? 8 : 7].Card!.Text))
                    && run.View.Selections.SequenceEqual([128]) && run.View.Match?.Decision?.Id == currentPrompt
                    && run.Engine.Replies.Count == 0,
                "Reading same-named gallery cards uses the exact offered slot's rules and never chooses either card");
            run.ClickHit("close-overlay");
            check(OriginalSlots().SequenceEqual([7, 8, 9, 10, 11]) && Text(run.Frame).Contains("Relic")
                    && run.View.Selections.SequenceEqual([128]) && run.Engine.Replies.Count == 0,
                "Returning from a later-page reader preserves its typed filter, page and complete choice draft");
        }
        run.Present(options with { Decision = options.Decision! with { Payload = new OrderingPayload(selection) } });
        run.Tick("ordered-gallery-read-entry"); run.ClickHit("choicesnext");
        run.ClickHit("choice:128"); run.ClickHit("choice:125");
        run.ClickHit("choice-read:8"); run.Settle("read-ordered-choice");
        check(run.View.Overlay == "inspect" && run.View.Selections.SequenceEqual([128, 125]) && run.Engine.Replies.Count == 0,
            "Reading an ordered choice preserves the authoritative indices and their selected order");
        run.ClickHit("close-overlay");
        check(OriginalSlots().SequenceEqual([7, 8, 9, 10, 11]) && run.View.Selections.SequenceEqual([128, 125])
                && run.View.ChoiceOrderBadges[128].Position == 1 && run.View.ChoiceOrderBadges[125].Position == 2,
            "The ordered gallery returns with its original page and numbered choice positions unchanged");
        var staleRead = run.Hit("choice-read:8");
        var point = run.PointFor(staleRead); run.Input(point, left: ButtonState.Pressed, label: "press-old-choice-read");
        run.Present(options); run.Input(point, label: "release-after-choice-replacement");
        check(!run.View.CurrentHit(staleRead) && run.View.Overlay.Length == 0 && run.View.Selections.Count == 0
                && run.Engine.Replies.Count == 0,
            "A Read card gesture captured before prompt replacement cannot inspect the replacement slot on release");
        run.ClickHit("choice-read:0"); run.Settle("reader-before-disclosure-withdrawal");
        check(run.View.Overlay == "inspect", "A current gallery face remains explicitly readable after an older read gesture expires");
        var hidden = choices[0].Card! with { FaceDown = true, Name = "Face-down card", Text = "", Type = "" };
        run.Present(state with { Decision = state.Decision! with { Payload = new RevealPayload([new(null, hidden.Name, hidden)]) } });
        run.Tick("withdraw-read-face");
        check(run.View.Overlay.Length == 0 && !run.Frame.Hits.Any(hit => hit.Id == "choice-read:0")
                && !Text(run.Frame).Any(text => text.Contains(choices[0].Card!.Text)) && run.Engine.Replies.Count == 0,
            "Replacing an inspected disclosure with a face-down card closes its old reader and offers no Read card gesture");
        CheckLongChoiceRules(run, state, check);
    }

    private static void CheckLongChoiceRules(Runner run, GameSnapshot state, Action<bool, string> check)
    {
        const string finalMarker = "FINAL_VISIBLE_RULE_47";
        var card = new Card { Name = "Complete multiline rules reference", Type = "Artifact", ManaCost = "{2}",
            Text = string.Join("\n", Enumerable.Range(0, 48).Select(index => index == 47
                ? finalMarker + ": Last rule." : $"VISIBLE_RULE_{index:D2}: Read me.")) };
        run.Present(state with { Decision = state.Decision! with { Payload = new OptionsPayload(
            new ChoiceSelection([new(91, card.Name, card)], new(0, 1), [])) } });
        run.Tick("multiline-card-choice"); run.ClickHit("choice:91"); run.ClickHit("choice-read:0");
        run.Settle("read-multiline-card");
        var metrics = new TextMetrics();
        var pages = new List<string>();
        string prompt = run.View.Match!.Decision!.Id;
        while (true) {
            var body = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
                .Single(text => text.X == 686 && text.Y == 378
                    && text.Value?.Contains("VISIBLE_RULE_", StringComparison.Ordinal) == true);
            var complete = metrics.TextSize(body.Value!, body.Size, body.Width, 80, body.Bold, run.Frame.Drawing.TextScale);
            var painted = metrics.TextSize(body.Value!, body.Size, body.Width, body.MaxLines, body.Bold, run.Frame.Drawing.TextScale);
            int navigationTop = run.Frame.Hits.Where(hit => hit.Id is "inspect-prev" or "inspect-next")
                .Select(hit => hit.Bounds.Top).DefaultIfEmpty(727).Min();
            check(complete.Y == painted.Y && body.Y + complete.Y <= navigationTop - 16
                    && body.X >= 0 && body.X + complete.X <= 1600 && body.Y >= 0,
                "Each actual multiline rules page paints every measured line above the inspector navigation at enlarged text");
            check(run.View.Overlay == "inspect" && run.View.Match?.Decision?.Id == prompt
                    && run.View.Selections.SequenceEqual([91]) && run.Engine.Replies.Count == 0,
                "Reading successive rules pages preserves the exact card-choice prompt and staged selection without submitting");
            pages.Add(body.Value!);
            var next = run.Frame.Hits.SingleOrDefault(hit => hit.Id == "inspect-next");
            if (next == null) break;
            check(pages.Count < 48, "The real More rules controls advance through a finite multiline card");
            run.ClickHit(next.Id);
        }
        static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        check(pages.Count > 1 && pages[^1].Contains(finalMarker, StringComparison.Ordinal)
                && Normalize(string.Join("\n", pages)) == Normalize(card.Text),
            "Following every More rules control reaches the final rule and preserves all multiline card text without omissions");
        run.ClickHit("close-overlay");
        check(run.View.Overlay.Length == 0 && run.Frame.Hits.Any(hit => hit.Id == "choice-read:0")
                && run.View.Selections.SequenceEqual([91]) && run.Engine.Replies.Count == 0,
            "Returning after the final rules page restores the original selected card and its independent Read card action");
    }
}
