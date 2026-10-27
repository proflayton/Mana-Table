using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;
using Mana.Planning;

namespace Mana.Table;

internal sealed record AiReleaseGate(string Id, bool Passed, string Detail);
internal sealed record AiReleaseRuntime(string ProductionEngineSha256, string FixtureLauncherSha256,
    string PolicyAssemblySha256, string PlanningAssemblySha256, string HarnessAssemblySha256, string HarnessVersion, string FixtureScope,
    string? SourceRevision, string Provenance, string? SourceManifestSha256,
    string Resources, string ResourceProvenance, string? CandidateBuildSha256);

// Release evidence is separate from the short CI prefix. A cutoff remains
// inconclusive; stopping a table cannot supply a natural-outcome witness.
internal static class AiReleaseEvaluation
{
    internal static AiReleaseRuntime VerifyRuntime(ForgePaths launch, string productionJar, bool verifyPackage = false,
        string? sourceRevision = null, string? sourceManifest = null)
    {
        using var launcher = ZipFile.OpenRead(launch.Jar);
        var manifestEntry = launcher.GetEntry("META-INF/MANIFEST.MF") ?? throw new InvalidDataException("Fixture launcher has no manifest.");
        using var reader = new StreamReader(manifestEntry.Open());
        string manifest = reader.ReadToEnd().Replace("\r\n", "\n");
        if (!manifest.Contains("Main-Class: forge.api.AiReleaseFixtureEngine\n", StringComparison.Ordinal)
            || !manifest.Contains("Class-Path: forge-engine.jar\n", StringComparison.Ordinal)
            || !launcher.Entries.Any(entry => entry.FullName == "forge/api/AiReleaseFixtureEngine.class")
            || launcher.Entries.Any(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal)
                && !entry.FullName.StartsWith("forge/api/AiReleaseFixtureEngine", StringComparison.Ordinal)))
            throw new InvalidDataException("Use the test-only fixture launcher, containing only its pregame factory classes.");
        string dependency = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(launch.Jar))!, "forge-engine.jar");
        string productionHash = Hash(productionJar);
        if (Hash(dependency) != productionHash) throw new InvalidDataException("The launcher's production engine dependency differs from the recorded engine.");
        using var production = ZipFile.OpenRead(productionJar);
        if (production.GetEntry("forge/api/DesktopEngine.class") == null
            || production.Entries.Any(entry => entry.FullName.StartsWith("forge/api/AiReleaseFixtureEngine", StringComparison.Ordinal)))
            throw new InvalidDataException("Shipping engine must contain the real entry point and exclude test fixture classes.");
        string? candidateBuild = null;
        if (verifyPackage) {
            if (sourceRevision == null) throw new InvalidDataException("Packaged verification requires its full source revision.");
            string engineDirectory = Path.GetDirectoryName(launch.Resources)!;
            string package = Path.GetDirectoryName(engineDirectory)!;
            candidateBuild = Path.Combine(package, "build.json");
            using var build = JsonDocument.Parse(File.ReadAllText(candidateBuild));
            var metadata = build.RootElement;
            if (metadata.GetProperty("sourceRevision").GetString() != sourceRevision || !metadata.GetProperty("release").GetBoolean()
                || !productionHash.Equals(metadata.GetProperty("engineSha256").GetString(), StringComparison.OrdinalIgnoreCase)
                || Hash(Path.Combine(engineDirectory, "forge-engine.jar")) != productionHash)
                throw new InvalidDataException("Use the exact source candidate's release package and resources.");
            foreach (var assembly in new[] { typeof(CasualCommanderPolicy).Assembly, typeof(ManaFundingPlanner).Assembly,
                typeof(GameSnapshot).Assembly, typeof(ForgeEngine).Assembly })
                if (Hash(Path.Combine(package, Path.GetFileName(assembly.Location))) != Hash(assembly.Location))
                    throw new InvalidDataException("The candidate package and regression harness use different " + assembly.GetName().Name + " bytes.");
        }
        string version = typeof(AiReleaseEvaluation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        string launcherHash = Hash(launch.Jar);
        if (sourceRevision != null && (!Regex.IsMatch(sourceRevision, "^[a-f0-9]{40}$")
            || version.Split('+').Length != 2 || version.Split('+')[1] != sourceRevision))
            throw new InvalidDataException("The harness informational version must embed the exact requested source revision after '+'.");
        if (sourceManifest != null) {
            if (sourceRevision == null) throw new InvalidDataException("A source manifest requires its expected source revision.");
            using var receipt = JsonDocument.Parse(File.ReadAllText(sourceManifest));
            var root = receipt.RootElement;
            bool Matches(string name, string hash, string file) => root.GetProperty(name).GetProperty("sha256").GetString()!
                .Equals(hash, StringComparison.OrdinalIgnoreCase) && root.GetProperty(name).GetProperty("bytes").GetInt64() == new FileInfo(file).Length;
            if (root.GetProperty("schemaVersion").GetInt32() != 2 || root.GetProperty("sourceRevision").GetString() != sourceRevision
                || !Matches("engine", productionHash, productionJar) || !Matches("fixtureLauncher", launcherHash, launch.Jar))
                throw new InvalidDataException("The source receipt does not bind these engine/launcher bytes to the requested revision.");
        }
        return new(productionHash, launcherHash, Hash(typeof(CasualCommanderPolicy).Assembly.Location),
            Hash(typeof(ManaFundingPlanner).Assembly.Location), Hash(typeof(AiReleaseEvaluation).Assembly.Location), version,
            "Test-only pregame battlefield setup; production IPC, rules, controllers, observations, whole-seat host and policy. Natural games use the default table setup.",
            sourceRevision, sourceManifest != null ? "source-receipt-verified" : sourceRevision != null ? "locally-asserted-source" : "unbound-source",
            sourceManifest == null ? null : Hash(sourceManifest), launch.Resources,
            verifyPackage ? "exact-source-release-package-build-metadata" : "external-resources-not-bound-to-candidate",
            candidateBuild == null ? null : Hash(candidateBuild));
    }

    internal static async Task RunAsync(ForgePaths launch, string productionJar, string sourceRevision, string? sourceManifest = null)
    {
        Directory.CreateDirectory(launch.Profile);
        var gates = new List<AiReleaseGate>();
        ImmutableArray<AiGoldenResult> goldens = [];
        ImmutableArray<AiCampaignResult> games = [];
        AiReleaseRuntime? runtime = null;
        string? failure = null;
        Exception? primaryError = null;
        var elapsed = Stopwatch.StartNew();
        void Check(bool passed, string detail) {
            gates.Add(new("check-" + gates.Count, passed, detail));
            try { File.AppendAllText(Path.Combine(launch.Profile, "ai-release-progress.log"), (passed ? "PASS " : "FAIL ") + detail + "\n"); }
            catch (Exception error) { gates.Add(new("progress-evidence-write", false, error.ToString())); }
        }
        try {
            runtime = VerifyRuntime(launch, productionJar, verifyPackage: true, sourceRevision: sourceRevision, sourceManifest: sourceManifest);
            Check(true, "Production engine, test-only launcher and policy/harness bytes are bound separately; no fixture classes ship.");
            await using var engine = new ForgeEngine(launch);
            await engine.InitializeAsync();
            goldens = await AiReleaseGoldens.RunAsync(engine, launch.Profile, Check);
            Check(goldens.Length == 6 && goldens.All(result => result.Passed),
                "All six real whole-seat policy journeys completed with their required observed outcomes.");
            if (goldens.Length != 6 || goldens.Any(result => !result.Passed))
                throw new InvalidOperationException("A golden journey failed; do not reuse its engine for release games.");
            games = await AiEngineCampaign.RunAsync(engine, launch.Profile, AiCampaignLimits.Release, Check,
                launch with { Jar = productionJar });
            Check(games.Length == 4 && games.Select(game => game.Match).Order().SequenceEqual([0, 1, 2, 3])
                    && games.All(game => game.MechanicalFailure == null && game.Continuation.Conclusive
                        && game.Outcome is { CapturedBeforeCleanup: true, VerifiedNaturalOutcome: true } outcome
                        && outcome.Seats.Length == 4 && outcome.Seats.All(seat => seat.Developed)),
                "Every varied deck occupied every seat across four natural games, with outcomes captured before cleanup.");
            Check(games.Length == 4 && games.All(game => game.Evidence.StaleAuthorityProbes.Length == 4
                    && game.Evidence.StaleAuthorityProbes.Select(probe => probe.SeatId).Distinct().Count() == 4
                    && game.Evidence.StaleAuthorityProbes.All(probe => probe.ExplicitlyRejected)),
                "Each seat rejected one previously accepted command payload sent again under a fresh transport request after its scope changed.");
            Check(games.All(game => game.Evidence.Findings.IsEmpty && game.Evidence.DiagnosticErrors.IsEmpty),
                "No unexplained payment cancellations, skipped land offers, harmful self-target findings or evidence-write errors remain in these sampled games.");
            Check(games.All(game => !game.Evidence.NumericInvestments.Any(investment => investment.ZeroXNonpositiveBodyReview)),
                "No accepted zero-X announcement resolved into a publicly observed nonpositive graveyard body without documented review of its intent.");
        } catch (Exception error) {
            primaryError = error;
            failure = error.ToString();
            Check(false, "Evaluation interrupted: " + error.Message);
        }
        bool automatedReady = runtime != null && failure == null && gates.Count > 0 && gates.All(gate => gate.Passed);
        bool allGames = games.Length == 4;
        bool allGoldens = goldens.Length == 6 && goldens.All(result => result.Passed);
        bool qualityFindings = games.Any(game => !game.Evidence.Findings.IsEmpty
            || game.Evidence.NumericInvestments.Any(investment => investment.ZeroXNonpositiveBodyReview));
        bool stateIntegrity = runtime != null && failure == null && allGames
            && !gates.Any(gate => gate.Id == "progress-evidence-write")
            && games.All(game => game.MechanicalFailure == null && game.PublicComparisons > 0
                && game.Evidence.DiagnosticErrors.IsEmpty && game.Evidence.StaleAuthorityProbes.Length == 4
                && game.Evidence.StaleAuthorityProbes.Select(probe => probe.SeatId).Distinct().Count() == 4
                && game.Evidence.StaleAuthorityProbes.All(probe => probe.ExplicitlyRejected));
        var categories = new[] {
            new { id = "provider-conformance", status = runtime == null ? "failed" : goldens.IsEmpty ? "not-run" : goldens.Length == 6 && goldens.All(result => result.Passed) ? "passed" : "failed", evidence = "Runtime identities and real provider journeys; exact-source CI provider/rules reports remain a separate required check." },
            new { id = "policy-continuations", status = goldens.IsEmpty ? "not-run" : !allGoldens || qualityFindings ? "failed" : allGames ? "passed" : "not-run", evidence = "Six real whole-seat policy journeys and sampled payment/zero-body review findings; a finding requires review rather than a universal legality judgment." },
            new { id = "whole-game-completion", status = games.IsEmpty ? "not-run" : allGames && games.All(game => game.MechanicalFailure == null && game.Continuation.Conclusive && game.Outcome is { CapturedBeforeCleanup: true, VerifiedNaturalOutcome: true } outcome && outcome.Seats.Length == 4 && outcome.Seats.All(seat => seat.Developed)) ? "passed" : "failed", evidence = "Four cyclic varied-deck rotations; every seat develops and outcomes are captured before cleanup." },
            new { id = "state-authority-lifecycle", status = games.IsEmpty ? "not-run" : stateIntegrity ? "passed" : "failed", evidence = "Independent mechanical/privacy/public-parity, exact fresh stale challenges and diagnostic-write checks; strategic review findings are classified under policy." },
            new { id = "human-gameplay-review", status = "pending", evidence = "Review the exact candidate in actual play; automated correctness cannot supply approval." }
        };
        try { File.WriteAllText(Path.Combine(launch.Profile, "ai-release-evaluation.json"), JsonSerializer.Serialize(new {
            formatVersion = 1, audience = "host-private", automatedReady, releaseApproved = false,
            runtime, limits = AiCampaignLimits.Release, categories, checks = gates, goldens, games, failure,
            elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            humanGameplayReview = new { status = "pending", candidate = runtime,
                journeys = new[] { "Observe a varied four-player AI game for targeting, pacing and obvious blunders.",
                    "Play a real Commander deck against the candidate and review interrupted/required choices and stack clarity." },
                evidence = "Record the exact candidate, reviewed journeys and player feedback; numeric correctness is not human approval." },
            limitsOfEvidence = "Four stochastic games and targeted regressions are sampled evidence, not all Magic interactions, independent rules correctness or proof of enjoyable AI. Policy seeds do not control Forge randomness. Private replays must not be published."
        }, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception error) { throw new AggregateException("Release evidence could not be preserved.", new[] { primaryError, error }.OfType<Exception>()); }
        if (!automatedReady) throw new InvalidOperationException("AI release gates are not satisfied. See ai-release-evaluation.json and its exact private evidence.");
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
