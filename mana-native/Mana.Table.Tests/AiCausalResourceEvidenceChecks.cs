using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class AiCausalResourceEvidenceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var fixture = new Fixture();
        AiCausalMechanicEvidence Collect(GameSnapshot? after = null, HistoryBatch? payment = null,
            bool prime = true, bool owned = true, GameSnapshot? publicState = null, AutomatedSeatObservation? before = null)
        {
            var evidence = new AiCausalMechanicEvidence();
            var input = before ?? fixture.Before;
            evidence.ObserveOwned(input);
            if (prime) evidence.ObserveHistory(fixture.Empty);
            evidence.Receipt(input, fixture.Command(input), true);
            var result = after ?? fixture.After;
            if (owned) evidence.ObserveOwned(input with { State = result });
            if (payment != null) evidence.ObserveHistory(payment);
            evidence.ObservePublic(publicState ?? result);
            return evidence;
        }
        bool Has(AiCausalMechanicEvidence evidence, string id) => evidence.Witnesses.Any(item => item.WitnessId == id);
        const string untap = "historic-traxos-untap", colorless = "required-colorless-payment";
        var actual = Collect(payment: fixture.Payment);
        check(Has(actual, untap) && Has(actual, colorless)
                && actual.Witnesses.Single(item => item.WitnessId == colorless).HistorySequences.SequenceEqual([2L, 4L])
                && actual.Witnesses.Single(item => item.WitnessId == colorless).Payments[0].Spent?.GetValueOrDefault("C") == 1,
            "The same consumer proves a rules-certified historic cast into Traxos's actual untap and an explicit C bill decrease through actual C consumption, completed payment and resolved owned spell");
        GameSnapshot Event(long id, Func<ActivityEntry, ActivityEntry> transform) => fixture.After with {
            Activity = fixture.After.Activity.Select(item => item.Id == id ? transform(item) : item).ToImmutableArray()
        };
        GameSnapshot Execution(string id, Func<ExecutionOccurrence, ExecutionOccurrence> transform) => fixture.After with {
            Executions = fixture.After.Executions! with { Occurrences = fixture.After.Executions.Occurrences
                .Select(item => item.Id == id ? transform(item) : item).ToImmutableArray() }
        };
        foreach (var after in new[] {
            Event(11, item => item with { HistoricSpell = null }),
            Event(11, item => item with { HistoricSpell = false }),
            Event(11, item => item with { PlayerId = 83 }),
            Event(11, item => item with { CardId = "another-artifact" }),
            Event(11, item => item with { ExecutionId = "another-cast" }),
            Event(12, item => item with { ExecutionId = "a" }),
            Event(12, item => item with { ExecutionStep = ExecutionStep.Cost }),
            Event(12, item => item with { CardId = "ally-traxos" }),
            Event(12, item => item with { PlayerId = 83 }),
            Event(12, item => item with { Change = new("state", "tapped", 0, 0) }),
            Execution("t", item => item with { ParentId = null, BudgetRootId = "a" }),
            Execution("t", item => item with { ParentId = "unrelated" }),
            Execution("t", item => item with { SourceCardId = "ally-traxos" }),
            Execution("t", item => item with { ActorPlayerId = 83 }),
            Execution("t", item => item with { Outcome = ExecutionOutcome.Declined })
        }) check(!Has(Collect(after), untap),
            "Unknown/nonhistoric casts, foreign occurrences, no-op or unrelated untaps, non-Resolution changes and a same-budget sibling cannot certify Traxos's defining interaction");
        check(!Has(Collect(fixture.After with { Activity = fixture.After.Activity.Where(item => item.Id != 12).ToImmutableArray() }), untap),
            "A completed Traxos trigger without the actual committed untap mutation receives no credit");
        var retapped = Fixture.Cards(fixture.After, fixture.Source, fixture.Traxos);
        check(!Has(Collect(publicState: retapped), untap),
            "An inconsistent currently visible tapped Traxos cannot confirm this bounded untap witness");
        var countered = Collect(Execution("a", item => item with { Outcome = ExecutionOutcome.Countered }), fixture.Payment);
        check(Has(countered, untap) && !Has(countered, colorless),
            "A real historic cast still triggers a resolved untap when that spell is later countered, but it cannot qualify a paid-and-resolved colorless spell");
        var renamedType = fixture.Before with { State = Fixture.Cards(fixture.Before.State,
            fixture.Source with { Type = "Creature" }, fixture.Traxos) };
        check(Has(Collect(before: renamedType), untap),
            "The verifier consumes the actual route's rules-certified historic fact rather than inferring it from the physical source's displayed type");

        HistoryBatch Payment(Func<HistoryRecord, HistoryRecord> change) => fixture.Payment with {
            Records = fixture.Payment.Records.Select(change).ToImmutableArray()
        };
        foreach (var payment in new[] {
            Payment(item => item.Payment == null ? item : item with { ExecutionId = "foreign" }),
            Payment(item => item with { Payment = item.Payment! with { PayerId = 83 } }),
            Payment(item => item.Sequence != 2 ? item : item with { Payment = item.Payment! with {
                BillBefore = [new("{1}", 1)], BillAfter = [], Spent = ImmutableDictionary<string, int>.Empty.Add("C", 1)
            } }),
            Payment(item => item.Sequence != 2 ? item : item with { Payment = item.Payment! with {
                Spent = ImmutableDictionary<string, int>.Empty.Add("U", 1)
            } }),
            Payment(item => item.Sequence != 2 ? item : item with { Payment = item.Payment! with {
                BillAfter = item.Payment.BillBefore
            } }),
            Payment(item => item.Sequence != 4 ? item : item with { SourceCardId = "another-source" }),
            Payment(item => item.Sequence != 4 ? item : item with { Payment = item.Payment! with { Kind = "cancelled" } }),
            fixture.Payment with { Records = fixture.Payment.Records.Where(item => item.Sequence != 4).ToImmutableArray(), LastSequence = 3, NextSequence = 3 }
        }) check(!Has(Collect(payment: payment), colorless),
            "Actual C requirements need exact payer/execution, decreasing C bill, C consumption and completed bill; generic payment, other colors, unchanged bills, foreign completion or cancellation never qualify");
        var produced = Payment(item => item.Sequence != 2 ? item : item with { Payment = new("produced", Fixture.Actor, "manual") {
            Produced = ImmutableDictionary<string, int>.Empty.Add("C", 9)
        } });
        check(!Has(Collect(payment: produced), colorless),
            "Producing colorless mana or observing a pool cannot stand in for an explicit C bill component being consumed");
        check(!Has(Collect(payment: fixture.Payment, prime: false), colorless),
            "An accepted cast without an owned history baseline cannot retroactively borrow a later first-disclosed payment batch");
        var gapped = fixture.Payment with { FirstSequence = 2, Records = fixture.Payment.Records.Where(item => item.Sequence > 1).ToImmutableArray() };
        var lost = Collect(payment: gapped);
        check(!Has(lost, colorless) && lost.IsIncomplete(fixture.Before.State.Id, Fixture.Actor),
            "History retention loss invalidates the pending payment epoch instead of certifying a bill across missing records");
        var noOwned = Collect(payment: fixture.WithExecutionHistory, owned: false);
        noOwned.ObserveOwned(fixture.Before);
        noOwned.ObservePublic(fixture.After);
        check(!Has(noOwned, colorless) && !Has(noOwned, untap),
            "Archival completed execution facts can lead the owned/public publication; history alone cannot claim a live committed resolution");
        noOwned.ObserveOwned(fixture.Before with { State = fixture.After });
        noOwned.ObservePublic(fixture.After);
        check(Has(noOwned, colorless) && Has(noOwned, untap),
            "The same retained archival facts become usable after their actual owned terminal and corresponding public cohort are committed");
        check(!Has(Collect(payment: fixture.Payment, publicState: fixture.Before.State), colorless),
            "An old spectator cohort cannot confirm the newer owned resolved spell even when payment history is available");
        var restored = new HistoryRecord(5, 1, "MAIN1", "public") {
            ExecutionId = "a", Payment = new("restored", Fixture.Actor, "manual") { RestoredExecutionId = "a" }
        };
        var beforeRestore = Collect(payment: fixture.Payment);
        beforeRestore.ObserveHistory(fixture.Payment with { LastSequence = 5, NextSequence = 5, Records = [restored] });
        check(!Has(beforeRestore, colorless) && Has(beforeRestore, untap),
            "A later authoritative payment restoration revokes only that colorless payment witness while retaining an independently proven historic untap");
        var duplicate = Collect(payment: fixture.Payment);
        duplicate.ObserveHistory(fixture.Payment);
        duplicate.ObserveOwned(fixture.Before with { State = fixture.After });
        duplicate.ObservePublic(fixture.After);
        check(duplicate.Witnesses.Count(item => item.WitnessId == colorless) == 1 && duplicate.HistoryCursor(fixture.Before.State.Id, Fixture.Actor) == 4,
            "Repeated owned/history/public snapshots neither duplicate consumption nor advance its ordinal beyond the exact ledger batch");
        bool forkRejected = false;
        try { duplicate.ObserveHistory(Payment(item => item.Sequence != 2 ? item : item with {
            Payment = item.Payment! with { Spent = ImmutableDictionary<string, int>.Empty.Add("U", 1) }
        })); } catch (InvalidDataException) { forkRejected = true; }
        check(forkRejected, "An immutable history ordinal returning a different payment fact is rejected rather than silently preserving conflicting evidence");
        var boundedHistory = new AiCausalMechanicEvidence();
        boundedHistory.ObserveOwned(fixture.Before); boundedHistory.ObserveHistory(fixture.Empty);
        boundedHistory.Receipt(fixture.Before, fixture.Command(fixture.Before), true);
        boundedHistory.ObserveOwned(fixture.Before with { State = fixture.After });
        for (int first = 1; first <= 1025; first += 128) {
            int count = Math.Min(128, 1026 - first);
            long next = first + count - 1;
            boundedHistory.ObserveHistory(new(1, fixture.Before.State.Id, Fixture.Actor, 1, 1025, next, next < 1025,
                Enumerable.Range(first, count).Select(sequence => new HistoryRecord(sequence, 1, "MAIN1", "seatPrivate") {
                    Payment = new("opened", Fixture.Actor, "manual") { BillBefore = [], BillAfter = [] }
                }).ToImmutableArray()));
        }
        boundedHistory.ObserveHistory(fixture.Payment with { LastSequence = 1029, NextSequence = 1029,
            Records = fixture.Payment.Records.Select(item => item with { Sequence = item.Sequence + 1025 }).ToImmutableArray() });
        boundedHistory.ObservePublic(fixture.After);
        check(!Has(boundedHistory, colorless) && boundedHistory.IsIncomplete(fixture.Before.State.Id, Fixture.Actor),
            "Local history retention is bounded; a later valid bill cannot repair a cast whose evidence epoch was dropped at the limit");
        var unsupported = new AiCausalMechanicEvidence();
        unsupported.ObserveOwned(fixture.Before);
        unsupported.HistoryUnavailable(fixture.Before.State.Id, Fixture.Actor);
        check(unsupported.IsHistoryUnavailable(fixture.Before.State.Id, Fixture.Actor),
            "An older provider's missing automated history capability remains explicit unavailable evidence");
        CheckBoundedReader(fixture, check);
    }

    private static void CheckBoundedReader(Fixture fixture, Action<bool, string> check)
    {
        var evidence = new AiCausalMechanicEvidence();
        evidence.ObserveOwned(fixture.Before);
        var provider = new HistoryProvider(fixture.Before.State.Id);
        var reader = new AiCausalHistoryReader();
        reader.DrainAsync(provider, [fixture.Before], evidence).GetAwaiter().GetResult();
        check(provider.Requests == 2 && evidence.HistoryCursor(fixture.Before.State.Id, Fixture.Actor) == 4,
            "One owned observation drains at most two bounded chunks and retains its exact history cursor");
        reader.DrainAsync(provider, [fixture.Before], evidence).GetAwaiter().GetResult();
        reader.DrainAsync(provider, [fixture.Before], evidence).GetAwaiter().GetResult();
        check(provider.Requests == 3 && evidence.HistoryCursor(fixture.Before.State.Id, Fixture.Actor) == 6,
            "An unchanged publication continues known backlog on its next bounded poll, then stops issuing redundant history reads");
    }

    private sealed class HistoryProvider(string gameId) : IAutomatedSeatProvider, IAutomatedMatchHistory
    {
        internal int Requests;
        public bool SupportsAutomatedHistory => true;
        public Task<HistoryBatch> AutomatedHistoryAsync(string game, int seat, long after, int limit = 128)
        {
            if (game != gameId || seat != Fixture.Actor || limit != 128)
                throw new InvalidDataException("The evidence reader changed its current owned query.");
            Requests++;
            long next = Math.Min(after + 2, 6);
            return Task.FromResult(new HistoryBatch(1, gameId, Fixture.Actor, 1, 6, next, next < 6,
                Enumerable.Range((int)after + 1, (int)(next - after)).Select(sequence =>
                    new HistoryRecord(sequence, 1, "MAIN1", "seatPrivate") {
                        Payment = new("opened", Fixture.Actor, "manual") { BillBefore = [], BillAfter = [] }
                    }).ToImmutableArray()));
        }
        public Task EnableAutomatedSeatsAsync() => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync() => throw new NotSupportedException();
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command) => throw new InvalidOperationException("A history reader cannot answer gameplay.");
        public Task ReportAutomationFaultAsync(string game, int seat, string reason) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        internal const int Actor = 47;
        internal readonly Card Source = new() { Key = "source-key", VisualId = "source", Name = "Mind Stone", Type = "Artifact" };
        internal readonly Card Traxos = new() { Key = "traxos-key", VisualId = "traxos", Name = "Traxos, Scourge of Kroog",
            Type = "Legendary Artifact Creature", Tapped = true };
        internal readonly AutomatedSeatObservation Before;
        internal readonly GameSnapshot After;
        internal readonly HistoryBatch Empty, Payment;
        internal Fixture()
        {
            var receipt = ForgeEngine.DecodeConfiguredSimulation(System.Text.Json.JsonSerializer.SerializeToElement(
                ConfiguredSimulationWireChecks.Receipt()), ConfiguredSimulationWireChecks.Setup());
            var state = Cards(receipt.State with { ViewerId = Actor, Status = "playing", Revision = 30, BoardRevision = 30,
                PriorityPlayerId = Actor, DecisionOwnerId = Actor, Activity = [],
                Decision = new("start", new(Source: new(Source.VisualId, Source, Actor)), new PriorityPayload(new("Pass", true))),
                Executions = new(1, 1, [new("old", "old", ExecutionKind.Mana, ExecutionStep.Complete) {
                    ActorPlayerId = Actor, SourceCardId = "old", Outcome = ExecutionOutcome.Resolved
                }], [new(1, "old", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }])
            }, Source, Traxos);
            Before = new(Actor, state, DecisionActionDomain.None with {
                Sources = [new(state.Id, "start", Source.Key, Source.VisualId, [new("cast", "cast", "Cast")])],
                Actions = [new("cast", ActionEffectKind.Permanent, [ActionEffectKind.Permanent],
                    [new(ActionCostKind.Mana, 2, false, "") { ManaSymbols = ["1", "C"] }], ActionAffordability.Payable)]
            }, new());
            After = Cards(state with { Revision = 31, BoardRevision = 31, Decision = null,
                Executions = new(1, 7, [
                    new("a", "a", ExecutionKind.Spell, ExecutionStep.Complete) {
                        ActorPlayerId = Actor, SourceCardId = Source.VisualId, SourceName = Source.Name, Outcome = ExecutionOutcome.Resolved },
                    new("t", "t", ExecutionKind.Trigger, ExecutionStep.Complete) {
                        ParentId = "a", ActorPlayerId = Actor, SourceCardId = Traxos.VisualId, SourceName = Traxos.Name, Outcome = ExecutionOutcome.Resolved }
                ], [new(2, "a", ExecutionStep.Preparation), new(3, "a", ExecutionStep.Cost), new(4, "a", ExecutionStep.Stack),
                    new(5, "t", ExecutionStep.Preparation), new(6, "t", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved },
                    new(7, "a", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
                Activity = [
                    new(11, 1, "MAIN1", "cast", Actor, "", Source.VisualId, Source.Name) {
                        ExecutionId = "a", ExecutionStep = ExecutionStep.Stack, HistoricSpell = true },
                    new(12, 1, "MAIN1", "tapped", Actor, "", Traxos.VisualId, Traxos.Name) {
                        ExecutionId = "t", ExecutionStep = ExecutionStep.Resolution, Change = new("state", "tapped", 1, 0) }
                ]
            }, Source, Traxos with { Tapped = false });
            Empty = new(1, state.Id, Actor, 1, 0, 0, false, []);
            var original = ImmutableArray.Create(new ManaBillPart("{1}", 1), new ManaBillPart("{C}", 1));
            var generic = ImmutableArray.Create(new ManaBillPart("{1}", 1));
            Payment = new(1, state.Id, Actor, 1, 4, 4, false, [
                Record(1, new("opened", Actor, "manual") { BillBefore = original, BillAfter = original }),
                Record(2, new("spent", Actor, "manual") {
                    BillBefore = original, BillAfter = generic, Spent = ImmutableDictionary<string, int>.Empty.Add("C", 1) }),
                Record(3, new("spent", Actor, "manual") {
                    BillBefore = generic, BillAfter = [], Spent = ImmutableDictionary<string, int>.Empty.Add("C", 1) }),
                Record(4, new("completed", Actor, "manual") { BillBefore = original, BillAfter = [] })
            ]);
        }
        private HistoryRecord Record(long sequence, HistoryPayment payment) => new(sequence, 1, "MAIN1", "public") {
            ExecutionId = "a", SourceCardId = Source.VisualId, SourceName = Source.Name, Payment = payment
        };
        internal HistoryBatch WithExecutionHistory => Payment with {
            Records = [
                new(1, 1, "MAIN1", "public") { ExecutionId = "a", Execution = new(After.Executions!.Occurrences[0] with {
                    Step = ExecutionStep.Preparation, Outcome = null }, new(2, "a", ExecutionStep.Preparation)) },
                new(2, 1, "MAIN1", "public") { ExecutionId = "a", Activity = After.Activity[0] },
                .. Payment.Records.Select(item => item with { Sequence = item.Sequence + 2 }),
                new(7, 1, "MAIN1", "public") { ExecutionId = "t", Activity = After.Activity[1] },
                new(8, 1, "MAIN1", "public") { ExecutionId = "t", ParentId = "a", Execution = new(After.Executions.Occurrences[1],
                    new(6, "t", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }) },
                new(9, 1, "MAIN1", "public") { ExecutionId = "a", Execution = new(After.Executions.Occurrences[0],
                    new(7, "a", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }) }
            ], LastSequence = 9, NextSequence = 9
        };
        internal GameCommand Command(AutomatedSeatObservation before) =>
            new UseOfferedAction(before.State.Id, before.State.Decision!.Id, Source.Key, "cast");
        internal static GameSnapshot Cards(GameSnapshot state, params Card[] cards) => state with {
            Players = state.Players.Select(player => player.Id == Actor
                ? player with { Zones = [new("Battlefield", cards.Length, cards.ToImmutableArray(), null)] } : player).ToImmutableArray()
        };
    }
}
