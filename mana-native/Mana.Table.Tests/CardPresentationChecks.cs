using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Hit = Mana.Table.Interaction.TableHit;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class CardPresentationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var first = new Card { Key = "first-key", VisualId = "first", Name = "First" };
        var second = new Card { Key = "second-key", VisualId = "second", Name = "Second" };
        var state = Snapshot(first, second);
        var context = new CardPresentationContext(state, "table:priority", false, false, false, false);
        var owner = new CardPresentationController();
        var firstRest = new Pose(new(600, 825), new(137, 192), -.2f);
        var secondRest = new Pose(new(720, 825), new(137, 192), .2f);
        int actions = 0;
        CardPresentationRequest Request(Card card, Pose pose, string zone = "Hand") => new(card.VisualId,
            card, pose, zone, 0, "card:" + card.Key, () => actions++);

        owner.BeginFrame(context, null, .016f, true);
        var normal = owner.Prepare(Request(first, firstRest));
        owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        owner.BeginFrame(context, normal.Hit.Id, .016f, true);
        var hovered = owner.Prepare(Request(first, firstRest));
        owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        check(hovered.Emphasized && !owner.HasPinnedCard && hovered.Display == firstRest
            && hovered.Layer == CardPresentationLayer.Table && actions == 0,
            "Card preparation highlights hover without opening a reader, moving its face, or dispatching an action");

        check(owner.Toggle(hovered.Hit, context) && owner.HasPinnedCard && actions == 0,
            "An explicit card click pins presentation without dispatching the underlying game action");
        // The opening frame first restores any old reader to its hand slot;
        // reconciliation then places the new reader at its settled target.
        owner.BeginFrame(context, null, .016f, true);
        owner.Prepare(Request(first, firstRest));
        owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        owner.BeginFrame(context, null, .016f, true);
        var pinned = owner.Prepare(Request(first, firstRest));
        var adjacent = owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        check(pinned.Focused && pinned.Display.Size == new Vector2(410, 574)
            && pinned.Hit.Contains(firstRest.Center.ToPoint()) && pinned.Hit.Contains(pinned.Display.Center.ToPoint())
            && owner.ReaderBounds == pinned.Display.Bounds,
            "A prepared reader shares its displayed pose with input and retains its original hand anchor");
        var browsing = owner.ResolveHit(secondRest.Center.ToPoint(), new[] { adjacent.Hit, pinned.Hit }, pinned.Hit, context);
        check(browsing?.Id == adjacent.Hit.Id,
            "The original adjacent hand slot stays reachable underneath a pinned reader");
        check(owner.TakeDeferred().Select(card => card.Id).SequenceEqual([first.VisualId])
            && owner.TakeDeferred().Count == 0,
            "A prepared lifted card is emitted once and draining its layer cannot repaint it");

        owner.Toggle(pinned.Hit, context);
        owner.BeginFrame(context, null, .016f, false);
        var collapsed = owner.Prepare(Request(first, firstRest)); owner.EndFrame();
        check(!owner.HasPinnedCard && collapsed.Display == firstRest
            && collapsed.Layer == CardPresentationLayer.Table && owner.ReaderBounds == null,
            "Clicking the selected card again restores its exact compact geometry before the next adjacent click");

        owner.Toggle(collapsed.Hit, context);
        owner.BeginFrame(context with { CompactDecision = true }, null, .016f, true);
        var compact = owner.Prepare(Request(first, firstRest)); owner.EndFrame();
        check(!owner.HasPinnedCard && !compact.Focused
            && !owner.Toggle(compact.Hit, context with { CompactDecision = true }),
            "Required compact choices revoke an existing reader and cannot open another one");

        owner.BeginFrame(context, null, .016f, true);
        var readable = owner.Prepare(Request(first, firstRest)); owner.EndFrame();
        owner.Toggle(readable.Hit, context);
        var revoked = context with { State = Snapshot(first with { FaceDown = true, Name = "" }, second) };
        owner.BeginFrame(revoked, null, .016f, true);
        var hidden = owner.Prepare(Request(first with { FaceDown = true, Name = "" }, firstRest)); owner.EndFrame();
        check(!owner.HasPinnedCard && !hidden.Focused && !hidden.Emphasized
            && !owner.Toggle(readable.Hit, revoked),
            "Current visibility revokes a reader even when a hidden occurrence retains the same presentation identity");

        // Revealed choices have no persistent card id. Their authority is the
        // original decision slot, including when an obsolete face is clicked.
        var anonymous = new Card { Name = "Revealed face" };
        var revealState = state with { Decision = new() { Id = "reveal-1", Kind = "reveal", Choices = [new(0, "Reveal", anonymous)] } };
        var reveal = context with { State = revealState, Scope = "table:reveal-1", OverlayOpen = true, GalleryOpen = true };
        var choiceRequest = new CardPresentationRequest("choice-face:reveal-1:0", anonymous,
            new(new(650, 450), new(120, 168)), "Choice", null, "choice:0", () => actions++, ChoiceSlot: 0);
        owner.BeginFrame(reveal, null, .016f, true);
        var choice = owner.Prepare(choiceRequest); owner.EndFrame();
        check(owner.Toggle(choice.Hit, reveal) && owner.RevealedEntry(anonymous, reveal)?.ChoiceSlot == 0
            && owner.RevealedEntry(anonymous with { }, reveal) == null && actions == 0,
            "Anonymous revealed faces are read through their original scoped slot and never guessed from matching card text");
        var nextDecision = reveal with { Scope = "table:reveal-2", State = revealState with { Decision = revealState.Decision! with { Id = "reveal-2" } } };
        owner.BeginFrame(nextDecision, null, .016f, true);
        owner.Prepare(choiceRequest with { Id = "choice-face:reveal-2:0" }); owner.EndFrame();
        check(!owner.HasPinnedCard && !owner.Toggle(choice.Hit, nextDecision)
            && owner.RevealedEntry(anonymous, reveal) == null,
            "Ending a reveal decision revokes its old reader and prevents obsolete scoped hits from reading a later decision");

        // Hit envelopes must not turn projected/rotated empty corners into
        // cards, including after the controller prepares them without a GPU.
        owner.Reset(); owner.BeginFrame(context, null, .016f, true);
        var rotated = owner.Prepare(Request(first, new(new(500, 400), new(80, 140), .65f), "Battlefield"));
        owner.EndFrame();
        check(rotated.Hit.Contains(rotated.Display.Center.ToPoint())
            && !rotated.Hit.Contains(new(rotated.Hit.Bounds.Left + 1, rotated.Hit.Bounds.Top + 1)),
            "Prepared input tests the exact rotated face instead of treating its rectangular envelope as painted content");
        owner.BeginFrame(context, null, .016f, true); owner.EndFrame();
        check(!owner.TryGetPose(first.VisualId, out _),
            "Card reconciliation removes unseen motion instead of retaining a revoked or departed face");

        owner.BeginFrame(context, null, .016f, true);
        var source = owner.Prepare(Request(first, firstRest)); owner.EndFrame(); owner.Toggle(source.Hit, context);
        owner.BeginFrame(context with { State = state with { Id = "different-game" } }, null, .016f, true);
        var fresh = owner.Prepare(Request(first, firstRest)); owner.EndFrame();
        check(!owner.HasPinnedCard && fresh.Display == firstRest && actions == 0,
            "Changing games clears presentation identity, pinned readers, and motion without executing a card action");
    }

    private static GameSnapshot Snapshot(params Card[] hand) => new("card-presentation", "playing", null, null,
        0, 1, "Main phase", "MAIN1", 0,
        [new(0, "You", 40, true, false, [], [new("Hand", hand.Length, hand, null)])], [], null,
        new() { Id = "priority", Kind = "input", Intent = DecisionIntent.Priority });
}
