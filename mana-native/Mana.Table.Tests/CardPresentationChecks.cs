using Mana.Table.Interaction;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
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
        CurrentInteractionState(check);
        OpeningHandReading(check);
        ProviderNeutralReading(check);
        var first = new Card { Key = "first-key", VisualId = "first", Name = "First" };
        var second = new Card { Key = "second-key", VisualId = "second", Name = "Second" };
        var state = Snapshot(first, second);
        var context = new CardPresentationContext(state, "table:priority", false, false, false, false);
        var owner = new CardPresentationController();
        var firstRest = new Pose(new(600, 825), new(137, 192), -.2f);
        var secondRest = new Pose(new(720, 825), new(137, 192), .2f);

        CardPresentationRequest Request(Card card, Pose pose, string zone = "Hand") => new(card.VisualId,
            card, pose, zone, 0, "card:" + card.Key, UiIntent.None);

        owner.BeginFrame(context, null, .016f, true);
        var normal = owner.Prepare(Request(first, firstRest));
        owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        owner.BeginFrame(context, normal.Hit.Id, .016f, true);
        var hovered = owner.Prepare(Request(first, firstRest));
        owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        check(hovered.Hovered && !hovered.Focused && hovered.Emphasized && !owner.HasPinnedCard && hovered.Display == firstRest
            && hovered.Layer == CardPresentationLayer.Table,
            "Card preparation highlights hover without opening a reader, moving its face, or dispatching an action");

        check(owner.Toggle(hovered.Hit, context) && owner.HasPinnedCard,
            "An explicit card click pins presentation without dispatching the underlying game action");
        // The opening frame first restores any old reader to its hand slot;
        // reconciliation then places the new reader at its settled target.
        owner.BeginFrame(context, null, .016f, true);
        owner.Prepare(Request(first, firstRest));
        owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        owner.BeginFrame(context, null, .016f, true);
        var pinned = owner.Prepare(Request(first, firstRest));
        var adjacent = owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        check(pinned.Focused && !pinned.Hovered && pinned.Display.Size == new Vector2(410, 574)
            && pinned.Hit.Contains(firstRest.Center.ToPoint()) && pinned.Hit.Contains(pinned.Display.Center.ToPoint())
            && owner.ReaderBounds == pinned.Display.Bounds,
            "A prepared reader shares its displayed pose with input and retains its original hand anchor");
        var browsing = owner.ResolveHit(secondRest.Center.ToPoint(), new[] { adjacent.Hit, pinned.Hit }, pinned.Hit, context);
        check(browsing?.Id == adjacent.Hit.Id,
            "The original adjacent hand slot stays reachable underneath a pinned reader");
        check(pinned.Layer == CardPresentationLayer.Reader && adjacent.Layer == CardPresentationLayer.Table,
            "A prepared reader retains its lifted layer while the adjacent hand card stays on the table");

        owner.BeginFrame(context, pinned.Hit.Id, .016f, true);
        var hoveredReader = owner.Prepare(Request(first, firstRest));
        owner.Prepare(Request(second, secondRest)); owner.EndFrame();
        check(hoveredReader.Focused && hoveredReader.Hovered && hoveredReader.Display == pinned.Display,
            "Pointer hover is separate from the pinned reading state and does not move the reader or invoke its action");

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
            new(new(650, 450), new(120, 168)), "Choice", null, "choice:0", UiIntent.None, ChoiceSlot: 0);
        owner.BeginFrame(reveal, null, .016f, true);
        var choice = owner.Prepare(choiceRequest); owner.EndFrame();
        check(owner.Toggle(choice.Hit, reveal) && owner.RevealedEntry(anonymous, reveal)?.ChoiceSlot == 0
            && owner.RevealedEntry(anonymous with { }, reveal) == null,
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
        check(!owner.HasPinnedCard && fresh.Display == firstRest,
            "Changing games clears presentation identity, pinned readers, and motion without executing a card action");

        // The layout study reads a copy while keeping the controller's actual
        // occurrence clickable. The space between those faces is not a card.
        owner.Reset();
        var study = context with { KeepBattlefieldAnchor = true,
            State = state with { Players = [state.Viewer! with { Zones = [new("Battlefield", 1, [first], null)] }] } };
        var battlefieldRest = new Pose(new(400, 300), new(112, 104));
        owner.BeginFrame(study, null, .016f, true);
        var battlefield = owner.Prepare(Request(first, battlefieldRest, "Battlefield")); owner.EndFrame();
        bool opened = owner.Toggle(battlefield.Hit, study);
        for (int frame = 0; frame < 2; frame++) {
            owner.BeginFrame(study, null, .016f, true);
            battlefield = owner.Prepare(Request(first, battlefieldRest, "Battlefield")); owner.EndFrame();
        }
        var betweenFaces = Vector2.Lerp(battlefieldRest.Center, battlefield.Display.Center, .5f).ToPoint();
        check(opened && battlefield.Focused && battlefield.Display.Size == new Vector2(410, 574)
            && battlefield.Display.Center.X > 800 && battlefield.Rest == battlefieldRest
            && battlefield.Hit.Contains(battlefieldRest.Center.ToPoint()) && battlefield.Hit.Contains(battlefield.Display.Center.ToPoint())
            && battlefield.Hit.Bounds.Contains(betweenFaces) && !battlefield.Hit.Contains(betweenFaces),
            "Study inspection keeps its original battlefield anchor and opposite-side full reader clickable, leaves their intervening space clear, and submits no game action");
        var anchorHit = owner.ResolveHit(battlefieldRest.Center.ToPoint(), [battlefield.Hit], battlefield.Hit, study);
        check(anchorHit is { } currentAnchor && currentAnchor.Id == battlefield.Hit.Id
            && owner.Toggle(currentAnchor, study) && !owner.HasPinnedCard,
            "A second click on the study's actual battlefield occurrence closes reading without activating its underlying action");
    }

    private static void OpeningHandReading(Action<bool, string> check)
    {
        var card = new Card { Key = "opening-card", VisualId = "opening-face", Name = "Forest", Type = "Basic Land" };
        var state = Snapshot(card) with { Turn = 0, Decision = new() { Id = "keep-hand", Kind = "input",
            Purpose = DecisionPurpose.OpeningHand, Intent = DecisionIntent.General, OkEnabled = true, CancelEnabled = true } };
        var context = new CardPresentationContext(state, "keep-hand", false, false, false, false);
        var owner = new CardPresentationController();

        var request = new CardPresentationRequest(card.VisualId, card, new(new(700, 825), new(137, 192)),
            "Hand", state.ViewerId, "opening-hit", UiIntent.None);
        owner.BeginFrame(context, null, .016f, true);
        var hand = owner.Prepare(request); owner.EndFrame();
        check(owner.Toggle(hand.Hit, context) && owner.HasPinnedCard,
            "The engine's exact keep/mulligan prompt permits read-only browsing of the viewer's authorized hand without an action");
        for (int frame = 0; frame < 2; frame++) {
            owner.BeginFrame(context, null, .016f, true);
            hand = owner.Prepare(request); owner.EndFrame();
        }
        check(hand.Focused && hand.Display.Size == new Vector2(410, 574) && hand.Hit.Contains(hand.Rest.Center.ToPoint()),
            "Opening-hand inspection uses the normal full-card reader and preserves the original hand anchor without keeping, redrawing or playing");

        var bottomCard = card with { Key = "current-bottom-card", Selectable = true };
        var bottom = context with { Scope = "bottom-hand", CompactDecision = true, State = state with { Players = Snapshot(bottomCard).Players,
            Decision = state.Decision! with { Id = "bottom-hand", Purpose = DecisionPurpose.Unspecified, Intent = DecisionIntent.Selection } } };
        owner.BeginFrame(bottom, null, .016f, true);
        var candidate = owner.Prepare(request with { Card = bottomCard }); owner.EndFrame();
        var selection = new InteractionController().CardClick(bottom.State!, bottomCard);
        check(!owner.HasPinnedCard && !candidate.Focused && owner.Toggle(candidate.Hit, bottom)
                && selection.Reply is SelectCard { DecisionId: "bottom-hand", Card: "current-bottom-card" },
            "Entering hand selection closes the old opening reader but permits fresh inspection; only the separate selection action retains the current engine command");
        for (int frame = 0; frame < 2; frame++) {
            owner.BeginFrame(bottom, null, .016f, true);
            candidate = owner.Prepare(request with { Card = bottomCard }); owner.EndFrame();
        }
        check(candidate.Focused && candidate.Display.Size == new Vector2(410, 574) && owner.Toggle(candidate.Hit, bottom) && !owner.HasPinnedCard,
            "A compact hand-selection decision preserves the full reader across frames and a second card click only collapses it");

        owner.BeginFrame(context, null, .016f, true);
        var otherZone = owner.Prepare(request with { Zone = "Command" }); owner.EndFrame();
        check(!owner.Toggle(otherZone.Hit, context),
            "The opening-hand reader exception does not extend to other zones during a general engine decision");
        var unknown = context with { State = state with { Decision = state.Decision! with { Purpose = DecisionPurpose.Unspecified } } };
        owner.BeginFrame(unknown, null, .016f, true);
        var otherPrompt = owner.Prepare(request); owner.EndFrame();
        check(!owner.Toggle(otherPrompt.Hit, unknown),
            "An unrelated general decision does not inherit keep/mulligan hand-browsing permissions");
    }

    private static void ProviderNeutralReading(Action<bool, string> check)
    {
        var top = new Card { Key = "offered-top-key", VisualId = "disclosed-top", Name = "A disclosed source", Selectable = true };
        foreach (bool capability in new[] { false, true })
        foreach (var intent in new[] { DecisionIntent.Priority, DecisionIntent.Mana })
        foreach (string zone in new[] { "Library", "Disclosed reserve" }) {
            var state = Snapshot(top);
            state = state with { Players = [state.Viewer! with { Zones = [new(zone, 10, [], top)] }],
                Decision = state.Decision! with { Intent = intent } };
            var context = new CardPresentationContext(state, state.Id + ":" + state.Decision!.Id,
                false, false, false, false, SourceActions: capability);
            var owner = new CardPresentationController();

            var request = new CardPresentationRequest(top.VisualId, top, new(new(700, 300), new(80, 112)),
                zone, 0, "top-card", UiIntent.None, OpensZone: true);
            owner.BeginFrame(context, null, .016f, true);
            var entry = owner.Prepare(request); owner.EndFrame();
            check(entry.Hit.Zone == zone && ReferenceEquals(context.CurrentOccurrence(entry), top) && context.CanRead(entry)
                    && owner.Toggle(entry.Hit, context),
                $"A disclosed {zone} top is readable during {intent} independently of action-query capability ({capability}), without committing input");
            var revoked = context with { State = state with { Players = [state.Viewer! with { Zones = [new(zone, 10, [], null)] }],
                Decision = state.Decision with { SourceCard = top, SourceCardId = top.VisualId } } };
            owner.BeginFrame(revoked, null, .016f, true); owner.EndFrame();
            check(!owner.HasPinnedCard && !revoked.CanRead(entry) && revoked.CurrentOccurrence(entry) == null,
                "A retained historical source cannot restore a reader after its actual disclosed top/location disappears");
        }

        var ability = Snapshot(top);
        ability = ability with { Decision = new() { Id = "ability-with-preview", Kind = "choice", Intent = DecisionIntent.General,
            Purpose = DecisionPurpose.AbilitySelection, Min = 1, Max = 1, SourceCard = top, SourceCardId = top.VisualId,
            Choices = [new(17, "Use the offered ability", top) { CardId = top.VisualId, PlayerId = 0 }] } };
        check(!TableChoices.IsDirect(ability)
                && DecisionDockPresentation.From(ability, new()).Status == DecisionDockStatus.AbilityChoice,
            "Explicit ability-selection semantics retain their interaction route when a provider supplies card/player preview metadata");
    }

    private static void CurrentInteractionState(Action<bool, string> check)
    {
        var reported = new Card { Key = "old-action-key", VisualId = "live-body", CombatId = "combat-body", Name = "Same name",
            Type = "Creature", Power = 1, Toughness = 1, CombatKeywords = ["Flying"] };
        var current = reported with { Key = "current-action-key", Power = 6, Toughness = 6, Damage = 1,
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2), States = [new("ward", "Ward {2}", "ability", null, null, "Pay {2}.", "ward")] };
        var otherCopy = reported with { VisualId = "other-body", CombatId = "other-combat", Power = 10, Toughness = 10,
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 9) };
        var state = Snapshot(current, otherCopy);
        var resolved = CardViews.Current(state, reported);
        check(ReferenceEquals(resolved, current) && AllocationGuide.Stats(resolved) == "6/6"
            && resolved.Counters["+1/+1"] == 2 && AllocationGuide.MarkedDamage(resolved) == "1 damage marked"
            && reported.Power == 1 && reported.Key == "old-action-key",
            "Interaction state binds the exact current occurrence without adding counters to authoritative P/T or replacing its original action handle");
        check(AllocationGuide.Abilities(resolved) == "Ward {2}"
            && AllocationGuide.Abilities(current with { States = [] }) == "",
            "Allocation state preserves effective ability qualifiers and explicit removal instead of reviving legacy keyword hints");

        var choice = new Choice(37, "Choose this recipient", reported) { CardId = current.VisualId, Id = "recipient-7", Minimum = 1, Maximum = 7, Lethal = 4 };
        var decision = new Decision { Id = "allocation-current", Kind = "allocate", Amount = 7,
            SourceCardId = current.CombatId, SourceCard = reported, Choices = [choice] };
        var prompt = state with { Decision = decision };
        var reply = AllocationGuide.Reply(prompt, [7]);
        check(ReferenceEquals(CardViews.Choice(prompt, choice), current)
            && ReferenceEquals(CardViews.Source(prompt, decision), current)
            && ReferenceEquals(choice.Card, reported) && ReferenceEquals(decision.SourceCard, reported)
            && choice is { Index: 37, Id: "recipient-7", Minimum: 1, Maximum: 7, Lethal: 4 }
            && reply is AllocateAmounts { DecisionId: "allocation-current" } && reply.Amounts!.SequenceEqual([7]),
            "Current source and recipient display leaves original prompt indexes, handles, thresholds and scoped allocation commands intact");

        var anonymous = new Card { Name = reported.Name, Type = "Creature", Power = 3, Toughness = 3 };
        var removed = reported with { VisualId = "reported-only", CombatId = "reported-combat" };
        check(ReferenceEquals(CardViews.Current(state, anonymous), anonymous)
            && ReferenceEquals(CardViews.Current(state, removed), removed)
            && ReferenceEquals(CardViews.Choice(state, choice with { Card = null }), current),
            "Same-name and anonymous reported faces are never rebound by name, while an explicit engine occurrence link can supply a currently disclosed face");

        var masked = current with { FaceDown = true, Name = "Face-down card", ArtName = "", Text = "", OtherFace = null,
            Power = 2, Toughness = 2, Counters = ImmutableDictionary<string, int>.Empty.Add("stun", 1) };
        var hidden = prompt with { Players = Snapshot(masked, otherCopy).Players,
            Decision = decision with { Kind = "reveal", Choices = [choice] } };
        var hiddenDisplay = CardViews.Choice(hidden, choice)!;
        var context = new CardPresentationContext(hidden, "current:reveal", true, true, false, false);
        check(ReferenceEquals(hiddenDisplay, masked) && context.ChoiceFace(0) is { FaceDown: true }
            && CardPresentation.Artwork(hiddenDisplay) == null && hiddenDisplay.Power == 2
            && hiddenDisplay.Counters["stun"] == 1 && hiddenDisplay.Text.Length == 0,
            "A current face-down projection revokes an older revealed face while retaining only its authorized public characteristics and counters");
    }

    private static GameSnapshot Snapshot(params Card[] hand) => new("card-presentation", "playing", null, null,
        0, 1, "Main phase", "MAIN1", 0,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Hand", hand.Length, hand.ToImmutableArray(), null)])], [], null,
        new() { Id = "priority", Kind = "input", Intent = DecisionIntent.Priority });
}
