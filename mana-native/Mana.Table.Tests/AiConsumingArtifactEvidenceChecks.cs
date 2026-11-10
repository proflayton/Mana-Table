using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class AiConsumingArtifactEvidenceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var fixture = new Fixture();
        const string witness = "positive-consuming-artifact-activation";
        AiCausalMechanicEvidence Collect(GameSnapshot? after = null, AutomatedSeatObservation? before = null,
            bool accepted = true, GameSnapshot? publicState = null)
        {
            var input = before ?? fixture.Before;
            var evidence = new AiCausalMechanicEvidence();
            evidence.Receipt(input, new UseOfferedAction(input.State.Id, input.State.Decision!.Id, fixture.Source.Key, "draw"), accepted);
            evidence.ObserveOwned(input with { State = after ?? fixture.After });
            evidence.ObservePublic(publicState ?? after ?? fixture.After);
            return evidence;
        }
        bool Has(AiCausalMechanicEvidence evidence) => evidence.Witnesses.Any(item => item.WitnessId == witness);
        var normal = Collect();
        check(Has(normal) && normal.Witnesses.Single(item => item.WitnessId == witness) is { } proof
                && proof.SourceCardId == fixture.Source.VisualId && proof.ActivityIds.SequenceEqual([11L, 12L])
                && proof.CausalExecutionIds.SequenceEqual(["activation"]),
            "A name-independent owned artifact sacrifice, exact resolved activation and attributed one-card draw certify a positive consuming activation with public hand/library/source confirmation");
        check(!Has(Collect(accepted: false)), "A rejected artifact initiation cannot acquire a later sacrifice/draw witness");
        GameSnapshot Event(long id, Func<ActivityEntry, ActivityEntry> change) => fixture.After with {
            Activity = fixture.After.Activity.Select(item => item.Id == id ? change(item) : item).ToImmutableArray()
        };
        GameSnapshot Execution(Func<ExecutionOccurrence, ExecutionOccurrence> change) => fixture.After with {
            Executions = fixture.After.Executions! with { Occurrences = fixture.After.Executions.Occurrences.Select(change).ToImmutableArray() }
        };
        foreach (var outcome in new[] { ExecutionOutcome.Cancelled, ExecutionOutcome.Countered, ExecutionOutcome.Failed }) {
            var result = Collect(Execution(item => item with { Outcome = outcome }));
            check(!Has(result) && result.Witnesses.Any(item => item.WitnessId == "source-cost-ownership"),
                "A real artifact sacrifice remains committed-cost evidence when its activation does not resolve, but never proves a positive consuming ability");
        }
        foreach (var after in new[] {
            Execution(item => item with { ActorPlayerId = 83 }),
            Execution(item => item with { SourceCardId = "different-artifact" }),
            Event(11, item => item with { CardId = "different-artifact" }),
            Event(11, item => item with { PlayerId = 83 }),
            Event(11, item => item with { ExecutionStep = ExecutionStep.Resolution }),
            Event(11, item => item with { Impact = item.Impact! with { FromZone = "Hand" } }),
            Event(12, item => item with { ExecutionId = "different-activation" }),
            Event(12, item => item with { PlayerId = 83 }),
            Event(12, item => item with { Count = 0 }),
            Event(12, item => item with { Count = 2 }),
            Event(12, item => item with { ExecutionStep = ExecutionStep.Cost }),
            fixture.After with { Activity = fixture.After.Activity.Where(item => item.Id != 12).ToImmutableArray() }
        }) check(!Has(Collect(after)),
            "A foreign source/actor, non-battlefield or non-Cost sacrifice, missing draw, unrelated draw, wrong recipient/amount or wrong stage cannot qualify a consuming artifact ability");
        foreach (var input in new[] {
            fixture.Before with { State = fixture.WithSource(fixture.Before.State, fixture.Source with { Type = "Creature" }) },
            fixture.Before with { Domain = fixture.Before.Domain with { Actions = [fixture.Semantic with { EffectAmount = null }] } },
            fixture.Before with { Domain = fixture.Before.Domain with { Actions = [fixture.Semantic with { EffectAmount = 2 }] } },
            fixture.Before with { Domain = fixture.Before.Domain with { Actions = [fixture.Semantic with {
                Costs = [new(ActionCostKind.Sacrifice, 1, false, "")]
            }] } },
            fixture.Before with { State = fixture.WithCounts(fixture.Before.State, 0, 0) }
        }) check(!Has(Collect(before: input)),
            "Only an actual owned artifact source with a known one-card draw, source sacrifice cost and nonempty library can receive this bounded witness");
        foreach (var publicState in new[] {
            fixture.WithCounts(fixture.After, 0, 1),
            fixture.WithCounts(fixture.After, 1, 2),
            fixture.WithSource(fixture.After with { Players = fixture.After.Players.Select(player => player.Id == Fixture.Actor
                ? player with { Zones = player.Zones.Select(zone => zone.Name == "Graveyard"
                    ? new Zone("Graveyard", 0, [], null) : zone).ToImmutableArray() } : player).ToImmutableArray() }, fixture.Source),
            fixture.After with { BoardRevision = 30 }
        }) check(!Has(Collect(publicState: publicState)),
            "A missing hand gain, unchanged library, restored battlefield source or stale public frame cannot confirm the consuming draw");
        var repeated = new AiCausalMechanicEvidence();
        repeated.Receipt(fixture.Before, new UseOfferedAction(fixture.Before.State.Id, "init", fixture.Source.Key, "draw"), true);
        for (int index = 0; index < 3; index++) {
            repeated.ObserveOwned(fixture.Before with { State = fixture.After }); repeated.ObservePublic(fixture.After);
        }
        check(repeated.Witnesses.Count(item => item.WitnessId == witness) == 1,
            "Repeated owner/public delivery retains one consuming artifact witness instead of counting repeated observations as new decisions");
        var lost = new AiCausalMechanicEvidence(maximumActivities: 1);
        lost.Receipt(fixture.Before, new UseOfferedAction(fixture.Before.State.Id, "init", fixture.Source.Key, "draw"), true);
        lost.ObserveOwned(fixture.Before with { State = fixture.After }); lost.ObservePublic(fixture.After);
        check(!Has(lost) && lost.IsIncomplete(fixture.Before.State.Id, Fixture.Actor),
            "Consuming artifact proof cannot cross a bounded evidence-loss epoch");
    }

    internal sealed class Fixture
    {
        internal const int Actor = 47;
        internal readonly Card Source = new() { Key = "artifact-key", VisualId = "artifact", Name = "Uncatalogued artifact", Type = "Artifact" };
        internal readonly ActionSemantics Semantic = new("draw", ActionEffectKind.Draw, [ActionEffectKind.Draw],
            [new(ActionCostKind.Mana, 1, false, ""), new(ActionCostKind.Sacrifice, 1, true, "")], ActionAffordability.Payable) { EffectAmount = 1 };
        internal readonly AutomatedSeatObservation Before;
        internal readonly GameSnapshot After;
        private readonly int actor;
        internal Fixture(int actor = Actor)
        {
            this.actor = actor;
            var receipt = ForgeEngine.DecodeConfiguredSimulation(System.Text.Json.JsonSerializer.SerializeToElement(
                ConfiguredSimulationWireChecks.Receipt()), ConfiguredSimulationWireChecks.Setup());
            var before = WithSource(WithCounts(receipt.State with { ViewerId = actor, Revision = 30, BoardRevision = 30,
                DecisionOwnerId = actor, PriorityPlayerId = actor, Status = "playing", Activity = [],
                Decision = new("init", new(Source: new(Source.VisualId, Source, actor)), new PriorityPayload(new("Pass", true))),
                Executions = new(1, 1, [], [])
            }, 0, 2), Source);
            Before = new(actor, before, DecisionActionDomain.None with {
                Sources = [new(before.Id, "init", Source.Key, Source.VisualId, [new("draw", "activate", "Draw")])],
                Actions = [Semantic]
            }, new());
            After = WithCounts(before with { Revision = 31, BoardRevision = 31, Decision = null,
                Players = before.Players.Select(player => player.Id == actor ? player with { Zones = [
                    new("Battlefield", 0, [], null), new("Graveyard", 1, [Source], null)
                ] } : player).ToImmutableArray(),
                Executions = new(1, 4, [new("activation", "activation", ExecutionKind.Activation, ExecutionStep.Complete) {
                    ActorPlayerId = actor, SourceCardId = Source.VisualId, SourceName = Source.Name, Outcome = ExecutionOutcome.Resolved
                }], [new(2, "activation", ExecutionStep.Preparation), new(3, "activation", ExecutionStep.Cost),
                    new(4, "activation", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
                Activity = [
                    new(11, 1, "MAIN1", "zone", actor, "", Source.VisualId, Source.Name) {
                        ExecutionId = "activation", ExecutionStep = ExecutionStep.Cost,
                        Impact = new("zone", 1, CardId: Source.VisualId, RecipientControllerId: actor, FromZone: "Battlefield", ToZone: "Graveyard")
                    },
                    new(12, 1, "MAIN1", "draw", actor, "", null, null) {
                        ExecutionId = "activation", ExecutionStep = ExecutionStep.Resolution, Count = 1
                    }
                ]
            }, 1, 1);
        }
        internal GameSnapshot WithSource(GameSnapshot state, Card source) => state with {
            Players = state.Players.Select(player => player.Id == actor ? player with {
                Zones = player.Zones.Where(zone => zone.Name != "Battlefield").Append(new Zone("Battlefield", 1, [source], null)).ToImmutableArray()
            } : player).ToImmutableArray()
        };
        internal GameSnapshot WithCounts(GameSnapshot state, int hand, int library) => state with {
            Players = state.Players.Select(player => player.Id == actor ? player with {
                Zones = player.Zones.Where(zone => zone.Name is not ("Hand" or "Library"))
                    .Append(new Zone("Hand", hand, [], null)).Append(new Zone("Library", library, [], null)).ToImmutableArray()
            } : player).ToImmutableArray()
        };
    }
}
