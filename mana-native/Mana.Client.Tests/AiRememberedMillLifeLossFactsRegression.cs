using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;

internal static class AiRememberedMillLifeLossFactsRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var frame = Frame();
        var option = frame.Semantics.Options[0];
        var fact = option.RememberedMillLifeLoss!;
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
        var wire = JsonSerializer.Serialize(frame.Semantics, json);
        var restored = JsonSerializer.Deserialize<DecisionSemantics>(wire, json)!;
        var roundTrip = new AiDecisionFrame(frame.State, semantics: restored);
        check(roundTrip.Semantics.Options[0].RememberedMillLifeLoss == fact
            && roundTrip.Semantics.Options[0].OperationGraph.Operations.Where(node => node.Effect != ActionEffectKind.Cleanup)
                .All(node => node.Knowledge == SemanticKnowledge.Unknown && node.AmountKnowledge == SemanticKnowledge.Unknown && node.Amount == null),
            "A current public mill dependency roundtrips without promoting any unknown outcome amount");

        var legacy = restored with { Options = restored.Options.Select(item => item with { RememberedMillLifeLoss = null }).ToImmutableArray() };
        var without = new AiDecisionFrame(frame.State, semantics: legacy);
        var policy = new CasualCommanderPolicy();
        var first = await AiDecisionReplay.EvaluateAsync(new(1, "facts-only", policy.Descriptor.Id, frame.State,
            frame.Domain, frame.Semantics, new([], []), 17), policy);
        var second = await AiDecisionReplay.EvaluateAsync(new(1, "facts-only", policy.Descriptor.Id, without.State,
            without.Domain, without.Semantics, new([], []), 17), policy);
        check(first.Proposal is ChooseOptions firstChoice && second.Proposal is ChooseOptions secondChoice
            && firstChoice.Choices.SequenceEqual(secondChoice.Choices) && frame.Allows(first.Proposal),
            "Phase A facts leave shipping v6's actual proposed current option unchanged");
        check(JsonSerializer.Deserialize<DecisionOptionSemantics>("{\"index\":7,\"effect\":\"Mill\"}", json)!.RememberedMillLifeLoss == null,
            "Old replay options have no inferred remembered-mill certificate");
        var oldReader = JsonSerializer.Deserialize<LegacyOption>(JsonSerializer.Serialize(option, json), json)!;
        check(oldReader.Index == option.Index && oldReader.Effect == option.Effect && oldReader.OperationGraph.Operations.Length == 3,
            "The current legacy option DTO settings ignore this additive field without a new effect enum");

        bool Rejected(DecisionOptionSemantics changed, GameSnapshot? state = null, DecisionSemantics? semantics = null) {
            try { _ = new AiDecisionFrame(state ?? frame.State, semantics: (semantics ?? frame.Semantics) with { Options = [changed] }); return false; }
            catch (ArgumentException) { return true; }
        }
        foreach (var changed in new[] {
            fact with { Version = 2 }, fact with { SourceCardId = "foreign" }, fact with { SourceCardId = "" },
            fact with { CounterType = "INFLUENCE" }, fact with { ObservedCount = 3 }, fact with { ObservedCount = -1 },
            fact with { CounterType = "INFLUENCE", ObservedCount = 0 }, fact with { CounterType = "invented", ObservedCount = 0 },
            fact with { LifeLossOperationId = 17 }, fact with { LifeLossOperationId = 41 }, fact with { LifeLossOperationId = 999 }
        }) check(Rejected(option with { RememberedMillLifeLoss = changed }), "Forged counter, source, version and operation references cannot enter a policy frame");

        var graph = option.OperationGraph;
        ActionOperationGraph Change(int id, Func<ActionOperation, ActionOperation> mutate) => graph with {
            Operations = graph.Operations.Select(node => node.Id == id ? mutate(node) : node).ToImmutableArray() };
        foreach (var changed in new[] {
            graph with { Knowledge = SemanticKnowledge.Known },
            Change(17, node => node with { Knowledge = SemanticKnowledge.Known, AmountKnowledge = SemanticKnowledge.Known, Amount = 4 }),
            Change(29, node => node with { AmountKnowledge = SemanticKnowledge.Known, Amount = 28 }),
            Change(29, node => node with { Recipients = EffectRecipients.Unknown }),
            Change(29, node => node with { Recipients = node.Recipients with { Entities = [] } }),
            Change(29, node => node with { Recipients = node.Recipients with { Entities = [Entity(29, 0), Entity(29, 1)] } }),
            Change(29, node => node with { Conditional = true }),
            Change(29, node => node with { Links = [new(41, OperationLinkKind.IfTrue)] }),
            Change(17, node => node with { Links = [new(29, OperationLinkKind.Sequence), new(41, OperationLinkKind.Additional)] }),
            Change(41, node => node with { Effect = ActionEffectKind.Draw, Amount = 1 }),
            Change(29, node => node with { Costs = [new(ActionCostKind.Life, 1, false, "")] }),
            graph with { Operations = graph.Operations.Add(new(99, ActionEffectKind.Cleanup)) }
        }) check(Rejected(option with { OperationGraph = changed }), "The certificate cannot hide a quantified, ambiguous, paid, conditional, unrelated or additional consequence");

        var decision = frame.State.Decision!;
        check(Rejected(option, frame.State with { DecisionOwnerId = 1 })
            && Rejected(option, frame.State with { Decision = decision with { Payload = new OptionsPayload(new([new(7, "", null)], new(0, 1))) } })
            && Rejected(option, frame.State with { Decision = decision with { Payload = new OrderingPayload(new([new(7, "", null)], new(1, 1))) } })
            && Rejected(option with { Index = 999 })
            && Rejected(option, semantics: frame.Semantics with { SelectionIsCost = true }),
            "Current owned mandatory choice and offered index remain prerequisites independent of semantic evidence");
        var hidden = frame.State with { Players = frame.State.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with { FaceDown = true }).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        check(Rejected(option, hidden), "A public face-down position cannot certify the source counter");
        var zeroState = frame.State with { Players = frame.State.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                Counters = card.Counters.SetItem("Influence", 0) }).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        var zeroOption = option with { RememberedMillLifeLoss = fact with { ObservedCount = 0 } };
        _ = new AiDecisionFrame(zeroState, semantics: frame.Semantics with { Options = [zeroOption] });
        var missingZero = zeroState with { Players = zeroState.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                Counters = card.Counters.Remove("Influence") }).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        check(Rejected(zeroOption, missingZero), "A zero snapshot requires an explicitly exposed exact counter key; an absent or invented key is not certified");

        var certificateWire = JsonSerializer.SerializeToNode(fact, json)!.AsObject();
        foreach (string name in new[] { "version", "sourceCardId", "counterType", "observedCount", "lifeLossOperationId" }) {
            var missing = certificateWire.DeepClone().AsObject(); missing.Remove(name);
            bool rejected; try { _ = JsonSerializer.Deserialize<RememberedMillLifeLoss>(missing.ToJsonString(), json); rejected = false; }
            catch (JsonException) { rejected = true; }
            check(rejected, "Every certificate field is required, including a zero-valued counter snapshot: " + name);
        }
        foreach (string malformed in new[] {
            certificateWire.ToJsonString().Replace("\"version\":1", "\"version\":1,\"Version\":1", StringComparison.Ordinal),
            certificateWire.ToJsonString().Replace("\"version\":1", "\"version\":1,\"actionId\":\"invented\"", StringComparison.Ordinal),
            certificateWire.ToJsonString().Replace("\"observedCount\":4", "\"observedCount\":\"4\"", StringComparison.Ordinal)
        }) {
            bool rejected; try { _ = JsonSerializer.Deserialize<RememberedMillLifeLoss>(malformed, json); rejected = false; }
            catch (JsonException) { rejected = true; }
            check(rejected, "The closed dependency wire rejects duplicate keys, extra authority fields and mistyped quantities");
        }
    }

    private sealed record LegacyOption(int Index, ActionEffectKind Effect, int? Amount = null)
    { public ActionOperationGraph OperationGraph { get; init; } = ActionOperationGraph.Unknown; }
    private static SemanticEntity Entity(int id, int player) => new($"op{id}:player:{player}", SemanticEntityKind.Player, PlayerId: player, ControllerId: player);
    private static EffectRecipients Source(int id) => new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
        [new($"op{id}:card:source", SemanticEntityKind.Card, "source", ControllerId: 1, OwnerId: 1)]);
    private static AiDecisionFrame Frame()
    {
        var source = new Card { VisualId = "source", Name = "Renamed public artifact", Type = "Artifact",
            Counters = ImmutableDictionary<string, int>.Empty.Add("Influence", 4) };
        var state = new GameSnapshot("dependency-contract", "playing", null, null, 0, 4, "End", "END_OF_TURN", 1,
            [new(0, "Chooser", 12, false, false, ImmutableDictionary<string, int>.Empty, []),
             new(1, "Controller", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [source], null)])], [], null,
            new("current-choice", new(), new OptionsPayload(new([new(7, "No English capability", null), new(3, "Another label", null)], new(1, 1))))) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0 };
        var graph = new ActionOperationGraph(SemanticKnowledge.Unknown, 17, [
            new(17, ActionEffectKind.Mill) { ActorPlayerId = 1, Sources = Source(17),
                Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null, [Entity(17, 1)]), Links = [new(29, OperationLinkKind.Sequence)] },
            new(29, ActionEffectKind.LifeLoss) { ActorPlayerId = 1, Sources = Source(29),
                Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Explicit, null, [Entity(29, 0)]), Links = [new(41, OperationLinkKind.Sequence)] },
            new(41, ActionEffectKind.Cleanup) { Knowledge = SemanticKnowledge.Known, ActorPlayerId = 1, Sources = Source(41), Recipients = Source(41) }
        ]);
        return new(state, semantics: new() { Options = [new(7, ActionEffectKind.Mill) { OperationGraph = graph,
            RememberedMillLifeLoss = new(1, "source", "Influence", 4, 29) }, new(3, ActionEffectKind.Draw, 1) {
            OperationGraph = new(SemanticKnowledge.Known, 0, [new(0, ActionEffectKind.Draw) {
                Knowledge = SemanticKnowledge.Known, AmountKnowledge = SemanticKnowledge.Known, Amount = 1, ActorPlayerId = 1,
                Sources = Source(0), Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null, [Entity(0, 1)]) }]) }] });
    }
}
