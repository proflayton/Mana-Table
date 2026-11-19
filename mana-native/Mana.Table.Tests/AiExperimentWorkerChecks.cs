using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mana.Table;

internal static class AiExperimentWorkerChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "experiment-worker-boundary");
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "strict-input.json");
        foreach (string invalid in new[] { "{\"id\":1,\"id\":2}", "{\"n\":1e9999}",
            new string('[', 65) + "0" + new string(']', 65) }) {
            File.WriteAllText(input, invalid);
            bool refused = false; try { using var value = AiExperimentWorker.Read(input, 1024); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { refused = true; }
            check(refused, "Experiment admission refuses ambiguous, nonfinite and excessively nested JSON");
        }
        File.WriteAllText(input, "{\"seed\":\"2668620592559112008\"}");
        using (var value = AiExperimentWorker.Read(input, 1024))
            check(value.RootElement.GetProperty("seed").GetString() == "2668620592559112008",
                "The experiment boundary retains the captured 64-bit seed as exact decimal text");
        bool oversized = false; try { using var value = AiExperimentWorker.Read(input, 4); }
        catch (InvalidDataException) { oversized = true; }
        check(oversized, "An experiment input cannot exceed its predeclared byte allowance");

        string driver = typeof(AiExperimentWorkerChecks).Assembly.Location;
        string driverDirectory = Path.GetDirectoryName(driver)!;
        var artifacts = Directory.GetFiles(driverDirectory, "*", SearchOption.AllDirectories).Select(file => new {
            path = Path.GetFullPath(file), bytes = new FileInfo(file).Length, sha256 = AiExperimentWorker.Hash(file)
        }).ToArray();
        const string jobId = "duplicate-worker";
        var locked = JsonSerializer.SerializeToNode(new {
            schemaVersion = 1, kind = "ai-experiment-lock", runtime = new { driverPath = driver },
            closureRoots = new[] { driverDirectory }, artifacts,
            limits = new { captureBytes = 1024 * 1024 },
            jobs = new[] { new { id = jobId, mode = "replay", arm = "baseline" } }
        })!.AsObject();
        AiExperimentWorker.ValidateArtifacts(JsonSerializer.SerializeToElement(locked));
        check(true, "Native worker admission accepts its actual complete immutable harness closure");
        var changed = locked.DeepClone();
        changed["artifacts"]![0]!["sha256"] = new string('0', 64);
        bool tamper = false; try { AiExperimentWorker.ValidateArtifacts(JsonSerializer.SerializeToElement(changed)); }
        catch (InvalidDataException) { tamper = true; }
        check(tamper, "Changed worker artifact bytes cannot inherit a prior experiment identity");

        string experiment = Path.Combine(directory, "reserved-experiment");
        string attempt = Path.Combine(experiment, "jobs", jobId, "attempt-0001");
        Directory.CreateDirectory(attempt);
        string lockPath = Path.Combine(experiment, "lock.json");
        File.WriteAllText(lockPath, locked.ToJsonString());
        string lockHash = AiExperimentWorker.Hash(lockPath);
        File.WriteAllText(Path.Combine(experiment, "lock.sha256"), lockHash);
        const string token = "retained-active-worker-token";
        File.WriteAllText(Path.Combine(attempt, "reservation.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, kind = "ai-experiment-reservation", lockSha256 = lockHash,
            jobId, attemptId = "attempt-0001", reservationToken = token
        }));
        string claim = Path.Combine(attempt, "worker-claim.json");
        File.WriteAllText(claim, "Existing owner's immutable claim");
        string claimHash = AiExperimentWorker.Hash(claim);
        int status = await AiExperimentWorker.RunAsync(["--ai-experiment-worker", lockPath, "--job-id", jobId,
            "--attempt-directory", attempt, "--reservation-token", token]);
        check(status == 2 && !File.Exists(Path.Combine(attempt, "result.json"))
            && !File.Exists(Path.Combine(attempt, "artifacts.json")) && AiExperimentWorker.Hash(claim) == claimHash,
            "A duplicate valid reservation cannot launch another worker or steal the first worker's result ownership");
        int foreign = await AiExperimentWorker.RunAsync(["--ai-experiment-worker", lockPath, "--job-id", jobId,
            "--attempt-directory", attempt, "--reservation-token", "different-token"]);
        check(foreign == 2 && AiExperimentWorker.Hash(claim) == claimHash,
            "A foreign reservation is refused before changing an active attempt");

        string nearLimit = Path.Combine(directory, "receipt-boundary");
        Directory.CreateDirectory(nearLimit);
        File.WriteAllBytes(Path.Combine(nearLimit, "payload.bin"), new byte[900]);
        bool manifestOverflow = false;
        try { await AiExperimentWorker.CommitAsync(nearLimit, new JsonObject { ["status"] = "completed" }, 1024); }
        catch (IOException) { manifestOverflow = true; }
        check(manifestOverflow && !File.Exists(Path.Combine(nearLimit, "result.json"))
            && !File.Exists(Path.Combine(nearLimit, "artifacts.json")),
            "A near-cap payload cannot publish a receipt whose own manifest exceeds the same evidence allowance");
        await AiExperimentWorker.CommitAsync(nearLimit, new JsonObject { ["status"] = "completed" }, 4096);
        check(Directory.GetFiles(nearLimit).Sum(path => new FileInfo(path).Length) <= 4096
            && File.Exists(Path.Combine(nearLimit, "artifacts.json")),
            "A bounded worker receipt accounts for payload, result and manifest bytes together");
        bool unknownOption = false;
        try { AiExperimentCommands.Run(["--ai-experiment-worker", lockPath, "--job-id", jobId,
            "--attempt-directory", attempt, "--reservation-token", token, "--unexpected", "value"]); }
        catch (ArgumentException) { unknownOption = true; }
        check(unknownOption, "The internal worker refuses unknown arguments before entering its reserved execution path");
    }
}
