using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class AiExperimentVariantRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await AiUncertainLifeRiskRegression.RunAsync(check);
        var full = new AiExperimentVariant("casual-commander.v6");
        var ablated = full with { AttachmentEvaluation = false };
        check(AiExperimentPolicyRegistry.Create(full).Descriptor.Id == new CasualCommanderPolicy().Descriptor.Id
                && AiExperimentPolicyRegistry.Create(ablated).Descriptor.Id == "casual-commander.v6.attachment-ablation.v1",
            "The closed experiment registry preserves default v6 and names the attachment ablation distinctly");
        check(!ReferenceEquals(AiExperimentPolicyRegistry.Create(full), AiExperimentPolicyRegistry.Create(full)),
            "Every experiment factory request creates an independent policy instance");
        var acquisition = new AiExperimentVariant(AiExperimentPolicyRegistry.LibraryAcquisitionFactoryId);
        check(AiExperimentPolicyRegistry.Create(acquisition) is CasualCommanderPolicy {
                Options: { AttachmentEvaluation: true, LibraryAcquisitionEvaluation: true } } registeredAcquisition
                && registeredAcquisition.Descriptor.Id == AiExperimentPolicyRegistry.LibraryAcquisitionFactoryId,
            "The versioned acquisition factory enables one experimental component with full attachment evaluation");
        check(!ReferenceEquals(AiExperimentPolicyRegistry.Create(acquisition), AiExperimentPolicyRegistry.Create(acquisition)),
            "Acquisition experiment seats receive independent policy instances");
        bool unsupportedCombination = false;
        try { AiExperimentPolicyRegistry.Create(acquisition with { AttachmentEvaluation = false }); }
        catch (ArgumentException) { unsupportedCombination = true; }
        check(unsupportedCombination, "The acquisition factory cannot silently combine a second attachment ablation treatment");
        using (var acquisitionJson = JsonDocument.Parse("{\"factoryId\":\"casual-commander.v7.library-acquisition.v1\",\"attachmentEvaluation\":true}"))
            check(AiExperimentPolicyRegistry.Parse(acquisitionJson.RootElement) == acquisition,
                "The strict serialized acquisition identity round trips without a hidden settings override");
        foreach (string invalid in new[] {
            "{}", "null", "{\"factoryId\":\"casual-commander.v5\"}",
            "{\"factoryId\":\"casual-commander.v6\",\"attachmentEvaluation\":null}",
            "{\"factoryId\":\"casual-commander.v6\",\"attachmentEvaluation\":1}",
            "{\"factoryId\":\"casual-commander.v6\",\"attachmentEvaluation\":true,\"attachmentEvaluation\":false}",
            "{\"factoryId\":\"casual-commander.v6\",\"unknownWeight\":1}",
            "{\"factoryId\":\"System.ArbitraryPolicy\"}" }) {
            using var json = JsonDocument.Parse(invalid);
            bool rejected = false;
            try { AiExperimentPolicyRegistry.Parse(json.RootElement); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Experiment policy configuration rejects unsupported, ambiguous or untyped input: " + invalid);
        }
        bool badFactory = false;
        try { AiExperimentPolicyRegistry.Create(new("unknown")); } catch (ArgumentException) { badFactory = true; }
        check(badFactory, "The typed factory also rejects unregistered policy identities without silently substituting v6");

        foreach (long seed in new[] { long.MinValue, long.MaxValue, 9007199254740993L, 2668620592559112008L }) {
            var captured = Case(seed);
            string before = JsonSerializer.Serialize(captured);
            var shipping = await AiDecisionReplay.EvaluateAsync(captured, new CasualCommanderPolicy());
            var registered = await AiDecisionReplay.EvaluateAsync(captured, AiExperimentPolicyRegistry.Create(full));
            var repeated = await AiDecisionReplay.EvaluateAsync(captured, AiExperimentPolicyRegistry.Create(full));
            check(Equivalent(shipping.Proposal, registered.Proposal) && Equivalent(registered.Proposal, repeated.Proposal)
                    && shipping.Proposal is SelectCard { Card: "own-key" }
                    && shipping.Evidence?.RemainingWork == registered.Evidence?.RemainingWork,
                $"Default registry evaluation agrees with shipping and A/A proposals/work on the exact frame at seed {seed}");
            var disabled = await AiDecisionReplay.EvaluateAsync(captured, AiExperimentPolicyRegistry.Create(ablated));
            check(disabled.Status == AiSeatStatus.Ready && disabled.Proposal is CancelSelection
                    && disabled.Evidence?.Seed == seed && registered.Evidence?.Seed == seed
                    && captured.RemainingWork == 4096 && captured.RemainingTime == TimeSpan.FromSeconds(3),
                $"Attachment ablation removes valuation while preserving exact seed, declared allowance and cancellation at seed {seed}");
            check(before == JsonSerializer.Serialize(captured),
                "Neither experiment arm edits the original immutable frame, semantic knowledge, history or replay input");
        }
        var forced = Case(17, cancel: false);
        var forcedResult = await AiDecisionReplay.EvaluateAsync(forced, AiExperimentPolicyRegistry.Create(ablated));
        check(forcedResult.Proposal is SelectCard && new AiDecisionFrame(forced.State, semantics: forced.Semantics).Allows(forcedResult.Proposal),
            "Disabling attachment valuation preserves a genuinely mandatory no-cancel legal answer");
        var payment = Case(17) with { Semantics = Case(17).Semantics with { SelectionIsCost = true } };
        var cost = await AiDecisionReplay.EvaluateAsync(payment, AiExperimentPolicyRegistry.Create(ablated));
        check(cost.Proposal is SelectCard { Card: "own-key" },
            "Attachment ablation cannot turn an actual selection cost into an optional-effect cancellation");
        var unknown = Case(17, known: false);
        check((await AiDecisionReplay.EvaluateAsync(unknown, AiExperimentPolicyRegistry.Create(full))).Proposal is CancelSelection
                && (await AiDecisionReplay.EvaluateAsync(unknown, AiExperimentPolicyRegistry.Create(ablated))).Proposal is CancelSelection,
            "Both arms preserve cancellation when attachment mechanics are genuinely unknown");
        var damage = Case(17);
        damage = damage with { Semantics = new(DecisionIntent.Damage, 2) {
            OperationId = 0, OperationGraph = damage.Semantics.OperationGraph with { Operations = [
                damage.Semantics.OperationGraph.Operations[0] with { Effect = ActionEffectKind.Damage, Attachment = null,
                    Amount = 2, AmountKnowledge = SemanticKnowledge.Known }
            ] }
        } };
        var a = await AiDecisionReplay.EvaluateAsync(damage, AiExperimentPolicyRegistry.Create(full));
        var b = await AiDecisionReplay.EvaluateAsync(damage, AiExperimentPolicyRegistry.Create(ablated));
        check(Equivalent(a.Proposal, b.Proposal) && a.Evidence?.RemainingWork == b.Evidence?.RemainingWork,
            "Non-attachment damage assessment and its work accounting remain identical across the ablation");
        check(CasualActionAssessment.Evaluate(Case(17).State, Case(17).Semantics.OperationGraph, 128).Status == CasualAssessmentStatus.Estimated
                && CasualActionAssessment.Evaluate(Case(17).State, Case(17).Semantics.OperationGraph, 128, attachmentEvaluation: false).Status == CasualAssessmentStatus.Incomplete,
            "The ablation changes assessment coverage without downgrading the supplied graph's mechanical knowledge");
    }

    private static bool Equivalent(DecisionCommand? first, DecisionCommand? second)
        => first?.GetType() == second?.GetType() && (first == null || JsonSerializer.Serialize(first, first.GetType())
            == JsonSerializer.Serialize(second, second!.GetType()));

    internal static AiDecisionReplayCase Case(long seed, bool cancel = true, bool known = true)
    {
        Card Body(string id) => new() { VisualId = id, CombatId = id, Key = id + "-key", Name = "Unrelated public face", Type = "Creature",
            Power = 2, Toughness = 2, Selectable = true, States = [] };
        var source = new Card { VisualId = "source", CombatId = "source", Key = "source-key", Name = "Unrelated source", Type = "Enchantment Aura" };
        var state = new GameSnapshot("experiment-frame", "playing", null, null, 11, 4, "Main", "MAIN1", 11,
            [new(11, "Actor", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 1, [source], null), new("Battlefield", 1, [Body("own")], null)]) { TeamId = 3 },
             new(29, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [Body("opponent")], null)]) { TeamId = 7 }], [], null,
            new("selection", new(), new TargetSelectionPayload(new("Done", false), new("Cancel", cancel), new(1, 1), []))) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 11
            };
        SemanticEntity Entity(string id, int player) => new(id, SemanticEntityKind.Card, id, ControllerId: player, OwnerId: player);
        var knowledge = known ? SemanticKnowledge.Known : SemanticKnowledge.Unknown;
        var semantics = new DecisionSemantics { OperationId = 0, OperationGraph = new(knowledge, 0, [new(0, ActionEffectKind.Attach) {
            ActorPlayerId = 11, Knowledge = knowledge,
            Attachment = known ? new(SemanticKnowledge.Known, 2, 0, ["trample"]) : AttachmentModifier.Unknown,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null, [Entity("source", 11)]),
            Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []),
            Targets = new(SemanticKnowledge.Known, 1, 1, [Entity("own", 11), Entity("opponent", 29)])
        }]) };
        return new(1, "authored-experiment", "casual-commander.v6", state, DecisionActionDomain.None, semantics, new([], []), seed) {
            RemainingWork = 4096, RemainingTime = TimeSpan.FromSeconds(3)
        };
    }
}
