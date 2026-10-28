using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Scenarios;
using Mana.Table.Presentation;

namespace Mana.Table;

// One fixture in the existing packaged JVM. Setup supplies only declared
// pregame permanents; every payment, input and result uses the native adapter.
internal static class ExecutionEngineJourney
{
    // Fast local development route. It verifies the launcher/dependency pair,
    // but cannot replace the release gate's exact-source package verification.
    internal static async Task RunStandaloneAsync(ForgePaths paths, string productionJar)
    {
        Directory.CreateDirectory(paths.Profile);
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar);
        var checks = new List<string>(); string? failure = null;
        try {
            await using var engine = new ForgeEngine(paths);
            await engine.InitializeAsync();
            await RunAsync(engine, paths.Profile, (passed, description) => {
                if (!passed) throw new InvalidOperationException(description);
                checks.Add(description);
            });
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            File.WriteAllText(Path.Combine(paths.Profile, "execution-journey-result.json"), JsonSerializer.Serialize(new {
                passed = failure == null, error = failure, runtime, checks,
                scope = "Local development journey; unbound source/resources, not exact-source release evidence."
            }));
        }
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, Action<bool, string> check, string? sourceRevision = null)
    {
        string directory = Path.Combine(profile, "execution-loch-mare"); Directory.CreateDirectory(directory);
        var observations = new List<GameSnapshot>();
        var commands = new List<object>();
        string actorDeck = await Save("Execution journey Loch Mare");
        string otherDeck = await Save("Execution journey harmless opponents");
        await engine.EnableAutomatedSeatsAsync();
        var spectator = await engine.StartSimulationAsync([actorDeck, otherDeck, otherDeck, otherDeck]);
        string gameId = spectator.Id, activation = "", sourceId = "";
        int beforeHand = -1, beforeSkulkerCounters = -1;
        bool started = false, stacked = false;
        GameSnapshot? result = null;
        var acceptedStackPasses = new List<GameSnapshot>();
        GameSnapshot? pending = null;
        var timer = Stopwatch.StartNew();
        var answered = new HashSet<(int Seat, long Revision, string Decision, long Offers)>();
        Exception? primaryFailure = null;
        try {
            while (result == null) {
                if (timer.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("The native Loch Mare execution journey exceeded its focused watchdog.");
                var seats = await engine.ObserveAutomatedSeatsAsync();
                var actor = seats.FirstOrDefault(item => item.SeatId == 0)?.State;
                if (actor?.Status is "error" or "finished") throw new InvalidOperationException(actor.Error ?? "Execution fixture ended before its result.");
                if (actor != null && (started || actor.Decision?.Payload is PriorityPayload
                        && actor.ActivePlayerId == 0 && actor.PhaseKey == "MAIN1")) {
                    if (!started) {
                        var mare = Mare(actor);
                        sourceId = CardViews.Identity(mare); beforeHand = actor.Viewer!.Zone("Hand").Count;
                        beforeSkulkerCounters = Skulker(actor).Counters.Values.Sum();
                        check(actor.Players.Length == 4 && mare.Counters.Values.Sum() == 3
                                && actor.Viewer.Zone("Battlefield").Cards.Count(card => card.Name == "Island" && !card.Tapped) == 3
                                && actor.Viewer.Zone("Battlefield").Cards.Any(card => card.Name == "Pir, Imaginative Rascal"),
                            "The packaged test wrapper supplies Loch Mare's real three ETB counters, three untapped Islands and Pir/Skulker before its actual activation");
                        started = true;
                    }
                    if (observations.Count == 0 || observations[^1].Revision != actor.Revision
                        || observations[^1].Executions?.LastSequence != actor.Executions?.LastSequence
                        || observations[^1].Activity.LastOrDefault()?.Id != actor.Activity.LastOrDefault()?.Id
                        || observations[^1].Decision?.Id != actor.Decision?.Id || observations[^1].Status != actor.Status) observations.Add(actor);
                    if (!stacked && activation.Length > 0 && actor.Stack.Any(item => item.ExecutionId == activation)) {
                        stacked = true;
                        var execution = ExecutionFrames.Find(actor, activation)!;
                        check(execution.Kind == ExecutionKind.Activation && execution.BudgetRootId == activation
                                && execution.SourceCardId == sourceId && execution.ActorPlayerId == 0
                                && Mare(actor).Counters.Values.Sum() == 2,
                            "The paid Loch Mare activation reaches its exact stack occurrence after removing one real counter");
                        var nested = actor.Executions!.Occurrences.Where(item => item.ParentId == activation && item.Kind == ExecutionKind.Mana).ToArray();
                        check(nested.Length == 2 && nested.All(item => item.BudgetRootId == activation && item.Outcome == ExecutionOutcome.Resolved),
                            "Both manually activated Islands retain separate mana occurrences under the activation's one budget root");
                        var counter = actor.Activity.LastOrDefault(item => item.ExecutionId == activation && item.Change is { Kind: "counters", Before: 3, After: 2 });
                        check(counter?.ExecutionStep == ExecutionStep.Cost,
                            "The actual committed three-to-two counter change names the activation's cost step rather than a later prompt or adjacent effect");
                    }
                    if (stacked && actor.Stack.IsEmpty && actor.Viewer!.Zone("Hand").Count == beforeHand + 1
                        && Skulker(actor).Counters.Values.Sum() == beforeSkulkerCounters + 2
                        && ExecutionFrames.Find(actor, activation)?.Outcome == ExecutionOutcome.Resolved
                        && actor.Decision?.Payload is PriorityPayload { QuietYieldToken.Length: > 0 }) {
                        result = actor; break;
                    }
                }
                bool submitted = false;
                foreach (var observation in seats.OrderBy(item => item.SeatId)) {
                    var state = observation.State;
                    if (state.Decision is not { } decision || state.Status != "playing"
                        || !answered.Add((observation.SeatId, state.Revision, decision.Id, observation.Domain.Version))) continue;
                    pending = state;
                    DecisionCommand command;
                    if (observation.SeatId == 0 && started && activation.Length == 0 && decision.Payload is PriorityPayload) {
                        var mare = Mare(state);
                        var source = observation.Domain.Sources.Single(set => set.SourceCardId == sourceId);
                        var offer = source.Actions.Single(action => observation.Domain.Actions.Any(semantic => semantic.ActionId == action.Id
                            && semantic.Effect == ActionEffectKind.Draw));
                        command = new UseOfferedAction(state.Id, decision.Id, mare.Key, offer.Id);
                    } else if (observation.SeatId == 0 && decision.Payload is PaymentPayload payment) {
                        activation = decision.ExecutionId;
                        if (activation.Length == 0 || decision.ExecutionStep != ExecutionStep.Cost)
                            throw new InvalidDataException("The real mana input did not retain its owning activation.");
                        var islands = state.Viewer!.Zone("Battlefield").Cards.Where(card => card.Name == "Island").ToArray();
                        if (islands.Count(card => card.Tapped) < 2) {
                            var island = islands.First(card => !card.Tapped);
                            var source = observation.Domain.Sources.Single(set => set.SourceCardId == CardViews.Identity(island));
                            var offer = source.Actions.Single(action => observation.Domain.Actions.Any(semantic => semantic.ActionId == action.Id && semantic.ManaAbility));
                            command = new UseOfferedAction(state.Id, decision.Id, island.Key, offer.Id);
                        } else if (payment.Pay.Enabled) command = new PayMana(state.Id, decision.Id);
                        else throw new InvalidDataException("Two real Island activations did not fund Loch Mare's actual mana bill.");
                    } else command = Answer(state);
                    if (!GameCommandPermissions.Allows(state, command)) throw new InvalidDataException("The focused driver proposed an answer outside its exact current capability.");
                    await engine.ExecuteAutomatedAsync(observation.SeatId, command);
                    if (observation.SeatId == 0 && started && activation.Length > 0 && command is PassPriority && !state.Stack.IsEmpty)
                        acceptedStackPasses.Add(state);
                    commands.Add(new { observation.SeatId, state.Revision, decision.Id, decision.ExecutionId, command = command.GetType().Name });
                    submitted = true; break;
                }
                if (!submitted) await Task.Delay(2);
            }
            check(result.Activity.Any(item => item.ExecutionId == activation && item.Kind == "draw" && item.ExecutionStep == ExecutionStep.Resolution)
                    && result.Viewer!.Zone("Battlefield").Cards.Count(card => card.Name == "Island" && card.Tapped) == 2
                    && Mare(result).Counters.Values.Sum() == 2,
                "Resolution draws exactly one actual card, keeps the paid counter removed and retains both tapped mana sources under the same execution");
            var replacement = result.Executions!.Occurrences.LastOrDefault(item => item.RuleKind == ExecutionRuleKind.Replacement
                && item.SourceName == "Pir, Imaginative Rascal" && item.RuleResult is ExecutionRuleResult.Updated or ExecutionRuleResult.Replaced);
            check(replacement != null && replacement.ParentId != activation
                    && ExecutionFrames.Find(result, replacement.ParentId ?? "")?.Kind == ExecutionKind.Trigger
                    && Skulker(result).Counters.Values.Sum() == beforeSkulkerCounters + 2
                    && result.Activity.Any(item => item.Change is { Kind: "counters" } change
                        && item.CardId == CardViews.Identity(Skulker(result)) && change.After - change.Before == 2),
                "The real draw trigger owns Pir's replacement and commits exactly two Skulker counters rather than predicting a printed one-counter result");
            Replay(observations, directory, activation, sourceId, check, sourceRevision);
            CheckAcceptedResponse(observations, acceptedStackPasses, result, check);
        } catch (Exception error) {
            primaryFailure = error; throw;
        } finally {
            try {
                File.WriteAllText(Path.Combine(directory, "private-native-observations.json"), JsonSerializer.Serialize(new {
                    privacy = "Exact authorized seat-zero observations, including its hand; local test evidence, never release assets.",
                    error = primaryFailure?.ToString(), pending, observations, commands
                }));
            } catch when (primaryFailure != null) { /* Preserve the actual failed journey. */ }
            try { await engine.StopSimulationAsync(gameId); }
            catch when (primaryFailure != null) { /* Cleanup cannot turn a failed journey into another claimed result. */ }
        }

        async Task<string> Save(string name)
        {
            var imported = await engine.ImportAsync(name, "Deck\n99 Island\nCommander\n1 The Reality Chip");
            var details = await engine.OpenDeckAsync(imported.Id);
            if (details.StructureValid == false || details.Problem.Length > 0 || !details.Issues.IsEmpty)
                throw new InvalidDataException("The execution journey requires a valid Commander deck.");
            var saved = await engine.SaveDeckAsync(details.Id, details.Revision);
            if (saved.SaveError.Length > 0) throw new InvalidDataException(saved.SaveError);
            return "deck:" + saved.Id;
        }
    }

    private static void CheckAcceptedResponse(IReadOnlyList<GameSnapshot> trace, IReadOnlyList<GameSnapshot> acceptedPasses,
        GameSnapshot result, Action<bool, string> check)
    {
        var accepted = acceptedPasses.FirstOrDefault();
        check(accepted?.Decision?.Payload is PriorityPayload && !accepted.Stack.IsEmpty,
            "The provider journey retained the real authorized nonempty-stack priority which its driver successfully passed");
        if (accepted == null) throw new InvalidDataException("No actual accepted stack response was recorded.");
        int start = Enumerable.Range(0, trace.Count).First(index => trace[index].Revision == accepted.Revision
            && trace[index].Decision?.Id == accepted.Decision?.Id && trace[index].Executions?.LastSequence == accepted.Executions?.LastSequence);
        var attention = new PriorityAttention(); attention.Observe(accepted);
        attention.Submitted(accepted, new PassPriority(accepted.Id, accepted.Decision!.Id));
        bool interrupted = false, triggerAcknowledged = false;
        foreach (var state in trace.Skip(start + 1)) {
            attention.Observe(state);
            if (state.StackInterruptionVersion != accepted.StackInterruptionVersion && attention.NeedsResponse) interrupted = true;
            if (acceptedPasses.Skip(1).Any(pass => pass.Revision == state.Revision && pass.Decision?.Id == state.Decision?.Id
                    && pass.Executions?.LastSequence == state.Executions?.LastSequence)) {
                triggerAcknowledged |= state.Stack.Any(item => item.Kind == StackActionKind.TriggeredAbility);
                attention.Submitted(state, new PassPriority(state.Id, state.Decision!.Id));
            }
        }
        check(interrupted && triggerAcknowledged && result.StackInterruptionVersion > accepted.StackInterruptionVersion
                && result.Decision?.Payload is PriorityPayload { QuietYieldToken.Length: > 0 }
                && !attention.NeedsResponse,
            "The actual new Skulker trigger interrupts the accepted Mare response, receives its own accepted pass and then regains quiet permission after correlated settlement");
    }

    private static Card Mare(GameSnapshot state) => state.Viewer!.Zone("Battlefield").Cards.Single(card => card.Name == "Loch Mare");
    private static Card Skulker(GameSnapshot state) => state.Viewer!.Zone("Battlefield").Cards.Single(card => card.Name == "Chasm Skulker");
    private static DecisionCommand Answer(GameSnapshot state)
    {
        var decision = state.Decision!;
        if (decision.Payload is EntitySelectionPayload { Complete.Enabled: false }) {
            var card = CardViews.Located(state).Select(item => item.Card).FirstOrDefault(card => card.Selectable);
            if (card != null) return new SelectCard(state.Id, decision.Id, card.Key);
            var players = ((EntitySelectionPayload)decision.Payload).Players;
            if (!players.IsEmpty) return new SelectPlayer(state.Id, decision.Id,
                players.Contains(state.ViewerId) ? state.ViewerId : players[0]);
        }
        if (decision.Payload is OptionsPayload options) return new ChooseOptions(state.Id, decision.Id,
            options.Selection.Choices.Take(options.Selection.Range.Min).Select(choice => choice.Index).ToImmutableArray());
        if (decision.Payload is AbilityChoicePayload abilities) return new ChooseOptions(state.Id, decision.Id,
            abilities.Selection.Choices.Take(abilities.Selection.Range.Min).Select(choice => choice.Index).ToImmutableArray());
        if (decision.Payload is OrderingPayload ordering) return new OrderOptions(state.Id, decision.Id,
            ordering.Selection.Choices.Select(choice => choice.Index).ToImmutableArray());
        if (decision.Payload is RevealPayload) return new AcknowledgeReveal(state.Id, decision.Id);
        if (decision.Payload is NumberPayload { Range: { } range }) return new ChooseNumber(state.Id, decision.Id, range.Min);
        return DecisionCommands.Confirm(state) ?? throw new InvalidDataException("Unhandled required execution-fixture input: " + decision.Payload.GetType().Name);
    }

    private static void Replay(IReadOnlyList<GameSnapshot> trace, string profile, string executionId, string sourceId, Action<bool, string> check, string? sourceRevision)
    {
        string path = Path.Combine(profile, "viewer-private-projection.json");
        new ProjectionRecording(1, "forge", sourceRevision, trace.ToImmutableArray()).WriteNew(path);
        var recording = ProjectionRecording.Read(path, "forge", sourceRevision);
        using var run = new ProjectionReplaySession(recording, Path.Combine(profile, "playback"), new(1600, 900));
        var view = run.View;
        var frame = run.Frame;
        bool hovered = false, announced = false;
        while (run.Advance()) {
            var state = run.Playback.Current; frame = run.Frame;
            if (state.Stack.FirstOrDefault(item => item.ExecutionId == executionId) is not { } stacked) continue;
            announced |= view.AnnouncementId?.StartsWith("execution:" + executionId + ":", StringComparison.Ordinal) == true;
            var hit = frame.Hits.LastOrDefault(hit => hit.Id == "study-stack-effect:" + stacked.Id);
            if (hit == null || hovered) continue;
            frame = run.Hover(hit.Bounds.Center);
            check(view.ReferencePreviewCardId == sourceId && view.ReferencePreviewStackId == stacked.Id && run.Playback.RejectedCommands == 0,
                "Hovering the actual adapter-produced Loch Mare stack occurrence opens its complete source ability through production presentation without answering or activating");
            hovered = true; frame = run.Hover(new(20, 60));
        }
        var final = trace[^1];
        var displayed = frame.Cards.Single(card => card.Id == sourceId && card.Zone == "Battlefield");
        var skulker = Skulker(final);
        var displayedSkulker = frame.Cards.Single(card => card.Id == CardViews.Identity(skulker) && card.Zone == "Battlefield");
        check(hovered && announced && displayed.Card.Counters.Values.Sum() == 2 && displayed.Card.Power == Mare(final).Power
                && displayedSkulker.Card.Counters.OrderBy(item => item.Key).SequenceEqual(skulker.Counters.OrderBy(item => item.Key))
                && displayedSkulker.Card.Power == skulker.Power && displayedSkulker.Card.Toughness == skulker.Toughness
                && frame.Stamp.GameId == final.Id && frame.Stamp.Revision == final.Revision && run.Playback.RejectedCommands == 0,
            "The same real execution is announced on the production stack and finishes with authoritative Mare and Pir-modified Skulker counters and stats, without replaying commands");
        File.WriteAllText(Path.Combine(profile, "production-final-frame.json"), JsonSerializer.Serialize(frame.Observe()));
    }

}
