using System.Collections.Immutable;
using Mana.Contracts;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckChoiceEligibility(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        var pool = new Card { VisualId = "search-pool", Name = "Breeding Pool", Type = "Land — Forest Island" };
        var forest = new Card { VisualId = "search-forest", Name = "Forest", Type = "Basic Land — Forest" };
        var wastes = new Card { VisualId = "search-wastes", Name = "Wastes", Type = "Basic Land" };
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
        run.ClickHit("choices-disclosure");
        check(Faces().ToHashSet().SetEquals(new[] { pool.VisualId, ring.VisualId, forest.VisualId, wastes.VisualId })
                && run.View.Selections.SequenceEqual([42]) && run.Engine.Replies.Count == 0
                && run.Frame.Cards.Single(card => card.Card.VisualId == wastes.VisualId).Id == original.Id,
            "Show all cards reveals only the already disclosed context and preserves staged choices and reader identity");
        run.ClickCard(run.Frame.Cards.Single(card => card.Card.VisualId == pool.VisualId).Id, right: true);
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
    }
}
