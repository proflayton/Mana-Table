using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// One fixture in the existing packaged JVM. Setup supplies only declared
// pregame permanents; every payment, input and result uses the native adapter.
internal static class ExecutionEngineJourney
{
    internal static async Task RunAsync(ForgeEngine engine, string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "execution-loch-mare"); Directory.CreateDirectory(directory);
        var observations = new List<GameSnapshot>();
        var commands = new List<object>();
        string actorDeck = await Save("Execution journey Loch Mare");
        string otherDeck = await Save("Execution journey harmless opponents");
        await engine.EnableAutomatedSeatsAsync();
        var spectator = await engine.StartSimulationAsync([actorDeck, otherDeck, otherDeck, otherDeck]);
        string gameId = spectator.Id, activation = "", sourceId = "";
        int beforeHand = -1;
        bool started = false, stacked = false;
        GameSnapshot? result = null;
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
                        check(actor.Players.Length == 4 && mare.Counters.Values.Sum() == 3
                                && actor.Viewer.Zone("Battlefield").Cards.Count(card => card.Name == "Island" && !card.Tapped) == 3,
                            "The packaged test wrapper supplies Loch Mare's real three ETB counters and three untapped Islands before its actual activation");
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
                        && ExecutionFrames.Find(actor, activation)?.Outcome == ExecutionOutcome.Resolved) {
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
                    commands.Add(new { observation.SeatId, state.Revision, decision.Id, decision.ExecutionId, command = command.GetType().Name });
                    submitted = true; break;
                }
                if (!submitted) await Task.Delay(2);
            }
            check(result.Activity.Any(item => item.ExecutionId == activation && item.Kind == "draw" && item.ExecutionStep == ExecutionStep.Resolution)
                    && result.Viewer!.Zone("Battlefield").Cards.Count(card => card.Name == "Island" && card.Tapped) == 2
                    && Mare(result).Counters.Values.Sum() == 2,
                "Resolution draws exactly one actual card, keeps the paid counter removed and retains both tapped mana sources under the same execution");
            Replay(observations, engine, directory, activation, sourceId, check);
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

    private static Card Mare(GameSnapshot state) => state.Viewer!.Zone("Battlefield").Cards.Single(card => card.Name == "Loch Mare");
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

    private static void Replay(IReadOnlyList<GameSnapshot> trace, ForgeEngine catalog, string profile, string executionId, string sourceId, Action<bool, string> check)
    {
        var connection = new ReadOnlyTrace(trace[0]);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(profile);
        using var app = new TableApplication(new(catalog, catalog, connection, () => Task.CompletedTask), profile, initialState: trace[0]);
        app.Automated = true; app.Initialize();
        var view = app.TestPort; view.SetTableStudy(true);
        double seconds = 0;
        var pointer = new Point(20, 60);
        PresentationFrame Tick()
        {
            seconds += 1.0 / 60;
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60)),
                new(pointer.X, pointer.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
            return app.ReadPresentation();
        }
        var frame = Tick();
        bool hovered = false, announced = false;
        foreach (var state in trace.Skip(1)) {
            connection.State = state; view.Accept(state); frame = Tick(); frame = Tick();
            if (state.Stack.FirstOrDefault(item => item.ExecutionId == executionId) is not { } stacked) continue;
            announced |= view.AnnouncementId?.StartsWith("execution:" + executionId + ":", StringComparison.Ordinal) == true;
            var hit = frame.Hits.LastOrDefault(hit => hit.Id == "study-stack-effect:" + stacked.Id);
            if (hit == null || hovered) continue;
            pointer = hit.Bounds.Center; frame = Tick(); frame = Tick();
            check(view.ReferencePreviewCardId == sourceId && view.ReferencePreviewStackId == stacked.Id && connection.Commands == 0,
                "Hovering the actual adapter-produced Loch Mare stack occurrence opens its complete source ability through production presentation without answering or activating");
            hovered = true; pointer = new(20, 60);
        }
        var final = trace[^1];
        var displayed = frame.Cards.Single(card => card.Id == sourceId && card.Zone == "Battlefield");
        check(hovered && announced && displayed.Card.Counters.Values.Sum() == 2 && displayed.Card.Power == Mare(final).Power
                && frame.Stamp.GameId == final.Id && frame.Stamp.Revision == final.Revision && connection.Commands == 0,
            "The same real execution is announced on the production stack and finishes with the authoritative counter count and stats, without replaying commands");
        File.WriteAllText(Path.Combine(profile, "production-final-frame.json"), JsonSerializer.Serialize(frame.Observe()));
    }

    private sealed class ReadOnlyTrace(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State { get; set; } = state;
        internal int Commands { get; private set; }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ExecuteAsync(GameCommand command) { Commands++; throw new InvalidOperationException("A recorded rules trace cannot grant replay command authority."); }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("A recorded rules trace cannot concede a live game.");
    }
}
