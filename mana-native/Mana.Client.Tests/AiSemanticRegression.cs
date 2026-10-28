using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;

internal static class AiSemanticRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var own = new Card { Key = "source-key", VisualId = "own-id", CombatId = "own-id", Name = "Public source" };
        var enemy = new Card { Key = "enemy-key", VisualId = "enemy-id", CombatId = "enemy-id", Name = "Public target" };
        var state = new GameSnapshot("semantic-game", "playing", null, null, 0, 2, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [own], null)]),
             new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [enemy], null)])],
            [], null, new("input", new(), new PriorityPayload(new("Pass", true)))) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0 };
        var decision = state.Decision!;
        var candidate = new SemanticEntity("target-candidate", SemanticEntityKind.Card, enemy.VisualId, ControllerId: 1, OwnerId: 1);
        var source = new SemanticEntity("source-reference", SemanticEntityKind.Card, own.VisualId, ControllerId: 0, OwnerId: 0);
        var node = new ActionOperation(0, ActionEffectKind.Damage) {
            Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Amount = 3, AmountKnowledge = SemanticKnowledge.Known,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null, [source]),
            Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []),
            Targets = new(SemanticKnowledge.Known, 1, 1, [candidate]) };
        var graph = new ActionOperationGraph(SemanticKnowledge.Known, 0, [node]);
        var metadata = new ActionSemantics("route", ActionEffectKind.Damage, [ActionEffectKind.Damage], [], ActionAffordability.Unknown) {
            OperationGraph = graph };
        DecisionActionDomain Domain(ActionSemantics semantic) => new(1, ActionDomainCoverage.Complete,
            [new(state.Id, decision.Id, own.Key, own.VisualId, [new("route", "activate", "Public action")])], [semantic]);
        bool Rejected(ActionOperationGraph malformed) {
            try { _ = new AiDecisionFrame(state, domain: Domain(metadata with { OperationGraph = malformed })); return false; }
            catch (ArgumentException) { return true; }
        }
        var frame = new AiDecisionFrame(state, domain: Domain(metadata));
        check(frame.Allows(new UseOfferedAction(state.Id, decision.Id, own.Key, "route"))
                && !frame.Allows(new UseOfferedAction(state.Id, decision.Id, candidate.Id, "route"))
                && !frame.Allows(new SelectCard(state.Id, decision.Id, candidate.Id)),
            "Typed target candidates inform assessment without minting source or selection action handles");
        check(new AiDecisionFrame(state, domain: Domain(metadata with { OperationGraph = ActionOperationGraph.Unknown })).Domain.Actions[0].OperationGraph.Operations.IsEmpty,
            "An explicit unknown empty operation graph remains supported without asserting semantic completeness");
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
        var decoded = JsonSerializer.Deserialize<ActionOperationGraph>(JsonSerializer.Serialize(graph, json), json)!;
        check(decoded.Knowledge == SemanticKnowledge.Known && decoded.Operations[0].Recipients.TargetOperationId == 0
                && decoded.Operations[0].Targets!.Candidates[0].ControllerId == 1
                && new AiDecisionFrame(state, domain: Domain(metadata with { OperationGraph = decoded })).Domain.Actions[0].ManaCostKnowledge == SemanticKnowledge.Unknown,
            "The neutral JSON graph preserves recipient references and controller facts while base mana-cost certainty defaults unknown");

        var malformedGraphs = new[] {
            graph with { Operations = default }, graph with { RootId = 42 }, graph with { Operations = [node, node] },
            graph with { Operations = [node with { Knowledge = (SemanticKnowledge)90 }] },
            graph with { Operations = [node with { Links = [new(42, OperationLinkKind.Sequence)] }] },
            graph with { Operations = [node with { Links = [new(0, OperationLinkKind.Sequence)] }] },
            graph with { Operations = [node with { Links = default }] },
            graph with { Operations = [node with { Conditional = true }] },
            graph with { Operations = [node with { Amount = null }] },
            graph with { Operations = [node with { Targets = node.Targets! with { Candidates = default } }] },
            graph with { Operations = [node with { Targets = node.Targets! with { Candidates = [candidate, candidate] } }] },
            graph with { Operations = [node with { Targets = node.Targets! with { Candidates = [candidate with { CardId = "undisclosed-library-occurrence" }] } }] },
            graph with { Operations = [node with { Targets = node.Targets! with { Candidates = [candidate with { Kind = SemanticEntityKind.Spell }] } }] },
            graph with { Operations = [node with { Targets = node.Targets! with { Candidates = [candidate with { ControllerId = 0 }] } }] },
            graph with { Operations = [node with { Recipients = node.Recipients with { TargetOperationId = 99 } }] },
            graph with { Operations = [node with { ActorPlayerId = 99 }] },
            graph with { Operations = [node with { Costs = [new(ActionCostKind.Mana, 1, false, "") { ManaSymbols = default }] }] },
            graph with { Operations = Enumerable.Range(0, 129).Select(index => node with { Id = index }).ToImmutableArray() }
        };
        foreach (var malformed in malformedGraphs)
            check(Rejected(malformed), "Malformed, cyclic, oversized or undisclosed semantic graphs fail closed before policy evaluation");
        bool activeRejected = false;
        try { _ = new AiDecisionFrame(state, domain: Domain(metadata), semantics: new() { OperationGraph = graph, OperationId = 99 }); }
        catch (ArgumentException) { activeRejected = true; }
        check(activeRejected, "A continuation cannot invent an active operation outside its current causal graph");
        var pending = new StackItem("Public source", "A pending action", StackActionKind.ActivatedAbility, own) {
            Id = "pending-1", ControllerId = 0, OperationGraph = graph
        };
        var pendingState = state with { Stack = [pending] };
        check(new AiDecisionFrame(pendingState).State.Stack[0].OperationGraph.Operations[0].Targets!.Candidates.Single().CardId == enemy.VisualId,
            "An AI observation validates the actual chosen pending recipient through the same graph boundary as prospective actions");
        foreach (var invalid in new[] {
            pending with { OperationGraph = graph with { RootId = 77 } },
            pending with { OperationGraph = graph with { Operations = [node with { Targets = node.Targets! with { Min = 0 } }] } },
            pending with { OperationGraph = graph with { Operations = [node with { Costs = [new(ActionCostKind.Life, 1, false, "")] }] } },
            pending with { Card = own with { FaceDown = true } }
        }) {
            bool rejected = false;
            try { _ = new AiDecisionFrame(pendingState with { Stack = [invalid] }); } catch (ArgumentException) { rejected = true; }
            check(rejected, "A malformed, hypothetical, unpaid or concealed pending operation is rejected before AI evaluation");
        }
        var targetState = pendingState with { Decision = decision with { Payload = new OptionsPayload(
            new ChoiceSelection([new(7, "A current pending action", null)], new(0, 1))) } };
        var stackTarget = new SemanticEntity("offered-target", SemanticEntityKind.Spell, ControllerId: 0) { StackItemId = "pending-1" };
        var targetedOptions = new DecisionSemantics { Options = [new(7, ActionEffectKind.Unknown) { Target = stackTarget }] };
        var targetFrame = new AiDecisionFrame(targetState, semantics: targetedOptions);
        check(targetFrame.Allows(new ChooseOptions(targetState.Id, decision.Id, [7]))
                && !targetFrame.Allows(new SelectCard(targetState.Id, decision.Id, stackTarget.StackItemId)),
            "An exact semantic stack target informs the offered option while only its current choice index grants selection authority");
        var departed = targetState with { Stack = [pending with { Card = own with { VisualId = "", CombatId = "" }, OperationGraph = ActionOperationGraph.Unknown }],
            Players = [state.Players[0] with { Zones = [new("Battlefield", 0, [], null)] }, state.Players[1]] };
        check(new AiDecisionFrame(departed, semantics: targetedOptions).Semantics.Options[0].Target!.StackItemId == "pending-1",
            "An actual pending ability stays targetable by stack identity after its source card departs, without inventing a live source occurrence");
        foreach (var invalid in new[] {
            targetedOptions with { Options = [new(7, ActionEffectKind.Unknown) { Target = stackTarget with { StackItemId = "missing" } }] },
            targetedOptions with { Options = [new(7, ActionEffectKind.Unknown) { Target = stackTarget with { CardId = enemy.VisualId } }] },
            targetedOptions with { Options = [new(99, ActionEffectKind.Unknown) { Target = stackTarget }] }
        }) {
            bool rejected = false;
            try { _ = new AiDecisionFrame(targetState, semantics: invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Private target semantics cannot invent a stack occurrence, borrow a different source or attach to an unoffered index");
        }
        foreach (var cost in new[] {
            new ActionCost(ActionCostKind.Mana, 1, false, "") { ManaSymbols = [null!] },
            new ActionCost(ActionCostKind.Mana, 1, false, "") { ManaSymbols = [" "] },
            new ActionCost(ActionCostKind.RemoveCounters, 1, true, null!)
        }) {
            bool rejected = false;
            try { _ = new AiDecisionFrame(state, domain: Domain(metadata with { Costs = [cost] })); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Malformed flat cost symbols and counter types fail before the mana planner parses them");
        }

        var together = new ManaProduction(SemanticKnowledge.Known,
            [new(ImmutableDictionary<string, int>.Empty.Add("G", 1).Add("U", 1))]);
        var alternatives = new ManaProduction(SemanticKnowledge.Known,
            [new(ImmutableDictionary<string, int>.Empty.Add("G", 1)), new(ImmutableDictionary<string, int>.Empty.Add("U", 1))]);
        check(new AiDecisionFrame(state, domain: Domain(metadata with { Production = together })).Domain.Actions[0].Production.Alternatives.Length == 1
                && new AiDecisionFrame(state, domain: Domain(metadata with { Production = alternatives })).Domain.Actions[0].Production.Alternatives.Length == 2,
            "Simultaneous mana bundles and mutually exclusive color alternatives survive the neutral boundary distinctly");
        check(new AiDecisionFrame(state, domain: Domain(metadata with { Production = new(SemanticKnowledge.Known,
                [new(ImmutableDictionary<string, int>.Empty)]) })).Domain.Actions[0].Production.Knowledge == SemanticKnowledge.Known,
            "Known zero nominal production is preserved without inventing positive mana output");
        foreach (var malformed in new[] {
            together with { Alternatives = default }, together with { Alternatives = [] },
            together with { Knowledge = (SemanticKnowledge)99 },
            together with { Alternatives = [new(ImmutableDictionary<string, int>.Empty.Add("invalid", 1))] },
            together with { Alternatives = [new(ImmutableDictionary<string, int>.Empty.Add("G", -1))] },
            together with { Alternatives = [new(ImmutableDictionary<string, int>.Empty.Add("G", int.MaxValue).Add("U", int.MaxValue))] },
            together with { Alternatives = [together.Alternatives[0], together.Alternatives[0]] },
            together with { Alternatives = Enumerable.Range(0, 33).Select(amount => new ManaProductionAlternative(ImmutableDictionary<string, int>.Empty.Add("C", amount))).ToImmutableArray() }
        }) {
            bool rejected = false;
            try { _ = new AiDecisionFrame(state, domain: Domain(metadata with { Production = malformed })); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Malformed or unbounded mana production cannot become a funding claim");
        }
    }
}
