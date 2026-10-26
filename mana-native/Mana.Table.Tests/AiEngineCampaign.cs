using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal sealed record AiCampaignLimits(int Matches, int Turns, TimeSpan MatchTime, long PolicySeed = 261027);
internal sealed record AiCampaignResult(int Match, string StopReason, string? MechanicalFailure, int Turns, int Steps,
    int DevelopedSeats, int Accepted, int OfferedRoutesUsed, int PublicComparisons, double ElapsedMilliseconds, object Evidence,
    AiCampaignContinuation Continuation);

// Sequential games reuse the existing JVM lane. Budgets stop tests explicitly;
// the driver never fabricates a bot response or disguises a test stop as a win.
internal static class AiEngineCampaign
{
    internal static async Task RunStandaloneAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        await using var engine = new ForgeEngine(paths);
        await engine.InitializeAsync();
        File.WriteAllText(Path.Combine(paths.Profile, "campaign-runtime.json"), JsonSerializer.Serialize(new {
            engineJar = paths.Jar, engineJarSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.Jar))),
            paths.Java, paths.Resources
        }, new JsonSerializerOptions { WriteIndented = true }));
        await RunAsync(engine, paths.Profile, new(2, 32, TimeSpan.FromSeconds(45)), (passed, detail) => {
            File.AppendAllText(Path.Combine(paths.Profile, "campaign-progress.log"), detail + "\n");
            if (!passed) throw new InvalidOperationException(detail);
        }, paths);
    }

    internal static async Task RunAsync(ForgeEngine engine, string profile, AiCampaignLimits limits, Action<bool, string> check, ForgePaths? runtime = null)
    {
        if (limits.Matches is < 1 or > 16 || limits.Turns is < 8 or > 160 || limits.MatchTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
        string directory = Path.Combine(profile, "ai-campaign"); Directory.CreateDirectory(directory);
        AiCampaignContinuation.CheckRegression(check);
        CheckDisclosureBounds(check);
        foreach (var manifest in AiCampaignDecks.All) manifest.Validate();
        await engine.EnableAutomatedSeatsAsync();
        var saved = new List<string>();
        foreach (var manifest in AiCampaignDecks.All) {
            try {
                var imported = await engine.ImportAsync("AI campaign " + manifest.Id, manifest.List);
                var details = await engine.OpenDeckAsync(imported.Id);
                if (details.StructureValid == false || details.Problem.Length > 0 || !details.Issues.IsEmpty)
                    throw new InvalidDataException("Campaign deck " + manifest.Id + " failed engine validation: " + JsonSerializer.Serialize(details.Issues) + details.Problem);
                var committed = await engine.SaveDeckAsync(details.Id, details.Revision);
                if (committed.SaveError.Length > 0) throw new InvalidDataException(committed.SaveError);
                saved.Add("deck:" + details.Id);
            } catch (Exception error) {
                File.WriteAllText(Path.Combine(directory, "setup-failure.json"), JsonSerializer.Serialize(new {
                    classification = "mechanical-setup-failure", manifest.Id, manifest.Hash, manifest.List, failure = error.ToString()
                }, new JsonSerializerOptions { WriteIndented = true }));
                throw;
            }
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new {
            formatVersion = 1, audience = "host-private", deckVersion = AiCampaignDecks.Version, limits,
            timeCutoffScope = "Checked between completed engine steps; engine requests and teardown can extend elapsed wall time.",
            engineRandomnessControlled = false, seedScope = "Policy tie-breaking only. Forge currently uses process-global SecureRandom; these are not seeded engine replays.",
            policy = new CasualCommanderPolicy().Descriptor.Id,
            policyAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CasualCommanderPolicy).Assembly.Location))),
            engineJarSha256 = runtime == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtime.Jar))),
            decks = AiCampaignDecks.All.Select((deck, index) => new { deck.Id, deck.Hash, deck.Commanders, deck.LandCount,
                spells = deck.SpellNames.Length, deck.IntendedMechanics, list = deck.List, handle = saved[index] })
        }, new JsonSerializerOptions { WriteIndented = true }));
        var results = new List<AiCampaignResult>();
        for (int index = 0; index < limits.Matches; index++) {
            var rotation = Enumerable.Range(0, 4).Select(seat => saved[(seat + index) % 4]).ToImmutableArray();
            var result = await Match(engine, Path.Combine(directory, "match-" + index), index, rotation, limits);
            results.Add(result);
            if (result.MechanicalFailure != null) {
                // The shared JVM may be blocked or compromised. Do not turn
                // its follow-on connection timeouts into independent failures.
                File.WriteAllText(Path.Combine(directory, "unrun-rotations.json"), JsonSerializer.Serialize(new {
                    failedMatch = index, reason = "Earlier mechanical failure; the shared engine was not reused.",
                    unrunMatches = Enumerable.Range(index + 1, limits.Matches - index - 1).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
                break;
            }
        }
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        check(results.All(result => result.MechanicalFailure == null), "Diverse AI campaign mechanical results: "
            + string.Join("; ", results.Select(result => $"match {result.Match}: {result.MechanicalFailure ?? result.StopReason}, turn {result.Turns}, {result.Accepted} accepted commands")));
        check(results.All(result => result.Continuation.Conclusive), "Diverse AI campaign continuation results: "
            + string.Join("; ", results.Select(result => $"match {result.Match}: {result.Continuation.Classification}, turn {result.Turns}/{limits.Turns}"))
            + ". A time cutoff is inconclusive; historical accepted commands do not establish completion or diagnose a policy loop.");
        check(results.All(result => result.DevelopedSeats >= 3 && result.Accepted >= 12 && result.OfferedRoutesUsed >= 8 && result.PublicComparisons > 0),
            "Every bounded diverse match develops multiple real battlefields and compares same-revision public projections; absent rare mechanics remain explicit coverage gaps.");
    }

    private static void CheckDisclosureBounds(Action<bool, string> check)
    {
        var face = new Card { Name = "Forest", Type = "Basic Land - Forest", ManaCost = "no cost" };
        Player Player(int id, ImmutableArray<Card> hand, ImmutableArray<Card> library, Card? top = null) => new(id,
            "Seat " + id, 40, false, false, ImmutableDictionary<string, int>.Empty,
            [new("Hand", hand.Length, hand, null), new("Library", library.Length, library, top)]);
        var revealed = new GameSnapshot("disclosure-conformance", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
            [Player(0, [], []), Player(1, [face], []), Player(2, [], [])], [], null,
            new("disclosure", new(), new RevealPayload([new(null, face.Name, face)])));
        check(AiCampaignVisibility.PrivateWindowsAreDisclosed(revealed),
            "One current anonymous reveal authorizes its exact read-only face window");
        var keyedReveal = revealed with {
            Players = [revealed.Players[0], Player(1, [face with { Key = "opaque-read-only-key", VisualId = null!, CombatId = null! }], []), revealed.Players[2]] };
        check(AiCampaignVisibility.PrivateWindowsAreDisclosed(keyedReveal),
            "A current reveal's bounded face remains read-only when its projection retains an opaque key without physical identity");
        var otherFace = new CardFace("Undisclosed reverse", "Creature", "{U}", "Undisclosed reverse rules", 1, 1, "reverse", "back");
        bool AcceptsFace(Card candidate) => AiCampaignVisibility.PrivateWindowsAreDisclosed(revealed with {
            Players = [revealed.Players[0], Player(1, [candidate], []), revealed.Players[2]] });
        check(!AcceptsFace(face with { Text = "Undisclosed library card: Hidden Sentinel" })
                && !AcceptsFace(face with { OtherFace = otherFace })
                && !AcceptsFace(face with { Power = 4, Toughness = 4 })
                && !AcceptsFace(face with { CombatKeywords = ["hexproof"] })
                && !AcceptsFace(face with { States = [new("extra", "Undisclosed ability", "ability", 1, null, "Hidden rules text")] }),
            "An anonymous face disclosure cannot authorize additional rules text, another face, statistics or readable abilities");
        check(AcceptsFace(face with { Edition = "a different occurrence printing", CollectorNumber = "42", Tapped = true }),
            "Anonymous face matching permits legitimate printing metadata and occurrence-state differences");
        var duplicate = keyedReveal with { Players = [keyedReveal.Players[0], keyedReveal.Players[1], Player(2, [face], [])] };
        check(!AiCampaignVisibility.PrivateWindowsAreDisclosed(duplicate)
                && !AiCampaignVisibility.PrivateWindowsAreDisclosed(keyedReveal with { Decision = null })
                && !AiCampaignVisibility.PrivateWindowsAreDisclosed(keyedReveal, domain: DecisionActionDomain.None with { PoolManaColors = ["U"] })
                && !AiCampaignVisibility.PrivateWindowsAreDisclosed(revealed with {
                    Players = [revealed.Players[0], Player(1, [face with { Selectable = true }], []), revealed.Players[2]] })
                && !AiCampaignVisibility.PrivateWindowsAreDisclosed(revealed with {
                    Players = [revealed.Players[0], Player(1, [face with { CombatId = "undisclosed-physical-identity" }], []), revealed.Players[2]] }),
            "A reveal cannot authorize extra hidden copies, outlive its decision, disclose physical identity or grant action permissions");
        var top = face with { VisualId = "known-top" };
        var look = revealed with { Decision = null, Players = [Player(0, [], [top], top), Player(1, [], []), Player(2, [], [])] };
        check(AiCampaignVisibility.PrivateWindowsAreDisclosed(look)
                && !AiCampaignVisibility.PrivateWindowsAreDisclosed(look, new(DecisionIntent.Search))
                && !AiCampaignVisibility.PrivateWindowsAreDisclosed(look with {
                    Players = [Player(0, [], [top, face], top), look.Players[1], look.Players[2]] }),
            "An explicit top disclosure authorizes exactly its single occurrence and no remainder of the library");
        var prior = new AiDecisionReplayCase(1, "previous-policy-request", "test-policy", revealed,
            DecisionActionDomain.None, new(), new([], []), 1);
        var rejected = revealed with { Revision = 11, Decision = revealed.Decision! with { Id = "rejected-observation" } };
        var raw = new AutomatedSeatObservation(0, rejected,
            DecisionActionDomain.None with { Version = 17, Sources = [new(rejected.Id, rejected.Decision!.Id, "current-key", "stale-occurrence", [])] }, new());
        var manifest = AiCampaignDiagnostics.DescribeFailure(new InvalidOperationException("original validation failure"),
            "host-step", 7, [raw], prior, true, ["secondary diagnostic I/O failure"]);
        check(manifest.GetProperty("authority").GetString() == "none"
                && manifest.GetProperty("rawObservation").GetProperty("scopes")[0].GetProperty("decisionId").GetString() == "rejected-observation"
                && manifest.GetProperty("rawObservation").GetProperty("scopes")[0].GetProperty("offerVersion").GetInt64() == 17
                && manifest.GetProperty("lastPolicyRequest").GetProperty("decisionId").GetString() == "disclosure"
                && manifest.GetProperty("lastPolicyRequest").GetProperty("role").GetString()!.Contains("not the rejected-observation")
                && manifest.GetProperty("exception").GetString()!.Contains("original validation failure")
                && manifest.GetProperty("secondaryDiagnosticErrors")[0].GetString() == "secondary diagnostic I/O failure",
            "Failure evidence distinguishes a raw rejected observation from the preceding policy request without granting authority or replacing the original error");
    }

    private static async Task<AiCampaignResult> Match(ForgeEngine engine, string directory, int index,
        ImmutableArray<string> decks, AiCampaignLimits limits)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "match-config.json"), JsonSerializer.Serialize(new {
            formatVersion = 1, audience = "host-private", match = index, decksInSeatOrder = decks,
            policySeed = limits.PolicySeed + index, limits.Turns, limits.MatchTime, engineRandomnessControlled = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        using var evidence = new AiCampaignDiagnostics(directory);
        var provider = new AiCampaignProvider(engine, evidence);
        await using var host = new AiTableHost(provider, _ => evidence.Record(new CasualCommanderPolicy()),
            limits.PolicySeed + index, new(TimeSpan.FromSeconds(10), 4096));
        await using var trace = new StreamWriter(new GZipStream(File.Create(Path.Combine(directory, "public.jsonl.gz")), CompressionLevel.Fastest));
        var elapsed = Stopwatch.StartNew();
        GameSnapshot? state = null;
        int steps = 0, comparisons = 0;
        string stop = "not-started", lastFacts = "", stage = "start-simulation";
        string? failure = null;
        Exception? primaryFailure = null;
        var developed = new HashSet<int>();
        try {
            state = await engine.StartSimulationAsync(decks);
            while (true) {
                stage = "host-step";
                await host.StepAsync();
                stage = "observe-public";
                state = await engine.ObserveSimulationAsync() ?? throw new InvalidDataException("The campaign lost its public observation.");
                steps++;
                stage = "validate-public-and-private-observations";
                if (host.LastFault != null) throw new InvalidOperationException("Host fault: " + JsonSerializer.Serialize(host.LastFault));
                if (state.Status == "error") throw new InvalidOperationException(state.Error ?? "Rules engine failure.");
                if (state.ViewerId != -1 || state.Decision != null
                    || state.Players.Any(player => !player.Zone("Hand").Cards.IsEmpty || !player.Zone("Library").Cards.IsEmpty)
                    || CardViews.InZones(state).Any(item => item.Card.Key.Length > 0))
                    throw new InvalidDataException("Public spectator disclosed private decision/card authority.");
                evidence.ObservePublic(state);
                foreach (var player in state.Players)
                    if (player.Zone("Battlefield").Cards.Any(card => card.Type.Contains("Land", StringComparison.Ordinal))) developed.Add(player.Id);
                foreach (var bot in provider.Last) {
                    if (bot.State.ViewerId != bot.SeatId || !AiCampaignVisibility.PrivateWindowsAreDisclosed(bot.State, bot.Semantics, bot.Domain))
                        throw new InvalidDataException("Automated seat disclosed another private hand or library order.");
                    if (state.BoardRevision == bot.State.BoardRevision && state.Id == bot.State.Id) {
                        if (!AiWholeSeatEngineCheck.ComparePublicProjection(state, bot.State, directory))
                            throw new InvalidDataException("Same-revision public projections diverged.");
                        comparisons++;
                    }
                }
                string facts = AiWholeSeatEngineCheck.ReplayFacts(state);
                stage = "record-public-checkpoint";
                if (facts != lastFacts) {
                    lastFacts = facts;
                    await trace.WriteLineAsync(JsonSerializer.Serialize(new { step = steps, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, state }));
                }
                stage = "check-continuation";
                if (state.Status == "finished") { stop = "natural-outcome"; break; }
                if (state.Turn >= limits.Turns) { stop = "turn-budget"; break; }
                if (elapsed.Elapsed >= limits.MatchTime) {
                    if (state.Turn < 8) throw new TimeoutException("The campaign did not reach two multiplayer rounds within its time budget.");
                    stop = "time-budget"; break;
                }
                await Task.Delay(1);
            }
        } catch (Exception error) { stop = "mechanical-failure"; failure = error.ToString(); primaryFailure = error; }
        finally {
            var captureError = evidence.PreserveObservations(provider.Last);
            if (captureError != null && primaryFailure == null) {
                stage = "capture-private-observations"; stop = "mechanical-failure";
                primaryFailure = captureError; failure = captureError.ToString();
            }
            if (primaryFailure != null) evidence.PreserveFailure(primaryFailure, stage, steps, provider.Last);
            if (state != null) {
                try { await engine.StopSimulationAsync(state.Id); }
                catch (Exception error) {
                    stop = "mechanical-failure";
                    failure = (failure == null ? "" : failure + "\n") + "Simulation teardown: " + error;
                    if (primaryFailure == null) evidence.PreserveFailure(error, "stop-simulation", steps, provider.Last);
                }
            }
        }
        var result = new AiCampaignResult(index, stop, failure, state?.Turn ?? 0, steps, developed.Count,
            evidence.Accepted, evidence.Commands.GetValueOrDefault(nameof(UseOfferedAction)), comparisons, elapsed.Elapsed.TotalMilliseconds, evidence.Summary(),
            AiCampaignContinuation.Assess(stop, limits.Turns, state?.Turn ?? 0, state?.Status == "finished" && !string.IsNullOrWhiteSpace(state.Result)));
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }
}
