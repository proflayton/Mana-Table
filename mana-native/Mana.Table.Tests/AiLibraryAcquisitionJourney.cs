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

// Explicit synthetic pregame fixture, then actual production ports and one-shot
// policy submissions. This checks acquisition, never a natural-game win rate.
internal static class AiLibraryAcquisitionJourney
{
    private const string Candidate = "casual-commander.v7.library-acquisition.v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task RunStandaloneAsync(ForgePaths paths, string fixtureMode, string? productionJar = null,
        string? sourceRevision = null, string? sourceManifest = null)
    {
        productionJar ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(paths.Jar))!, "forge-engine.jar");
        var runtime = AiReleaseEvaluation.VerifyRuntime(paths, productionJar, sourceRevision: sourceRevision, sourceManifest: sourceManifest);
        var derived = DeriveLaunch(paths, fixtureMode, productionJar, runtime);
        foreach (string factory in new[] { AiExperimentPolicyRegistry.CasualCommanderFactoryId, Candidate }) {
            string arm = factory == Candidate ? "candidate" : "baseline";
            await RunAsync(derived with { Profile = Path.Combine(paths.Profile, arm) }, fixtureMode, factory,
                (passed, detail) => { if (!passed) throw new InvalidOperationException(detail); Console.WriteLine(arm + ": " + detail); });
        }
    }

    private static ForgePaths DeriveLaunch(ForgePaths paths, string mode, string productionJar, AiReleaseRuntime runtime)
    {
        string bootstrap = mode switch { "direct-battlefield" => "DirectBattlefield", "direct-hand" => "DirectHand", "bundle" => "Bundle",
            "renamed-bundle" => "RenamedBundle", _ => throw new ArgumentException("Unsupported private fixture mode.", nameof(mode)) };
        string prefix = "forge/api/LibraryAcquisitionFixtureEngine";
        string root = Path.Combine(paths.Profile, "engine-launch"); Directory.CreateDirectory(root);
        string wrapper = Path.Combine(root, "library-" + mode + "-" + Guid.NewGuid().ToString("N") + ".jar");
        if (Hash(paths.Jar) != runtime.FixtureLauncherSha256 || Hash(productionJar) != runtime.ProductionEngineSha256)
            throw new InvalidDataException("Verified fixture or production bytes changed before launcher derivation.");
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        string manifest = "Manifest-Version: 1.0\r\n" + ManifestLine("Main-Class: forge.api.LibraryAcquisitionFixtureEngine$" + bootstrap)
            + ManifestLine("Class-Path: " + new Uri(Path.GetFullPath(productionJar)).AbsoluteUri) + "\r\n";
        using (var source = ZipFile.OpenRead(paths.Jar))
        using (var stream = new FileStream(wrapper, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var target = new ZipArchive(stream, ZipArchiveMode.Create)) {
            if (source.GetEntry(prefix + ".class") == null || source.GetEntry(prefix + "$" + bootstrap + ".class") == null)
                throw new InvalidDataException("The source-bound fixture archive lacks the requested private acquisition bootstrap.");
            foreach (var entry in source.Entries.Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal)
                    && (entry.FullName == prefix + ".class" || entry.FullName.StartsWith(prefix + "$", StringComparison.Ordinal)))) {
                using var input = entry.Open(); using var buffer = new MemoryStream(); input.CopyTo(buffer);
                byte[] bytes = buffer.ToArray(); classes.Add(entry.FullName, Convert.ToHexString(SHA256.HashData(bytes)));
                using var output = target.CreateEntry(entry.FullName).Open(); output.Write(bytes);
            }
            using var writer = new StreamWriter(target.CreateEntry("META-INF/MANIFEST.MF").Open(), new UTF8Encoding(false)); writer.Write(manifest);
        }
        using (var archive = ZipFile.OpenRead(wrapper)) {
            if (archive.Entries.Count != classes.Count + 1) throw new InvalidDataException("The derived private launcher has undeclared entries.");
            foreach (var item in classes) {
                using var content = archive.GetEntry(item.Key)!.Open();
                if (Convert.ToHexString(SHA256.HashData(content)) != item.Value) throw new InvalidDataException("A derived fixture class differs from its verified source archive.");
            }
        }
        if (Hash(paths.Jar) != runtime.FixtureLauncherSha256 || Hash(productionJar) != runtime.ProductionEngineSha256)
            throw new InvalidDataException("The source input pair changed during private launcher derivation.");
        File.WriteAllText(Path.Combine(root, "launch-provenance.json"), JsonSerializer.Serialize(new {
            runtime, originalFixture = Path.GetFullPath(paths.Jar), originalFixtureSha256 = Hash(paths.Jar),
            productionJar = Path.GetFullPath(productionJar), productionSha256 = Hash(productionJar), derivedJar = wrapper,
            derivedSha256 = Hash(wrapper), manifest, classSha256 = classes,
            scope = "Private synthetic pregame fixture; unchanged production provider/rules and policy ports thereafter."
        }, Json));
        return paths with { Jar = wrapper };
    }

    private static string ManifestLine(string value)
    {
        // Both the explicit class name and absolute percent-encoded file URI
        // are ASCII. Fold physical lines without splitting a UTF-8 character.
        var text = new StringBuilder();
        while (value.Length > 70) { text.Append(value[..70]).Append("\r\n"); value = " " + value[70..]; }
        return text.Append(value).Append("\r\n").ToString();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static async Task RunAsync(ForgePaths paths, string fixtureMode, string factoryId, Action<bool, string> check)
    {
        if (fixtureMode is not ("direct-battlefield" or "direct-hand" or "bundle" or "renamed-bundle"))
            throw new ArgumentException("Choose an explicit supported private fixture mode.", nameof(fixtureMode));
        if (factoryId != AiExperimentPolicyRegistry.CasualCommanderFactoryId && factoryId != Candidate)
            throw new ArgumentException("The acquisition journey compares its frozen baseline and explicit candidate only.", nameof(factoryId));
        string bootstrap = fixtureMode switch { "direct-battlefield" => "DirectBattlefield", "direct-hand" => "DirectHand", "bundle" => "Bundle", _ => "RenamedBundle" };
        using (var launch = ZipFile.OpenRead(paths.Jar)) {
            using var manifest = new StreamReader(launch.GetEntry("META-INF/MANIFEST.MF")?.Open()
                ?? throw new InvalidDataException("The acquisition journey requires an explicit private fixture launcher."));
            if (!manifest.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n ", "", StringComparison.Ordinal)
                .Contains("Main-Class: forge.api.LibraryAcquisitionFixtureEngine$" + bootstrap + "\n", StringComparison.Ordinal))
                throw new InvalidDataException("The private launcher does not match the requested fixture mode.");
        }
        bool candidate = factoryId == Candidate;
        bool bundle = fixtureMode is "bundle" or "renamed-bundle";
        string expectedDestination = bundle ? "Library" : fixtureMode == "direct-hand" ? "Hand" : "Battlefield";
        Directory.CreateDirectory(paths.Profile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var clock = Stopwatch.StartNew();
        await using var engine = new ForgeEngine(paths);
        IAutomatedSeatProvider port = engine;
        ConfiguredSimulationStart? start = null;
        GameSnapshot? final = null;
        var sessions = new Dictionary<int, AiSeatSession>();
        var replays = new ConcurrentQueue<AiDecisionReplayCase>();
        var receipts = new List<object>();
        var events = new Dictionary<long, ActivityEntry>();
        var rootExecutions = new HashSet<string>(StringComparer.Ordinal);
        var captureIds = new HashSet<string>(StringComparer.Ordinal);
        string? sourceExecution = null, sourceId = null, firstChoiceId = null, failure = null;
        bool activated = false, firstChoice = false, resolved = false;
        int focal = -1, beforeHand = 0, beforeField = 0, captureSequence = 0;
        HashSet<string> beforeHandIds = [], beforeFieldIds = [];
        long capturedBytes = 0;
        try {
            await engine.InitializeAsync().WaitAsync(deadline.Token);
            await port.EnableAutomatedSeatsAsync(deadline.Token);
            var handles = ImmutableArray.CreateBuilder<string>();
            for (int slot = 0; slot < 4; slot++) {
                var deck = await engine.ImportAsync("Acquisition fixture seat " + slot, "Deck\n99 Plains\nCommander\n1 Rhys the Redeemed").WaitAsync(deadline.Token);
                var opened = await engine.OpenDeckAsync(deck.Id).WaitAsync(deadline.Token);
                var saved = await engine.SaveDeckAsync(deck.Id, opened.Revision).WaitAsync(deadline.Token);
                if (saved.SaveError.Length > 0 || string.IsNullOrEmpty(saved.LibraryToken)) throw new IOException("The controlled fixture deck could not be committed.");
                handles.Add("deck:" + saved.Id + ":" + saved.LibraryToken);
            }
            var setup = new ConfiguredSimulationSetup(new(MatchRulesets.FreeForAllCommander,
                Enumerable.Range(0, 4).Select(slot => new MatchSeatAssignment(slot, slot, MatchSeatRole.Participant)).ToImmutableArray()), handles.ToImmutable());
            start = await engine.StartConfiguredSimulationAsync(setup).WaitAsync(deadline.Token);
            ConfiguredSimulationFacts.ValidateReceipt(start, setup);
            focal = start.Participants.Single(participant => participant.Index == 0).PlayerId;
            Write("accepted-setup.json", new { fixtureMode, syntheticPregame = true, factoryId, start });
            foreach (var participant in start.Participants) {
                int player = participant.PlayerId;
                IAiPolicy policy = AiExperimentPolicyRegistry.Create(new(player == focal ? factoryId : AiExperimentPolicyRegistry.CasualCommanderFactoryId));
                if (player == focal) policy = new CapturePolicy(policy, replays);
                sessions.Add(player, new(start.State.Id, player, policy, 261118, new(TimeSpan.FromSeconds(3), 4096)));
            }
            for (int steps = 0; steps < 4096; steps++) {
                deadline.Token.ThrowIfCancellationRequested();
                var observations = await port.ObserveAutomatedSeatsAsync(deadline.Token);
                if (observations.Length != start.Participants.Length
                    || observations.Select(item => item.SeatId).Distinct().Count() != observations.Length
                    || !observations.Select(item => item.SeatId).Order().SequenceEqual(start.Participants.Select(item => item.PlayerId).Order()))
                    throw new InvalidDataException("The controlled owned batch changed the accepted four-player roster.");
                foreach (var item in observations) {
                    if (!sessions.ContainsKey(item.SeatId) || item.State.ViewerId != item.SeatId
                        || !AiCampaignVisibility.PrivateWindowsAreDisclosed(item.State, item.Semantics, item.Domain))
                        throw new InvalidDataException("The controlled journey received an invalid owned audience.");
                    ConfiguredSimulationFacts.ValidateOwnedState(start, item.State);
                    if (item.State.Status is "error" or "finished") throw new InvalidOperationException(item.State.Error ?? "The controlled acquisition ended before verification.");
                    if (item.SeatId != focal) continue;
                    final = item.State;
                    if (sourceExecution != null) {
                        rootExecutions.Add(sourceExecution);
                        foreach (var occurrence in item.State.Executions?.Occurrences ?? [])
                            if (occurrence.Id == sourceExecution || occurrence.BudgetRootId == sourceExecution
                                || occurrence.ParentId != null && rootExecutions.Contains(occurrence.ParentId)) rootExecutions.Add(occurrence.Id);
                        foreach (var entry in item.State.Activity) events[entry.Id] = entry;
                        resolved |= item.State.Executions?.Occurrences.Any(occurrence => occurrence.Id == sourceExecution
                            && occurrence.Outcome == ExecutionOutcome.Resolved) == true;
                    }
                    if (activated && item.State.Decision?.Payload is OptionsPayload { LibraryAcquisition: { } acquisition }) {
                        if (acquisition.ActorPlayerId != focal || acquisition.LibraryOwnerId != focal || acquisition.RecipientPlayerId != focal)
                            throw new InvalidDataException("The fixture's acquisition context belongs to another actor.");
                        if (!firstChoice) {
                            if (acquisition.Stage != (bundle ? LibraryAcquisitionStage.SelectBundle : LibraryAcquisitionStage.Direct)
                                || acquisition.CurrentDestination != expectedDestination)
                                throw new InvalidDataException("The requested fixture mode does not match the actual provider stage.");
                            firstChoice = true; firstChoiceId = item.State.Decision.Id; sourceExecution = item.State.Decision.ExecutionId;
                            if (string.IsNullOrWhiteSpace(sourceExecution)) throw new InvalidDataException("The actual acquisition has no execution identity.");
                            beforeHand = item.State.Viewer!.Zone("Hand").Count; beforeField = item.State.Viewer.Zone("Battlefield").Count;
                            beforeHandIds = Ids(item.State.Viewer.Zone("Hand")); beforeFieldIds = Ids(item.State.Viewer.Zone("Battlefield"));
                            Write("pre-acquisition-owned.json", item);
                        }
                    }
                }
                if (firstChoice && resolved && final is { Stack.IsEmpty: true, Decision.Payload: PriorityPayload }) break;
                foreach (var item in observations) {
                    if (item.State.Decision == null || item.State.Status != "playing") { sessions[item.SeatId].ObserveAutomaticProgress(item.State); continue; }
                    if (!activated && item.SeatId == focal && item.State.Turn == 1 && item.State.PhaseKey == "MAIN1"
                        && item.State.Decision.Payload is PriorityPayload) {
                        // Deliberate setup action only: identify the sole pregame
                        // fixture artifact by current type and offered capability.
                        var sources = item.Domain.Sources.Where(source => CardViews.FindActionSource(item.State, source.SourceCardKey)
                            is { Zone: "Battlefield", Card: { Type: var type } } && type.Split(' ').Contains("Artifact", StringComparer.Ordinal)).ToArray();
                        if (sources.Length != 1 || sources[0].Actions.Length != 1) throw new InvalidDataException("The private fixture has no unique current artifact activation.");
                        var source = sources[0]; sourceId = source.SourceCardId;
                        var action = new UseOfferedAction(item.State.Id, item.State.Decision.Id, source.SourceCardKey, source.Actions[0].Id);
                        if (!new AiDecisionFrame(item.State, domain: item.Domain, semantics: item.Semantics).Allows(action))
                            throw new InvalidDataException("The fixture bootstrap did not hold a current offered action.");
                        Write("fixture-bootstrap.json", new { purpose = "explicit-synthetic-setup-not-policy-credit", item.State.Id, item.SeatId, sourceId, action });
                        await port.ExecuteAutomatedAsync(focal, action, deadline.Token);
                        receipts.Add(new { fixtureBootstrap = true, seat = focal, command = action, status = "accepted", resolved = false });
                        activated = true; break;
                    }
                    var session = sessions[item.SeatId];
                    session.Observe(new(item.State, domain: item.Domain, semantics: item.Semantics));
                    var evaluated = await session.EvaluateAsync(deadline.Token);
                    while (replays.TryDequeue(out var replay))
                        if (captureIds.Add(replay.State.Decision!.Id)) Write("policy-request-" + ++captureSequence + ".json", replay);
                    if (evaluated.Status is AiSeatStatus.Unavailable or AiSeatStatus.Invalid or AiSeatStatus.BudgetExceeded or AiSeatStatus.NeedsRecovery)
                        throw new InvalidOperationException("The actual policy could not answer its current input: " + JsonSerializer.Serialize(evaluated));
                    if (session.TakeCommand() is not { } submission) continue;
                    try {
                        await port.ExecuteAutomatedAsync(item.SeatId, submission.Command, deadline.Token);
                        session.Report(submission, AiExecutionReceipt.Accepted);
                        receipts.Add(new { fixtureBootstrap = false, seat = item.SeatId, submission.Stamp,
                            command = JsonSerializer.SerializeToElement(submission.Command, submission.Command.GetType()), submission.ReasonCode,
                            status = "accepted", resolved = false });
                    } catch (EngineCommandRejectedException) { session.Report(submission, AiExecutionReceipt.Rejected); throw; }
                    catch { session.Report(submission, AiExecutionReceipt.Unknown); throw; }
                }
                await Task.Delay(1, deadline.Token);
            }
            if (!firstChoice || !resolved || final?.Viewer == null) throw new InvalidDataException("The controlled source did not reach a verified completed acquisition.");
            var after = final.Viewer;
            var newHand = Ids(after.Zone("Hand")).Except(beforeHandIds).ToHashSet(StringComparer.Ordinal);
            var newField = Ids(after.Zone("Battlefield")).Except(beforeFieldIds).ToHashSet(StringComparer.Ordinal);
            var zoneEvents = events.Values.Where(entry => (rootExecutions.Contains(entry.ExecutionId)
                    || entry.Cause?.Id is { } cause && rootExecutions.Contains(cause))
                && entry.Impact is { Kind: "zone", FromZone: "Library", ToZone: "Hand" or "Battlefield", RecipientControllerId: var recipient }
                && recipient == focal).ToArray();
            var arrived = zoneEvents.Select(entry => entry.Impact!.CardId ?? entry.CardId).OfType<string>().Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
            int expectedHand = candidate && (bundle || fixtureMode == "direct-hand") ? 1 : 0;
            int expectedField = candidate && (bundle || fixtureMode == "direct-battlefield") ? 1 : 0;
            check(after.Zone("Hand").Count - beforeHand == expectedHand && after.Zone("Battlefield").Count - beforeField == expectedField
                && newHand.Count == expectedHand && newField.Count == expectedField,
                "The actual source resolution changed the focal hand and battlefield by its declared acquisition counts");
            check(arrived.SetEquals(newHand.Concat(newField)),
                "Actual library zone-change events under this source execution identify exactly the newly acquired occurrences");
            var acquisitionAnswers = sessions[focal].History.Decisions.Where(record => record.Outcome == AiDecisionOutcome.Accepted
                && record.Task == AiTask.Options && record.Command == nameof(ChooseOptions)).ToArray();
            var firstAnswer = acquisitionAnswers.Single(record => record.Stamp.DecisionId == firstChoiceId);
            check(firstAnswer.AnswerEvidence is { } answer && answer.GetProperty("Choices").GetArrayLength() == (candidate ? bundle ? 2 : 1 : 0),
                "The actual one-shot policy session submitted its expected initial acquisition count and received the production provider acknowledgement");
            Write("pre-cleanup-resolution-proof.json", new { scope = "controlled-synthetic-fixture-not-natural-strength", factoryId, fixtureMode,
                focalPlayerId = focal, sourceId, sourceExecution, rootExecutions, beforeHand, beforeField, expectedHand, expectedField,
                newHandIds = newHand, newBattlefieldIds = newField, zoneEvents, resolved, finalOwnedState = final });
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            string? cleanupError = null;
            foreach (var session in sessions.Values) session.Dispose();
            if (start != null) {
                try { await engine.StopSimulationAsync(start.State.Id).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { cleanupError = error.ToString(); }
            }
            Write("journey-result.json", new { formatVersion = 1, fixtureMode, factoryId, focalPlayerId = focal, syntheticPregame = true,
                policyOwnsAcquisitionChoices = true, naturalGame = false, elapsedMs = clock.Elapsed.TotalMilliseconds,
                activated, firstChoice, sourceExecution, sourceId, resolved, failure, cleanupError, receipts,
                histories = sessions.Select(pair => new { playerId = pair.Key, history = pair.Value.History }) });
            if (failure == null && cleanupError != null) throw new IOException("The verified acquisition's controlled engine cleanup failed: " + cleanupError);
        }

        void Write(string file, object value) {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (capturedBytes + bytes.Length > 16 * 1024 * 1024) throw new IOException("The bounded private acquisition capture budget was exhausted.");
            File.WriteAllBytes(Path.Combine(paths.Profile, file), bytes); capturedBytes += bytes.Length;
        }
    }

    private static HashSet<string> Ids(Zone zone) => zone.Cards.Select(CardViews.Identity).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);

    private sealed class CapturePolicy(IAiPolicy inner, ConcurrentQueue<AiDecisionReplayCase> captures) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            if (request.Frame.State.Decision?.Payload is OptionsPayload { LibraryAcquisition: not null } or RevealPayload)
                captures.Enqueue(AiDecisionReplayCase.Capture("library-live-" + request.Frame.State.Decision.Id, inner.Descriptor.Id, request));
            return inner.EvaluateAsync(request, cancellation);
        }
    }
}
