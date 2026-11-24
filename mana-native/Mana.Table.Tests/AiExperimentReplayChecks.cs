using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class AiExperimentReplayChecks
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } };

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        string output = Path.GetFullPath(Path.Combine(profile, "experiment-replay-checks"));
        Directory.CreateDirectory(output);
        int sequence = 0;
        Task<JsonObject> Run(JsonObject job, JsonElement? limits = null)
            => AiExperimentReplay.RunAsync(JsonSerializer.SerializeToElement(job), limits ?? Limits(), Path.Combine(output, "attempt-" + ++sequence));
        var positive = Job("attachment-rancor", "target-self");
        var full = await Run(positive);
        var repeated = await Run(positive);
        check(Grade(full) == "pass" && Grade(repeated) == "pass"
                && full["details"]!["proposal"] != null && JsonNode.DeepEquals(full["details"]!["proposal"], repeated["details"]!["proposal"])
                && full["details"]!["admittedReplaySha256"]!.ToString() == repeated["details"]!["admittedReplaySha256"]!.ToString(),
            "Exact owned-frame A/A evaluations agree on proposal, input identity and independently graded useful targeting");
        var ablated = positive.DeepClone().AsObject(); ablated["variant"]!["attachmentEvaluation"] = false;
        var disabled = await Run(ablated);
        check(disabled["status"]!.ToString() == "completed" && Grade(disabled) == "fail"
                && disabled["replay"]!["eligible"]!.GetValue<bool>() && disabled["replay"]!["admissible"]!.GetValue<bool>()
                && disabled["details"]!["command"]!.ToString() == nameof(CancelSelection)
                && disabled["details"]!["admittedReplaySha256"]!.ToString() == full["details"]!["admittedReplaySha256"]!.ToString(),
            "The weaker positive control remains an eligible failed useful-action grade despite returning an admissible cancellation on identical facts");
        foreach (var item in new[] { ("attachment-renamed-positive", "target-self"), ("attachment-dead-weight", "target-opponent"),
            ("attachment-mixed", "cancel"), ("attachment-conditional", "cancel") }) {
            var result = await Run(Job(item.Item1, item.Item2));
            check(Grade(result) == "pass" && result["started"]!.GetValue<bool>() == false
                    && !result.ContainsKey("outcome") && !result.ContainsKey("proposal") && !result["details"]!["executed"]!.GetValue<bool>(),
                "An independent typed replay grade preserves its local, non-game scope: " + item.Item1);
        }
        check(full["details"]!["seed"]!.ToString() == "9007199254740993"
                && full["details"]!["historyBasis"]!.ToString().StartsWith("empty authored diagnostic history", StringComparison.Ordinal)
                && !full["details"]!["observedHandshake"]!.GetValue<bool>(),
            "Owned diagnostic evidence retains an exact large decimal seed and identifies empty history and unobserved handshake honestly");

        foreach (var change in new Action<JsonObject>[] {
            job => job["replay"]!["sha256"] = new string('0', 64),
            job => job["variant"]!["factoryId"] = "unknown",
            job => job["variant"]!["extra"] = true,
            job => job["replay"]!["certifiedUndo"] = false,
            job => job["replay"]!.AsObject().Remove("capabilityContext"),
            job => job["seed"] = "9223372036854775808",
            job => job["seed"] = 17,
            job => job["replay"]!["expectation"]!["kind"] = "utility-from-card-name"
        }) {
            var invalid = positive.DeepClone().AsObject(); change(invalid);
            var result = await Run(invalid);
            check(result["status"]!.ToString() == "setup-failure" && result["details"]!["incompatible"]!.GetValue<bool>()
                    && Grade(result) == "ungraded" && !result["started"]!.GetValue<bool>(),
                "Replay preflight refuses mismatched artifacts, variants, capability context, seeds and unsupported evaluators without dropping the planned job");
        }

        var admittedPath = Path.Combine(output, "attempt-1", "replay-input.json");
        var captured = JsonSerializer.Deserialize<AiDecisionReplayCase>(await File.ReadAllTextAsync(admittedPath), Json)!;
        var frame = new AiDecisionFrame(captured.State, domain: captured.Domain, semantics: captured.Semantics);
        captured = captured with { Id = "retained-history-fixture", PolicyId = "captured-policy.v1", Seed = long.MinValue + 1,
            RemainingTime = TimeSpan.FromTicks(12345678), RemainingWork = 777, PlanVersion = 3,
            History = captured.History with { Events = [new(1, captured.State.Turn, "turn", captured.State.ViewerId, null, null, null, null, null)] },
            Plan = new(captured.State.Id, captured.State.ViewerId, captured.State.Turn, 3, AiPlanPurpose.Develop, [], []) { CreatedFrom = frame.Stamp }
        };
        string capturedPath = Path.Combine(output, "captured-replay.json");
        await File.WriteAllTextAsync(capturedPath, JsonSerializer.Serialize(captured, Json));
        var jobReplay = positive.DeepClone().AsObject();
        jobReplay["seed"] = "9223372036854775807";
        jobReplay["replay"]!["path"] = capturedPath;
        jobReplay["replay"]!["sha256"] = Hash(await File.ReadAllBytesAsync(capturedPath));
        jobReplay["replay"]!["format"] = "replay";
        int originalAttempt = sequence + 1;
        var replayed = await Run(jobReplay);
        var retained = JsonSerializer.Deserialize<AiDecisionReplayCase>(await File.ReadAllTextAsync(Path.Combine(output, "attempt-" + originalAttempt, "replay-input.json")), Json)!;
        check(Grade(replayed) == "pass" && replayed["details"]!["seed"]!.ToString() == (long.MinValue + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                && replayed["metrics"]!["grantedWorkUnits"]!.GetValue<int>() == 777 && retained.RemainingTime.Ticks == 12345678
                && JsonSerializer.Serialize(captured.History, Json) == JsonSerializer.Serialize(retained.History, Json)
                && captured.PlanVersion == retained.PlanVersion && captured.Plan == retained.Plan,
            "A captured replay keeps its exact original seed, fractional allowance, history and plan instead of borrowing the job seed or larger limits");
        check((await Run(jobReplay, Limits(work: 776)))["status"]!.ToString() == "setup-failure"
                && (await Run(jobReplay, Limits(milliseconds: 1000)))["status"]!.ToString() == "setup-failure",
            "A lower declared work/time ceiling refuses an exact-replay job rather than renewing or silently changing its captured treatment");

        var illegal = new SelectCard(frame.State.Id, "retired-input", "invented-key");
        var invalidGrade = AiExperimentReplay.EvaluateGrade(frame, "target-self", new("forged", "test", AiSeatStatus.Ready, illegal, null));
        check(invalidGrade["eligible"]!.GetValue<bool>() && !invalidGrade["admissible"]!.GetValue<bool>() && invalidGrade["grade"]!.ToString() == "fail",
            "An illegal proposal remains an eligible failed grade and cannot gain credit from a read-only identity or stale scope");
        var enemy = CardViews.InZones(frame.State).First(row => PlayerRelations.ForViewer(frame.State, row.PlayerId) == PlayerRelation.Opponent && row.Card.Selectable);
        var wrongTarget = new SelectCard(frame.State.Id, frame.State.Decision!.Id, enemy.Card.Key);
        check(AiExperimentReplay.EvaluateGrade(frame, "target-self", new("wrong-controller", "test", AiSeatStatus.Ready, wrongTarget, null))["grade"]!.ToString() == "fail"
                && AiExperimentReplay.EvaluateGrade(frame, "target-opponent", new("right-controller", "test", AiSeatStatus.Ready, wrongTarget, null))["grade"]!.ToString() == "pass",
            "Target grades follow actual current controller and typed team relation rather than card wording or the candidate's utility");
        var noAnswer = AiExperimentReplay.EvaluateGrade(frame, "target-self", new("unavailable", "test", AiSeatStatus.Unavailable, null, null));
        check(noAnswer["eligible"]!.GetValue<bool>() && noAnswer["grade"]!.ToString() == "fail",
            "A missing policy answer cannot erase original opportunity eligibility from the paired denominator");
        var ungraded = positive.DeepClone().AsObject(); ungraded["replay"]!["expectation"]!["kind"] = "ungraded";
        var ungradedResult = await Run(ungraded);
        check(Grade(ungradedResult) == "ungraded" && !ungradedResult["replay"]!["eligible"]!.GetValue<bool>()
                && ungradedResult["status"]!.ToString() == "completed",
            "An explicitly ungraded legal proposal stays visible without inventing a tactical verdict");
        AiLibraryAcquisitionGradeChecks.Run(check);
    }

    private static JsonObject Job(string fixture, string expectation)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AttachmentOwned", fixture + ".json.gz");
        return new() {
            ["seed"] = "9007199254740993",
            ["variant"] = new JsonObject { ["factoryId"] = "casual-commander.v6", ["attachmentEvaluation"] = true },
            ["replay"] = new JsonObject { ["path"] = path, ["sha256"] = Hash(File.ReadAllBytes(path)), ["format"] = "owned",
                ["family"] = "attachment-development", ["expectation"] = new JsonObject { ["kind"] = expectation },
                ["certifiedUndo"] = true, ["capabilityContext"] = "Pinned synthetic provider fixture; declared certified-one-step-undo; no observed handshake." }
        };
    }
    private static JsonElement Limits(int work = 4096, int milliseconds = 3000) => JsonSerializer.SerializeToElement(new {
        decisionMs = milliseconds, workUnits = work, matchMs = 180000, turns = 160, workerMs = 30000, captureBytes = 8 * 1024 * 1024
    });
    private static string Grade(JsonObject result) => result["replay"]!["grade"]!.GetValue<string>();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
