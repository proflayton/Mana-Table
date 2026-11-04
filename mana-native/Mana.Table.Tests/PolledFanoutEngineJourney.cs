using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;

namespace Mana.Table;

// A declared pregame fixture supplies resources; every draw, trigger, payment,
// consent and receipt then uses the real ordinary TCP/polled composition.
internal static class PolledFanoutEngineJourney
{
    internal static async Task RunStandaloneAsync(ForgePaths paths, string productionJar)
    {
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar);
        await RunAsync(paths, runtime, (passed, message) => {
            if (!passed) throw new InvalidDataException(message);
        });
    }

    internal static async Task RunAsync(ForgePaths paths, AiReleaseRuntime runtime, Action<bool, string> check)
    {
        string directory = Path.Combine(paths.Profile, "polled-fanout"); Directory.CreateDirectory(directory);
        var launch = PrepareLauncher(paths, directory, runtime);
        var engines = Enumerable.Range(0, 2).Select(index => new ForgeEngine(launch with {
            Profile = Path.Combine(directory, "connection-" + index)
        })).ToArray();
        var elapsed = Stopwatch.StartNew();
        var setupTime = Stopwatch.StartNew();
        var workload = new Stopwatch();
        string? failurePhase = null;
        var checks = new List<string>();
        var facts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Require(bool value, string message) {
            if (!value) throw new InvalidDataException(message);
            check(value, message); checks.Add(message);
        }
        var botPort = new PolledLobbyPorts.BotPort(engines[0]);
        var ai = new AiTableHost(botPort, _ => new CasualCommanderPolicy(), 261104,
            // This declared workload needs its full receipt window (~480
            // records per seat), rather than production's128-record ring.
            new(TimeSpan.FromSeconds(3), 2048, historyCapacity: 1024), diagnostics: botPort);
        var players = engines.Select((engine, index) => new Player(engine, index)).ToArray();
        var cohorts = new Dictionary<(string Game, long Board), (string Facts,
            ForgePublicProjectionFacts.ExecutionCorrelation Correlations, HashSet<int> Viewers)>();
        ImmutableHashSet<string> captured = [], eyeTriggers = [];
        GameSnapshot? burst = null, beforeCast = null;
        string castExecution = "", source = "";
        int steps = 0, coherent = 0;
        var stages = new List<object>();
        void Stage(string name) => stages.Add(new { name, seconds = elapsed.Elapsed.TotalSeconds });
        bool complete = false;
        Exception? failure = null;
        var cleanupErrors = new List<string>();
        object? stopped = null;
        var selections = new Dictionary<int, string>();
        try {
            await Task.WhenAll(engines.Select(engine => engine.InitializeAsync()));
            Stage("providers-ready");
            // Enable before the first lobby operation, exactly as app startup.
            await ai.StartAsync();
            var host = await Import(engines[0], "Polled fanout host", "99 Island", "Toothy, Imaginary Friend");
            // Toothy alone is a legal Commander list; this fixture does not
            // claim the subsequently substituted deterministic host library is.
            var guest = await Import(engines[1], "Polled fanout guest", "99 Plains", "Isamaru, Hound of Konda");
            var quietRed = await Import(engines[0], "Quiet red", "99 Mountain", "Rograkh, Son of Rohgahh");
            var quietWhite = await Import(engines[0], "Quiet white", "99 Plains", "Isamaru, Hound of Konda");
            var lobby = await engines[0].HostAsync(4, false);
            string port = lobby.Addresses.First(address => address.Url.Contains(':')).Url.Split(':').Last();
            await engines[1].JoinAsync("127.0.0.1:" + port);
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 2, "AI", "deck:" + quietRed.Id + ":" + quietRed.LibraryToken);
            selections[2] = lobby.Seats.Single(seat => seat.Index == 2).DeckPreview?.SelectionId ?? "";
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 3, "AI", "deck:" + quietWhite.Id + ":" + quietWhite.LibraryToken);
            selections[3] = lobby.Seats.Single(seat => seat.Index == 3).DeckPreview?.SelectionId ?? "";
            foreach (var selection in new[] { (Engine: engines[0], Deck: host), (Engine: engines[1], Deck: guest) }) {
                var receipt = await selection.Engine.SelectDeckAsync(new(lobby.TableId, selection.Deck.Id, selection.Deck.LibraryToken));
                var own = receipt.Seats.Single(seat => seat.Local);
                selections[own.Index] = own.DeckPreview?.SelectionId ?? "";
                await selection.Engine.SetReadyAsync(true);
            }
            while (!(lobby = await engines[0].LobbyAsync()).CanStart) { Guard(); await Task.Delay(5); }
            Require(lobby.Seats.Count(seat => seat.Type == "AI") == 2
                    && lobby.Seats.Where(seat => seat.Type == "AI").Select(seat => seat.OpponentId).Distinct().Count() == 2
                    && selections.Count == 4 && selections.Values.All(id => id.Length > 0)
                    && lobby.Seats.All(seat => seat.Ready && seat.DeckPreview is { } preview
                        && string.IsNullOrEmpty(preview.Problem) && selections.GetValueOrDefault(seat.Index) == preview.SelectionId),
                "The fanout fixture binds a real TCP guest and two distinct legal quiet AI lists before pregame setup");
            foreach (int index in new[] { 0, 1 }) players[index].Client.BindLobby(await engines[index].LobbyAsync());
            await engines[0].StartAsync();
            Stage("table-started");

            while (!complete) {
                Guard(); steps++;
                foreach (var player in players) {
                    player.Client.Pump(); player.RequireHealthy();
                    if (player.Intent is { IsActive: false } stoppedIntent && stoppedIntent.Progress.CompletedCount < 110)
                        throw new InvalidDataException("The ordinary human-" + player.Index + " finite owner stopped before completing its captured queue: "
                            + stoppedIntent.StopReason);
                }
                if (ai.LastFault != null) throw new InvalidDataException("The background AI host faulted: " + JsonSerializer.Serialize(ai.LastFault));
                foreach (var player in players) {
                    if (player.Client.State is not { Status: "playing", Players.Length: 4 } state) { player.Poll(); continue; }
                    // A script tick is not another publication. Compare each
                    // admitted immutable frame once rather than serializing the
                    // entire large board again while its owner awaits I/O.
                    if (!ReferenceEquals(player.Compared, state)) {
                        player.Compared = state;
                        var key = (state.Id, state.BoardRevision);
                        string publicFacts = ForgePublicProjectionFacts.Capture(state);
                        var correlations = ForgePublicProjectionFacts.CaptureExecutionCorrelations(state);
                        if (!correlations.Valid) throw new InvalidDataException("An ordinary fanout observation has invalid execution references.");
                        if (cohorts.TryGetValue(key, out var prior)) {
                            if (prior.Facts != publicFacts) throw new InvalidDataException("Ordinary TCP viewers disagree on the same public fanout board.");
                            var merged = ForgePublicProjectionFacts.MergeExecutionCorrelations(prior.Correlations, correlations)
                                ?? throw new InvalidDataException("Ordinary TCP viewers contradict a shared execution correlation.");
                            prior.Viewers.Add(state.ViewerId); cohorts[key] = (publicFacts, merged, prior.Viewers);
                            if (prior.Viewers.Count == 2) coherent++;
                        } else cohorts.Add(key, (publicFacts, correlations, [state.ViewerId]));
                    }
                    if (captured.IsEmpty && state.Stack.Length == 110) {
                        Require(state.Stack.All(item => item.Kind == StackActionKind.TriggeredAbility
                                && item.Repetition != null && item.ExecutionId.Length > 0)
                                && state.Stack.Select(item => item.ExecutionId).Distinct().Count() == 110,
                            "One real Shamanic Revelation produces110 distinct typed trigger executions after55 actual draws");
                        burst = state;
                        setupTime.Stop(); workload.Start();
                        Stage("actual110-trigger-queue");
                        captured = state.Stack.Select(item => item.ExecutionId).ToImmutableHashSet(StringComparer.Ordinal);
                        eyeTriggers = state.Stack.Where(item => item.Card?.Name == "Mind's Eye").Select(item => item.ExecutionId).ToImmutableHashSet(StringComparer.Ordinal);
                        Require(eyeTriggers.Count == 55 && state.Stack.Count(item => item.Card?.Name == "Toothy, Imaginary Friend") == 55,
                            "The captured queue contains55Toothy counter triggers and55Mind's Eye optional-payment triggers");
                        foreach (var participant in players) participant.Captured = captured;
                    }
                    if (!captured.IsEmpty) {
                        if (player.Intent == null && state.Decision?.Payload is PriorityPayload) {
                            Require(state.Stack.Select(item => item.ExecutionId).ToHashSet(StringComparer.Ordinal).SetEquals(captured),
                                "Each human consents to the exact complete110-trigger queue from their own ordinary priority observation");
                            player.Intent = FiniteResolutionIntent.TryArm(state, state.Stack.Select(item => item.Id))
                                ?? throw new InvalidDataException("The ordinary client could not arm its current finite queue.");
                            Stage("human-" + player.Index + "-finite-consent");
                        }
                        if (!player.Client.Busy && !player.Client.Polling && player.Intent?.IsActive == true) {
                            player.ProbeRetiredPayment();
                            player.Client.AdvanceFinite(player.Intent, player.PauseForRetiredPaymentProbe);
                        }
                    } else if (!player.Client.Busy && !player.Client.Polling) {
                        bool casting = player.Step(beforeCast == null);
                        if (casting) {
                            beforeCast = state;
                            Stage("ordinary-revelation-cast");
                            source = CardViews.Identity(state.Viewer!.Zone("Battlefield").Cards.Single(card => card.Name == "Toothy, Imaginary Friend"));
                            var other = state.Players.Single(participant => participant.Id != state.ViewerId
                                && participant.Zone("Battlefield").Cards.Any(card => card.Name == "Mind's Eye"));
                            Require(state.Viewer.Zone("Battlefield").Cards.Count(card => card.Type.Contains("Creature", StringComparison.Ordinal)) == 55
                                    && state.Viewer.Zone("Battlefield").Cards.Count(card => card.Name == "Forest" && !card.Tapped) == 5
                                    && other.Zone("Battlefield").Cards.Length == 1 && other.Mana.Values.Sum() == 0,
                                "Actual pre-cast ordinary state contains55 host creatures/five untapped Forests and an unfunded lone opposing Mind's Eye");
                        }
                    }
                    player.Poll();
                }
                foreach (var key in cohorts.Keys.OrderByDescending(key => key.Board).Skip(64).ToArray()) cohorts.Remove(key);
                if (!captured.IsEmpty && players.All(player => player.Client.State is { Stack.IsEmpty: true }
                        && !player.Client.Busy && !player.Client.Polling
                        && player.Intent?.Progress is { CapturedCount: 110, CompletedCount: 110, ResolvedCount: 110, RemainingCount: 0, UnknownCount: 0 })
                        && botPort.Executing == 0
                        && ai.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted)) == botPort.Accepted) {
                    complete = true;
                    Stage("both110-terminal-progress");
                }
                await Task.Delay(1);
            }
            var finalHost = players[0].Client.State!; var finalGuest = players[1].Client.State!;
            Require(beforeCast != null && burst != null && players.All(player => player.Terminals.IsSupersetOf(captured)
                    && player.Client.ObservationDelivery == ObservationDeliveryMode.Polled && player.Publications > 0 && player.PollCallbacks > 0),
                "Both default-polled humans observe all110 captured terminal receipts through normal finite continuation");
            var initialHost = beforeCast!.Viewer!; var settledHost = finalHost.Viewer!;
            var initialGuest = beforeCast.Players.Single(player => player.Id == finalGuest.ViewerId);
            facts["hostHandDelta"] = settledHost.Zone("Hand").Count - initialHost.Zone("Hand").Count;
            facts["hostLibraryConsumed"] = initialHost.Zone("Library").Count - settledHost.Zone("Library").Count;
            facts["toothyCounterIncrease"] = Counters(settledHost) - Counters(initialHost);
            facts["hostTappedForests"] = settledHost.Zone("Battlefield").Cards.Count(card => card.Name == "Forest" && card.Tapped);
            Require(settledHost.Zone("Hand").Count == initialHost.Zone("Hand").Count - 1 + 55
                    && settledHost.Zone("Library").Count == initialHost.Zone("Library").Count - 55
                    && CardViews.Identity(settledHost.Zone("Battlefield").Cards.Single(card => card.Name == "Toothy, Imaginary Friend")) == source
                    && Counters(settledHost) == Counters(initialHost) + 55,
                "The cast consumes its card, draws exactly55 library cards and adds55 counters to the same physical Toothy");
            Require(finalGuest.Viewer!.Zone("Hand").Count == initialGuest.Zone("Hand").Count
                    && finalGuest.Viewer.Zone("Library").Count == initialGuest.Zone("Library").Count
                    && finalGuest.Viewer.Mana.OrderBy(pair => pair.Key).SequenceEqual(initialGuest.Mana.OrderBy(pair => pair.Key))
                    && finalGuest.Viewer.Zone("Battlefield").Cards.All(card => !card.Tapped),
                "The unfunded artifact controller draws no cards and spends or taps no resources while55 optional payments are declined");
            var cancels = players[1].Acknowledgements.Where(receipt => receipt.Command is CancelPayment
                && receipt.Offer != null && captured.Contains(receipt.Offer.ProducingTriggerExecutionId)).ToArray();
            facts["capturedOptionalCostDeclines"] = cancels.Length;
            facts["distinctDeclinedProducingTriggers"] = cancels.Select(receipt => receipt.Offer!.ProducingTriggerExecutionId).Distinct().Count();
            facts["humanCertifiedAutomaticPasses"] = players.Sum(player => player.Client.State!.FiniteStackYield?.AutomaticPasses ?? 0);
            Require(cancels.Length == 55 && cancels.All(receipt => receipt.Unpayable
                    && receipt.Offer!.PayerId == finalGuest.ViewerId && !receipt.Offer.Mandatory)
                    && cancels.Select(receipt => receipt.Offer!.ProducingTriggerExecutionId).ToHashSet(StringComparer.Ordinal).SetEquals(eyeTriggers)
                    && cancels.Select(receipt => receipt.Offer!.OfferId).Distinct().Count() == 55,
                "Finite consent answers each distinct engine-certified unpayable producing-trigger offer exactly once with its current negative command");
            Require(players.All(player => player.Client.State!.FiniteStackYield is {
                    Status: FiniteStackYieldStatus.Completed, AutomaticPasses: 110, RemainingExecutionIds.IsEmpty: true })
                    && players.All(player => player.Port.Failures == 0 && !player.Client.UncertainCommand && !player.Client.NeedsRecovery)
                    && ai.LastFault == null && ai.Histories.All(history => history.Decisions.All(record => record.Outcome != AiDecisionOutcome.ExecutionUnknown)),
                "Both seat-owned grants certify110 actual rules passes each without unknown receipts, recovery or relabelling declined costs as declined triggers");
            var botPasses = botPort.Receipts.Where(record => record.Command is PassPriority
                && record.StackExecutions.Any(captured.Contains)).ToArray();
            facts["acceptedBotQueuePasses"] = botPasses.Length;
            Require(botPasses.Length == 220 && botPasses.Select(record => record.Seat).Distinct().Count() == 2
                    && ai.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted)) == botPort.Accepted,
                "The real background host records220 accepted bot priority passes alongside220 certified human grant passes");
            Require(coherent > 0, "The two ordinary private-safe humans share at least one coherent public board during the real fanout");
            Require(players[1].RetiredPaymentRejected,
                "An answered optional-payment command cannot be submitted or emit another RPC against a later actual offer");
            var history = await ReadHistory(engines[0], finalHost);
            castExecution = history.Actions.Single(action => action.Kind == ExecutionKind.Spell && action.SourceName == "Shamanic Revelation").Id;
            facts["committedRevelationDrawActivities"] = history.Records.Count(record => record.Activity is { Kind: "draw" } activity
                    && activity.PlayerId == finalHost.ViewerId && record.ExecutionId == castExecution);
            Require(facts["committedRevelationDrawActivities"] == 55,
                "Typed authoritative history attributes55 actual draw activities to the single committed Revelation execution");
            var cast = history.Actions.Single(action => action.Id == castExecution);
            var mana = history.Actions.Where(action => action.ParentId == castExecution && action.Kind == ExecutionKind.Mana).ToArray();
            facts["committedManaSpent"] = cast.Records.Where(record => record.Payment?.Kind == "spent")
                .Sum(record => record.Payment!.Spent?.Values.Sum() ?? 0);
            facts["manaProductionChildren"] = mana.Length;
            Require(cast.Records.Any(record => record.Payment is { Kind: "completed", BillAfter.IsEmpty: true })
                    && cast.Records.Where(record => record.Payment?.Kind == "spent").Sum(record => record.Payment!.Spent?.Values.Sum() ?? 0) == 5
                    && mana.Length == 5 && mana.All(action => action.SourceName == "Forest")
                    && mana.Sum(action => action.Records.Where(record => record.Payment?.Kind == "produced")
                        .Sum(record => record.Payment!.Produced?.GetValueOrDefault("G") ?? 0)) == 5
                    && facts["hostTappedForests"] == 5 && settledHost.Mana.Values.Sum() == 0
                    && settledHost.Zone("Battlefield").Cards.Single(card => card.Name == "Reliquary Tower").Tapped == false,
                "The actual spell payment records five Forest production children, five committed mana consumption and a completed empty bill");
            Guard();
            File.WriteAllText(Path.Combine(directory, "private-history.json"), JsonSerializer.Serialize(history));
        } catch (Exception error) {
            failure = error;
            failurePhase = workload.IsRunning ? "workload" : "setup";
            File.WriteAllText(Path.Combine(directory, "primary-error.txt"), error.ToString());
        }
        finally {
            setupTime.Stop(); workload.Stop();
            // Freeze authority and evidence before withdrawing any live owner.
            stopped = new { audience = "host-private", captured, eyeTriggers, beforeCast, burst, castExecution,
                players = players.Select(player => new { state = player.Client.State, source = player.Client.SourceActions.View,
                    acknowledgements = player.Acknowledgements.ToArray(), player.Terminals,
                    progress = player.Intent?.Progress is { } progress ? new {
                        progress.CapturedCount, progress.CompletedCount, progress.RemainingCount, progress.UnknownCount,
                        outcomes = progress.Outcomes.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value)
                    } : null, stop = player.Intent?.StopReason,
                    sourceErrors = player.SourceErrors.ToArray(), sourceTrace = player.SourceTrace.ToArray() }).ToArray(),
                histories = ai.Histories, botReceipts = botPort.Receipts.ToArray(), faults = botPort.Faults.ToArray() };
            foreach (var player in players) player.Port.Release.TrySetResult();
            try {
                var settle = DateTime.UtcNow.AddSeconds(3);
                while (players.Any(player => player.Client.Busy || player.Client.Polling) && DateTime.UtcNow < settle) {
                    foreach (var player in players) player.Client.Pump();
                    await Task.Delay(1);
                }
                if (players.Any(player => player.Client.Busy || player.Client.Polling)) throw new TimeoutException("Fanout player lanes did not settle before cleanup.");
            } catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            foreach (var player in players) player.Client.Close();
            try { await ai.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            foreach (var engine in engines.Reverse()) {
                try { await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) { cleanupErrors.Add(error.ToString()); }
            }
            if (failure == null && cleanupErrors.Count > 0) failure = new IOException("Fanout teardown failed: " + string.Join("\n", cleanupErrors));
            try { File.WriteAllText(Path.Combine(directory, "private-final-observations.json"), JsonSerializer.Serialize(stopped)); }
            catch (Exception error) {
                cleanupErrors.Add("Private evidence write failed: " + error);
                failure ??= new IOException("Fanout evidence could not be preserved.", error);
            }
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
                formatVersion = 1, scenario = "real-polled-tcp-fifty-five-draws", passed = complete && failure == null,
                failure = failure?.ToString(), failurePhase, cleanupErrors, checks, facts, stages, steps, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                setupSeconds = setupTime.Elapsed.TotalSeconds, workloadSeconds = workload.Elapsed.TotalSeconds,
                setupBudgetSeconds = 45, workloadBudgetSeconds = 45,
                aiHistoryCapacity = 1024,
                capturedTriggers = captured.Count, optionalPaymentTriggers = eyeTriggers.Count, coherentPublicCheckpoints = coherent,
                runtime, launch = new { fixture = Hash(paths.Jar), derivedFixture = Hash(launch.Jar), bootstrap = "forge.api.AiReleaseFixtureEngine$PolledFanout" },
                scope = "Controlled pregame resources; actual ordinary TCP, default polling, finite consent and background AI. No host deck legality, natural-outcome, strategic-quality or human UX claim."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        void Guard() {
            if (workload.IsRunning ? workload.Elapsed >= TimeSpan.FromSeconds(45) : setupTime.Elapsed >= TimeSpan.FromSeconds(45))
                throw new TimeoutException("The real polled110-trigger fixture exceeded its fixed45s " + (workload.IsRunning ? "workload" : "setup") + " budget.");
        }
    }

    private static int Counters(Mana.Contracts.Player player) => player.Zone("Battlefield").Cards
        .Single(card => card.Name == "Toothy, Imaginary Friend").Counters.Values.Sum();

    private static async Task<DeckSummary> Import(ForgeEngine engine, string name, string list, string commander)
    {
        // Import already commits a selectable library version, as NativeSmoke
        // does. Actual SelectDeck/Ready/CanStart still enforce format legality
        // and the captured seat version; redundant Open/Save adds no evidence.
        var imported = await engine.ImportAsync(name, "Deck\n" + list + "\nCommander\n1 " + commander);
        if (imported.Id.Length == 0 || imported.LibraryToken.Length == 0)
            throw new InvalidDataException("The imported fixture list has no committed library version: " + name);
        return imported;
    }

    private static async Task<MatchHistoryReadModel> ReadHistory(ForgeEngine engine, GameSnapshot state)
    {
        var model = MatchHistoryReadModel.Empty(state.Id, state.ViewerId);
        for (int page = 0; page < 64; page++) {
            var batch = await engine.HistoryAsync(state.Id, model.Cursor);
            long before = model.Cursor; model = model.Append(batch, out _);
            if (!model.CoverageComplete) throw new InvalidDataException("The actual fanout history has a retained-stream gap.");
            if (!batch.HasMore) return model;
            if (model.Cursor <= before) throw new InvalidDataException("The actual fanout history failed to advance.");
        }
        throw new InvalidDataException("The actual fanout history exceeded its bounded scan.");
    }

    private static ForgePaths PrepareLauncher(ForgePaths paths, string directory, AiReleaseRuntime runtime)
    {
        // VerifyRuntime already binds the original fixture/engine. Derive only
        // a test-only entry point, with unchanged allowlisted class bytes.
        string dependency = Path.Combine(Path.GetDirectoryName(paths.Jar)!, "forge-engine.jar");
        if (Hash(paths.Jar) != runtime.FixtureLauncherSha256 || Hash(dependency) != runtime.ProductionEngineSha256)
            throw new InvalidDataException("The already-verified fixture or its engine dependency changed before derivation.");
        string staging = Path.Combine(directory, "engine-launch"); Directory.CreateDirectory(staging);
        File.Copy(dependency, Path.Combine(staging, "forge-engine.jar"), false);
        string wrapper = Path.Combine(staging, "forge-engine-polled-fanout.jar");
        const string derivedManifest = "Manifest-Version: 1.0\r\nMain-Class: forge.api.AiReleaseFixtureEngine$PolledFanout\r\nClass-Path: forge-engine.jar\r\n\r\n";
        string originalManifest;
        string originalManifestHash;
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var source = ZipFile.OpenRead(paths.Jar))
        using (var target = ZipFile.Open(wrapper, ZipArchiveMode.Create)) {
            using (var input = source.GetEntry("META-INF/MANIFEST.MF")!.Open())
            using (var buffer = new MemoryStream()) {
                input.CopyTo(buffer); byte[] bytes = buffer.ToArray();
                originalManifest = System.Text.Encoding.UTF8.GetString(bytes);
                originalManifestHash = Convert.ToHexString(SHA256.HashData(bytes));
            }
            if (source.GetEntry("forge/api/AiReleaseFixtureEngine$PolledFanout.class") == null)
                throw new InvalidDataException("The source-bound test launcher lacks its private polled-fanout bootstrap.");
            foreach (var entry in source.Entries.Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal))) {
                if (!entry.FullName.StartsWith("forge/api/AiReleaseFixtureEngine", StringComparison.Ordinal))
                    throw new InvalidDataException("A non-fixture class cannot enter the derived test launcher.");
                using var input = entry.Open(); using var buffer = new MemoryStream(); input.CopyTo(buffer);
                byte[] bytes = buffer.ToArray(); classes.Add(entry.FullName, Convert.ToHexString(SHA256.HashData(bytes)));
                using var output = target.CreateEntry(entry.FullName).Open(); output.Write(bytes);
            }
            using var writer = new StreamWriter(target.CreateEntry("META-INF/MANIFEST.MF").Open());
            writer.Write(derivedManifest);
        }
        using (var derived = ZipFile.OpenRead(wrapper)) {
            if (derived.Entries.Count != classes.Count + 1) throw new InvalidDataException("The derived wrapper has undeclared entries.");
            using var reader = new StreamReader(derived.GetEntry("META-INF/MANIFEST.MF")!.Open());
            if (reader.ReadToEnd() != derivedManifest) throw new InvalidDataException("The derived test entry point changed.");
            foreach (var entry in derived.Entries.Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal))) {
                using var input = entry.Open();
                if (!classes.TryGetValue(entry.FullName, out var expected) || Convert.ToHexString(SHA256.HashData(input)) != expected)
                    throw new InvalidDataException("The derived fixture class differs from its verified original.");
            }
        }
        if (Hash(Path.Combine(staging, "forge-engine.jar")) != runtime.ProductionEngineSha256)
            throw new InvalidDataException("The derived launcher does not use its exact verified engine dependency.");
        File.WriteAllText(Path.Combine(directory, "launcher-derivation.json"), JsonSerializer.Serialize(new {
            audience = "test-only", originalJarSha256 = Hash(paths.Jar), derivedJarSha256 = Hash(wrapper),
            originalManifest, originalManifestSha256 = originalManifestHash,
            derivedManifest, derivedManifestSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(derivedManifest))),
            productionEngineSha256 = runtime.ProductionEngineSha256, unchangedClasses = classes,
            provenance = "Derived private nested bootstrap; original source receipt remains bound only to the unchanged original fixture and production engine."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return paths with { Jar = wrapper };
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record Receipt([property: JsonIgnore] GameCommand Command, long Revision, OptionalDeclineOffer? Offer, bool Unpayable)
    {
        public long MonotonicTicks { get; } = Stopwatch.GetTimestamp();
        public string Kind => Command.GetType().Name;
        public JsonElement Answer => JsonSerializer.SerializeToElement(Command, Command.GetType());
    }
    private sealed class SourcePort(ForgeEngine engine, ConcurrentQueue<string> errors, ConcurrentQueue<object> trace) : ISourceActions
    {
        public bool SupportsSourceActions => engine.SupportsSourceActions;
        public async Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var timer = Stopwatch.StartNew();
            trace.Enqueue(new { phase = "started", gameId, decisionId, sourceCardKey, tick = Stopwatch.GetTimestamp() });
            try {
                var result = await engine.SourceActionsAsync(gameId, decisionId, sourceCardKey);
                trace.Enqueue(new { phase = "returned", result, milliseconds = timer.Elapsed.TotalMilliseconds });
                return result;
            } catch (Exception error) {
                errors.Enqueue(error.ToString());
                trace.Enqueue(new { phase = "failed", error = error.ToString(), milliseconds = timer.Elapsed.TotalMilliseconds }); throw;
            }
        }
    }
    private sealed class Player
    {
        internal readonly int Index;
        internal readonly PolledLobbyPorts.ReadPort Port;
        internal readonly ClientSession Client;
        internal readonly ConcurrentQueue<Receipt> Acknowledgements = new();
        internal readonly ConcurrentQueue<string> SourceErrors = new();
        internal readonly ConcurrentQueue<object> SourceTrace = new();
        internal readonly HashSet<string> Terminals = new(StringComparer.Ordinal);
        internal ImmutableHashSet<string> Captured = [];
        internal FiniteResolutionIntent? Intent;
        internal GameSnapshot? Compared;
        internal int Publications, PollCallbacks;
        internal volatile bool RetiredPaymentRejected;
        private int paymentProbePaused;
        private readonly ConcurrentQueue<Exception> failures = new();
        private GameSnapshot? prior;
        private long nextPoll;
        private (string Game, long Revision, string Decision)? handed;
        internal Player(ForgeEngine engine, int index)
        {
            Index = index;
            Port = new(engine) { Acknowledged = (command, state) => {
                var payment = state?.Decision?.Payload as PaymentPayload;
                Acknowledgements.Enqueue(new(command, state?.Revision ?? -1, payment?.OptionalDecline,
                    payment?.OptionalPaymentSupport?.Availability == OptionalManaPaymentAvailability.Unpayable));
            } };
            Client = new(Port, engine, sourceActions: new SourcePort(engine, SourceErrors, SourceTrace));
            Client.Failed += failures.Enqueue;
            Client.Changed += (_, state) => {
                if (state == null) { if (prior != null) throw new InvalidDataException("The ordinary fanout viewer lost its match."); return; }
                if (prior != null && (state.Id != prior.Id || state.ViewerId != prior.ViewerId
                    || state.Revision < prior.Revision || state.BoardRevision < prior.BoardRevision))
                    throw new InvalidDataException("An ordinary fanout publication changed audience or rolled back.");
                if (state.Status is "error" or "finished") throw new InvalidDataException(state.Error ?? "The fanout match ended prematurely.");
                if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(state)) throw new InvalidDataException("The ordinary fanout viewer received an unauthorized private face.");
                foreach (var item in Client.ExecutionChanges.Transitions.Where(item => item.Step == ExecutionStep.Complete)) Terminals.Add(item.ExecutionId);
                prior = state; Publications++;
            };
        }
        internal void RequireHealthy()
        {
            if (failures.TryPeek(out var error)) ExceptionDispatchInfo.Capture(error).Throw();
            if (Client.UncertainCommand || Client.NeedsRecovery) throw new InvalidDataException("The fanout client entered uncertainty or recovery.");
        }
        internal void Poll()
        {
            long now = Environment.TickCount64;
            if (Client.Busy || Client.Polling || now < nextPoll) return;
            nextPoll = now + (Client.State?.Decision == null ? 50 : 120);
            Client.Poll(true, _ => PollCallbacks++);
        }
        internal bool PauseForRetiredPaymentProbe(GameSnapshot _, GameSnapshot state)
        {
            if (Index != 1 || RetiredPaymentRejected || state.Decision?.Payload is not PaymentPayload { OptionalDecline: { } offer }
                || !Captured.Contains(offer.ProducingTriggerExecutionId)) return false;
            var previous = Acknowledgements.FirstOrDefault(receipt => receipt.Command is CancelPayment
                && receipt.Offer != null && Captured.Contains(receipt.Offer.ProducingTriggerExecutionId));
            return previous != null && previous.Offer!.OfferId != offer.OfferId
                && Interlocked.CompareExchange(ref paymentProbePaused, 1, 0) == 0;
        }
        internal void ProbeRetiredPayment()
        {
            if (Volatile.Read(ref paymentProbePaused) != 1 || RetiredPaymentRejected) return;
            var retired = Acknowledgements.First(receipt => receipt.Command is CancelPayment
                && receipt.Offer != null && Captured.Contains(receipt.Offer.ProducingTriggerExecutionId));
            if (Client.State?.Decision?.Payload is not PaymentPayload { OptionalDecline: { } current }
                || current.OfferId == retired.Offer!.OfferId) throw new InvalidDataException("The stale-payment control did not retain a different real current offer.");
            int before = Port.Accepted;
            if (Client.Submit(retired.Command) || Port.Accepted != before)
                throw new InvalidDataException("The retired optional-payment command was admitted into a later offer.");
            RetiredPaymentRejected = true;
        }
        internal bool Step(bool mayCast)
        {
            if (Client.State is not { Status: "playing", Decision: { } decision, Viewer: { } viewer } state
                || handed == (state.Id, state.Revision, decision.Id)) return false;
            GameCommand? command = null;
            bool casting = false;
            if (Index == 0 && mayCast && decision.Payload is PriorityPayload && state.ActivePlayerId == state.ViewerId
                && state.PhaseKey == "MAIN1" && state.Stack.IsEmpty) {
                var revelation = viewer.Zone("Hand").Cards.First(card => card.Name == "Shamanic Revelation" && card.Selectable);
                command = Source(state, revelation, "cast");
                if (command == null) return false;
                casting = true;
            } else if (decision.Payload is PaymentPayload payment) {
                if (payment.OptionalPaymentSupport?.Availability == OptionalManaPaymentAvailability.Unpayable)
                    command = new CancelPayment(state.Id, decision.Id);
                else if (Index == 0 && viewer.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Forest" && !card.Tapped) is { } forest) {
                    command = Source(state, forest, "activate"); if (command == null) return false;
                } else if (payment.Pay.Enabled) command = new PayMana(state.Id, decision.Id);
            }
            if (command == null) {
                var presentation = DecisionDockPresentation.From(state, new());
                if (presentation.Selection is { } selection)
                    command = DecisionCommands.Choose(state, selection.Choices.Take(selection.Range.Min).Select(choice => choice.Index));
                else command = DecisionCommands.Confirm(state);
                if (command == null && StateClarity.Recipients(state) is { Length: > 0 } recipients)
                    command = StateClarity.ChooseRecipient(state, recipients.Any(player => player.Id == state.ViewerId) ? state.ViewerId : recipients[0].Id);
            }
            if (command == null || !Client.Submit(command)) throw new InvalidDataException("The fanout setup has no admitted ordinary reply for " + decision.Payload.GetType().Name);
            handed = (state.Id, state.Revision, decision.Id); return casting;
        }
        private GameCommand? Source(GameSnapshot state, Card source, string kind)
        {
            var context = new SourceActionContext(state.Id, state.Decision!.Id, state.Revision, state.ViewerId, source.Key, CardViews.Identity(source));
            Client.SourceActions.Observe(context);
            if (Client.SourceActions.View.Status is SourceActionStatus.Failed or SourceActionStatus.Unsupported)
                throw new InvalidDataException("The ordinary fanout source query failed: " + Client.SourceActions.View.Error
                    + "\n" + string.Join("\n", SourceErrors));
            if (Client.SourceActions.View.Status != SourceActionStatus.Ready) return null;
            var action = Client.SourceActions.View.Actions.Single(item => item.Kind == kind);
            return Client.SourceActions.TryCreateCommand(context, action.Id)
                ?? throw new InvalidDataException("The ordinary fanout source action could not retain its current command context.");
        }
    }
}
