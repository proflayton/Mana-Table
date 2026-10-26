using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;

internal static class HandPlacementRegression
{
    public static void Run(Action<bool, string> check)
    {
        var forest = new Card { Name = "Forest", Type = "Basic Land — Forest", VisualId = "forest", Key = "prompt:forest" };
        var other = forest with { VisualId = "other", Key = "prompt:other" };
        var source = new Card { Name = "Gaea's Touch", Type = "Enchantment", VisualId = "touch", Key = "prompt:touch", Selectable = true };
        var placement = new PriorityAbility("place-forest", source.Key, source.Name, "Put a basic Forest from hand onto the battlefield.", false) {
            EligibleHandKeys = [forest.Key, other.Key]
        };
        var viewer = new Player(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Hand", 2, [forest, other], null), new("Battlefield", 1, [source], null)]) {
            LandPlaysUsed = 1, LandPlayLimit = 1
        };
        var state = new GameSnapshot("placement-game", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0,
            [viewer], [], null, new Decision("placement-prompt", new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", false), null, CanAutoPass: false, QuietYieldToken: "", Abilities: [placement])));
        var click = new InteractionController();
        check(HandPlacements.Availability(state, forest) == "1 placement available" && HandPlacements.AllOptions(state).Length == 1,
            "One source supplying two eligible Forests is one available placement, not two land plays");
        var reply = InteractionController.Play(state, forest);
        check(reply is PlaceFromHand { GameId: "placement-game", DecisionId: "placement-prompt", Card: "prompt:forest", AbilityId: "place-forest" }
            && click.CardClick(state, forest).Reply == reply, "Blocked normal land play can use the exact projected placement through the same scoped click and drag command");
        reply!.Validate();
        var ignored = state with { Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision!.Payload) with { Abilities = [placement with { IgnoredUntilNextTurn = true }] } } };
        check(HandPlacements.Options(ignored, forest).Length == 1, "An Auto-pass preference does not disable a legal placement");
        var noPlacement = state with { Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision!.Payload) with { Abilities = [] } } };
        check(InteractionController.Play(noPlacement, forest) == null && click.CardClick(noPlacement, forest).Effect == InteractionEffect.Inspect,
            "Printed text and card names never invent a missing placement action");
        check(InteractionController.Play(state, forest with { VisualId = "retired" }) == null
            && HandPlacements.Start(state, forest, "sacrifice-for-mana") == null, "A retired hand occurrence or different ability cannot borrow a placement command");
        var changed = state with { Decision = state.Decision! with { Id = "next", Payload = ((PriorityPayload)state.Decision!.Payload) with { Abilities = [placement with { EligibleHandKeys = ["next:forest"] }] } } };
        check(InteractionController.Play(changed, forest) == null, "Old hand handles cannot match new-prompt eligibility");
        check(HandPlacements.Options(state with { Decision = state.Decision! with { Payload = new EntitySelectionPayload(new("Confirm", false), new("Cancel", false), []) } }, forest).Length == 0
            && HandPlacements.Options(state with { Status = "finished" }, forest).Length == 0, "Placement hints disappear outside their current live priority window");
        check(HandPlacements.Options(state with { Players = [viewer with { Zones = [new("Hand", 2, [forest, other], null)] }] }, forest).Length == 0,
            "A source leaving the battlefield revokes the placement affordance");
        var secondSource = source with { VisualId = "touch-2", Key = "prompt:touch-2" };
        var direct = new PlaceFromHand(state.Id, state.Decision!.Id, forest.Key, placement.Id);
        foreach (var forbidden in new[] {
            state with { Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision!.Payload) with { Abilities = [placement with { HandPlacementLimit = 0 }] } } },
            state with { Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision!.Payload) with { Abilities = [placement with { SourceCardKey = "departed-source" }] } } },
            state with { Players = [viewer with { Zones = [new("Hand", 2, [forest, other], null), new("Battlefield", 1, [source with { PhasedOut = true }], null)] }] },
            state with { Players = [viewer with { Zones = [new("Hand", 2, [forest, other], null), new("Battlefield", 1, [source with { FaceDown = true }], null)] }] },
            state with { Players = [viewer with { Zones = [new("Hand", 2, [forest with { FaceDown = true }, other], null), new("Battlefield", 1, [source], null)] }] }
        }) check(!GameCommandPermissions.Allows(forbidden, direct) && HandPlacements.Options(forbidden, forest).Length == 0,
            "Direct placement dispatch and its gesture share the live disclosed-source and remaining-limit gate");
        var two = state with { Players = [viewer with { Zones = [new("Hand", 2, [forest, other], null), new("Battlefield", 2, [source, secondSource], null)] }],
            Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision!.Payload) with { Abilities = [placement, placement with { Id = "place-2", SourceCardKey = secondSource.Key }] } } };
        check(HandPlacements.Availability(two, forest) == "2 placements available" && InteractionController.Play(two, forest) == null
            && click.CardClick(two, forest).Effect == InteractionEffect.ChooseHandPlacement,
            "Multiple identical sources remain separate choices instead of silently spending either one");
        var normalForest = forest with { Selectable = true };
        var normal = state with { Players = [viewer with { Zones = [new("Hand", 1, [normalForest], null), new("Battlefield", 1, [source], null)] }] };
        check(InteractionController.Play(normal, normalForest) is PlayCard
            && HandPlacements.Start(normal, normalForest, placement.Id) is PlaceFromHand,
            "Normal play remains the default when available and the placement remains an explicit alternative");
        check(ControlGuidance.Card(state, forest, "Hand") is { Title: "1 placement available", Key: "hand-play" },
            "The eligible hand card explains its available placement at the point of interaction");
        try { new PlaceFromHand(state.Id, "", Card: forest.Key, AbilityId: placement.Id).Validate(); check(false, "Unscoped placement was accepted"); }
        catch (ArgumentException) { check(true, "Hand placements require a current decision scope"); }
    }
}
