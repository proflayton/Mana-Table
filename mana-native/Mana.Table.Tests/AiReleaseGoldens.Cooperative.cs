using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static partial class AiReleaseGoldens
{
    private static AcceptedCommand? AlliedCounterTarget(IEnumerable<AcceptedCommand> receipts, int actorId, int allyId, string sourceId) =>
        sourceId.Length == 0 ? null : receipts.FirstOrDefault(item => item.Seat == actorId && item.Kind == nameof(SelectCard)
            && !item.SelectionIsCost && item.CostOwner == sourceId && item.ExecutionId.Length > 0
            && item.SelectedCardId.Length > 0 && item.SelectedControllerId == allyId && item.SelectedPlusOneCounters.HasValue);

    internal static async Task RunCooperativeStandaloneAsync(ForgePaths paths, string productionJar)
    {
        Directory.CreateDirectory(paths.Profile);
        string directory = Path.Combine(paths.Profile, "ai-goldens"); Directory.CreateDirectory(directory);
        AiReleaseRuntime? runtime = null;
        ImmutableArray<AiGoldenResult> results = [];
        Exception? failure = null;
        void Check(bool passed, string detail)
        {
            File.AppendAllText(Path.Combine(paths.Profile, "team-goldens-progress.log"), (passed ? "PASS " : "FAIL ") + detail + "\n");
            if (!passed) throw new InvalidOperationException(detail);
        }
        try {
            runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar, verifyPackage: false);
            await using var engine = new ForgeEngine(paths);
            await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
            results = await RunCooperativeGoldensAsync(engine, directory, Check);
            Check(results.Length == 2 && results.All(result => result.Passed), "Both targeted cooperative policy consequences were observed.");
        } catch (Exception error) {
            failure = error;
            string partial = Path.Combine(directory, "cooperative-summary.json");
            if (File.Exists(partial)) results = JsonSerializer.Deserialize<ImmutableArray<AiGoldenResult>>(File.ReadAllText(partial));
        } finally {
            File.WriteAllText(Path.Combine(paths.Profile, "team-goldens-results.json"), JsonSerializer.Serialize(new {
                runtime, results, failure = failure?.ToString(), passed = failure == null && results.Length == 2 && results.All(result => result.Passed),
                resourceBinding = "external-resources-not-bound-to-candidate", campaignDeckQualificationCredit = false,
                naturalGameCompletionCredit = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure != null) throw new InvalidOperationException("A cooperative policy golden failed; inspect the retained first evidence.", failure);
    }

    private static async Task<ImmutableArray<AiGoldenResult>> RunCooperativeGoldensAsync(ForgeEngine engine,
        string directory, Action<bool, string> check)
    {
        var results = ImmutableArray.CreateBuilder<AiGoldenResult>();
        foreach (bool removal in new[] { false, true }) {
            string id = removal ? "cooperative-enemy-removal" : "cooperative-ally-benefit";
            var imported = await engine.ImportAsync(removal ? "AI cooperative enemy-removal" : "AI cooperative ally-benefit",
                "Deck\n99 Wastes\nCommander\n1 Traxos, Scourge of Kroog");
            var actor = await engine.SaveDeckAsync(imported.Id, (await engine.OpenDeckAsync(imported.Id)).Revision);
            var otherImport = await engine.ImportAsync("Cooperative tactical opponents " + id, "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed");
            var others = await engine.SaveDeckAsync(otherImport.Id, (await engine.OpenDeckAsync(otherImport.Id)).Revision);
            if (actor.SaveError.Length > 0 || others.SaveError.Length > 0 || actor.StructureValid == false || others.StructureValid == false)
                throw new InvalidDataException("The cooperative tactical fixture decks were not legal committed Commander decks.");
            var result = await RunCooperativeGolden(engine, Path.Combine(directory, id), id, removal,
                ["deck:" + actor.Id, "deck:" + others.Id, "deck:" + others.Id, "deck:" + others.Id]);
            results.Add(result);
            File.WriteAllText(Path.Combine(directory, "cooperative-summary.json"), JsonSerializer.Serialize(results,
                new JsonSerializerOptions { WriteIndented = true }));
            check(result.Passed, "Real cooperative policy golden " + id + ": " + result.Detail);
            if (!result.Passed) break;
        }
        return results.ToImmutable();
    }

    private static async Task<AiGoldenResult> RunCooperativeGolden(ForgeEngine engine, string directory, string id,
        bool removal, ImmutableArray<string> decks)
    {
        Directory.CreateDirectory(directory);
        using var diagnostics = new AiCampaignDiagnostics(directory);
        var observed = new AiCampaignProvider(engine, diagnostics);
        var provider = new ReceiptProbe(observed);
        await using var host = new AiTableHost(provider, _ => diagnostics.Record(new CasualCommanderPolicy()), 261026,
            new(TimeSpan.FromSeconds(10), 4096));
        await using var trace = new StreamWriter(Path.Combine(directory, "public.jsonl"));
        var clock = Stopwatch.StartNew();
        GameSnapshot? state = null;
        ConfiguredSimulationStart? accepted = null;
        int steps = 0, comparisons = 0, actorId = -1, allyId = -1, enemyId = -1, otherEnemy = -1;
        string sourceId = "", targetId = "", allyCardId = "", otherEnemyCardId = "", stop = "not-started", detail = "";
        int beforeCounter = 0;
        bool passed = false, stackSeen = false, resolved = false, ownCost = false, targetChosen = false, reportedDamage = false,
            counterApplied = false;
        Exception? failure = null;
        var captureErrors = new List<string>();
        try {
            var setup = new ConfiguredSimulationSetup(new(MatchRulesets.CooperativeCommander,
                [new(0, 17, MatchSeatRole.Party), new(1, 17, MatchSeatRole.Party),
                 new(2, 42, MatchSeatRole.Opposition), new(3, 42, MatchSeatRole.Opposition)]), decks);
            accepted = await engine.StartConfiguredSimulationAsync(setup);
            ConfiguredSimulationFacts.ValidateReceipt(accepted, setup);
            ConfiguredSimulationFacts.ValidateState(accepted, accepted.State);
            observed.BindConfigured(accepted);
            state = accepted.State;
            int Player(int slot) => accepted.Participants.Single(item => item.Index == slot).PlayerId;
            actorId = Player(0); allyId = Player(1); enemyId = Player(2); otherEnemy = Player(3);
            Card[] Battlefield(int player) => state.Players.Single(item => item.Id == player).Zone("Battlefield").Cards.ToArray();
            var actorCards = Battlefield(actorId);
            string sourceName = removal ? "Aeolipile" : "Dragon Blood";
            string targetName = removal ? "Priest of Titania" : "Grizzly Bears";
            string allyName = removal ? "Ancient Brontodon" : "Grizzly Bears";
            var expected = removal ? new[] { "Aeolipile", "Wastes" } : new[] { "Dragon Blood", "Wastes", "Wastes" };
            if (!actorCards.Select(card => card.Name).Order().SequenceEqual(expected.Order())
                || Battlefield(allyId).Length != 1 || Battlefield(allyId)[0].Name != allyName
                || Battlefield(enemyId).Length != 1 || Battlefield(enemyId)[0].Name != (removal ? "Priest of Titania" : "Colossal Dreadmaw")
                || Battlefield(otherEnemy).Length != 1 || Battlefield(otherEnemy)[0].Name != "Colossal Dreadmaw"
                || state.Players.Single(item => item.Id == actorId).Zone("Command").Cards.Single().Name != "Traxos, Scourge of Kroog")
                throw new InvalidDataException("The test-only cooperative battlefield did not match the declared four-seat setup before policy execution.");
            sourceId = CardViews.Identity(actorCards.Single(card => card.Name == sourceName));
            targetId = CardViews.Identity(Battlefield(removal ? enemyId : allyId).Single(card => card.Name == targetName));
            allyCardId = CardViews.Identity(Battlefield(allyId).Single()); otherEnemyCardId = CardViews.Identity(Battlefield(otherEnemy).Single());
            beforeCounter = PlusOneCounters(Battlefield(allyId).Single());
            provider.Target = sourceId;
            while (true) {
                if (clock.Elapsed > TimeSpan.FromSeconds(18)) { stop = "time-budget"; detail = "The bounded tactical consequence was not observed."; break; }
                await host.StepAsync(); steps++;
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("Cooperative golden public state disappeared.");
                ConfiguredSimulationFacts.ValidateState(accepted, state);
                if (host.LastFault != null) throw new InvalidOperationException(JsonSerializer.Serialize(host.LastFault));
                foreach (var bot in provider.Last) {
                    if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain))
                        throw new InvalidDataException("An ally relationship disclosed hidden hand/library information.");
                    if (state.BoardRevision == bot.State.BoardRevision) {
                        if (!AiWholeSeatEngineCheck.ComparePublicProjection(state, bot.State, directory))
                            throw new InvalidDataException("Same-revision cooperative tactical projections diverged.");
                        comparisons++;
                    }
                }
                await trace.WriteLineAsync(JsonSerializer.Serialize(new { steps, state }));
                var actorReceipts = provider.Accepted.Where(item => item.Seat == actorId).ToArray();
                // The ally may naturally cast another creature before our turn.
                // Follow the actual accepted allied target, retaining its value
                // before dispatch; no card-name or final-board guess supplies it.
                if (!removal && !targetChosen && AlliedCounterTarget(actorReceipts, actorId, allyId, sourceId) is { } alliedTarget) {
                    targetId = alliedTarget.SelectedCardId;
                    beforeCounter = alliedTarget.SelectedPlusOneCounters!.Value;
                }
                targetChosen = actorReceipts.Any(item => !item.SelectionIsCost && item.SelectedCardId == targetId
                    && item.SelectedControllerId == (removal ? enemyId : allyId) && item.CostOwner == sourceId);
                var executions = actorReceipts.Where(item => !item.SelectionIsCost && item.SelectedCardId == targetId
                    && item.CostOwner == sourceId && item.ExecutionId.Length > 0).Select(item => item.ExecutionId).ToHashSet(StringComparer.Ordinal);
                stackSeen |= state.Stack.Any(item => item.Card is { } card && CardViews.Identity(card) == sourceId);
                // A sacrificed source can lose its readable card reference.
                // The accepted target/payment execution remains authoritative;
                // source-name matching cannot replace that historical identity.
                var actorActivity = provider.Last.Where(item => item.SeatId == actorId).SelectMany(item => item.State.Activity).ToArray();
                resolved |= actorActivity.Any(entry => entry.Kind == "resolved" && entry.Outcome == "resolved" && executions.Contains(entry.ExecutionId));
                counterApplied |= actorActivity.Any(entry => executions.Contains(entry.ExecutionId)
                    && entry.CardId == targetId && entry.Change is { Kind: "counters", Property: "+1/+1" } change
                    && change.Before == beforeCounter && change.After == beforeCounter + 1);
                reportedDamage |= actorActivity.Any(entry => executions.Contains(entry.ExecutionId) && entry.Impact is {
                    Kind: "damage", Amount: 2, SourceControllerId: var sourceController, RecipientControllerId: var recipient, CardId: var damaged }
                    && sourceController == actorId && recipient == enemyId && damaged == targetId);
                var actorNow = state.Players.Single(item => item.Id == actorId);
                // The engine can commit payment as the final mana source is
                // activated. A separate PayMana answer is not guaranteed.
                // Keep the actual payer, producing occurrence and execution
                // identity through either legal payment continuation.
                ownCost |= actorReceipts.Any(item => item.SourceId == sourceId && item.Kind == nameof(UseOfferedAction)
                        && (!removal || item.Consumed))
                    && actorReceipts.Where(item => !item.SelectionIsCost && item.SelectedCardId == targetId
                        && item.CostOwner == sourceId && item.ExecutionId.Length > 0).Any(target => {
                            var funding = actorReceipts.Where(item => item.CostOwner == sourceId && item.Payer == actorId
                                && item.ExecutionId == target.ExecutionId).ToArray();
                            return funding.Any(item => item.Kind is nameof(PayMana) or nameof(SpendPoolMana))
                                || funding.Where(item => item.Kind == nameof(UseOfferedAction) && item.ManaAbility
                                    && actorNow.Zone("Battlefield").Cards.Any(card => CardViews.Identity(card) == item.SourceId && card.Tapped))
                                    .Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() >= (removal ? 1 : 3);
                        });
                var allyNow = state.Players.Single(item => item.Id == allyId).Zone("Battlefield").Cards.SingleOrDefault(card => CardViews.Identity(card) == allyCardId);
                var buffTarget = state.Players.Single(item => item.Id == allyId).Zone("Battlefield").Cards.SingleOrDefault(card => CardViews.Identity(card) == targetId);
                bool enemyIntact = state.Players.Single(item => item.Id == otherEnemy).Zone("Battlefield").Cards.Any(card => CardViews.Identity(card) == otherEnemyCardId);
                bool useful = removal
                    ? state.Players.Single(item => item.Id == enemyId).Zone("Graveyard").Cards.Any(card => CardViews.Identity(card) == targetId)
                        && actorNow.Zone("Graveyard").Cards.Any(card => CardViews.Identity(card) == sourceId)
                        && allyNow != null && enemyIntact && reportedDamage
                    : buffTarget != null && counterApplied && PlusOneCounters(buffTarget) == beforeCounter + 1
                        && actorNow.Zone("Battlefield").Cards.Any(card => CardViews.Identity(card) == sourceId && card.Tapped)
                        && enemyIntact;
                if (targetChosen && ownCost && stackSeen && resolved && useful && comparisons > 0) {
                    passed = true; stop = "observed-consequence";
                    detail = removal ? "The real policy paid and sacrificed its own Aeolipile, targeted the lethal enemy Priest, and observed its graveyard transition while the ally and nonlethal enemy survived."
                        : "The real policy paid its own Dragon Blood cost, targeted the allied creature and observed exactly one new +1/+1 counter while the source tapped.";
                    break;
                }
                if (state.Status == "finished" || state.Turn >= 8) { stop = "unobserved"; detail = "The declared tactical branch was not proven within two rounds."; break; }
                await Task.Delay(1);
            }
        } catch (Exception error) { failure = error; stop = "mechanical-failure"; detail = error.ToString(); }
        finally {
            try { File.WriteAllText(Path.Combine(directory, "pre-cleanup-public-state.json"), JsonSerializer.Serialize(state)); }
            catch (Exception error) { captureErrors.Add(error.ToString()); passed = false; }
            if (state != null) try { await engine.StopSimulationAsync(state.Id); }
                catch (Exception error) { captureErrors.Add(error.ToString()); passed = false; }
        }
        var evidence = JsonSerializer.SerializeToElement(new { audience = "host-private targeted fixture", accepted,
            actorId, allyId, enemyId, sourceId, targetId, beforeCounter, targetChosen, ownCost, stackSeen, resolved, reportedDamage, counterApplied,
            commands = provider.Accepted, diagnostics = diagnostics.Summary(), failure = failure?.ToString(), captureErrors,
            campaignDeckQualificationCredit = false, naturalGameCompletionCredit = false,
            scope = "Actual policy/engine/controller/command consequence; fixture lists are not the pinned campaign deck versions." });
        var result = new AiGoldenResult(id, passed && failure == null && captureErrors.Count == 0, detail,
            comparisons, provider.Accepted.Count, steps, clock.Elapsed.TotalMilliseconds, stop, evidence);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }
}
