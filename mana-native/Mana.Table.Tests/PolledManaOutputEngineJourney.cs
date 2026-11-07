using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Reproduces the failed consuming-source payment through current ordinary
// owners, not historical command handles or a special rules/payment adapter.
internal static class PolledManaOutputEngineJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths, string productionJar,
        string? sourceRevision = null, string? sourceManifest = null)
    {
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar, verifyPackage: sourceRevision != null,
            sourceRevision: sourceRevision, sourceManifest: sourceManifest);
        await RunAsync(paths, runtime, (passed, message) => {
            if (!passed) throw new InvalidDataException(message);
        });
    }

    internal static async Task RunAsync(ForgePaths paths, AiReleaseRuntime runtime, Action<bool, string> check)
    {
        string directory = Path.Combine(paths.Profile, "polled-mana-output"); Directory.CreateDirectory(directory);
        var launch = PolledFanoutEngineJourney.PrepareLauncher(paths, directory, runtime, "PolledManaOutput");
        var engines = Enumerable.Range(0, 2).Select(index => new ForgeEngine(launch with {
            Profile = Path.Combine(directory, "connection-" + index)
        })).ToArray();
        var elapsed = Stopwatch.StartNew();
        Stopwatch? active = null;
        var checks = new List<string>();
        var receipts = new ConcurrentQueue<Receipt>();
        var sourceQueries = new ConcurrentQueue<SourceActionSet>();
        var sources = engines.Select(engine => new Sources(engine, sourceQueries)).ToArray();
        var ports = engines.Select((engine, index) => new PolledLobbyPorts.ReadPort(engine) {
            Acknowledged = (command, before) => receipts.Enqueue(new(index, command, before))
        }).ToArray();
        using var evidence = new AiCampaignDiagnostics(directory);
        var players = engines.Select((engine, index) => new ConnectedPlayerDriver(ports[index], engine, sources[index],
            engine, engine, evidence, 261106 + index, MixedGameDriveMode.PolledBackground,
            testPolicy: new KeepFixtureOpening(),
            passing: ConnectedPlayerPassingMode.FullControl)).ToArray();
        var botPort = new PolledLobbyPorts.BotPort(engines[0]);
        var ai = new AiTableHost(botPort, _ => new KeepFixtureOpening(), 261106,
            new(TimeSpan.FromSeconds(3), 256), diagnostics: botPort, pacing: new TimedAiHostPacing(TimeSpan.FromSeconds(2.4)));
        var acceptedFrames = new List<GameSnapshot>[] { [], [] };
        var lastFrames = new GameSnapshot?[2];
        var lastPayloads = new string?[2];
        var observedFrames = new int[2];
        var identicalFrames = new int[2];
        Exception? failure = null;
        var cleanupErrors = new List<string>();
        bool complete = false;
        GameSnapshot? initial = null;
        ImmutableArray<HistoryRecord> history = [];
        int steps = 0, coherent = 0;
        var facts = new Dictionary<string, int>();
        double? creditedTotalSeconds = null, creditedActiveSeconds = null;
        MixedGamePublicCohorts? comparisons = null;
        void Guard() {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(90) || active?.Elapsed > TimeSpan.FromSeconds(45))
                throw new TimeoutException("The bounded ordinary mana-output journey exceeded its unchanged declared phase envelope.");
        }
        void Require(bool value, string message) {
            if (!value) throw new InvalidDataException(message);
            checks.Add(message); check(true, message);
        }
        try {
            await Task.WhenAll(engines.Select(engine => engine.InitializeAsync())); Guard();
            await ai.StartAsync(); Guard();
            var host = await Import(engines[0], "AI gate consuming-output-payment", "Wastes", "Traxos, Scourge of Kroog");
            var guest = await Import(engines[1], "Mana-output guest", "Plains", "Isamaru, Hound of Konda");
            var red = await Import(engines[0], "Mana-output red", "Mountain", "Rograkh, Son of Rohgahh");
            var white = await Import(engines[0], "Mana-output white", "Plains", "Isamaru, Hound of Konda");
            var lobby = await engines[0].HostAsync(4, false); Guard();
            string port = lobby.Addresses.First(address => address.Url.Contains(':')).Url.Split(':').Last();
            await engines[1].JoinAsync("127.0.0.1:" + port); Guard();
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 2, "AI", "deck:" + red.Id + ":" + red.LibraryToken); Guard();
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 3, "AI", "deck:" + white.Id + ":" + white.LibraryToken); Guard();
            foreach (var entry in new[] { (Engine: engines[0], Deck: host), (Engine: engines[1], Deck: guest) }) {
                await entry.Engine.SelectDeckAsync(new(lobby.TableId, entry.Deck.Id, entry.Deck.LibraryToken)); Guard();
                await entry.Engine.SetReadyAsync(true); Guard();
            }
            while (!(lobby = await engines[0].LobbyAsync()).CanStart) { Guard(); await Task.Delay(5); }
            Require(lobby.Seats.Count(seat => seat.Type == "AI") == 2
                    && lobby.Seats.Where(seat => seat.Type == "AI").Select(seat => seat.OpponentId).Distinct().Count() == 2
                    && lobby.Seats.All(seat => seat.Ready && seat.DeckPreview is { Problem: null or "" }),
                "The consuming-output witness starts a real TCP two-human/two-distinct-AI lobby with legal selected lists");
            for (int index = 0; index < 2; index++) players[index].BindLobby(await engines[index].LobbyAsync());
            active = Stopwatch.StartNew(); await engines[0].StartAsync(); Guard();
            while (!complete) {
                Guard(); steps++;
                foreach (var player in players) await player.ObserveAsync();
                if (ai.LastFault != null) throw new InvalidDataException("The actual background AI faulted: " + JsonSerializer.Serialize(ai.LastFault));
                for (int index = 0; index < 2; index++) {
                    if (players[index].Client.State is not { } state) continue;
                    if (ReferenceEquals(lastFrames[index], state)) continue;
                    lastFrames[index] = state; observedFrames[index]++;
                    string payload = JsonSerializer.Serialize(state);
                    if (lastPayloads[index] == payload) identicalFrames[index]++;
                    else {
                        if (acceptedFrames[index].Count >= 128) throw new InvalidDataException("The bounded payment witness exceeded128 distinct admitted frames per ordinary owner.");
                        acceptedFrames[index].Add(state); lastPayloads[index] = payload;
                    }
                    if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(state)) throw new InvalidDataException("An ordinary owner received a concealed private window.");
                    if (state.Players.Length != 4) continue;
                    comparisons ??= new(state.Players.Select(participant => participant.Id));
                    comparisons.Observe(state.ViewerId, state);
                    coherent = comparisons.Comparisons;
                }
                var hostState = players[0].Client.State;
                if (initial == null && hostState is { Turn: 1, PhaseKey: "MAIN1", Decision.Payload: PriorityPayload }
                        && hostState.ActivePlayerId == hostState.ViewerId) {
                    Require(hostState.Viewer!.Zone("Command").Cards.Count(card => card.Name == "Power Play") == 1
                            && hostState.Viewer.Zone("Battlefield").Cards.Count(card => card.Name == "Wastes" && !card.Tapped) == 1
                            && hostState.Viewer.Zone("Battlefield").Cards.Count(card => card.Name == "Crystal Vein" && !card.Tapped) == 1
                            && hostState.Viewer.Zone("Hand").Cards.All(card => card.Name == "Foundry Inspector"),
                        "Declared pregame resources put the real host first with Inspector, one untapped Wastes and one untapped Crystal Vein");
                    initial = hostState;
                }
                if (initial != null && players.All(player => player.Client.State is { Turn: 1, Stack.IsEmpty: true } state
                        && state.Players.Single(participant => participant.Id == initial.ViewerId).Zone("Battlefield").Cards.Any(card => card.Name == "Foundry Inspector"))) {
                    complete = true; break;
                }
                foreach (var player in players) await player.StepAsync();
                await Task.Delay(1);
            }
            var final = players[0].Client.State!;
            var owner = final.Viewer!;
            Require(owner.Zone("Battlefield").Cards.Count(card => card.Name == "Foundry Inspector") == 1
                    && owner.Zone("Battlefield").Cards.Count(card => card.Name == "Wastes" && card.Tapped) == 1
                    && owner.Zone("Battlefield").Cards.All(card => card.Name != "Crystal Vein")
                    && owner.Zone("Graveyard").Cards.Count(card => card.Name == "Crystal Vein") == 1 && owner.Mana.Values.Sum() == 0,
                "The actual three-mana cast resolves once, spends the pool, taps Wastes and sacrifices Crystal Vein before the next untap");
            var consuming = receipts.Where(receipt => receipt.Index == 0 && receipt.Command is UseOfferedAction
                && CardViews.FindActionSource(receipt.Before!, ((UseOfferedAction)receipt.Command).Card)?.Card.Name == "Crystal Vein").ToArray();
            Require(consuming is [{ Command: UseOfferedAction { ManaOutput: null } used }]
                    && sourceQueries.Any(set => set.SourceCardKey == used.Card && set.DecisionId == used.DecisionId
                        && set.Actions.Any(action => action.Id == used.ActionId
                            && action.Text.Contains("Sacrifice", StringComparison.OrdinalIgnoreCase) && action.QuickMana == null)),
                "The current ordinary source reader and client dispatch exactly one consuming activation without an unauthorized explicit output");
            facts["consumingActivationReceipts"] = consuming.Length;
            facts["explicitConsumingOutputs"] = consuming.Count(receipt => ((UseOfferedAction)receipt.Command).ManaOutput != null);
            facts["resolvedInspectors"] = owner.Zone("Battlefield").Cards.Count(card => card.Name == "Foundry Inspector");
            facts["sacrificedCrystalVeins"] = owner.Zone("Graveyard").Cards.Count(card => card.Name == "Crystal Vein");
            facts["tappedWastes"] = owner.Zone("Battlefield").Cards.Count(card => card.Name == "Wastes" && card.Tapped);
            facts["remainingPool"] = owner.Mana.Values.Sum();
            history = await ReadHistory(engines[0], final);
            facts["committedCrystalVeinColorless"] = history.Where(record => record.SourceName == "Crystal Vein" && record.Payment?.Produced != null)
                .Sum(record => record.Payment!.Produced!.GetValueOrDefault("C"));
            facts["committedWastesColorless"] = history.Where(record => record.SourceName == "Wastes" && record.Payment?.Produced != null)
                .Sum(record => record.Payment!.Produced!.GetValueOrDefault("C"));
            facts["committedManaSpent"] = history.Where(record => record.Payment?.Spent != null).Sum(record => record.Payment!.Spent!.Values.Sum());
            Require(facts["committedCrystalVeinColorless"] == 2 && facts["committedWastesColorless"] == 1 && facts["committedManaSpent"] == 3,
                "Committed typed payment history attributes two colorless mana to the sacrificed source, one to Wastes and three spent");
            Require(players.All(player => player.Client.ObservationDelivery == ObservationDeliveryMode.Polled
                        && player.ProviderAcceptances == player.ClientAdmissions && !player.Client.NeedsRecovery && !player.Client.UncertainCommand
                        && player.AutoEvidence is { AcceptedGrants: 0, CertifiedAutomaticPasses: 0 })
                    && ports.All(port => port.Failures == 0) && coherent > 0 && botPort.Faults.IsEmpty,
                "Both FullControl ordinary owners retain current receipts, public coherence and private visibility without standing Auto, rejected commands or recovery");
            Guard(); creditedTotalSeconds = elapsed.Elapsed.TotalSeconds; creditedActiveSeconds = active.Elapsed.TotalSeconds;
        }
        catch (Exception error) { failure = error; }
        finally {
            var stopped = players.Select(player => player.Client.State).ToArray();
            foreach (var player in players) player.Dispose();
            try { await ai.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            foreach (var engine in engines) try { await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            evidence.DrainPending();
            File.WriteAllText(Path.Combine(directory, "private-evidence.json"), JsonSerializer.Serialize(new {
                initial, stopped, acceptedFrames, receipts = receipts.ToArray(), sourceQueries = sourceQueries.ToArray(), history,
                botReceipts = botPort.Receipts.ToArray(), botFaults = botPort.Faults.ToArray(), botHistories = ai.Histories
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
                scenario = "ordinary-polled-consuming-output-payment", passed = complete && failure == null && cleanupErrors.Count == 0,
                failure = failure?.ToString(), cleanupErrors, checks, facts, steps, coherentPublicComparisons = coherent,
                publicComparison = comparisons?.Evidence,
                creditedTotalSeconds, creditedActiveSeconds,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, activeSeconds = active?.Elapsed.TotalSeconds,
                totalBudgetSeconds = 90, activeBudgetSeconds = 45, passing = ConnectedPlayerPassingMode.FullControl.ToString(),
                opening = "Declared fixture script keeps the opening hand; every subsequent decision delegates to production CasualCommanderPolicy.",
                observedFrames, identicalFrames,
                players = players.Select((player, index) => new { index, viewerId = stopped[index]?.ViewerId,
                    player.ClientAdmissions, player.ProviderAcceptances, player.AutoEvidence }), runtime,
                scope = "Declared deterministic pregame resources; actual current ordinary TCP owners, production policy, source readers, cost/choice path and typed history. Not a natural completed game, standing-Auto qualification or human UX approval."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanupErrors.Count > 0) throw new IOException("The payment witness could not cleanly dispose its actual owners.");
    }

    private static Task<DeckSummary> Import(ForgeEngine engine, string name, string land, string commander)
        => engine.ImportAsync(name, "Deck\n99 " + land + "\nCommander\n1 " + commander);

    private static async Task<ImmutableArray<HistoryRecord>> ReadHistory(ForgeEngine engine, GameSnapshot state)
    {
        var history = MatchHistoryReadModel.Empty(state.Id, state.ViewerId);
        for (int page = 0; page < 16; page++) {
            var batch = await engine.HistoryAsync(state.Id, history.Cursor);
            history = history.Append(batch, out _);
            if (!history.CoverageComplete) throw new InvalidDataException("The bounded payment witness lost retained history coverage.");
            if (!batch.HasMore) return history.Records;
        }
        throw new InvalidDataException("The bounded payment witness exceeded16 history pages.");
    }

    private sealed record Receipt(int Index, [property: JsonIgnore] GameCommand Command, GameSnapshot? Before)
    {
        public string Kind => Command.GetType().Name;
        public JsonElement Answer => JsonSerializer.SerializeToElement(Command, Command.GetType());
    }
    private sealed class Sources(ForgeEngine engine, ConcurrentQueue<SourceActionSet> trace) : ISourceActions
    {
        public bool SupportsSourceActions => engine.SupportsSourceActions;
        public async Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var answer = await engine.SourceActionsAsync(gameId, decisionId, sourceCardKey);
            trace.Enqueue(answer); return answer;
        }
    }

    private sealed class KeepFixtureOpening : IAiPolicy
    {
        private readonly CasualCommanderPolicy ordinary = new();
        public AiPolicyDescriptor Descriptor => ordinary.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
            => request.Frame.State.Decision is { Payload: OpeningHandPayload } decision
                ? Task.FromResult<AiProposal>(new AiProposal.Answer(new KeepOpeningHand(request.Frame.State.Id, decision.Id), "declared-fixture-opening-keep"))
                : ordinary.EvaluateAsync(request, cancellation);
    }
}
