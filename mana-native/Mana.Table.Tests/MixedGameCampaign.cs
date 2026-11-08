using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal enum MixedGameDriveMode { ManualStepped, PolledBackground }

// Qualification evidence for ordinary host/TCP player connections plus two
// independent bots. Human-seat scripts use their own audience and ClientSession;
// neither they nor a natural finish substitute for actual human playtesting.
internal static class MixedGameCampaign
{
    internal static async Task RunAsync(ForgePaths paths, string profile,
        MixedGameDriveMode mode = MixedGameDriveMode.ManualStepped)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var limits = profile switch {
            "smoke" => new AiCampaignLimits(1, 16, TimeSpan.FromSeconds(45)),
            "natural-single" => AiCampaignLimits.Release with { Matches = 1, Profile = "natural-single" },
            "natural" or "release" => AiCampaignLimits.Release,
            _ => throw new ArgumentException("Unknown mixed-game profile: " + profile)
        };
        string directory = Path.Combine(paths.Profile, "mixed-game-campaign");
        Directory.CreateDirectory(directory);
        string automatedPriorityPolicy = new CasualCommanderPolicy().Descriptor.PriorityAutomation.ToString();
        await SaveAsync(Path.Combine(directory, "manifest.json"), new {
            formatVersion = 3, audience = "host-private", profile, limits, mode = mode.ToString(), source = "ordinary-host-and-tcp-guest-with-two-host-owned-ai",
            automatedPriorityPolicy,
            passingMode = mode == MixedGameDriveMode.PolledBackground ? "ServerAuto" : "LegacyPolicy",
            priorityPolicy = mode == MixedGameDriveMode.PolledBackground ? "PriorityAttention+QuietPriorityPolicy" : "policy-only",
            humanSeats = "Scripted through owned observations and ClientSession; human UX review remains pending.",
            observationOwnership = mode == MixedGameDriveMode.PolledBackground
                ? "Two default-polled ordinary clients; owned rich reads propose only against the exact accepted ordinary payload; one background AI host."
                : "Legacy manually sampled owned observations and manually stepped AI host.",
            presentationPacing = "No timed presentation pacing is configured; this is not a visual-tempo qualification.",
            priorityOwnership = mode == MixedGameDriveMode.PolledBackground
                ? "Shared shipping PriorityAttention and QuietPriorityPolicy choose negotiated server NoResponse or bounded original-token Quiet continuation; no per-prompt policy fallback while the server owns priority. Responses and required choices remain current manual policy answers; manual mana withdraws before dispatch."
                : "Legacy individually scripted policy answers.",
            engineRandomnessControlled = false, seedScope = "Policy tie breaking only; not a deterministic engine replay.",
            timeCutoffScope = "Checked between bounded engine/client operations, not an exact process wall deadline.",
            launchJarSha256 = Hash(paths.Jar), harnessAssemblySha256 = Hash(typeof(MixedGameCampaign).Assembly.Location),
            policyAssemblySha256 = Hash(typeof(CasualCommanderPolicy).Assembly.Location),
            decks = AiCampaignDecks.All.Select(deck => new { deck.Id, deck.Hash, deck.Commanders, deck.IntendedMechanics, deck.RequiredWitnessIds })
        });
        var results = new List<MixedGameResult>();
        for (int match = 0; match < limits.Matches; match++) {
            var result = await MatchAsync(paths with { Profile = Path.Combine(directory, "match-" + match) }, match, limits, mode);
            results.Add(result);
            await SaveAsync(Path.Combine(directory, "summary.json"), new {
                formatVersion = 3, audience = "host-private", profile, mode = mode.ToString(), results, automatedPriorityPolicy,
                passingMode = mode == MixedGameDriveMode.PolledBackground ? "ServerAuto" : "LegacyPolicy",
                priorityPolicy = mode == MixedGameDriveMode.PolledBackground ? "PriorityAttention+QuietPriorityPolicy" : "policy-only",
                unrunRotations = Enumerable.Range(results.Count, limits.Matches - results.Count),
                allRequestedGamesCompleted = results.Count == limits.Matches && results.All(item => item.NaturalOutcomeVerified),
                humanReview = "pending", allInteractionsClaimed = false,
                complexDeckQualification = results.All(item => item.Qualification?.Status == MixedGameQualificationStatus.Passed)
                    ? "observed-required-mechanics" : "required-causal-evidence-incomplete"
            });
            // A failed connection/engine is not reused or turned into additional
            // independent game failures. A cutoff is preserved as inconclusive.
            if (result.Failure != null || result.Qualification?.Status == MixedGameQualificationStatus.Failed) break;
        }
        if (results.Any(result => result.Failure != null || result.Qualification?.Status == MixedGameQualificationStatus.Failed))
            throw new InvalidOperationException("Mixed-game mechanical or evidence-integrity failure; inspect the retained first failure and private observations.");
        if (profile is "natural-single" or "natural" or "release" && (results.Count != limits.Matches || results.Any(result => !result.NaturalOutcomeVerified)))
            throw new InvalidOperationException("Mixed-game qualification is inconclusive: not every requested rotation reached a verified natural outcome.");
        if (profile == "release" && results.Any(result => result.Qualification?.Status != MixedGameQualificationStatus.Passed))
            throw new InvalidOperationException("Mixed-game qualification is inconclusive: required role, public-state or causal mechanic evidence is missing.");
    }

    private static async Task<MixedGameResult> MatchAsync(ForgePaths paths, int match, AiCampaignLimits limits, MixedGameDriveMode mode)
    {
        Directory.CreateDirectory(paths.Profile);
        var engines = Enumerable.Range(0, 2).Select(index => new ForgeEngine(paths with {
            Profile = Path.Combine(paths.Profile, "connection-" + index)
        })).ToArray();
        using var evidence = new AiCampaignDiagnostics(paths.Profile);
        var inbox = mode == MixedGameDriveMode.PolledBackground ? evidence.EnableOrderedHandoff(256) : null;
        var provider = new AiCampaignProvider(engines[0], evidence, inbox: inbox);
        var bots = new AiTableHost(provider, _ => evidence.Record(new CasualCommanderPolicy()),
            limits.PolicySeed + match, new(TimeSpan.FromSeconds(10), 4096), diagnostics: inbox);
        using var host = new ConnectedPlayerDriver(engines[0], evidence, limits.PolicySeed + match, mode,
            passing: mode == MixedGameDriveMode.PolledBackground ? ConnectedPlayerPassingMode.ServerAuto : ConnectedPlayerPassingMode.FullControl);
        using var guest = new ConnectedPlayerDriver(engines[1], evidence, limits.PolicySeed + match + 100, mode,
            passing: mode == MixedGameDriveMode.PolledBackground ? ConnectedPlayerPassingMode.ServerAuto : ConnectedPlayerPassingMode.FullControl);
        var trace = new StreamWriter(new GZipStream(File.Create(Path.Combine(paths.Profile, "public-facts.jsonl.gz")), CompressionLevel.Fastest));
        var elapsed = Stopwatch.StartNew();
        MixedGameProgress? progress = null;
        ConnectedPlayerProgressEvidence? hostProgress = null, guestProgress = null;
        MixedGamePublicCohorts? publicCohorts = null;
        int completedCohortCount = 0;
        var developed = new HashSet<int>();
        var identities = new HashSet<int>();
        var bindings = ImmutableArray.CreateBuilder<AiCampaignDeckBinding>();
        ImmutableArray<AutomatedSeatObservation> last = [];
        GameSnapshot? state = null;
        int steps = 0, comparisons = 0;
        string stage = "initialize", stop = "not-started";
        string? failure = null;
        bool verified = false, audiencesBound = false;
        ImmutableArray<AiSeatHistory> stoppedHistories = [];
        ImmutableArray<AiAutomatedPriorityEvidence> finalPriorityContinuations = [];
        AiCampaignQualificationTracker? qualification = null;
        string configurationId = "mixed-lobby-rotation-" + match, configurationHash = "";
        void CleanupFailure(string operation, Exception error) {
            failure = (failure == null ? "" : failure + "\n") + operation + ": " + error;
            stop = "mechanical-failure"; verified = false;
        }
        async Task ProtectCleanup(string operation, Func<Task> action) {
            try { await action(); } catch (Exception error) { CleanupFailure(operation, error); }
        }
        try {
            await Task.WhenAll(engines.Select(engine => engine.InitializeAsync()));
            if (engines.Any(engine => !engine.SupportsOwnedDecisionObservations))
                throw new NotSupportedException("Both connected player endpoints must expose owned decision observations.");
            if (mode == MixedGameDriveMode.PolledBackground) await bots.StartAsync();
            else await engines[0].EnableAutomatedSeatsAsync();
            var decks = new List<DeckDetails>();
            stage = "bind-four-distinct-decks";
            for (int slot = 0; slot < 4; slot++) {
                var manifest = AiCampaignDecks.All[(slot + match) % 4];
                var engine = engines[slot == 1 ? 1 : 0];
                var imported = await engine.ImportAsync("Mixed campaign " + manifest.Id, manifest.List);
                var details = await engine.OpenDeckAsync(imported.Id);
                var committed = await engine.SaveDeckAsync(details.Id, details.Revision);
                string handle = "deck:" + committed.Id + ":" + committed.LibraryToken;
                bindings.Add(await AiCampaignQualification.BindDeckAsync(engine, manifest, committed, handle));
                decks.Add(committed);
            }
            stage = "start-real-tcp-table";
            var lobby = await engines[0].HostAsync(4, false);
            var port = lobby.Addresses.First(address => address.Url.Contains(':')).Url.Split(':').Last();
            await engines[1].JoinAsync("127.0.0.1:" + port);
            var selectionIds = new string[4];
            for (int index = 0; index < 2; index++) {
                var receipt = await engines[index].SelectDeckAsync(new(lobby.TableId, decks[index].Id, decks[index].LibraryToken));
                selectionIds[index] = receipt.Seats.Single(seat => seat.Index == index).DeckPreview?.SelectionId
                    ?? throw new InvalidDataException("The connected player's deck selection has no public assignment receipt.");
            }
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 2, "AI", bindings[2].Handle);
            selectionIds[2] = lobby.Seats.Single(seat => seat.Index == 2).DeckPreview?.SelectionId ?? "";
            lobby = await engines[0].SetSeatAsync(lobby.TableId, 3, "AI", bindings[3].Handle);
            selectionIds[3] = lobby.Seats.Single(seat => seat.Index == 3).DeckPreview?.SelectionId ?? "";
            if (lobby.Seats.Count(seat => seat.Type == "AI") != 2 || lobby.Seats.Count(seat => seat.Type == "REMOTE") != 1)
                throw new InvalidDataException("The requested mixed table did not retain two player connections and two AI seats.");
            await engines[0].SetReadyAsync(true); await engines[1].SetReadyAsync(true);
            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!(lobby = await engines[0].LobbyAsync()).CanStart) {
                if (DateTime.UtcNow >= readyDeadline) throw new TimeoutException("The mixed table did not become ready.");
                await Task.Delay(1);
            }
            configurationHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                configurationId, lobby, selectionIds, decks = bindings.ToImmutable(), limits
            })));
            await SaveAsync(Path.Combine(paths.Profile, "accepted-lobby-decks.json"), new {
                lobby, selectionIds, configurationId, configurationHash, bindings = bindings.ToImmutable()
            });
            if (mode == MixedGameDriveMode.PolledBackground) {
                host.BindLobby(await engines[0].LobbyAsync());
                guest.BindLobby(await engines[1].LobbyAsync());
            }
            await engines[0].StartAsync();
            // Setup/warm-up time has its own bounded operations. The game budget
            // starts at launch, matching the existing natural AI campaign.
            elapsed.Restart();
            progress = new(() => elapsed.ElapsedTicks, Stopwatch.Frequency);
            async Task WaitForOwnerWork()
            {
                progress.Enter(MixedGameStage.OwnerWait, steps);
                if (mode == MixedGameDriveMode.PolledBackground)
                    await CampaignOwnerWait.WaitAsync(CampaignOwnerWait.Bound(limits.MatchTime - elapsed.Elapsed,
                    host.TimeUntilNextPoll, guest.TimeUntilNextPoll,
                    host.Client.TimeUntilSynchronizationHealthCheck, guest.Client.TimeUntilSynchronizationHealthCheck), evidence.WaitForQueuedWorkAsync,
                    host.Client, guest.Client);
                else await Task.Delay(1);
                progress.Complete();
            }
            while (true) {
                if (elapsed.Elapsed >= limits.MatchTime) { progress.Stop(); stop = "time-budget"; break; }
                progress.BeginAttempt();
                stage = "owned-player-observations";
                if (!await ObservePlayersAsync(host, guest, progress, steps)) {
                    await WaitForOwnerWork(); continue;
                }
                stage = mode == MixedGameDriveMode.PolledBackground ? "background-bot-processing" : "bot-step";
                if (mode == MixedGameDriveMode.ManualStepped) {
                    progress.Enter(MixedGameStage.ManualBotStep, steps);
                    await bots.StepAsync();
                }
                progress.Enter(MixedGameStage.EvidenceDrain, steps);
                evidence.DrainPending();
                if (bots.LastFault != null) throw new InvalidDataException("Bot fault: " + JsonSerializer.Serialize(bots.LastFault));
                var botObservations = mode == MixedGameDriveMode.PolledBackground ? provider.ProcessedObservations : provider.Last;
                last = new[] { host.Last, guest.Last }.OfType<AutomatedSeatObservation>().Concat(botObservations).ToImmutableArray();
                steps++;
                stage = "shared-state-and-visibility";
                if (mode == MixedGameDriveMode.PolledBackground && last.Length < 4 && !audiencesBound) { await WaitForOwnerWork(); continue; }
                if (last.Length != 4 || last.Select(item => item.SeatId).Distinct().Count() != 4)
                    throw new InvalidDataException("The mixed table lost one of its four disjoint owned audiences.");
                audiencesBound = true;
                if (last.Any(item => item.State.Status == "starting")) {
                    foreach (var item in last.Where(item => item.State.Status == "starting"))
                        if (item.State.Decision != null || item.State.Players.Length is not (0 or 4)
                            || item.Domain.Coverage != ActionDomainCoverage.NotApplicable)
                            throw new InvalidDataException("An uncommitted starting audience acquired decision authority.");
                    await WaitForOwnerWork();
                    continue;
                }
                if (qualification == null) {
                    if (mode == MixedGameDriveMode.PolledBackground
                        && (!host.Client.SupportsPriorityContinuation || !guest.Client.SupportsPriorityContinuation))
                        throw new InvalidDataException("Both ordinary polled players must negotiate server-owned priority continuation.");
                    qualification = AiCampaignQualificationTracker.ForMixedTable(bindings.ToImmutable(), lobby,
                        last.Select(item => new OwnedDecisionObservation(item.State, item.Domain, item.Semantics)).ToImmutableArray(),
                        selectionIds.ToImmutableArray(), configurationId, configurationHash);
                    evidence.BindQualification(qualification);
                    foreach (var item in last) evidence.Observe(item);
                }
                evidence.DrainPending();
                if (mode == MixedGameDriveMode.PolledBackground) {
                    progress.Enter(MixedGameStage.AutomatedHistory, steps);
                    await provider.DrainHistoryAsync();
                }
                progress.Enter(MixedGameStage.PublicComparison, steps);
                publicCohorts ??= new(last.Select(item => item.SeatId), preparation: evidence.PublicProjectionPreparation);
                foreach (var item in last) {
                    var frame = item.State;
                    var observation = publicCohorts.Observe(item.SeatId, frame);
                    if (!observation.Compared) continue;
                    identities.Add(item.SeatId);
                    foreach (var player in frame.Players)
                        if (player.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal))) developed.Add(player.Id);
                    if (observation.FirstCohort) {
                        progress.NewPublicBoard(frame.BoardRevision);
                        await trace.WriteLineAsync(JsonSerializer.Serialize(new { steps, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                            facts = JsonSerializer.Deserialize<JsonElement>(observation.Facts!) }));
                    }
                }
                comparisons = publicCohorts.Comparisons; completedCohortCount = publicCohorts.FourAudienceCohorts;
                state = host.Last!.State;
                evidence.ObservePublic(state);
                if (steps % 64 == 0) await SaveAsync(Path.Combine(paths.Profile, "checkpoint.json"), new {
                    audience = "public", steps, state.Turn, state.PhaseKey, state.BoardRevision,
                    elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, comparisons,
                    fourAudienceCohorts = completedCohortCount, developedSeats = developed.Order(), progress = progress.Capture()
                });
                if (state.Status == "finished") {
                    if (state.Outcome == null) throw new InvalidDataException("A finished mixed game has no typed rules outcome.");
                    MatchOutcomeFacts.Validate(state);
                    verified = last.All(item => item.State.Status == "finished"
                        && item.State.Outcome != null
                        && item.State.BoardRevision == state.BoardRevision
                        && (evidence.PublicProjectionPreparation?.Facts(item.State) ?? ForgePublicProjectionFacts.Capture(item.State))
                            == (evidence.PublicProjectionPreparation?.Facts(state) ?? ForgePublicProjectionFacts.Capture(state)));
                    if (verified) foreach (var item in last) MatchOutcomeFacts.Validate(item.State);
                    if (verified && mode == MixedGameDriveMode.PolledBackground
                        && (provider.Executing != 0 || provider.PriorityControlsExecuting != 0)) verified = false;
                    if (!verified) { stop = "terminal-audiences-not-yet-coherent"; }
                    else {
                        if (mode == MixedGameDriveMode.PolledBackground && new[] { host.AutoEvidence, guest.AutoEvidence }
                            .Any(item => item.AcceptedGrants == 0 || item.CertifiedAutomaticPasses == 0))
                            throw new InvalidDataException("A completed polled game must witness actual server-owned Auto progress for both ordinary players.");
                        progress.Stop(); stop = "natural-outcome"; break;
                    }
                }
                if (state.Turn >= limits.Turns) { progress.Stop(); stop = "turn-budget"; break; }
                if (elapsed.Elapsed >= limits.MatchTime) { progress.Stop(); stop = "time-budget"; break; }
                stage = "connected-player-client-dispatch";
                progress.Enter(MixedGameStage.HostStep, steps);
                await host.StepAsync();
                progress.Enter(MixedGameStage.GuestStep, steps);
                await guest.StepAsync();
                await WaitForOwnerWork();
            }
        } catch (Exception error) {
            progress?.Stop(failed: true);
            stop = "mechanical-failure"; failure = error.ToString();
            try { evidence.PreserveFailure(error, stage, steps, last); }
            catch (Exception captureError) { CleanupFailure("Original failure evidence", captureError); }
        } finally {
            // Keep active-run time separate from evidence and owner teardown.
            // This does not change either the existing match budget or result's
            // end-to-end elapsed-time field.
            progress?.Stop();
            hostProgress = host.ProgressEvidence; guestProgress = guest.ProgressEvidence;
            await ProtectCleanup("Ordered evidence drain", () => { evidence.DrainPending(); return Task.CompletedTask; });
            await ProtectCleanup("Private observation capture", () => {
                if (evidence.PreserveObservations(last) is { } error) throw new IOException("Private observation capture failed.", error);
                return Task.CompletedTask;
            });
            await ProtectCleanup("Private receipt capture", async () => {
                stoppedHistories = bots.Histories;
                await SaveAsync(Path.Combine(paths.Profile, "private-player-receipts.json"), new {
                    host = new { host.ClientAdmissions, host.ProviderAcceptances, host.History, host.PollCallbacks,
                        host.OrdinaryPublications, host.RetiredProposals, host.LastRetiredOwnedRead, source = host.Client.SourceActions.View,
                        consent = host.ConsentEvidence, progress = hostProgress },
                    guest = new { guest.ClientAdmissions, guest.ProviderAcceptances, guest.History, guest.PollCallbacks,
                        guest.OrdinaryPublications, guest.RetiredProposals, guest.LastRetiredOwnedRead, source = guest.Client.SourceActions.View,
                        consent = guest.ConsentEvidence, progress = guestProgress },
                    automated = new { histories = stoppedHistories, fault = bots.LastFault,
                        historyScope = "Bounded diagnostic rings before owner cancellation; provider acceptance counters are cumulative and separate.",
                        cumulativeProviderAcceptances = provider.AcceptedBySeat, provider.Executing, provider.PriorityControlsExecuting },
                    orderedEvidence = evidence.HandoffStatus
                });
            });
            // All outcome/evidence is captured before lifecycle teardown. This
            // runner never concedes, substitutes a seat or declares a cutoff a win.
            await ProtectCleanup("AI owner teardown", () => bots.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await ProtectCleanup("Final ordered evidence drain", () => {
                evidence.DrainPending();
                if (mode == MixedGameDriveMode.PolledBackground && (bots.LastFault != null || evidence.CapturedHostFault != null))
                    throw new InvalidDataException("The background AI host faulted before its owner joined.");
                if (mode == MixedGameDriveMode.PolledBackground && evidence.HandoffStatus?.MissingOwnedBatch == true)
                    throw new InvalidDataException("The background AI host lost its owned audiences before its owner joined.");
                return Task.CompletedTask;
            });
            if (mode == MixedGameDriveMode.PolledBackground)
                await ProtectCleanup("Final owned history", () => provider.DrainHistoryAsync(force: true));
            await ProtectCleanup("Retire ordered evidence", () => { evidence.RetireHandoff(); return Task.CompletedTask; });
            await ProtectCleanup("Final receipt evidence", async () => {
                finalPriorityContinuations = bots.PriorityContinuations;
                await SaveAsync(Path.Combine(paths.Profile, "private-owner-teardown.json"), new {
                    cumulativeProviderAcceptances = provider.AcceptedBySeat, provider.Executing, provider.PriorityControlsExecuting,
                    automatedPriorityContinuations = finalPriorityContinuations,
                    publicProjectionPreparation = evidence.PublicProjectionPreparation?.Evidence,
                    orderedEvidence = evidence.HandoffStatus,
                    host = new { host.ClientAdmissions, host.ProviderAcceptances },
                    guest = new { guest.ClientAdmissions, guest.ProviderAcceptances }
                });
            });
            await ProtectCleanup("Close guest client", () => { guest.Dispose(); return Task.CompletedTask; });
            await ProtectCleanup("Close host client", () => { host.Dispose(); return Task.CompletedTask; });
            foreach (var engine in mode == MixedGameDriveMode.PolledBackground ? engines.Reverse() : engines)
                await ProtectCleanup("Connection teardown", () => engine.DisposeAsync().AsTask());
            await ProtectCleanup("Close public facts trace", () => trace.DisposeAsync().AsTask());
            await ProtectCleanup("Close evidence trace", () => { evidence.Dispose(); return Task.CompletedTask; });
        }
        double totalElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        var progressEvidence = progress?.Capture();
        double? activeElapsedMilliseconds = progressEvidence?.ActiveStopElapsedTicks is { } activeTicks
            ? (double)activeTicks / progressEvidence.TimestampFrequency * 1000 : null;
        var result = new MixedGameResult(match, stop, failure, verified, state?.Turn ?? 0, steps,
            totalElapsedMilliseconds, developed.Order().ToImmutableArray(), comparisons, completedCohortCount,
            host.ProviderAcceptances, guest.ProviderAcceptances, state?.Outcome, evidence.Summary(), bindings.ToImmutable()) {
                DriveMode = mode.ToString(),
                Participants = qualification?.Participants.Select(participant => new MixedGameParticipantEvidence(
                    participant.Index, participant.PlayerId, participant.Index == 0 ? MixedGameParticipantRole.Host
                        : participant.Index == 1 ? MixedGameParticipantRole.Guest : MixedGameParticipantRole.Ai,
                    participant.Index == 0 ? host.ProviderAcceptances : participant.Index == 1 ? guest.ProviderAcceptances
                        : mode == MixedGameDriveMode.PolledBackground ? provider.AcceptedBySeat.GetValueOrDefault(participant.PlayerId)
                        : stoppedHistories.SelectMany(history => history.Decisions).Count(record => record.Stamp.SeatId == participant.PlayerId
                            && record.Outcome == AiDecisionOutcome.Accepted))).ToImmutableArray() ?? [],
                CausalDecks = qualification?.Summary(stop, verified, failure) ?? [],
                PublicComparison = publicCohorts?.Evidence,
                Progress = progressEvidence,
                HostProgress = hostProgress, GuestProgress = guestProgress,
                ActiveElapsedMilliseconds = activeElapsedMilliseconds,
                CleanupElapsedMilliseconds = activeElapsedMilliseconds is { } activeMilliseconds
                    ? totalElapsedMilliseconds - activeMilliseconds : null,
                HostAuto = host.AutoEvidence, GuestAuto = guest.AutoEvidence,
                AutomatedPriorityContinuations = finalPriorityContinuations
            };
        result = result with { Qualification = MixedGameQualification.Assess(result, last) };
        await SaveAsync(Path.Combine(paths.Profile, "result.json"), result);
        return result;
    }

    // A retired read owns only its audience. Still pump the other ordinary
    // client, including its queued publications and delivery acknowledgements.
    // An incomplete round receives no comparison or new policy-dispatch credit.
    internal static async Task<bool> ObservePlayersAsync(ConnectedPlayerDriver host, ConnectedPlayerDriver guest,
        MixedGameProgress progress, int steps)
    {
        bool complete = true;
        progress.Enter(MixedGameStage.HostObservation, steps);
        try { await host.ObserveAsync(); progress.Complete(); }
        catch (EngineObservationSupersededException) {
            progress.SupersededObservation(); complete = false;
        }
        progress.Enter(MixedGameStage.GuestObservation, steps);
        try { await guest.ObserveAsync(); progress.Complete(); }
        catch (EngineObservationSupersededException) {
            progress.SupersededObservation(); complete = false;
        }
        return complete;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static Task SaveAsync(string path, object value) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

internal sealed record MixedGameResult(int Rotation, string StopReason, string? Failure, bool NaturalOutcomeVerified,
    int Turns, int Steps, double ElapsedMilliseconds, ImmutableArray<int> DevelopedSeats, int PublicComparisons,
    int FourAudienceCohorts, int HostProviderAcceptances, int GuestProviderAcceptances, MatchOutcome? Outcome,
    AiCampaignEvidence Evidence, ImmutableArray<AiCampaignDeckBinding> Decks)
{
    public string Audience => "host-private";
    public string DriveMode { get; init; } = nameof(MixedGameDriveMode.ManualStepped);
    public ImmutableArray<MixedGameParticipantEvidence> Participants { get; init; } = [];
    public ImmutableArray<AiCampaignDeckQualification> CausalDecks { get; init; } = [];
    public MixedGameQualificationAssessment? Qualification { get; init; }
    public MixedPublicComparisonEvidence? PublicComparison { get; init; }
    public MixedGameProgressEvidence? Progress { get; init; }
    public ConnectedPlayerProgressEvidence? HostProgress { get; init; }
    public ConnectedPlayerProgressEvidence? GuestProgress { get; init; }
    public double? ActiveElapsedMilliseconds { get; init; }
    public double? CleanupElapsedMilliseconds { get; init; }
    public ConnectedPlayerAutoEvidence? HostAuto { get; init; }
    public ConnectedPlayerAutoEvidence? GuestAuto { get; init; }
    public ImmutableArray<AiAutomatedPriorityEvidence> AutomatedPriorityContinuations { get; init; } = [];
}
