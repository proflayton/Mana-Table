using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class AiCausalMechanicEvidenceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        AiConsumingArtifactEvidenceChecks.Run(check);
        var fixture = new Fixture();
        AiCausalMechanicEvidence Collect(GameSnapshot? after = null, AutomatedSeatObservation? before = null,
            bool accepted = true, bool publicResult = true)
        {
            var evidence = new AiCausalMechanicEvidence();
            var input = before ?? fixture.Before;
            evidence.Receipt(input, fixture.Command(input), accepted);
            var result = after ?? fixture.After;
            evidence.ObserveOwned(input with { State = result });
            if (publicResult) evidence.ObservePublic(result);
            return evidence;
        }
        bool Has(AiCausalMechanicEvidence evidence, string id) => evidence.Witnesses.Any(item => item.WitnessId == id && !item.IntegrityFailure);
        const string cost = "source-cost-ownership", mare = "loch-mare-counter-cost-draw", partner = "pir-toothy-draw-counters";
        var normal = Collect();
        check(Has(normal, cost) && Has(normal, mare) && Has(normal, partner)
            && normal.Witnesses.Single(item => item.WitnessId == partner) is { ObservedCounterDelta: 2 } proof
            && proof.CausalExecutionIds.SequenceEqual(["a", "t", "p"]) && proof.ActivityIds.SequenceEqual([11L, 12L, 13L]),
            "An accepted offered activation with no SelectCard proves its committed counter cost, actual draw and distinct Toothy/Pir parent chain with typed exact occurrence/event IDs");
        var missingBaseline = Collect(before: fixture.Before with { State = fixture.Before.State with { Executions = null } });
        check(missingBaseline.Witnesses.IsEmpty && missingBaseline.IsIncomplete(fixture.Before.State.Id, Fixture.Actor),
            "Without an accepted pre-initiation execution cursor, a later first-disclosed historical activation cannot be claimed as new work from this command");
        check(!Collect(accepted: false).Witnesses.Any() && !Has(Collect(publicResult: false), mare),
            "Rejected initiation supplies no evidence, and an owned result alone cannot bypass public-state confirmation for a whole mechanic");
        var chargeBefore = fixture.Before with {
            State = Fixture.Cards(fixture.Before.State, fixture.Mare with {
                Counters = ImmutableDictionary<string, int>.Empty.Add("charge", 3)
            }, fixture.Toothy, fixture.Pir),
            Domain = fixture.Before.Domain with {
                Actions = [fixture.Semantic with { Costs = [new(ActionCostKind.RemoveCounters, 1, true, "")] }]
            }
        };
        var chargeAfter = Fixture.Cards(fixture.After with { Activity = fixture.After.Activity.Select(item => item.Id == 11
            ? item with { Change = new("counters", "charge", 3, 2) } : item).ToImmutableArray() },
            fixture.Mare with { Counters = ImmutableDictionary<string, int>.Empty.Add("charge", 2) }, fixture.ToothyAfter, fixture.Pir);
        check(Has(Collect(chargeAfter, chargeBefore), mare)
            && !Has(Collect(chargeAfter, fixture.Before with { Domain = fixture.Before.Domain with {
                Actions = [fixture.Semantic with { Costs = [new(ActionCostKind.RemoveCounters, 1, true, "+1/+1")] }]
            } }), cost),
            "An any-counter cost uses the actual committed type; a known fixed counter type cannot borrow a different type's decrement");
        foreach (var invalidCosts in new[] {
            ImmutableArray.Create(new ActionCost(ActionCostKind.RemoveCounters, null, true, "")),
            ImmutableArray.Create(new ActionCost(ActionCostKind.RemoveCounters, 1, false, "")),
            ImmutableArray.Create(new ActionCost(ActionCostKind.RemoveCounters, 1, true, ""), new ActionCost(ActionCostKind.RemoveCounters, 1, true, ""))
        }) {
            var input = fixture.Before with { Domain = fixture.Before.Domain with {
                Actions = [fixture.Semantic with { Costs = invalidCosts }]
            } };
            check(!Collect(before: input).Witnesses.Any(),
                "Unknown amounts, a non-source cost or indistinguishable same-kind cost parts remain unproven rather than guessed");
        }
        foreach (int? amount in new int?[] { null, 2 }) {
            var uncertainDraw = Collect(before: fixture.Before with { Domain = fixture.Before.Domain with {
                Actions = [fixture.Semantic with { EffectAmount = amount }]
            } });
            check(Has(uncertainDraw, cost) && !Has(uncertainDraw, mare) && !Has(uncertainDraw, partner),
                "Unknown or multi-card draw semantics cannot borrow a single retained draw event as proof of this bounded one-card branch");
        }
        GameSnapshot Execution(string id, Func<ExecutionOccurrence, ExecutionOccurrence> transform) => fixture.After with {
            Executions = fixture.After.Executions! with { Occurrences = fixture.After.Executions.Occurrences
                .Select(item => item.Id == id ? transform(item) : item).ToImmutableArray() }
        };
        GameSnapshot Event(long id, Func<ActivityEntry, ActivityEntry> transform) => fixture.After with {
            Activity = fixture.After.Activity.Select(item => item.Id == id ? transform(item) : item).ToImmutableArray()
        };
        foreach (var after in new[] {
            Execution("a", item => item with { ActorPlayerId = 83 }),
            Execution("a", item => item with { SourceCardId = "ally-loch" }),
            Event(11, item => item with { ExecutionStep = ExecutionStep.Resolution }),
            Event(11, item => item with { CardId = "ally-loch" }),
            Event(11, item => item with { PlayerId = 83 }),
            Event(11, item => item with { Change = new("counters", "+1/+1", 3, 1) })
        }) check(!Has(Collect(after), cost), "A foreign actor/source, unrelated target, wrong stage or wrong amount cannot satisfy source-cost ownership");
        foreach (var after in new[] {
            Execution("a", item => item with { Outcome = ExecutionOutcome.Cancelled }),
            Execution("a", item => item with { Outcome = ExecutionOutcome.Countered }),
            Execution("a", item => item with { Outcome = ExecutionOutcome.Failed }),
            Event(12, item => item with { ExecutionId = "unrelated" }),
            Event(12, item => item with { Count = 0 }),
            Event(12, item => item with { Count = 2 }),
            Event(12, item => item with { PlayerId = 83 }),
            Event(12, item => item with { Kind = "zone", Impact = new("zone", 1, ToZone: "Hand", FromZone: "Library") })
        }) {
            var evidence = Collect(after);
            check(Has(evidence, cost) && !Has(evidence, mare) && !Has(evidence, partner),
                "An actually committed cost survives parent failure/cancellation; failed, foreign, multiple or merely inferred draws never establish the whole ability");
        }
        foreach (var after in new[] {
            Execution("t", item => item with { ParentId = "hidden" }),
            Execution("t", item => item with { ParentId = null, BudgetRootId = "a" }),
            Execution("t", item => item with { ActorPlayerId = 83 }),
            Execution("t", item => item with { SourceCardId = "ally-toothy" }),
            Execution("t", item => item with { Outcome = ExecutionOutcome.Countered }),
            Execution("p", item => item with { ParentId = "a", BudgetRootId = "t" }),
            Execution("p", item => item with { RuleResult = ExecutionRuleResult.NotReplaced }),
            Execution("p", item => item with { ActorPlayerId = 83 }),
            Execution("p", item => item with { SourceCardId = "ally-pir" }),
            Event(13, item => item with { ExecutionId = "a" }),
            Event(13, item => item with { Cause = item.Cause! with { ControllerId = 83 } }),
            Event(13, item => item with { Change = new("counters", "+1/+1", 0, 0) })
        }) check(!Has(Collect(after), partner),
            "Toothy/Pir proof requires disclosed actual ancestors, exact owned occurrences, successful replacement and an attributed committed positive counter result; budget-root equality grants no causality");
        var noCounter = fixture.After with { Activity = fixture.After.Activity.Where(item => item.Id != 13).ToImmutableArray() };
        check(!Has(Collect(noCounter), partner),
            "A resolved Toothy trigger and successful Pir replacement without the committed recipient mutation remain unobserved");
        var wrongPublic = Fixture.Cards(fixture.After, fixture.Mare with {
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1)
        }, fixture.ToothyAfter, fixture.Pir);
        var publicMismatch = Collect(wrongPublic);
        check(Has(publicMismatch, cost) && !Has(publicMismatch, mare) && !Has(publicMismatch, partner),
            "Current visible source counters must confirm the recorded paid decrement before whole-mechanic credit; a mismatching public result cannot be papered over");
        var twoTriggers = fixture.After with { Executions = fixture.After.Executions! with {
            Occurrences = fixture.After.Executions.Occurrences.Add(fixture.After.Executions.Occurrences.Single(item => item.Id == "t")
                with { Id = "other-toothy-trigger", BudgetRootId = "other-toothy-trigger" })
        } };
        check(!Has(Collect(twoTriggers), partner), "Competing plausible draw triggers remain ambiguous instead of crediting the nearest or first same-name occurrence");
        var indirectTrigger = Execution("t", item => item with { ParentId = "other-draw" });
        indirectTrigger = indirectTrigger with { Executions = indirectTrigger.Executions! with {
            Occurrences = indirectTrigger.Executions.Occurrences.Add(new("other-draw", "other-draw", ExecutionKind.Trigger, ExecutionStep.Complete) {
                ParentId = "a", ActorPlayerId = Fixture.Actor, SourceCardId = "other-card", Outcome = ExecutionOutcome.Resolved })
        } };
        check(!Has(Collect(indirectTrigger), partner),
            "A Toothy trigger parented by a different nested draw effect cannot borrow the initiating activation's separate witnessed draw");
        var twoActivations = fixture.After with { Executions = fixture.After.Executions! with {
            Occurrences = fixture.After.Executions.Occurrences.Add(fixture.After.Executions.Occurrences.Single(item => item.Id == "a")
                with { Id = "other-activation", BudgetRootId = "other-activation" }),
            Transitions = fixture.After.Executions.Transitions.Add(new(8, "other-activation", ExecutionStep.Preparation)),
            LastSequence = 8
        } };
        check(!Collect(twoActivations).Witnesses.Any(),
            "An accepted offer cannot choose arbitrarily between two plausible new source activations");
        var duplicates = new AiCausalMechanicEvidence();
        duplicates.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        for (int index = 0; index < 3; index++) {
            duplicates.ObserveOwned(fixture.Before with { State = fixture.After });
            duplicates.ObservePublic(fixture.After);
        }
        check(duplicates.Witnesses.Length == 3, "Repeated owned/public snapshots deduplicate one cost, one draw mechanic and one partner chain");
        var redacted = new AiCausalMechanicEvidence();
        redacted.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        redacted.ObserveOwned(fixture.Before with { State = fixture.After });
        var anonymous = fixture.After with { Executions = fixture.After.Executions! with {
            Occurrences = fixture.After.Executions.Occurrences.Select(item => item with { SourceCardId = null, SourceName = null }).ToImmutableArray()
        } };
        redacted.ObserveOwned(fixture.Before with { State = anonymous }); redacted.ObservePublic(anonymous);
        check(Has(redacted, mare) && Has(redacted, partner),
            "Source identity redaction after an already admitted initiation preserves historical exact execution evidence without inventing current command authority");
        var laterBefore = fixture.Before with {
            State = fixture.After with { Decision = fixture.Before.State.Decision! with { Id = "later" } },
            Domain = fixture.Before.Domain with { Sources = fixture.Before.Domain.Sources.Select(item => item with { DecisionId = "later" }).ToImmutableArray() }
        };
        duplicates.Receipt(laterBefore, fixture.Command(laterBefore), true);
        var later = fixture.After with { BoardRevision = 32, Revision = 32, Executions = fixture.After.Executions! with {
            LastSequence = 10, Occurrences = fixture.After.Executions.Occurrences.Add(new("later-a", "later-a",
                ExecutionKind.Activation, ExecutionStep.Complete) { ActorPlayerId = Fixture.Actor, SourceCardId = "loch",
                    Outcome = ExecutionOutcome.Resolved }),
            Transitions = fixture.After.Executions.Transitions.Add(new(8, "later-a", ExecutionStep.Preparation))
                .Add(new(10, "later-a", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved })
        } };
        duplicates.ObserveOwned(laterBefore with { State = later }); duplicates.ObservePublic(later);
        check(duplicates.Witnesses.Length == 3, "A witnessed earlier source payment cannot satisfy a later activation on the same physical card");
        var absentFirst = new AiCausalMechanicEvidence();
        absentFirst.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        var nextBefore = fixture.Before with {
            State = fixture.Before.State with { Decision = fixture.Before.State.Decision! with { Id = "next" }, Revision = 31, BoardRevision = 31 },
            Domain = fixture.Before.Domain with { Sources = fixture.Before.Domain.Sources.Select(item => item with { DecisionId = "next" }).ToImmutableArray() }
        };
        absentFirst.Receipt(nextBefore, fixture.Command(nextBefore), true);
        absentFirst.ObserveOwned(nextBefore with { State = fixture.After with { BoardRevision = 32, Revision = 32 } });
        absentFirst.ObservePublic(fixture.After with { BoardRevision = 32, Revision = 32 });
        check(absentFirst.Witnesses.Length == 3 && absentFirst.Witnesses.All(item => item.Before.State.Decision?.Id == "next"),
            "A failed/no-op initiation with no disclosed occurrence cannot borrow the later accepted same-source action's cost or resolution");
        var noPreparation = fixture.After with { Executions = fixture.After.Executions! with {
            Transitions = fixture.After.Executions.Transitions.Where(item => item.Step != ExecutionStep.Preparation).ToImmutableArray()
        } };
        check(!Collect(noPreparation).Witnesses.Any(),
            "Arbitrary later non-Preparation transitions do not identify an unbound accepted source activation");
        var exactContinuation = new AiCausalMechanicEvidence();
        exactContinuation.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        var costInput = fixture.Before with { State = fixture.After with {
            Decision = new("exact-cost", new(Source: new(fixture.Mare.VisualId, fixture.Mare, Fixture.Actor)),
                new PaymentPayload(new("Pay", true), new("Cancel", true))) { ExecutionId = "a", ExecutionStep = ExecutionStep.Cost },
            Executions = new(1, 3, [fixture.After.Executions!.Occurrences.Single(item => item.Id == "a")
                with { Step = ExecutionStep.Cost, Outcome = null }], [new(3, "a", ExecutionStep.Cost)]),
            Activity = fixture.After.Activity.Where(item => item.Id == 11).ToImmutableArray()
        } };
        exactContinuation.ObserveOwned(costInput);
        exactContinuation.ObserveOwned(fixture.Before with { State = noPreparation });
        exactContinuation.ObservePublic(noPreparation);
        check(Has(exactContinuation, mare) && Has(exactContinuation, partner),
            "An exact later owned Cost decision binds its actual new execution without requiring a particular Preparation, SelectCard or PayMana callback");
        var gap = new AiCausalMechanicEvidence();
        gap.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        var gapped = fixture.After with { Executions = fixture.After.Executions! with {
            FirstSequence = 5, Transitions = fixture.After.Executions.Transitions.Where(item => item.Sequence >= 5).ToImmutableArray()
        } };
        gap.ObserveOwned(fixture.Before with { State = gapped }); gap.ObservePublic(gapped);
        check(gap.Witnesses.IsEmpty && gap.IsIncomplete(fixture.Before.State.Id, Fixture.Actor),
            "An actual retained-stream gap retires pending causal claims and records incomplete evidence rather than joining observations across the gap");
        var bounded = new AiCausalMechanicEvidence(maximumOccurrences: 2);
        bounded.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        bounded.ObserveOwned(fixture.Before with { State = fixture.After }); bounded.ObservePublic(fixture.After);
        check(bounded.Witnesses.IsEmpty && bounded.IsIncomplete(fixture.Before.State.Id, Fixture.Actor),
            "Bounded retention overflow drops incomplete attempts without converting a later matching delta into proof");
        var activityBounded = new AiCausalMechanicEvidence(maximumActivities: 2);
        activityBounded.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        activityBounded.ObserveOwned(fixture.Before with { State = fixture.After }); activityBounded.ObservePublic(fixture.After);
        check(activityBounded.Witnesses.IsEmpty && activityBounded.IsIncomplete(fixture.Before.State.Id, Fixture.Actor),
            "Local activity retention bounds retire incomplete proof without assuming that sparse provider activity IDs are contiguous");
        var foreignGame = new AiCausalMechanicEvidence();
        foreignGame.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        foreignGame.ObserveOwned(fixture.Before with { State = fixture.After with { Id = "different-game" } });
        foreignGame.ObservePublic(fixture.After with { Id = "different-game" });
        check(foreignGame.Witnesses.IsEmpty, "Another game's reused source/execution IDs cannot satisfy this game's accepted initiation");
        var stale = Collect(fixture.After with { BoardRevision = 29 });
        check(stale.Witnesses.IsEmpty, "A stale owned frame predating accepted initiation cannot bind an execution or mutate evidence");
    }

    private sealed class Fixture
    {
        internal const int Actor = 47;
        internal readonly Card Mare = new() { Key = "loch-key", VisualId = "loch", Name = "Loch Mare", Type = "Creature",
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 3) };
        internal readonly Card Toothy = new() { Key = "toothy-key", VisualId = "toothy", Name = "Toothy, Imaginary Friend", Type = "Creature" };
        internal readonly Card Pir = new() { Key = "pir-key", VisualId = "pir", Name = "Pir, Imaginative Rascal", Type = "Creature" };
        internal Card ToothyAfter => Toothy with { Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2) };
        internal readonly ActionSemantics Semantic = new("draw", ActionEffectKind.Draw, [ActionEffectKind.Draw],
            [new(ActionCostKind.Mana, 2, false, ""), new(ActionCostKind.RemoveCounters, 1, true, "")], ActionAffordability.Payable) { EffectAmount = 1 };
        internal readonly AutomatedSeatObservation Before;
        internal readonly GameSnapshot After;
        internal Fixture()
        {
            var receipt = ForgeEngine.DecodeConfiguredSimulation(System.Text.Json.JsonSerializer.SerializeToElement(
                ConfiguredSimulationWireChecks.Receipt()), ConfiguredSimulationWireChecks.Setup());
            var before = Cards(receipt.State with { ViewerId = Actor, Revision = 30, BoardRevision = 30,
                DecisionOwnerId = Actor, PriorityPlayerId = Actor, Status = "playing", Activity = [],
                Decision = new("init", new(Source: new(Mare.VisualId, Mare, Actor)), new PriorityPayload(new("Pass", true))),
                Executions = new(1, 1, [new("old", "old", ExecutionKind.Activation, ExecutionStep.Complete) {
                    ActorPlayerId = Actor, SourceCardId = "old", Outcome = ExecutionOutcome.Resolved }],
                    [new(1, "old", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }])
            }, Mare, Toothy, Pir);
            var domain = DecisionActionDomain.None with { Sources = [new(before.Id, "init", Mare.Key, Mare.VisualId,
                [new("draw", "activate", "Draw")])], Actions = [Semantic] };
            Before = new(Actor, before, domain, new());
            After = Cards(before with { Revision = 31, BoardRevision = 31, Decision = null,
                Executions = new(1, 7, [
                    new("a", "a", ExecutionKind.Activation, ExecutionStep.Complete) { ActorPlayerId = Actor,
                        SourceCardId = Mare.VisualId, SourceName = Mare.Name, Outcome = ExecutionOutcome.Resolved },
                    new("t", "t", ExecutionKind.Trigger, ExecutionStep.Complete) { ParentId = "a", ActorPlayerId = Actor,
                        SourceCardId = Toothy.VisualId, SourceName = Toothy.Name, Outcome = ExecutionOutcome.Resolved },
                    new("p", "t", ExecutionKind.Rules, ExecutionStep.Complete) { ParentId = "t", ActorPlayerId = Actor,
                        SourceCardId = Pir.VisualId, SourceName = Pir.Name, RuleKind = ExecutionRuleKind.Replacement,
                        RuleResult = ExecutionRuleResult.Updated, Outcome = ExecutionOutcome.Resolved }
                ], [new(2, "a", ExecutionStep.Preparation), new(3, "a", ExecutionStep.Cost),
                    new(4, "a", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved },
                    new(6, "p", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved, RuleResult = ExecutionRuleResult.Updated },
                    new(7, "t", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
                Activity = [
                    new(11, 1, "MAIN1", "counters", Actor, "", Mare.VisualId, Mare.Name) { ExecutionId = "a",
                        ExecutionStep = ExecutionStep.Cost, Change = new("counters", "+1/+1", 3, 2) },
                    new(12, 1, "MAIN1", "draw", Actor, "", null, null) { ExecutionId = "a",
                        ExecutionStep = ExecutionStep.Resolution, Count = 1 },
                    new(13, 1, "MAIN1", "counters", Actor, "", Toothy.VisualId, Toothy.Name) { ExecutionId = "t",
                        ExecutionStep = ExecutionStep.Resolution, Change = new("counters", "+1/+1", 0, 2),
                        Cause = new(Toothy.VisualId, Toothy.Name, "", Actor) }
                ]
            }, Mare with { Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2) }, ToothyAfter, Pir);
        }
        internal GameCommand Command(AutomatedSeatObservation before) =>
            new UseOfferedAction(before.State.Id, before.State.Decision!.Id, Mare.Key, "draw");
        internal static GameSnapshot Cards(GameSnapshot state, params Card[] cards) => state with {
            Players = state.Players.Select(player => player.Id == Actor
                ? player with { Zones = [new("Battlefield", cards.Length, cards.ToImmutableArray(), null)] } : player).ToImmutableArray()
        };
    }
}
