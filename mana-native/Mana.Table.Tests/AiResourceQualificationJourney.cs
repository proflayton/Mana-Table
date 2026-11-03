using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// One declared pregame fixture in the existing warmed test JVM. Root choices
// and costs are scripted through ordinary current capabilities; this is actual
// rules/provider evidence, not a claim of AI strategy or campaign qualification.
internal static class AiResourceQualificationJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths, string productionJar)
    {
        Directory.CreateDirectory(paths.Profile);
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar, verifyPackage: false);
        var checks = new List<string>();
        Exception? failure = null;
        try {
            await using var engine = new ForgeEngine(paths);
            await engine.InitializeAsync();
            await RunAsync(engine, paths.Profile, (passed, description) => {
                if (!passed) throw new InvalidOperationException(description);
                checks.Add(description);
            });
        } catch (Exception error) { failure = error; throw; }
        finally {
            File.WriteAllText(Path.Combine(paths.Profile, "resource-qualification-result.json"), JsonSerializer.Serialize(new {
                passed = failure == null, error = failure?.ToString(), runtime, checks,
                scope = "Local controlled rules journey; external resources/source-package binding unverified, not a valid runtime Commander deck or campaign qualification."
            }));
        }
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "resource-qualification"); Directory.CreateDirectory(directory);
        var evidence = new AiCausalMechanicEvidence();
        var history = new AiCausalHistoryReader();
        var snapshots = new List<GameSnapshot>();
        var commands = new List<object>();
        var sourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var executionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var answered = new HashSet<(int Seat, long Revision, string Decision, long Offers)>();
        string actorDeck = await Save("Resource qualification Traxos", "Traxos, Scourge of Kroog", "Wastes");
        string opponent = await Save("Resource qualification passive opponents", "The Reality Chip", "Island");
        await engine.EnableAutomatedSeatsAsync();
        if (!engine.SupportsAutomatedHistory) throw new NotSupportedException("The resource journey requires actual owned-seat payment history.");
        var started = await engine.StartSimulationAsync([actorDeck, opponent, opponent, opponent]);
        string gameId = started.Id;
        var clock = Stopwatch.StartNew();
        bool initial = false, genericControl = false, complete = false;
        int initiated = 0, firstTurn = 0;
        GameSnapshot? pending = null, lastPublic = null;
        Exception? failure = null;
        try {
            while (!complete) {
                if (clock.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("The resource qualification journey exceeded its focused watchdog.");
                var seats = await engine.ObserveAutomatedSeatsAsync();
                foreach (var seat in seats) evidence.ObserveOwned(seat);
                await history.DrainAsync(engine, seats, evidence);
                lastPublic = await engine.ObserveSimulationAsync() ?? throw new IOException("The resource fixture lost its public observer.");
                evidence.ObservePublic(lastPublic);
                var actor = seats.FirstOrDefault(item => item.SeatId == 0)?.State;
                if (actor?.Status is "error" or "finished")
                    throw new InvalidOperationException(actor.Error ?? "The controlled resource fixture ended before its witnesses.");
                if (actor != null) {
                    foreach (var name in sourceIds.Keys)
                        if (!executionIds.ContainsKey(name) && actor.Executions?.Occurrences.LastOrDefault(item =>
                            item.Kind == ExecutionKind.Spell && item.ActorPlayerId == 0 && item.SourceCardId == sourceIds[name]) is { } execution)
                            executionIds[name] = execution.Id;
                    if (snapshots.Count == 0 || snapshots[^1].Revision != actor.Revision
                        || snapshots[^1].Executions?.LastSequence != actor.Executions?.LastSequence
                        || snapshots[^1].Decision?.Id != actor.Decision?.Id) snapshots.Add(actor);
                    bool Resolved(string name) => executionIds.TryGetValue(name, out var id)
                        && ExecutionFrames.Find(actor, id) is { Step: ExecutionStep.Complete, Outcome: ExecutionOutcome.Resolved };
                    if (initiated > 0 && Resolved("Traxos, Scourge of Kroog") && !genericControl) {
                        var traxos = Own(actor, "Traxos, Scourge of Kroog");
                        var payments = evidence.PaymentsFor(gameId, 0, executionIds["Traxos, Scourge of Kroog"]);
                        check(traxos.Tapped && payments.Any(item => item.Kind == "spent" && item.Spent?.GetValueOrDefault("C") > 0
                                && item.BillBefore.All(part => part.Symbol != "{C}") && item.BillAfter.All(part => part.Symbol != "{C}"))
                            && !evidence.Witnesses.Any(item => item.WitnessId == "required-colorless-payment"),
                            "The actual command-zone Traxos enters tapped after C paid a generic bill; that genuine consumption cannot qualify an explicit C requirement.");
                        genericControl = true;
                    }
                    if (initiated == 3 && Resolved("Spatial Contortion")
                        && evidence.Witnesses.Any(item => item.WitnessId == "required-colorless-payment"
                            && item.SourceCardId == sourceIds["Spatial Contortion"])
                        && actor.Stack.IsEmpty && actor.Decision?.Payload is PriorityPayload) {
                        var traxos = Own(actor, "Traxos, Scourge of Kroog");
                        check(genericControl && evidence.Witnesses.Any(item => item.WitnessId == "historic-traxos-untap"
                                && item.SourceCardId == sourceIds["Mind Stone"])
                            && !traxos.Tapped && traxos.Power == 10 && traxos.Toughness == 4
                            && actor.Viewer!.Zone("Battlefield").Cards.Count(card => card.Name == "Wastes" && card.Tapped) == 8,
                            "The same actual consumer proves Mind Stone's historic cast into Traxos's committed untap and Spatial Contortion's real C bill, with eight paid Wastes and surviving authoritative 10/4 Traxos.");
                        check(evidence.CastFactsFor(gameId, 0, executionIds["Mind Stone"]).Single().HistoricSpell == true
                            && evidence.CastFactsFor(gameId, 0, executionIds["Spatial Contortion"]).Single().HistoricSpell == false,
                            "The same archived native history preserves actual Mind Stone historic and Spatial Contortion nonhistoric facts rather than reconstructing them from their names or card faces.");
                        complete = true; break;
                    }
                }
                bool submitted = false;
                foreach (var observation in seats.OrderBy(item => item.SeatId)) {
                    var state = observation.State;
                    if (state.Status != "playing" || state.Decision is not { } decision
                        || !answered.Add((observation.SeatId, state.Revision, decision.Id, observation.Domain.Version))) continue;
                    pending = state;
                    DecisionCommand command;
                    if (observation.SeatId == 0 && decision.Payload is PriorityPayload
                        && state.ActivePlayerId == 0 && state.PhaseKey == "MAIN1" && state.Stack.IsEmpty) {
                        if (!initial) {
                            check(state.Players.Length == 4 && state.Viewer!.Zone("Hand").Count == 8
                                && state.Viewer.Zone("Library").Count == 0
                                && state.Viewer.Zone("Hand").Cards.Any(card => card.Name == "Mind Stone")
                                && state.Viewer.Zone("Hand").Cards.Any(card => card.Name == "Spatial Contortion")
                                && state.Viewer.Zone("Battlefield").Cards.Count(card => card.Name == "Wastes" && !card.Tapped) == 8,
                                "The declared test-only eight-card library reaches both actual hand cards through opening seven plus the genuine draw, with eight real pregame Wastes and a proper commander.");
                            initial = true; firstTurn = state.Turn;
                        }
                        if (state.Turn != firstTurn) throw new InvalidOperationException("The fixture cannot continue into an empty-library draw.");
                        string? next = initiated switch {
                            0 => "Traxos, Scourge of Kroog",
                            1 when genericControl => "Mind Stone",
                            2 when evidence.Witnesses.Any(item => item.WitnessId == "historic-traxos-untap"
                                && item.SourceCardId == sourceIds["Mind Stone"])
                                && state.Viewer!.Zone("Battlefield").Cards.Any(card => card.Name == "Mind Stone") => "Spatial Contortion",
                            _ => null
                        };
                        if (next == null) command = new PassPriority(state.Id, decision.Id);
                        else {
                            string zone = initiated == 0 ? "Command" : "Hand";
                            var card = state.Viewer!.Zone(zone).Cards.Single(item => item.Name == next);
                            var source = observation.Domain.Sources.Single(item => item.SourceCardId == CardViews.Identity(card));
                            var route = source.Actions.Single(item => item.Kind == "cast");
                            command = new UseOfferedAction(state.Id, decision.Id, source.SourceCardKey, route.Id);
                            sourceIds.Add(next, source.SourceCardId); initiated++;
                        }
                    } else if (observation.SeatId == 0 && decision.Payload is PaymentPayload payment) {
                        if (payment.Pay.Enabled) command = new PayMana(state.Id, decision.Id);
                        else if (observation.Domain.PoolManaColors.Contains("C") && state.Viewer!.Mana.GetValueOrDefault("C") > 0)
                            command = new SpendPoolMana(state.Id, decision.Id, "C");
                        else {
                            var wastes = state.Viewer!.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Wastes" && !card.Tapped)
                                ?? throw new InvalidDataException("The actual resource bill exceeded the declared owned Wastes.");
                            var source = observation.Domain.Sources.Single(item => item.SourceCardId == CardViews.Identity(wastes));
                            var route = source.Actions.Single(item => observation.Domain.Actions.Any(action =>
                                action.ActionId == item.Id && action.ManaAbility));
                            command = new UseOfferedAction(state.Id, decision.Id, source.SourceCardKey, route.Id);
                        }
                    } else if (observation.SeatId == 0 && initiated == 3
                        && decision.Payload is TargetSelectionPayload { Complete.Enabled: false } or EntitySelectionPayload { Complete.Enabled: false }) {
                        var traxos = Own(state, "Traxos, Scourge of Kroog");
                        command = traxos.Selectable ? new SelectCard(state.Id, decision.Id, traxos.Key) : Answer(state);
                    } else command = Answer(state);
                    if (!GameCommandPermissions.Allows(state, command)) throw new InvalidDataException("The resource driver left the exact current capability.");
                    await engine.ExecuteAutomatedAsync(observation.SeatId, command);
                    evidence.Receipt(observation, command, true);
                    await history.DrainAsync(engine, [observation], evidence, force: true);
                    commands.Add(new { observation.SeatId, state.Revision, decision.Id, decision.ExecutionId,
                        command = command.GetType().Name, scripted = true });
                    submitted = true; break;
                }
                if (!submitted) await Task.Delay(2);
            }
        } catch (Exception error) { failure = error; throw; }
        finally {
            try {
                File.WriteAllText(Path.Combine(directory, "private-resource-witnesses.json"), JsonSerializer.Serialize(new {
                    scope = "Scripted actual engine/provider witnesses. Saved Commander deck and controlled eight-card runtime are separate; no campaign qualification or AI-strategy credit.",
                    failure = failure?.ToString(), initiated, complete, genericControl, firstTurn,
                    sourceIds, executionIds, pending, lastPublic, snapshots, commands, witnesses = evidence.Witnesses,
                    genericPayments = executionIds.TryGetValue("Traxos, Scourge of Kroog", out var root)
                        ? evidence.PaymentsFor(gameId, 0, root) : []
                }, new JsonSerializerOptions { WriteIndented = true }));
            } catch when (failure != null) { }
            try { await engine.StopSimulationAsync(gameId); }
            catch when (failure != null) { }
        }

        async Task<string> Save(string name, string commander, string basic)
        {
            var imported = await engine.ImportAsync(name, "Deck\n99 " + basic + "\nCommander\n1 " + commander);
            var deck = await engine.OpenDeckAsync(imported.Id);
            if (deck.StructureValid == false || deck.Problem.Length > 0 || !deck.Issues.IsEmpty)
                throw new InvalidDataException("The saved resource-fixture Commander deck must remain legal.");
            var saved = await engine.SaveDeckAsync(deck.Id, deck.Revision);
            if (saved.SaveError.Length > 0) throw new InvalidDataException(saved.SaveError);
            return "deck:" + saved.Id;
        }
    }

    private static Card Own(GameSnapshot state, string name) => state.Viewer!.Zone("Battlefield").Cards.Single(card => card.Name == name);
    private static DecisionCommand Answer(GameSnapshot state)
    {
        var decision = state.Decision!;
        if (decision.Payload is EntitySelectionPayload selection && !selection.Complete.Enabled) {
            var card = CardViews.Located(state).Select(item => item.Card).FirstOrDefault(item => item.Selectable);
            if (card != null) return new SelectCard(state.Id, decision.Id, card.Key);
            if (!selection.Players.IsEmpty) return new SelectPlayer(state.Id, decision.Id,
                selection.Players.Contains(0) ? 0 : selection.Players[0]);
        }
        if (decision.Payload is TargetSelectionPayload targets && !targets.Complete.Enabled) {
            var card = CardViews.Located(state).Select(item => item.Card).FirstOrDefault(item => item.Selectable);
            if (card != null) return new SelectCard(state.Id, decision.Id, card.Key);
            if (!targets.Players.IsEmpty) return new SelectPlayer(state.Id, decision.Id, targets.Players[0]);
        }
        if (decision.Payload is OptionsPayload options) return new ChooseOptions(state.Id, decision.Id,
            options.Selection.Choices.Take(options.Selection.Range.Min).Select(item => item.Index).ToImmutableArray());
        if (decision.Payload is AbilityChoicePayload abilities) return new ChooseOptions(state.Id, decision.Id,
            abilities.Selection.Choices.Take(abilities.Selection.Range.Min).Select(item => item.Index).ToImmutableArray());
        if (decision.Payload is OrderingPayload ordering) return new OrderOptions(state.Id, decision.Id,
            ordering.Selection.Choices.Select(item => item.Index).ToImmutableArray());
        if (decision.Payload is RevealPayload) return new AcknowledgeReveal(state.Id, decision.Id);
        if (decision.Payload is NumberPayload { Range: { } range }) return new ChooseNumber(state.Id, decision.Id, range.Min);
        return DecisionCommands.Confirm(state) ?? throw new InvalidDataException("Unhandled actual resource input: " + decision.Payload.GetType().Name);
    }
}
