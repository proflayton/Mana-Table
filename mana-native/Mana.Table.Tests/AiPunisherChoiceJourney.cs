using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Synthetic setup, actual owned native choices and production consequences.
// Hidden future composition is fixture setup/analysis only, never policy input.
internal static class AiPunisherChoiceJourney
{
    private const string Candidate = "casual-commander.v6.uncertain-life-risk.v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task RunStandaloneAsync(ForgePaths paths, string? productionJar,
        string? sourceRevision, string? sourceManifest)
    {
        productionJar ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(paths.Jar))!, "forge-engine.jar");
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar, verifyPackage: sourceRevision != null,
            sourceRevision: sourceRevision, sourceManifest: sourceManifest);
        foreach (string mode in new[] { "LowZero", "LowSeven", "HighZero", "HighSeven" }) {
            var launch = PrepareLauncher(paths with { Profile = Path.Combine(paths.Profile, mode) }, mode, productionJar, runtime);
            foreach (string factory in new[] { AiExperimentPolicyRegistry.CasualCommanderFactoryId, Candidate }) {
                string arm = factory == Candidate ? "candidate" : "baseline";
                await RunAsync(launch with { Profile = Path.Combine(launch.Profile, arm) }, mode, factory);
                Console.WriteLine("PASS owned punisher continuation " + mode + " " + arm);
            }
        }
    }

    private static ForgePaths PrepareLauncher(ForgePaths paths, string mode, string production, AiReleaseRuntime runtime)
    {
        const string prefix = "forge/api/PunisherChoiceFixtureEngine";
        string root = Path.Combine(paths.Profile, "private-launch"); Directory.CreateDirectory(root);
        string targetPath = Path.Combine(root, "punisher.jar");
        if (Hash(paths.Jar) != runtime.FixtureLauncherSha256 || Hash(production) != runtime.ProductionEngineSha256)
            throw new InvalidDataException("Verified punisher fixture inputs changed before derivation.");
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        string manifest = "Manifest-Version: 1.0\r\n" + Fold("Main-Class: forge.api.PunisherChoiceFixtureEngine$" + mode)
            + Fold("Class-Path: " + new Uri(Path.GetFullPath(production)).AbsoluteUri) + "\r\n";
        using (var source = ZipFile.OpenRead(paths.Jar))
        using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create)) {
            if (source.GetEntry(prefix + ".class") == null || source.GetEntry(prefix + "$" + mode + ".class") == null)
                throw new InvalidDataException("Source-bound archive lacks the declared private punisher bootstrap.");
            foreach (var entry in source.Entries.Where(entry => entry.FullName == prefix + ".class"
                || entry.FullName.StartsWith(prefix + "$", StringComparison.Ordinal) && entry.FullName.EndsWith(".class", StringComparison.Ordinal))) {
                using var content = entry.Open(); using var buffer = new MemoryStream(); content.CopyTo(buffer);
                byte[] bytes = buffer.ToArray(); classes.Add(entry.FullName, Convert.ToHexString(SHA256.HashData(bytes)));
                using var member = target.CreateEntry(entry.FullName).Open(); member.Write(bytes);
            }
            using var writer = new StreamWriter(target.CreateEntry("META-INF/MANIFEST.MF").Open(), new UTF8Encoding(false));
            writer.Write(manifest);
        }
        using (var target = ZipFile.OpenRead(targetPath)) {
            if (target.Entries.Count != classes.Count + 1) throw new InvalidDataException("Undeclared derived fixture entries.");
            foreach (var item in classes) {
                using var member = target.GetEntry(item.Key)!.Open();
                if (Convert.ToHexString(SHA256.HashData(member)) != item.Value)
                    throw new InvalidDataException("Derived punisher class bytes changed.");
            }
        }
        if (Hash(paths.Jar) != runtime.FixtureLauncherSha256 || Hash(production) != runtime.ProductionEngineSha256)
            throw new InvalidDataException("Source pair changed during punisher launcher derivation.");
        File.WriteAllText(Path.Combine(root, "provenance.json"), JsonSerializer.Serialize(new {
            runtime, mode, originalFixture = paths.Jar, production, derivedJar = targetPath,
            derivedSha256 = Hash(targetPath), manifest, classSha256 = classes,
            scope = "Private synthetic pregame setup; production choice authority and rules thereafter."
        }, Json));
        return paths with { Jar = targetPath };
    }

    private static async Task RunAsync(ForgePaths paths, string mode, string factory)
    {
        Directory.CreateDirectory(paths.Profile);
        bool low = mode.StartsWith("Low", StringComparison.Ordinal), seven = mode.EndsWith("Seven", StringComparison.Ordinal);
        bool candidate = factory == Candidate;
        // Frozen preferences, distinct from the private setup's resulting loss.
        bool expectDraw = candidate && low;
        int initialLife = low ? 12 : 40;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var clock = Stopwatch.StartNew();
        await using var engine = new ForgeEngine(paths);
        var port = (IAutomatedSeatProvider)engine;
        ConfiguredSimulationStart? start = null;
        var sessions = new Dictionary<int, AiSeatSession>();
        var captures = new ConcurrentQueue<AiDecisionReplayCase>();
        var receipts = new List<object>();
        var activity = new Dictionary<long, ActivityEntry>();
        var executions = new HashSet<string>(StringComparer.Ordinal);
        int controller = -1, decider = -1, beforeHand = -1, beforeLibrary = -1;
        string? rootExecution = null, failure = null;
        bool activated = false, acceptedChoice = false, resolved = false;
        GameSnapshot? deciding = null, final = null;
        long capturedBytes = 0;
        try {
            await engine.InitializeAsync().WaitAsync(deadline.Token);
            await port.EnableAutomatedSeatsAsync(deadline.Token);
            var handles = ImmutableArray.CreateBuilder<string>();
            for (int slot = 0; slot < 4; slot++) {
                var deck = await engine.ImportAsync("Punisher fixture seat " + slot, "Deck\n99 Plains\nCommander\n1 Rhys the Redeemed").WaitAsync(deadline.Token);
                var opened = await engine.OpenDeckAsync(deck.Id).WaitAsync(deadline.Token);
                var saved = await engine.SaveDeckAsync(deck.Id, opened.Revision).WaitAsync(deadline.Token);
                if (saved.SaveError.Length > 0 || string.IsNullOrEmpty(saved.LibraryToken)) throw new IOException("Fixture deck save failed.");
                handles.Add("deck:" + saved.Id + ":" + saved.LibraryToken);
            }
            var setup = new ConfiguredSimulationSetup(new(MatchRulesets.FreeForAllCommander,
                Enumerable.Range(0, 4).Select(slot => new MatchSeatAssignment(slot, slot, MatchSeatRole.Participant)).ToImmutableArray()), handles.ToImmutable());
            start = await engine.StartConfiguredSimulationAsync(setup).WaitAsync(deadline.Token);
            ConfiguredSimulationFacts.ValidateReceipt(start, setup);
            controller = start.Participants.Single(person => person.Index == 0).PlayerId;
            decider = start.Participants.Single(person => person.Index == 1).PlayerId;
            Write("accepted-setup.json", new { mode, factory, syntheticPregame = true, setup, start });
            foreach (var person in start.Participants) {
                IAiPolicy policy = AiExperimentPolicyRegistry.Create(new(person.PlayerId == decider ? factory : AiExperimentPolicyRegistry.CasualCommanderFactoryId));
                if (person.PlayerId == decider) policy = new CapturePolicy(policy, captures);
                sessions.Add(person.PlayerId, new(start.State.Id, person.PlayerId, policy, 17, new(TimeSpan.FromSeconds(10), 4096)));
            }
            for (int step = 0; step < 4096; step++) {
                deadline.Token.ThrowIfCancellationRequested();
                var batch = await port.ObserveAutomatedSeatsAsync(deadline.Token);
                if (batch.Length != 4 || !batch.Select(item => item.SeatId).Order().SequenceEqual(start.Participants.Select(person => person.PlayerId).Order()))
                    throw new InvalidDataException("Owned punisher batch changed its accepted roster.");
                foreach (var item in batch) {
                    ConfiguredSimulationFacts.ValidateOwnedState(start, item.State);
                    if (item.State.ViewerId != item.SeatId || !AiCampaignVisibility.PrivateWindowsAreDisclosed(item.State, item.Semantics, item.Domain)
                        || item.State.Players.Any(player => player.Zones.Any(zone => zone.Name == "Library" && (!zone.Cards.IsEmpty || zone.TopCard != null))))
                        throw new InvalidDataException("Punisher policy input exposed private library faces or changed audience.");
                    if (item.State.Status == "error") throw new InvalidOperationException(item.State.Error);
                    if (item.SeatId != decider) continue;
                    final = item.State;
                    if (rootExecution != null) {
                        executions.Add(rootExecution);
                        foreach (var occurrence in item.State.Executions?.Occurrences ?? []) {
                            if (occurrence.Id == rootExecution || occurrence.BudgetRootId == rootExecution) executions.Add(occurrence.Id);
                            if (occurrence.Id == rootExecution && occurrence.Outcome == ExecutionOutcome.Resolved) resolved = true;
                        }
                        foreach (var entry in item.State.Activity) activity[entry.Id] = entry;
                    }
                }
                // Do not dispatch further gameplay after the actual choice.
                // This makes the single resolved source's public deltas causal.
                if (acceptedChoice) {
                    if (resolved) break;
                    await Task.Delay(10, deadline.Token); continue;
                }
                foreach (var item in batch) {
                    var state = item.State;
                    if (state.Decision == null || state.Status != "playing") continue;
                    var frame = new AiDecisionFrame(state, domain: item.Domain, semantics: item.Semantics);
                    DecisionCommand? setupCommand = null;
                    if (state.Decision.Payload is OpeningHandPayload) setupCommand = new KeepOpeningHand(state.Id, state.Decision.Id);
                    else if (!activated && item.SeatId == controller && state.Turn == 1 && state.PhaseKey == "MAIN1" && state.Decision.Payload is PriorityPayload) {
                        var sources = item.Domain.Sources.Where(source => CardViews.FindActionSource(state, source.SourceCardKey)
                            is { Zone: "Battlefield", Card: { Type: var type } } && type.Split(' ').Contains("Artifact", StringComparer.Ordinal)).ToArray();
                        if (sources.Length != 1 || sources[0].Actions.Length != 1) throw new InvalidDataException("No unique synthetic setup activation.");
                        setupCommand = new UseOfferedAction(state.Id, state.Decision.Id, sources[0].SourceCardKey, sources[0].Actions[0].Id);
                        activated = true;
                    } else if (activated && item.SeatId == controller && state.Decision.Payload is TargetSelectionPayload target) {
                        if (target.Progress?.SelectedPlayers.Contains(decider) == true && target.Complete.Enabled)
                            setupCommand = new CompleteSelection(state.Id, state.Decision.Id);
                        else if (target.Players.Contains(decider)) setupCommand = new SelectPlayer(state.Id, state.Decision.Id, decider);
                        else throw new InvalidDataException("The current setup target does not offer the intended deciding opponent.");
                    }
                    if (setupCommand != null) {
                        if (!frame.Allows(setupCommand)) throw new InvalidDataException("Synthetic setup command lacks current authority.");
                        await port.ExecuteAutomatedAsync(item.SeatId, setupCommand, deadline.Token);
                        receipts.Add(new { setupOnly = true, seat = item.SeatId, status = "accepted", command = JsonSerializer.SerializeToElement(setupCommand, setupCommand.GetType()) });
                        break;
                    }
                    bool riskChoice = item.SeatId == decider && state.Decision.Payload is OptionsPayload
                        && item.Semantics.Options.Any(option => option.RememberedMillLifeLoss != null);
                    if (item.SeatId == decider && state.Decision.Payload is OptionsPayload
                        && item.Semantics.Options.Any(option => option.OperationGraph.Operations.Any(node => node.Effect == ActionEffectKind.Mill)) && !riskChoice) {
                        Write("uncertified-owned-choice.json", item);
                        throw new InvalidDataException("The actual mechanics branch lacks the required current-choice facts.");
                    }
                    if (riskChoice) {
                        deciding = state;
                        Write("owned-choice.json", item);
                        if (state.Players.Single(player => player.Id == decider).Life != initialLife)
                            throw new InvalidDataException("The actual deciding input has the wrong public starting life.");
                        var owner = state.Players.Single(player => player.Id == controller);
                        var risk = item.Semantics.Options.Single(option => option.RememberedMillLifeLoss != null);
                        var certificate = risk.RememberedMillLifeLoss!;
                        var source = owner.Zone("Battlefield").Cards.Single(card => CardViews.Identity(card) == certificate.SourceCardId);
                        var mill = risk.OperationGraph.Operations.Single(node => node.Id == risk.OperationGraph.RootId);
                        var loss = risk.OperationGraph.Operations.Single(node => node.Id == certificate.LifeLossOperationId);
                        if (certificate.Version != 1 || certificate.ObservedCount != 4
                            || !source.Counters.TryGetValue(certificate.CounterType, out int counter) || counter != 4
                            || mill.ActorPlayerId != controller || loss.ActorPlayerId != controller
                            || mill.Recipients.Entities is not [{ Kind: SemanticEntityKind.Player, PlayerId: var milled }] || milled != controller
                            || loss.Recipients is not { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.Explicit,
                                Entities: [{ Kind: SemanticEntityKind.Player, PlayerId: var harmed }] } || harmed != decider
                            || mill.Amount != null || loss.Amount != null || loss.AmountKnowledge != SemanticKnowledge.Unknown)
                            throw new InvalidDataException("The actual deciding input lacks the frozen public four-card exposure and exact affected players.");
                        beforeHand = owner.Zone("Hand").Count; beforeLibrary = owner.Zone("Library").Count;
                        string execution = state.Decision.ExecutionId;
                        rootExecution = state.Executions?.Occurrences.SingleOrDefault(occurrence => occurrence.Id == execution)?.BudgetRootId;
                        if (string.IsNullOrEmpty(rootExecution)) throw new InvalidDataException("Owned choice lacks its actual execution root.");
                    }
                    var session = sessions[item.SeatId]; session.Observe(frame);
                    var evaluation = await session.EvaluateAsync(deadline.Token);
                    while (captures.TryDequeue(out var replay)) Write("policy-request.json", replay);
                    if (evaluation.Status is AiSeatStatus.Unavailable or AiSeatStatus.Invalid or AiSeatStatus.BudgetExceeded or AiSeatStatus.NeedsRecovery)
                        throw new InvalidOperationException("Actual fixture policy failed: " + JsonSerializer.Serialize(evaluation));
                    if (session.TakeCommand() is not { } submission) continue;
                    if (riskChoice) {
                        var draw = item.Semantics.Options.Single(option => option.OperationGraph.Operations is [{ Effect: ActionEffectKind.Draw, Amount: 1 }]);
                        bool drew = submission.Command is ChooseOptions { Choices.Length: 1 } answer && answer.Choices[0] == draw.Index;
                        Write("pre-dispatch-proposal.json", new { submission.Stamp, submission.ReasonCode,
                            command = JsonSerializer.SerializeToElement(submission.Command, submission.Command.GetType()),
                            expectedDraw = expectDraw, actualDraw = drew });
                        if (drew != expectDraw) throw new InvalidDataException("Native choice disagrees with the frozen low/high-life preference or baseline control.");
                    }
                    try { await port.ExecuteAutomatedAsync(item.SeatId, submission.Command, deadline.Token); session.Report(submission, AiExecutionReceipt.Accepted); }
                    catch (EngineCommandRejectedException) { session.Report(submission, AiExecutionReceipt.Rejected); throw; }
                    catch { session.Report(submission, AiExecutionReceipt.Unknown); throw; }
                    receipts.Add(new { setupOnly = false, seat = item.SeatId, status = "accepted", submission.Stamp, submission.ReasonCode,
                        command = JsonSerializer.SerializeToElement(submission.Command, submission.Command.GetType()) });
                    if (riskChoice) { acceptedChoice = true; break; }
                    break; // Refresh all owned publications after any accepted command.
                }
                await Task.Delay(1, deadline.Token);
            }
            if (!acceptedChoice || !resolved || final == null || deciding == null) throw new InvalidDataException("Choice did not reach an accepted, source-bound resolved continuation.");
            var affected = final.Players.Single(player => player.Id == decider);
            var ownerAfter = final.Players.Single(player => player.Id == controller);
            int loss = !expectDraw && seven ? 28 : 0;
            var causal = activity.Values.Where(entry => executions.Contains(entry.ExecutionId) || executions.Contains(entry.Cause?.Id ?? "")).ToArray();
            Write("pre-cleanup-proof.json", new { mode, factory, expectDraw, controller, decider, rootExecution, executions, resolved,
                initialLife, expectedLossFromPrivateFixture = loss, beforeHand, beforeLibrary, causal, decidingState = deciding, finalOwnedState = final });
            if (affected.Life != initialLife - loss || affected.Eliminated != (initialLife - loss <= 0)
                || ownerAfter.Zone("Hand").Count != beforeHand + (expectDraw ? 1 : 0)
                || ownerAfter.Zone("Library").Count != beforeLibrary - (expectDraw ? 1 : 4)
                || !expectDraw && ownerAfter.Zone("Graveyard").Count != 4)
                throw new InvalidDataException("Accepted choice's actual public life/zone consequences disagree with the controlled rules outcome.");
            if (loss > 0 && !causal.Any(entry => entry.Impact is { Kind: "life", PlayerId: var player, Amount: var amount } && player == decider && amount == -loss))
                throw new InvalidDataException("Life loss lacks its same-execution public event.");
            if (!expectDraw && causal.Count(entry => entry.Kind == "moved" && entry.PlayerId == controller) != 4)
                throw new InvalidDataException("Actual Mill lacks four same-execution public movement events.");
            if (expectDraw && !causal.Any(entry => entry.Kind == "draw" && entry.PlayerId == controller && entry.Count == 1))
                throw new InvalidDataException("Actual draw lacks its same-execution public draw event.");
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            string? cleanupError = null;
            foreach (var session in sessions.Values) session.Dispose();
            if (start != null) {
                try { await engine.StopSimulationAsync(start.State.Id).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { cleanupError = error.ToString(); }
            }
            Write("journey-result.json", new { formatVersion = 1, mode, factory, syntheticPregame = true, naturalGame = false,
                expectedDraw = expectDraw, activated, acceptedChoice, resolved, rootExecution, failure, cleanupError,
                elapsedMs = clock.Elapsed.TotalMilliseconds, receipts,
                comparisonScope = "Independent real games, not byte-identical public inputs or controlled engine RNG. Hidden outcomes never determine the frozen preference.",
                histories = sessions.Select(pair => new { playerId = pair.Key, history = pair.Value.History }) });
            if (failure == null && cleanupError != null) throw new IOException("Controlled punisher engine cleanup failed: " + cleanupError);
        }

        void Write(string name, object value) {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (capturedBytes + bytes.Length > 16 * 1024 * 1024) throw new IOException("Bounded punisher evidence budget exceeded.");
            using var file = new FileStream(Path.Combine(paths.Profile, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(bytes); capturedBytes += bytes.Length;
        }
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static string Fold(string value) {
        var text = new StringBuilder();
        while (value.Length > 70) { text.Append(value[..70]).Append("\r\n"); value = " " + value[70..]; }
        return text.Append(value).Append("\r\n").ToString();
    }
    private sealed class CapturePolicy(IAiPolicy inner, ConcurrentQueue<AiDecisionReplayCase> captures) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation) {
            if (request.Frame.Semantics.Options.Any(option => option.RememberedMillLifeLoss != null))
                captures.Enqueue(AiDecisionReplayCase.Capture("owned-punisher-choice", inner.Descriptor.Id, request));
            return inner.EvaluateAsync(request, cancellation);
        }
    }
}
