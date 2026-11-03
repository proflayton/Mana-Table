using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.AI;

namespace Mana.Table;

internal static class AiCampaignQualificationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        AiCausalMechanicEvidenceChecks.Run(check);
        AiCausalResourceEvidenceChecks.Run(check);
        var decks = AiCampaignDecks.All.Select((deck, index) => new AiCampaignDeckBinding(deck.Id, deck.Hash,
            new string((char)('a' + index), 64), "saved-" + index, 1, "deck:saved-" + index + ":bytes", deck.Family,
            deck.IntendedMechanics, deck.RequiredWitnessIds)).ToImmutableArray();
        var decoded = ForgeEngine.DecodeConfiguredSimulation(JsonSerializer.SerializeToElement(ConfiguredSimulationWireChecks.Receipt()),
            ConfiguredSimulationWireChecks.Setup());
        var start = decoded with { Setup = decoded.Setup with { Decks = decks.Select(deck => deck.Handle).ToImmutableArray() } };
        string hash = AiCampaignQualification.DefinitionHash("case", start.Setup.Specification, decks, "smoke");
        AiCampaignQualificationTracker Tracker() => new(decks, start, "case", hash);
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        check(hash != AiCampaignQualification.DefinitionHash("case", start.Setup.Specification,
                decks.SetItem(0, decks[0] with { SemanticVersionId = new('f', 64) }), "smoke")
                && hash != AiCampaignQualification.DefinitionHash("case", start.Setup.Specification, decks, "release")
                && hash != AiCampaignQualification.DefinitionHash("other-case", start.Setup.Specification, decks, "smoke"),
            "Qualification configuration identity changes with semantic deck content, case or policy profile; friendly candidate names cannot preserve old credit");
        check(Reject(() => _ = new AiCampaignQualificationTracker(decks.SetItem(0, decks[0] with { CandidateId = "unknown" }), start, "case", hash))
                && Reject(() => _ = new AiCampaignQualificationTracker(decks.SetItem(0, decks[0] with { SourceSha256 = new('f', 64) }), start, "case", hash))
                && Reject(() => _ = new AiCampaignQualificationTracker(decks.SetItem(0, decks[0] with { Handle = "different-selection" }), start, "case", hash)),
            "Unknown candidates, changed source lists and mismatched accepted deck handles fail closed before per-deck attribution");
        const int actor = 29;
        var rhys = new Card { Key = "rhys-key", VisualId = "rhys", Name = "Rhys the Redeemed", Type = "Legendary Creature" };
        var token = new Card { VisualId = "new-token", Name = "Elf Warrior Token", Type = "Creature", GamePieceType = "token" };
        GameSnapshot Cards(GameSnapshot frame, int playerId, params Card[] cards) => frame with {
            Players = frame.Players.Select(player => player.Id == playerId
                ? player with { Zones = [new("Battlefield", cards.Length, cards.ToImmutableArray(), null)] } : player).ToImmutableArray()
        };
        var before = Cards(start.State with { ViewerId = actor, Revision = 10, BoardRevision = 10 }, actor, rhys) with {
            Decision = new("current", new(Source: new(rhys.VisualId, rhys, actor)), new PriorityPayload(new("Pass", true))) {
                ExecutionId = "activation", ExecutionStep = ExecutionStep.Preparation },
            Executions = new(1, 1, [new("activation", "activation", ExecutionKind.Activation, ExecutionStep.Preparation) {
                ActorPlayerId = actor, SourceCardId = rhys.VisualId, SourceName = rhys.Name }], [new(1, "activation", ExecutionStep.Preparation)])
        };
        var domain = DecisionActionDomain.None with { Sources = [new(before.Id, "current", rhys.Key, rhys.VisualId,
            [new("ability", "activate", "Create a token")])] };
        var observation = new AutomatedSeatObservation(actor, before, domain, new());
        var command = new UseOfferedAction(before.Id, "current", rhys.Key, "ability");
        var entered = new ActivityEntry(12, 1, "MAIN1", "zone", actor, "", token.VisualId, token.Name) {
            ExecutionId = "activation", ExecutionStep = ExecutionStep.Resolution,
            Cause = new(rhys.VisualId, rhys.Name, "", actor),
            Impact = new("zone", 1, token.VisualId, token.Name, SourceCardId: rhys.VisualId,
                SourceControllerId: actor, RecipientControllerId: actor, ToZone: "Battlefield")
        };
        var after = Cards(before with { Revision = 11, BoardRevision = 11, Decision = null,
            Executions = new(1, 2, [new("activation", "activation", ExecutionKind.Activation, ExecutionStep.Complete) {
                ActorPlayerId = actor, SourceCardId = rhys.VisualId, Outcome = ExecutionOutcome.Resolved }],
                [new(1, "activation", ExecutionStep.Preparation), new(2, "activation", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]),
            Activity = [entered] }, actor, rhys, token);
        var publicAfter = after with { ViewerId = -1, Activity = [], Players = after.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with { Key = "" }).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        bool Has(AiCampaignQualificationTracker tracker) => tracker.Summary("turn-budget", false, null)[0].RequiredWitnesses
            .Single(item => item.Id == "rhys-paid-tokens").Status == "observed";
        var noReceipt = Tracker(); noReceipt.ObserveOwned(observation with { State = after }); noReceipt.ObservePublic(publicAfter);
        var rejected = Tracker(); rejected.Receipt(observation, command, false, "rejected"); rejected.ObserveOwned(observation with { State = after }); rejected.ObservePublic(publicAfter);
        var acceptedOnly = Tracker(); acceptedOnly.Receipt(observation, command, true, null); acceptedOnly.ObservePublic(publicAfter);
        check(!Has(noReceipt) && !Has(rejected) && !Has(acceptedOnly),
            "Offered routes, rejected commands, accepted initiation alone and global token changes cannot satisfy a deck's paid-token witness");
        var credited = Tracker();
        string evidenceDirectory = Path.Combine(Path.GetTempPath(), "ManaTableNative-tests", "qualification-" + Guid.NewGuid());
        Directory.CreateDirectory(evidenceDirectory);
        ImmutableArray<AutomatedSeatObservation> Batch(GameSnapshot frame) => start.Participants.Select(participant => {
            bool owner = participant.PlayerId == actor;
            var owned = frame with { ViewerId = participant.PlayerId, Decision = owner ? frame.Decision : null,
                Players = frame.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Cards = zone.Cards.Select(card => card with { Key = player.Id == participant.PlayerId ? card.Key : "" }).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray() };
            return new AutomatedSeatObservation(participant.PlayerId, owned, owner && frame.Decision != null ? domain : DecisionActionDomain.None, new());
        }).ToImmutableArray();
        using (var diagnostics = new AiCampaignDiagnostics(evidenceDirectory)) {
            diagnostics.BindQualification(credited);
            var inner = new PublishedProvider(Batch(before), Batch(after));
            var provider = new AiCampaignProvider(inner, diagnostics); provider.BindConfigured(start);
            provider.ObserveAutomatedSeatsAsync().GetAwaiter().GetResult();
            provider.ExecuteAutomatedAsync(actor, command).GetAwaiter().GetResult();
            provider.ObserveAutomatedSeatsAsync().GetAwaiter().GetResult();
            diagnostics.ObservePublic(publicAfter);
        }
        check(Has(credited) && credited.Summary("turn-budget", false, null).Skip(1).All(deck => deck.RequiredWitnesses.All(witness => witness.Status != "observed"))
                && credited.Summary("turn-budget", false, null)[0].RequiredWitnesses.Single(item => item.Id == "rhys-paid-tokens").Evidence.Single().PlayerId == actor,
            "One accepted exact-source activation and attributed authoritative token entry credits only its pinned deck and actual mapped player");
        var foreign = Tracker(); foreign.Receipt(observation, command, true, null);
        foreign.ObserveOwned(observation with { State = after with { Activity = [entered with { Cause = entered.Cause! with { ControllerId = 83 } }] } });
        foreign.ObservePublic(publicAfter);
        check(!Has(foreign), "An ally's identical source/counter/token event cannot be borrowed as this deck's defining mechanic");
        check(Tracker().Summary("time-budget", false, null).All(deck => deck.SampledNaturalGame == "inconclusive" && deck.HumanReview == "pending"
                && deck.RequiredWitnesses.All(witness => witness.Status == (AiCampaignQualification.SupportedWitnessIds.Contains(witness.Id) ? "inconclusive" : "not-instrumented")))
                && Tracker().Summary("turn-budget", false, null).All(deck => deck.RequiredWitnesses.All(witness => witness.Status == (AiCampaignQualification.SupportedWitnessIds.Contains(witness.Id) ? "not-observed" : "not-instrumented"))),
            "A cutoff, an implemented but unobserved branch, an absent verifier, natural completion and pending human review remain distinct");
        var wrongCard = rhys with { Key = "other-key", VisualId = "other" , Selectable = true };
        var costState = Cards(before, actor, rhys, wrongCard) with { Decision = new("cost", new(Source: new(rhys.VisualId, rhys, actor)),
            new EntitySelectionPayload(new("Done", true), new("Cancel", true), [])) { ExecutionId = "activation" } };
        var cost = new CostPaymentFunding(rhys.VisualId, actor, [new(ActionCostKind.Tap, 1, true, "")],
            new(SemanticKnowledge.Known, ImmutableDictionary<string, int>.Empty, [])) { CurrentCostIndex = 0 };
        var wrong = Tracker(); wrong.Receipt(new(actor, costState, DecisionActionDomain.None, new() { CostPayment = cost }),
            new SelectCard(costState.Id, "cost", wrongCard.Key), true, null);
        check(wrong.Summary("turn-budget", false, null)[0].IntegrityFailures.IsEmpty,
            "Accepted cost selection remains unobserved until an authoritative committed cost consequence follows");
        var cancelled = new ExecutionFrame(1, 2, [new("activation", "activation", ExecutionKind.Activation, ExecutionStep.Complete) {
            ActorPlayerId = actor, SourceCardId = rhys.VisualId, Outcome = ExecutionOutcome.Cancelled }],
            [new(1, "activation", ExecutionStep.Preparation), new(2, "activation", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Cancelled }]);
        var afterCost = Cards(costState with { BoardRevision = 11, Revision = 11, Executions = cancelled }, actor, rhys, wrongCard with { Tapped = true });
        wrong.ObserveOwned(new(actor, afterCost, DecisionActionDomain.None, new()));
        check(wrong.Summary("turn-budget", false, null)[0].IntegrityFailures.IsEmpty,
            "A cancelled uncommitted selection followed by an unrelated tap never becomes paid-cost success or universal integrity failure");
        var counterState = Cards(costState, actor, rhys, wrongCard with { Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) });
        var counterCost = cost with { RemainingCosts = [new(ActionCostKind.RemoveCounters, 1, true, "+1/+1")] };
        var committedWrong = Tracker();
        committedWrong.Receipt(new(actor, counterState, DecisionActionDomain.None, new() { CostPayment = counterCost }),
            new SelectCard(counterState.Id, "cost", wrongCard.Key), true, null);
        var counterMutation = new ActivityEntry(20, 1, "MAIN1", "counters", actor, "", wrongCard.VisualId, wrongCard.Name) {
            ExecutionId = "activation", ExecutionStep = ExecutionStep.Cost, Change = new("counters", "+1/+1", 1, 0)
        };
        var committedAfter = Cards(counterState with { BoardRevision = 11, Revision = 11, Executions = cancelled,
            Activity = [counterMutation] }, actor, rhys, wrongCard);
        committedWrong.ObserveOwned(new(actor, committedAfter, DecisionActionDomain.None, new()));
        check(committedWrong.Summary("turn-budget", false, null)[0] is { MechanicalIntegrity: "failed", IntegrityFailures.Length: 1 }
                && !committedWrong.Summary("turn-budget", false, null)[0].RequiredWitnesses.Any(item => item.Id == "source-cost-ownership")
                && committedWrong.Summary("turn-budget", false, null)[0].IntegrityFailures[0].ActivityIds.SequenceEqual([20L]),
            "An exact attributed Cost-stage mutation of the wrong occurrence fails the non-Pir Rhys deck independently of its defining mechanics");
        const int pirActor = 47;
        var loch = wrongCard with { Key = "loch-key", VisualId = "loch", Name = "Loch Mare", Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) };
        var paidBefore = Cards(start.State with { ViewerId = pirActor, BoardRevision = 30, Revision = 30 }, pirActor, loch) with {
            Decision = new("paid-cost", new(Source: new(loch.VisualId, loch, pirActor)),
                new EntitySelectionPayload(new("Done", true), new("Cancel", true), [])) { ExecutionId = "paid-counter" },
            Executions = new(1, 1, [new("paid-counter", "paid-counter", ExecutionKind.Activation, ExecutionStep.Cost) {
                ActorPlayerId = pirActor, SourceCardId = loch.VisualId }], [new(1, "paid-counter", ExecutionStep.Cost)])
        };
        var paidTracker = Tracker();
        paidTracker.Receipt(new(pirActor, paidBefore, DecisionActionDomain.None, new() {
            CostPayment = counterCost with { SourceCardId = loch.VisualId, PayerId = pirActor }
        }), new SelectCard(paidBefore.Id, "paid-cost", loch.Key), true, null);
        var paidAfter = Cards(paidBefore with { BoardRevision = 31, Revision = 31,
            Activity = [counterMutation with { Id = 21, CardId = loch.VisualId, CardName = loch.Name, PlayerId = pirActor, ExecutionId = "paid-counter" }],
            Executions = new(1, 2, [new("paid-counter", "paid-counter", ExecutionKind.Activation, ExecutionStep.Complete) {
                ActorPlayerId = pirActor, SourceCardId = loch.VisualId, Outcome = ExecutionOutcome.Cancelled }],
                [new(1, "paid-counter", ExecutionStep.Cost), new(2, "paid-counter", ExecutionStep.Complete) { Outcome = ExecutionOutcome.Cancelled }])
        }, pirActor, loch with { Counters = ImmutableDictionary<string, int>.Empty });
        paidTracker.ObserveOwned(new(pirActor, paidAfter, DecisionActionDomain.None, new()));
        check(paidTracker.Summary("turn-budget", false, null)[1].RequiredWitnesses.Single(item => item.Id == "source-cost-ownership") is { Status: "observed", Evidence.Length: 1 }
                && paidTracker.Summary("turn-budget", false, null)[1].IntegrityFailures.IsEmpty,
            "An attributed committed counter subcost remains real evidence even when its parent later cancels; no whole ability or draw is inferred");
        var leaked = Batch(before);
        var leakingActor = leaked.Single(item => item.SeatId == actor);
        var leakedState = leakingActor.State with { Players = leakingActor.State.Players.Select(player => player.Id == 83
            ? player with { Zones = [new("Hand", 1, [new Card { Name = "Unauthorized ally hand", Key = "secret" }], null)] } : player).ToImmutableArray() };
        leaked = leaked.Select(item => item.SeatId == actor ? item with { State = leakedState } : item).ToImmutableArray();
        foreach (bool configured in new[] { false, true }) {
            string privacyDirectory = Path.Combine(evidenceDirectory, configured ? "configured-private-window" : "legacy-private-window");
            Directory.CreateDirectory(privacyDirectory);
            using var diagnostics = new AiCampaignDiagnostics(privacyDirectory);
            var inner = new PublishedProvider(leaked, leaked);
            var provider = new AiCampaignProvider(inner, diagnostics);
            if (configured) provider.BindConfigured(start);
            var policy = new CountingPolicy();
            var host = new AiTableHost(provider, _ => policy, 1);
            bool blocked = false;
            try { host.StepAsync().GetAwaiter().GetResult(); }
            catch (InvalidDataException) { blocked = true; }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            check(blocked && policy.Evaluations == 0 && inner.Executions == 0,
                "An unauthorized ally-hand disclosure is rejected before any policy evaluation or command in " + (configured ? "configured" : "legacy") + " campaigns");
        }
    }

    private sealed class PublishedProvider(ImmutableArray<AutomatedSeatObservation> before,
        ImmutableArray<AutomatedSeatObservation> after) : IAutomatedSeatProvider
    {
        internal int Executions;
        public Task EnableAutomatedSeatsAsync() => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync() => Task.FromResult(Executions == 0 ? before : after);
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command) { Executions++; return Task.CompletedTask; }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode) => Task.CompletedTask;
    }
    private sealed class CountingPolicy : IAiPolicy
    {
        internal int Evaluations;
        public AiPolicyDescriptor Descriptor => new CasualCommanderPolicy().Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        { Evaluations++; return Task.FromException<AiProposal>(new InvalidOperationException("The privacy gate should precede policy evaluation.")); }
    }
}
