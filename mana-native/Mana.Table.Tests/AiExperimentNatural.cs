using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// One isolated natural game. The coordinator owns identity, reservation, worker
// lifetime and final receipts. This owner never concedes or scores a cutoff.
internal static class AiExperimentNatural
{
    internal static async Task<JsonObject> RunAsync(JsonElement locked, JsonElement job, string directory)
    {
        var limits = locked.GetProperty("limits");
        int decisionMs = Positive(limits, "decisionMs"), units = Positive(limits, "workUnits");
        int matchMs = Positive(limits, "matchMs"), turns = Positive(limits, "turns");
        long bytes = limits.GetProperty("captureBytes").GetInt64();
        if (bytes is < 1048576 or > 1073741824) throw new ArgumentException("Natural capture must be between 1 MiB and 1 GiB.");
        string arm = job.GetProperty("arm").GetString()!;
        if (arm is not ("baseline" or "candidate")) throw new ArgumentException("Unknown natural arm.");
        var variants = locked.GetProperty("variants");
        var focalVariant = AiExperimentPolicyRegistry.Parse(variants.GetProperty(arm));
        var reference = AiExperimentPolicyRegistry.Parse(variants.GetProperty("reference"));
        long seed = long.Parse(job.GetProperty("seed").GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var natural = job.GetProperty("natural");
        int focalSlot = natural.GetProperty("focalSlot").GetInt32();
        var decks = natural.GetProperty("decks").EnumerateArray().ToArray();
        if (decks.Length != 4 || focalSlot is < 0 or > 3) throw new ArgumentException("Natural jobs require four decks and one focal slot.");

        Directory.CreateDirectory(directory);
        var runtime = locked.GetProperty("runtime");
        var paths = new ForgePaths(runtime.GetProperty("javaPath").GetString()!,
            runtime.GetProperty("engineJarPath").GetString()!, runtime.GetProperty("resourcesPath").GetString()!,
            Path.Combine(directory, "engine-profile"));
        using var capture = new AiExperimentNaturalCapture(directory, bytes);
        // The normal bounded stderr drain/tail remains; no unbounded engine.log
        // competes with measurement capture. Optional diagnostics acquire no rules authority.
        var engine = new ForgeEngine(paths, null, _ => new NullDiagnosticSink());
        AiTableHost? host = null;
        AiExperimentNaturalProvider? provider = null;
        ConfiguredSimulationStart? configured = null;
        GameSnapshot? state = null;
        AiCampaignOutcomeProof? proof = null;
        var developed = new HashSet<int>();
        var cleanup = new List<string>();
        var clock = new Stopwatch();
        var workerClock = Stopwatch.StartNew();
        string stage = "initialize", status = "setup-failure";
        string? error = null, failureStage = null;
        bool startDispatched = false, safety = false, proofCommitted = false;
        int steps = 0, comparisons = 0;
        try {
            await engine.InitializeAsync();
            if (!engine.SupportsConfiguredSimulation) throw new NotSupportedException("Configured simulation capability is required.");
            await engine.EnableAutomatedSeatsAsync();
            stage = "import-pinned-decks";
            var handles = ImmutableArray.CreateBuilder<string>();
            var imported = new List<object>();
            for (int slot = 0; slot < 4; slot++) {
                var deck = decks[slot];
                byte[] raw = await File.ReadAllBytesAsync(deck.GetProperty("path").GetString()!);
                if (raw.Length > 2 * 1024 * 1024 || !Hash(raw).Equals(deck.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A pinned deck changed before import.");
                string list = new UTF8Encoding(false, true).GetString(raw).TrimStart('\uFEFF');
                // Neutral public labels reveal neither arm nor implementation.
                var loaded = await engine.ImportAsync("Experiment seat " + slot, list);
                var details = await engine.OpenDeckAsync(loaded.Id);
                if (details.StructureValid != true || details.Problem.Length > 0 || !details.Issues.IsEmpty)
                    throw new InvalidDataException("A pinned Commander deck failed authoritative validation at slot " + slot);
                var saved = await engine.SaveDeckAsync(details.Id, details.Revision);
                if (saved.SaveError.Length != 0 || string.IsNullOrEmpty(saved.LibraryToken))
                    throw new InvalidDataException("A pinned deck could not be committed at slot " + slot);
                handles.Add("deck:" + saved.Id + ":" + saved.LibraryToken);
                imported.Add(new { slot, id = deck.GetProperty("id").GetString(), sha256 = Hash(raw),
                    engineDeckId = saved.Id, saved.Revision, saved.LibraryToken });
            }
            var setup = new ConfiguredSimulationSetup(new(MatchRulesets.FreeForAllCommander,
                Enumerable.Range(0, 4).Select(slot => new MatchSeatAssignment(slot, slot, MatchSeatRole.Participant)).ToImmutableArray()), handles.ToImmutable());
            stage = "start-configured-game";
            // A lost start receipt cannot prove no game was created.
            startDispatched = true; clock.Start();
            configured = await engine.StartConfiguredSimulationAsync(setup);
            ConfiguredSimulationFacts.ValidateReceipt(configured, setup);
            ConfiguredSimulationFacts.ValidateState(configured, configured.State);
            state = configured.State;
            var assignment = BindVariants(configured, focalSlot, focalVariant, reference);
            int focal = configured.Participants.Single(person => person.Index == focalSlot).PlayerId;
            capture.FocalPlayerId = focal;
            capture.Required("accepted-setup.json", new { setup, configured.Participants, imported,
                focalSlot, focalPlayerId = focal, arm, assignments = assignment, engineRandomnessControlled = false });
            provider = new(engine, configured, capture);
            host = new(provider, playerId => capture.Record(AiExperimentPolicyRegistry.Create(assignment.TryGetValue(playerId, out var selected)
                    ? selected : throw new InvalidDataException("No variant is bound to this accepted player."))),
                seed, new(TimeSpan.FromMilliseconds(decisionMs), units));

            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, matchMs - clock.ElapsedMilliseconds)));
            while (true) {
                stage = "validate-public-observation";
                ConfiguredSimulationFacts.ValidateState(configured, state);
                ValidatePublic(state);
                foreach (var player in state.Players)
                    if (player.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal))) developed.Add(player.Id);
                if (state.Status == "finished") { status = "completed"; break; }
                if (state.Turn >= turns) { status = "turn-cutoff"; break; }
                if (clock.ElapsedMilliseconds >= matchMs) { status = "time-cutoff"; break; }
                stage = "policy-host-step";
                await host.StepAsync(deadline.Token);
                steps++;
                stage = "drain-measurements";
                capture.Drain();
                if (host.LastFault is { } fault) {
                    failureStage = "policy-host-step";
                    safety |= fault.Failure?.Kind == AiFailureKind.InvalidPolicyOutput || fault.ReasonCode == "rejected-command";
                    status = capture.Failure != null ? "evidence-failure" : provider.LastError == null ? "policy-failure" : "provider-failure";
                    error = capture.Failure?.ToString() ?? JsonSerializer.Serialize(fault);
                    capture.CriticalReplay(fault.SeatId);
                    break;
                }
                stage = "observe-public";
                state = await engine.ObserveSimulationAsync().WaitAsync(deadline.Token)
                    ?? throw new IOException("The configured game disappeared.");
                ConfiguredSimulationFacts.ValidateState(configured, state);
                ValidatePublic(state);
                foreach (var item in provider.Last) {
                    if (item.State.BoardRevision == state.BoardRevision && item.State.Id == state.Id) {
                        // Same-revision comparison reads only already-returned values.
                        if (ForgePublicProjectionFacts.Capture(state) != ForgePublicProjectionFacts.Capture(item.State)
                            || !ForgePublicProjectionFacts.ExecutionCorrelationsAgree(ForgePublicProjectionFacts.CaptureExecutionCorrelations(state),
                                ForgePublicProjectionFacts.CaptureExecutionCorrelations(item.State)))
                            throw new AiExperimentNaturalSafetyException("Same-revision public projections disagree.");
                        comparisons++;
                    }
                }
                await Task.Delay(1, deadline.Token);
            }
        } catch (OperationCanceledException) when (clock.IsRunning && clock.ElapsedMilliseconds >= matchMs) {
            failureStage = stage;
            status = "time-cutoff";
            error = "Match deadline expired; a sent action may still have executed. No command is replayed.";
        } catch (AiExperimentNaturalEvidenceException failure) {
            failureStage = stage; status = "evidence-failure"; error = failure.ToString();
        } catch (Exception failure) {
            failureStage = stage;
            safety |= failure is AiExperimentNaturalSafetyException || provider?.SafetyFailure == true;
            status = !startDispatched ? "setup-failure" : "provider-failure";
            error = failure.ToString();
        } finally {
            clock.Stop();
            safety |= provider?.SafetyFailure == true;
            try { capture.Drain(); }
            catch (Exception failure) {
                status = "evidence-failure"; error = (error == null ? "" : error + "\n") + "Measurement drain: " + failure;
            }
            stage = "capture-pre-cleanup-outcome";
            proof = AiCampaignOutcomeProof.Capture(state, status == "completed" ? "natural-outcome" : status,
                host?.LastFault, developed, provider?.Last ?? []);
            if (status == "completed" && !proof.VerifiedNaturalOutcome) {
                status = "provider-failure"; error = "The provider finished without a verified typed natural outcome.";
            }
            try {
                capture.Required("outcome-proof.json", proof);
                if (state != null) capture.Required("pre-cleanup-public-state.json", state);
                proofCommitted = true;
            } catch (Exception failure) {
                status = "evidence-failure";
                error = (error == null ? "" : error + "\n") + "Outcome capture: " + failure;
            }
            if (host != null) try { await host.DisposeAsync(); }
                catch (Exception failure) { cleanup.Add("host: " + failure); }
            if (configured != null) try { await engine.StopSimulationAsync(configured.State.Id).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception failure) { cleanup.Add("stop-simulation: " + failure); }
            try { await engine.DisposeAsync(); }
                catch (Exception failure) { cleanup.Add("engine: " + failure); }
            try { capture.Dispose(); }
            catch (Exception failure) {
                status = "evidence-failure"; error = (error == null ? "" : error + "\n") + "Measurement finalization: " + failure;
            }
        }
        // Cleanup cannot turn an unresolved game into a terminal one, or erase
        // a terminal proof that was durably captured before cleanup began.
        if (capture.Failure is { } captureFailure && status != "evidence-failure") {
            status = "evidence-failure";
            error = (error == null ? "" : error + "\n") + "Measurement integrity: " + captureFailure;
        }
        var metrics = capture.Metrics(steps, state?.Turn ?? 0, clock.Elapsed.TotalMilliseconds,
            workerClock.Elapsed.TotalMilliseconds, comparisons);
        metrics["acceptedGameplayCommands"] = provider?.Accepted ?? 0;
        metrics["rejectedGameplayCommands"] = provider?.Rejected ?? 0;
        metrics["unknownGameplayReceipts"] = provider?.Unknown ?? 0;
        var result = new JsonObject {
            ["status"] = status, ["started"] = startDispatched, ["criticalSafetyFailure"] = safety,
            ["natural"] = Outcome(configured, focalSlot, proof, proofCommitted),
            ["metrics"] = metrics,
            ["details"] = JsonSerializer.SerializeToNode(new {
                stage, failureStage, hostFault = host?.LastFault, gameStartVerified = configured != null,
                startMeaning = "Start request dispatched; lost receipt remains unresolved.",
                engineRandomnessControlled = false, javaPath = paths.Java,
                evidenceScope = "Bounded measurement files; engine-profile is separate operational deck/cache data.",
                captureBytesLimit = bytes, cleanupFailed = cleanup.Count != 0,
                consequenceCoverage = "not-instrumented; accepted commands are not resolved-effect credit",
                coverageCounters = "not-instrumented", timingScope = "Inner policy wall spans, not per-decision CPU or cumulative session elapsed",
                replaySampling = "First focal evaluation per task, at most 15, within optional byte budget; critical failure reserve separate"
            }),
            ["error"] = error, ["cleanupErrors"] = JsonSerializer.SerializeToNode(cleanup)
        };
        return result;
    }

    internal static ImmutableDictionary<int, AiExperimentVariant> BindVariants(ConfiguredSimulationStart start,
        int focalSlot, AiExperimentVariant focal, AiExperimentVariant reference)
    {
        ConfiguredSimulationFacts.ValidateState(start, start.State);
        if (focalSlot is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(focalSlot));
        return start.Participants.ToImmutableDictionary(person => person.PlayerId, person => person.Index == focalSlot ? focal : reference);
    }

    internal static JsonObject Outcome(ConfiguredSimulationStart? configured, int focalSlot, AiCampaignOutcomeProof? proof, bool committed)
    {
        int? focal = configured?.Participants.Single(person => person.Index == focalSlot).PlayerId;
        var participants = configured?.Participants.Select(person => new { slot = person.Index, playerId = person.PlayerId }).ToArray();
        bool verified = committed && proof is { VerifiedNaturalOutcome: true, CapturedBeforeCleanup: true, Outcome: not null };
        var typed = verified ? proof!.Outcome : null;
        return new JsonObject {
            ["terminalVerified"] = verified, ["focalPlayerId"] = focal,
            ["participants"] = JsonSerializer.SerializeToNode(participants ?? []),
            ["outcome"] = typed == null ? null : JsonSerializer.SerializeToNode(new {
                kind = typed.Kind == MatchOutcomeKind.Win ? "win" : "draw", winningPlayerIds = typed.WinningPlayerIds,
                winningTeamIds = typed.WinningTeamIds, gameId = typed.GameId, ruleReason = typed.RuleReason
            })
        };
    }

    internal static void ValidatePublic(GameSnapshot state)
    {
        if (state.ViewerId != -1 || state.Decision != null || !state.ActionSources.IsEmpty || !state.PlayAccesses.IsEmpty
            || state.Players.Any(player => !player.Zone("Hand").Cards.IsEmpty || !player.Zone("Library").Cards.IsEmpty)
            || CardViews.InZones(state).Any(row => row.Card.Key.Length > 0 || row.Card.Selectable))
            throw new AiExperimentNaturalSafetyException("Public observation disclosed private decision/card authority.");
    }
    private static int Positive(JsonElement value, string key)
        => value.GetProperty(key).GetInt32() is > 0 and var number ? number : throw new ArgumentException("A natural limit must be positive: " + key);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class NullDiagnosticSink : IEngineDiagnosticSink
    {
        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellation) => ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken cancellation) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class AiExperimentNaturalSafetyException(string message) : IOException(message);
