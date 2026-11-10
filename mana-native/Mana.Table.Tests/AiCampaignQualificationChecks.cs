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
        MixedBindings(decks, check);
        var decoded = ForgeEngine.DecodeConfiguredSimulation(JsonSerializer.SerializeToElement(ConfiguredSimulationWireChecks.Receipt()),
            ConfiguredSimulationWireChecks.Setup());
        var start = decoded with { Setup = decoded.Setup with { Decks = decks.Select(deck => deck.Handle).ToImmutableArray() } };
        string hash = AiCampaignQualification.DefinitionHash("case", start.Setup.Specification, decks, "smoke");
        AiCampaignQualificationTracker Tracker() => new(decks, start, "case", hash);
        CheckConsumingArtifactAttribution(Tracker, check);
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
        CheckOwnedLookupCompatibility(Tracker, observation, command, after, publicAfter, entered, check);
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

    private static void CheckConsumingArtifactAttribution(Func<AiCampaignQualificationTracker> create, Action<bool, string> check)
    {
        const string witnessId = "positive-consuming-artifact-activation";
        AiCampaignQualificationTracker Collect(int playerId)
        {
            var fixture = new AiConsumingArtifactEvidenceChecks.Fixture(playerId);
            var tracker = create();
            tracker.Receipt(fixture.Before, new UseOfferedAction(fixture.Before.State.Id, "init", fixture.Source.Key, "draw"), true, null);
            tracker.ObserveOwned(fixture.Before with { State = fixture.After, Domain = DecisionActionDomain.None });
            tracker.ObservePublic(fixture.After with { ViewerId = -1, Activity = [], Players = fixture.After.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with { Key = "" }).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray() });
            return tracker;
        }
        var own = Collect(11);
        var summary = own.Summary("turn-budget", false, null);
        var proof = summary.Single(deck => deck.SlotIndex == 3).RequiredWitnesses.Single(witness => witness.Id == witnessId);
        var otherPlayer = Collect(47);
        check(proof.Status == "observed" && proof.Evidence is [{ PlayerId: 11, SlotIndex: 3, ExecutionId: "activation" }]
                && proof.Evidence[0].ActivityIds.SequenceEqual([11L, 12L])
                && summary.Where(deck => deck.SlotIndex != 3).All(deck => deck.RequiredWitnesses.All(witness => witness.Status != "observed"))
                && otherPlayer.CausalEvidence.Witnesses.Any(witness => witness.WitnessId == witnessId && witness.Before.SeatId == 47)
                && otherPlayer.Summary("turn-budget", false, null)[3].RequiredWitnesses.Single(witness => witness.Id == witnessId).Status == "not-observed",
            "The campaign imports a genuine consuming artifact witness only into its bound Traxos slot/player; another participant's identical accepted cost and draw cannot lend that deck mechanic credit");
    }

    private static void CheckOwnedLookupCompatibility(Func<AiCampaignQualificationTracker> create,
        AutomatedSeatObservation before, UseOfferedAction command, GameSnapshot after, GameSnapshot publicAfter,
        ActivityEntry entered, Action<bool, string> check)
    {
        bool Has(AiCampaignQualificationTracker tracker) => tracker.Summary("turn-budget", false, null)[0].RequiredWitnesses
            .Single(item => item.Id == "rhys-paid-tokens").Status == "observed";
        bool Credits(AutomatedSeatObservation initial, GameSnapshot current) {
            var tracker = create(); tracker.Receipt(initial, command, true, null);
            tracker.ObserveOwned(initial with { State = current }); tracker.ObservePublic(publicAfter);
            return Has(tracker);
        }
        var original = after.Executions!.Occurrences.Single();
        var sibling = original with { Id = "sibling", BudgetRootId = original.Id, ParentId = null, Kind = ExecutionKind.Trigger };
        var shared = after with { Activity = [entered with { ExecutionId = sibling.Id }],
            Executions = new(1, 3, [original, sibling], after.Executions.Transitions.Add(
                new(3, sibling.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved })) };
        check(Credits(before, shared),
            "Generic deck qualification retains a same-budget sibling's exact owned-source event without substituting ancestry for its existing association rule");
        var foreignRoot = original with { Id = "foreign-root", BudgetRootId = "foreign-root", SourceCardId = "foreign-source" };
        var unrelated = shared with { Executions = new(1, 4, [original, foreignRoot, sibling with { BudgetRootId = foreignRoot.Id }],
            after.Executions.Transitions.Add(new(3, foreignRoot.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved })
                .Add(new(4, sibling.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved })) };
        check(!Credits(before, unrelated)
                && !Credits(before, after with { Activity = [entered with { ExecutionId = "missing-execution" }] }),
            "An otherwise identical owned-source token change from another root or an absent execution cannot borrow this accepted action's generic witness");

        var pendingOccurrence = original with { Step = ExecutionStep.Resolution, Outcome = null };
        var pending = after with { Executions = new(1, 2, [pendingOccurrence],
            [new(1, original.Id, ExecutionStep.Preparation), new(2, original.Id, ExecutionStep.Resolution)]) };
        var latest = create(); latest.Receipt(before, command, true, null);
        latest.ObserveOwned(before with { State = pending }); latest.ObservePublic(publicAfter);
        bool initiallyPending = !Has(latest);
        latest.ObserveOwned(before with { State = after }); latest.ObservePublic(publicAfter);
        var cancelled = after with { Executions = new(1, 2, [original with { Outcome = ExecutionOutcome.Cancelled }],
            [new(1, original.Id, ExecutionStep.Preparation), new(2, original.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Cancelled }]) };
        check(initiallyPending && Has(latest) && !Credits(before, cancelled),
            "Fresh owned observations with unchanged headers still read the current terminal row: pending is not resolved, a later Resolved is credited and Cancelled is not");

        // These direct collector inputs deliberately include a duplicate row.
        // Adapter execution-invariant validation remains a separate admission gate.
        check(!Credits(before, after with { Executions = after.Executions with { Occurrences = [pendingOccurrence, original] } })
                && Credits(before, after with { Executions = after.Executions with { Occurrences = [original, pendingOccurrence] } }),
            "Direct collector lookup compatibility keeps the first disclosed duplicate occurrence, including its terminal state, rather than silently normalizing it to the last row");

        var undisclosed = before with { State = before.State with {
            Decision = before.State.Decision! with { ExecutionId = "", ExecutionStep = null },
            Executions = new(11, 10, [], [])
        } };
        GameSnapshot Disclosed(long sequence) => after with { Executions = new(sequence, sequence, [original],
            [new(sequence, original.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]) };
        var multipleTransitions = after with { Executions = new(9, 11, [original],
            [new(9, original.Id, ExecutionStep.Preparation), new(11, original.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]) };
        check(!Credits(undisclosed, Disclosed(10)) && Credits(undisclosed, Disclosed(11)) && Credits(undisclosed, multipleTransitions),
            "An undisclosed action binds only after a strictly newer matching transition; the maximum matching sequence preserves Any semantics across its older preparation and terminal rows");

        var first = original with { Id = "first-candidate", BudgetRootId = "first-candidate" };
        var second = original with { Id = "second-candidate", BudgetRootId = "second-candidate" };
        var wrongSource = original with { Id = "wrong-source-candidate", BudgetRootId = "wrong-source-candidate", SourceCardId = "other-source" };
        var wrongActor = original with { Id = "wrong-actor-candidate", BudgetRootId = "wrong-actor-candidate", ActorPlayerId = 83 };
        var candidates = after with { Activity = [entered with { ExecutionId = second.Id }],
            Executions = new(11, 14, [wrongSource, wrongActor, first, second],
                [new(11, wrongSource.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved },
                    new(12, wrongActor.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved },
                    new(13, first.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved },
                    new(14, second.Id, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved }]) };
        check(!Credits(undisclosed, candidates)
                && Credits(undisclosed, candidates with { Executions = candidates.Executions! with { Occurrences = [wrongSource, wrongActor, second, first] } }),
            "Candidate binding preserves the original disclosed occurrence order after exact actor/source filtering instead of iterating an index or choosing a later convenient effect");
    }

    private static void MixedBindings(ImmutableArray<AiCampaignDeckBinding> decks, Action<bool, string> check)
    {
        // Authored boundary fixtures only: the real mixed-TCP campaign supplies
        // the accepted selection receipts and connection-owned rows at runtime.
        var seed = ForgeEngine.DecodeConfiguredSimulation(JsonSerializer.SerializeToElement(ConfiguredSimulationWireChecks.Receipt()),
            ConfiguredSimulationWireChecks.Setup()).State;
        int[] ids = [29, 83, 47, 12];
        var commanders = decks.Select(deck => AiCampaignDecks.All.Single(candidate => candidate.Id == deck.CandidateId)
            .Commanders.Select(name => new Card { Name = name, Type = "Legendary Creature" }).ToImmutableArray()).ToImmutableArray();
        var players = seed.Players.Select((player, index) => player with {
            Id = ids[index], TeamId = index, Zones = [new("Command", commanders[index].Length, commanders[index], null)]
        }).ToImmutableArray();
        var selections = Enumerable.Range(0, 4).Select(index => "accepted-selection-" + index).ToImmutableArray();
        var seats = Enumerable.Range(0, 4).Select(index => new Seat(index, "Seat " + index,
            index == 0 ? "LOCAL" : index == 1 ? "REMOTE" : "AI", "Deck " + index, true, index == 0) {
                ParticipantId = "actual-participant-" + index,
                DeckPreview = new(selections[index], "Deck " + index, commanders[index], null),
                OpponentId = index >= 2 ? decks[index].Handle : null
            }).ToImmutableArray();
        var lobby = new Lobby("hosting", "Connected", "", true, null, false, "disabled", null, [], seats, "mixed-accepted-table");
        var rows = ids.Select(id => new OwnedDecisionObservation(seed with { Id = "mixed-accepted-game", ViewerId = id, Players = players },
            DecisionActionDomain.None, new())).ToImmutableArray();
        AiCampaignQualificationTracker Bind(Lobby? accepted = null, ImmutableArray<OwnedDecisionObservation>? owned = null,
            ImmutableArray<string>? pins = null) => AiCampaignQualificationTracker.ForMixedTable(decks, accepted ?? lobby,
                owned ?? rows, pins ?? selections, "mixed-boundary-case", new string('a', 64));
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var tracker = Bind(owned: [rows[2], rows[0], rows[3], rows[1]]);
        check(tracker.Participants.Select(participant => participant.PlayerId).SequenceEqual(ids)
                && tracker.Participants.Select(participant => participant.TeamId).SequenceEqual([0, 1, 2, 3])
                && tracker.Summary("turn-budget", false, null).Select(deck => deck.Deck.Handle).SequenceEqual(decks.Select(deck => deck.Handle)),
            "Mixed qualification binds distinct accepted commander/selection identities to actual player IDs independently of owned-row order, without a fabricated simulation receipt");
        foreach (var row in rows) tracker.ObserveOwned(new(row.State.ViewerId, row.State, row.Domain, row.Semantics));
        tracker.ObservePublic(rows[0].State);
        check(Reject(() => Bind(pins: selections.SetItem(1, "replaced-selection")))
                && Reject(() => Bind(accepted: lobby with { Error = "Actual lobby failure" }))
                && Reject(() => Bind(accepted: lobby with { Seats = seats.SetItem(1, seats[1] with {
                    DeckPreview = seats[1].DeckPreview! with { SelectionId = "replaced-selection" }
                }) }))
                && Reject(() => Bind(accepted: lobby with { Seats = seats.SetItem(2, seats[2] with { OpponentId = decks[3].Handle }) }))
                && Reject(() => Bind(accepted: lobby with { Seats = seats.SetItem(1, seats[1] with { Ready = false }) })),
            "A replaced human selection with the same commander, changed AI deck handle or unaccepted ready state cannot borrow pinned mixed deck qualification");
        var missingCommand = rows[1] with { State = rows[1].State with {
            Players = players.SetItem(1, players[1] with { Zones = [new("Command", 0, [], null)] })
        } };
        var foreignTeam = rows[2] with { State = rows[2].State with { Players = players.SetItem(2, players[2] with { TeamId = 9 }) } };
        check(Reject(() => Bind(owned: rows.SetItem(1, missingCommand)))
                && Reject(() => Bind(owned: rows.SetItem(2, foreignTeam)))
                && Reject(() => Bind(owned: rows.SetItem(1, rows[1] with { State = rows[1].State with { ViewerId = 999 } })))
                && Reject(() => Bind(owned: rows.SetItem(1, rows[1] with { State = rows[1].State with { Id = "foreign-game" } })))
                && Reject(() => Bind(owned: rows.RemoveAt(1))),
            "Missing initial own commanders, foreign game/viewer/team or an incomplete quartet fail before mixed causal attribution begins");
        bool foreignRejected = false;
        try { tracker.ObserveOwned(new(ids[0], rows[0].State with { Players = players.SetItem(0, players[0] with { TeamId = 9 }) },
            DecisionActionDomain.None, new())); } catch (InvalidDataException) { foreignRejected = true; }
        check(foreignRejected && tracker.Summary("time-budget", false, null).All(deck => deck.SampledNaturalGame == "inconclusive"
                && deck.RequiredWitnesses.All(witness => witness.Status != "observed")),
            "Later mixed observations retain the accepted player/team map, and binding/progression alone never credits a defining mechanic or natural outcome");
    }

    private sealed class PublishedProvider(ImmutableArray<AutomatedSeatObservation> before,
        ImmutableArray<AutomatedSeatObservation> after) : IAutomatedSeatProvider
    {
        internal int Executions;
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(Executions == 0 ? before : after); }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); Executions++; return Task.CompletedTask; }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
    private sealed class CountingPolicy : IAiPolicy
    {
        internal int Evaluations;
        public AiPolicyDescriptor Descriptor => new CasualCommanderPolicy().Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        { Evaluations++; return Task.FromException<AiProposal>(new InvalidOperationException("The privacy gate should precede policy evaluation.")); }
    }
}
