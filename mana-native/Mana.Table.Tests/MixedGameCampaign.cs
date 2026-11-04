using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Qualification evidence for ordinary host/TCP player connections plus two
// independent bots. Human-seat scripts use their own audience and ClientSession;
// neither they nor a natural finish substitute for actual human playtesting.
internal static class MixedGameCampaign
{
    internal static async Task RunAsync(ForgePaths paths, string profile)
    {
        var limits = profile switch {
            "smoke" => new AiCampaignLimits(1, 16, TimeSpan.FromSeconds(45)),
            "natural" or "release" => AiCampaignLimits.Release,
            _ => throw new ArgumentException("Unknown mixed-game profile: " + profile)
        };
        string directory = Path.Combine(paths.Profile, "mixed-game-campaign");
        Directory.CreateDirectory(directory);
        await SaveAsync(Path.Combine(directory, "manifest.json"), new {
            formatVersion = 1, audience = "host-private", profile, limits, source = "ordinary-host-and-tcp-guest-with-two-host-owned-ai",
            humanSeats = "Scripted through owned observations and ClientSession; human UX review remains pending.",
            engineRandomnessControlled = false, seedScope = "Policy tie breaking only; not a deterministic engine replay.",
            timeCutoffScope = "Checked between bounded engine/client operations, not an exact process wall deadline.",
            launchJarSha256 = Hash(paths.Jar), harnessAssemblySha256 = Hash(typeof(MixedGameCampaign).Assembly.Location),
            policyAssemblySha256 = Hash(typeof(CasualCommanderPolicy).Assembly.Location),
            decks = AiCampaignDecks.All.Select(deck => new { deck.Id, deck.Hash, deck.Commanders, deck.IntendedMechanics, deck.RequiredWitnessIds })
        });
        var results = new List<MixedGameResult>();
        for (int match = 0; match < limits.Matches; match++) {
            var result = await MatchAsync(paths with { Profile = Path.Combine(directory, "match-" + match) }, match, limits);
            results.Add(result);
            await SaveAsync(Path.Combine(directory, "summary.json"), new {
                formatVersion = 1, audience = "host-private", profile, results,
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
        if (profile is "natural" or "release" && (results.Count != limits.Matches || results.Any(result => !result.NaturalOutcomeVerified)))
            throw new InvalidOperationException("Mixed-game qualification is inconclusive: not every requested rotation reached a verified natural outcome.");
        if (profile == "release" && results.Any(result => result.Qualification?.Status != MixedGameQualificationStatus.Passed))
            throw new InvalidOperationException("Mixed-game qualification is inconclusive: required role, public-state or causal mechanic evidence is missing.");
    }

    private static async Task<MixedGameResult> MatchAsync(ForgePaths paths, int match, AiCampaignLimits limits)
    {
        Directory.CreateDirectory(paths.Profile);
        var engines = Enumerable.Range(0, 2).Select(index => new ForgeEngine(paths with {
            Profile = Path.Combine(paths.Profile, "connection-" + index)
        })).ToArray();
        using var evidence = new AiCampaignDiagnostics(paths.Profile);
        var provider = new AiCampaignProvider(engines[0], evidence);
        await using var bots = new AiTableHost(provider, _ => evidence.Record(new CasualCommanderPolicy()),
            limits.PolicySeed + match, new(TimeSpan.FromSeconds(10), 4096));
        using var host = new ConnectedPlayerDriver(engines[0], evidence, limits.PolicySeed + match);
        using var guest = new ConnectedPlayerDriver(engines[1], evidence, limits.PolicySeed + match + 100);
        await using var trace = new StreamWriter(new GZipStream(File.Create(Path.Combine(paths.Profile, "public-facts.jsonl.gz")), CompressionLevel.Fastest));
        var elapsed = Stopwatch.StartNew();
        var cohorts = new Dictionary<(string Game, long Board), (string Facts,
            ForgePublicProjectionFacts.ExecutionCorrelation Correlations, HashSet<int> Viewers)>();
        var completeCohorts = new HashSet<(string Game, long Board)>();
        var developed = new HashSet<int>();
        var identities = new HashSet<int>();
        var bindings = ImmutableArray.CreateBuilder<AiCampaignDeckBinding>();
        ImmutableArray<AutomatedSeatObservation> last = [];
        GameSnapshot? state = null;
        int steps = 0, comparisons = 0;
        string stage = "initialize", stop = "not-started";
        string? failure = null;
        bool verified = false;
        AiCampaignQualificationTracker? qualification = null;
        string configurationId = "mixed-lobby-rotation-" + match, configurationHash = "";
        try {
            await Task.WhenAll(engines.Select(engine => engine.InitializeAsync()));
            if (engines.Any(engine => !engine.SupportsOwnedDecisionObservations))
                throw new NotSupportedException("Both connected player endpoints must expose owned decision observations.");
            await engines[0].EnableAutomatedSeatsAsync();
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
            await engines[0].StartAsync();
            // Setup/warm-up time has its own bounded operations. The game budget
            // starts at launch, matching the existing natural AI campaign.
            elapsed.Restart();
            while (true) {
                if (elapsed.Elapsed >= limits.MatchTime) { stop = "time-budget"; break; }
                stage = "owned-player-observations";
                try { await host.ObserveAsync(); await guest.ObserveAsync(); }
                catch (EngineObservationSupersededException) { continue; }
                stage = "bot-step";
                await bots.StepAsync();
                if (bots.LastFault != null) throw new InvalidDataException("Bot fault: " + JsonSerializer.Serialize(bots.LastFault));
                last = new[] { host.Last, guest.Last }.OfType<AutomatedSeatObservation>().Concat(provider.Last).ToImmutableArray();
                steps++;
                stage = "shared-state-and-visibility";
                if (last.Length != 4 || last.Select(item => item.SeatId).Distinct().Count() != 4)
                    throw new InvalidDataException("The mixed table lost one of its four disjoint owned audiences.");
                if (last.Any(item => item.State.Status == "starting")) {
                    foreach (var item in last.Where(item => item.State.Status == "starting"))
                        if (item.State.Decision != null || item.State.Players.Length is not (0 or 4)
                            || item.Domain.Coverage != ActionDomainCoverage.NotApplicable)
                            throw new InvalidDataException("An uncommitted starting audience acquired decision authority.");
                    await Task.Delay(1);
                    continue;
                }
                if (qualification == null) {
                    qualification = AiCampaignQualificationTracker.ForMixedTable(bindings.ToImmutable(), lobby,
                        last.Select(item => new OwnedDecisionObservation(item.State, item.Domain, item.Semantics)).ToImmutableArray(),
                        selectionIds.ToImmutableArray(), configurationId, configurationHash);
                    evidence.BindQualification(qualification);
                    foreach (var item in last) evidence.Observe(item);
                }
                foreach (var item in last) {
                    var frame = item.State;
                    if (frame.Status == "error" || frame.Error != null) throw new InvalidDataException(frame.Error ?? "Rules error.");
                    if (frame.Players.Length != 4 || !frame.Players.Select(player => player.Id).ToHashSet().SetEquals(last.Select(row => row.SeatId)))
                        throw new InvalidDataException("The mixed table changed its player mapping.");
                    if (state != null && frame.Id != state.Id) throw new InvalidDataException("An audience changed games during the match.");
                    identities.Add(item.SeatId);
                    foreach (var player in frame.Players)
                        if (player.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal))) developed.Add(player.Id);
                    string facts = ForgePublicProjectionFacts.Capture(frame);
                    var correlations = ForgePublicProjectionFacts.CaptureExecutionCorrelations(frame);
                    if (!correlations.Valid) throw new InvalidDataException("An audience published invalid execution references.");
                    var key = (frame.Id, frame.BoardRevision);
                    if (cohorts.TryGetValue(key, out var prior)) {
                        if (prior.Facts != facts) throw new InvalidDataException("The same published board disagreed across observations; see private-final-observations.json.");
                        var merged = ForgePublicProjectionFacts.MergeExecutionCorrelations(prior.Correlations, correlations)
                            ?? throw new InvalidDataException("Audiences disclosed contradictory references for the same stack occurrence.");
                        cohorts[key] = (prior.Facts, merged, prior.Viewers);
                        if (prior.Viewers.Add(item.SeatId)) comparisons++;
                        if (prior.Viewers.Count == 4) completeCohorts.Add(key);
                    } else {
                        cohorts.Add(key, (facts, correlations, [item.SeatId]));
                        await trace.WriteLineAsync(JsonSerializer.Serialize(new { steps, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, facts = JsonSerializer.Deserialize<JsonElement>(facts) }));
                    }
                }
                // Retain bounded public cohorts, not old private card authority.
                foreach (var key in cohorts.Keys.OrderByDescending(key => key.Board).Skip(64).ToArray()) cohorts.Remove(key);
                state = host.Last!.State;
                evidence.ObservePublic(state);
                if (steps % 64 == 0) await SaveAsync(Path.Combine(paths.Profile, "checkpoint.json"), new {
                    audience = "public", steps, state.Turn, state.PhaseKey, state.BoardRevision,
                    elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, comparisons,
                    fourAudienceCohorts = completeCohorts.Count, developedSeats = developed.Order()
                });
                if (state.Status == "finished") {
                    if (state.Outcome == null) throw new InvalidDataException("A finished mixed game has no typed rules outcome.");
                    MatchOutcomeFacts.Validate(state);
                    verified = last.All(item => item.State.Status == "finished"
                        && item.State.BoardRevision == state.BoardRevision
                        && ForgePublicProjectionFacts.Capture(item.State) == ForgePublicProjectionFacts.Capture(state));
                    if (!verified) { stop = "terminal-audiences-not-yet-coherent"; }
                    else { stop = "natural-outcome"; break; }
                }
                if (state.Turn >= limits.Turns) { stop = "turn-budget"; break; }
                if (elapsed.Elapsed >= limits.MatchTime) { stop = "time-budget"; break; }
                stage = "connected-player-client-dispatch";
                await host.StepAsync(); await guest.StepAsync();
                await Task.Delay(1);
            }
        } catch (Exception error) {
            stop = "mechanical-failure"; failure = error.ToString();
            evidence.PreserveFailure(error, stage, steps, last);
        } finally {
            if (evidence.PreserveObservations(last) is { } captureFailure) {
                failure = (failure == null ? "" : failure + "\n") + "Private evidence capture: " + captureFailure;
                stop = "mechanical-failure"; verified = false;
            }
            await SaveAsync(Path.Combine(paths.Profile, "private-player-receipts.json"), new {
                host = new { host.ClientAdmissions, host.ProviderAcceptances, host.History },
                guest = new { guest.ClientAdmissions, guest.ProviderAcceptances, guest.History },
                automated = new { histories = bots.Histories, fault = bots.LastFault }
            });
            // All outcome/evidence is captured before lifecycle teardown. This
            // runner never concedes, substitutes a seat or declares a cutoff a win.
            foreach (var engine in engines) {
                try { await engine.DisposeAsync(); }
                catch (Exception error) {
                    failure = (failure == null ? "" : failure + "\n") + "Connection teardown: " + error;
                    stop = "mechanical-failure"; verified = false;
                }
            }
        }
        var result = new MixedGameResult(match, stop, failure, verified, state?.Turn ?? 0, steps,
            elapsed.Elapsed.TotalMilliseconds, developed.Order().ToImmutableArray(), comparisons, completeCohorts.Count,
            host.ProviderAcceptances, guest.ProviderAcceptances, state?.Outcome, evidence.Summary(), bindings.ToImmutable()) {
                Participants = qualification?.Participants.Select(participant => new MixedGameParticipantEvidence(
                    participant.Index, participant.PlayerId, participant.Index == 0 ? MixedGameParticipantRole.Host
                        : participant.Index == 1 ? MixedGameParticipantRole.Guest : MixedGameParticipantRole.Ai,
                    participant.Index == 0 ? host.ProviderAcceptances : participant.Index == 1 ? guest.ProviderAcceptances
                        : bots.Histories.SelectMany(history => history.Decisions).Count(record => record.Stamp.SeatId == participant.PlayerId
                            && record.Outcome == AiDecisionOutcome.Accepted))).ToImmutableArray() ?? [],
                CausalDecks = qualification?.Summary(stop, verified, failure) ?? []
            };
        result = result with { Qualification = MixedGameQualification.Assess(result, last) };
        await SaveAsync(Path.Combine(paths.Profile, "result.json"), result);
        return result;
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
    public ImmutableArray<MixedGameParticipantEvidence> Participants { get; init; } = [];
    public ImmutableArray<AiCampaignDeckQualification> CausalDecks { get; init; } = [];
    public MixedGameQualificationAssessment? Qualification { get; init; }
}
