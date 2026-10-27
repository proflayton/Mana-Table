using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class CardIdentityBoundaryRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var face = JsonSerializer.Deserialize<Card>("""
            {"Key":null,"VisualId":null,"CombatId":null,"Name":"Read-only final face","Type":"Creature","Power":3,"Toughness":4}
            """)!;
        check(face.Key == "" && face.VisualId == "" && face.CombatId == "" && CardViews.Identity(face) == ""
                && face.Name == "Read-only final face" && face.Power == 3 && face.Toughness == 4,
            "A decoded legacy or terminal read-only card preserves its face and canonical empty identities without inventing an occurrence");
        var copied = new Card { Key = "old-key", VisualId = "old-visual", CombatId = "old-combat" }
            with { Key = null!, VisualId = null!, CombatId = null! };
        check(copied.Key == "" && copied.VisualId == "" && copied.CombatId == ""
                && JsonSerializer.Deserialize<Card>(JsonSerializer.Serialize(face)) == face,
            "Record initialization, record copies and JSON round trips keep absent card identity fields non-null");
        var identified = new Card { Key = "scope:key", VisualId = "opaque:visual", CombatId = "opaque:combat" };
        check(JsonSerializer.Deserialize<Card>(JsonSerializer.Serialize(identified)) == identified,
            "Canonical absence leaves every actual opaque source, visual and combat identifier unchanged");
        var source = identified with { Name = "Current source" };
        var state = new GameSnapshot("identity-boundary", "playing", null, null, 0, 2, "Main", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [source], null)]),
             new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [face], null)])], [], null,
            new("current", new(), new PriorityPayload(new("Pass", true)))) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0
            };
        check(!GameCommandPermissions.Allows(state, new ActivateCard(state.Id, state.Decision!.Id, face.Key))
                && !GameCommandPermissions.Allows(state, new UseOfferedAction(state.Id, state.Decision.Id, face.Key, "invented-route")),
            "Canonical empty display identities cannot supply source action or activation authority");
        var node = new ActionOperation(0, ActionEffectKind.Exile) {
            ActorPlayerId = 0,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
                [new("source", SemanticEntityKind.Card, source.VisualId, ControllerId: 0)]),
            Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []),
            Targets = new(SemanticKnowledge.Known, 1, 1,
                [new("missing-current-target", SemanticEntityKind.Card, "retired-or-undisclosed-occurrence", ControllerId: 1)])
        };
        bool rejected = false;
        try { _ = new AiDecisionFrame(state, domain: new(0, ActionDomainCoverage.Complete, [], []),
            semantics: new() { OperationGraph = new(SemanticKnowledge.Unknown, 0, [node]), OperationId = 0 }); }
        catch (ArgumentException) { rejected = true; }
        check(rejected,
            "Normalizing a read-only face cannot make a missing current operation reference pass semantic validation");
    }
}
