using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mana.Table;

// The coordinator reserves a single immutable attempt before this process starts.
// A valid receipt certifies recorded evidence, never a policy win or command.
internal static class AiExperimentWorker
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HashSet<string> Statuses = ["completed", "setup-failure", "policy-failure", "provider-failure",
        "evidence-failure", "time-cutoff", "turn-cutoff", "interrupted"];

    internal static async Task<int> RunAsync(string[] args)
    {
        string? directory = null;
        JsonObject? identity = null;
        long captureBytes = 1024 * 1024;
        bool naturalMayHaveStarted = false;
        try {
            string lockPath = Path.GetFullPath(AiExperimentCommands.Argument(args, "--ai-experiment-worker"));
            string jobId = AiExperimentCommands.Argument(args, "--job-id");
            string token = AiExperimentCommands.Argument(args, "--reservation-token");
            directory = Path.GetFullPath(AiExperimentCommands.Argument(args, "--attempt-directory"));
            byte[] lockBytes = ReadBytes(lockPath, 64 * 1024 * 1024);
            using var locked = Parse(lockBytes);
            string lockHash = Convert.ToHexString(SHA256.HashData(lockBytes)).ToLowerInvariant();
            if (!Regex.IsMatch(jobId, "^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$") || string.IsNullOrWhiteSpace(token) || token.Length > 256)
                throw new InvalidDataException("Invalid experiment job or reservation identity.");
            string sidecar = Encoding.UTF8.GetString(ReadBytes(Path.Combine(Path.GetDirectoryName(lockPath)!, "lock.sha256"), 128)).Trim();
            if (!lockHash.Equals(sidecar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The experiment lock hash changed.");
            var root = locked.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("kind").GetString() != "ai-experiment-lock")
                throw new InvalidDataException("Unsupported experiment lock.");
            var jobs = root.GetProperty("jobs").EnumerateArray().Where(job => job.GetProperty("id").GetString() == jobId).ToArray();
            if (jobs.Length != 1) throw new InvalidDataException("The lock must identify exactly one planned job.");
            string expectedParent = Path.Combine(Path.GetDirectoryName(lockPath)!, "jobs", jobId);
            if (!Path.GetDirectoryName(directory)!.Equals(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(Path.GetFileName(directory), "^attempt-[0-9]{4}$"))
                throw new InvalidDataException("The worker must use its reserved job attempt directory.");
            using var reservation = Read(Path.Combine(directory, "reservation.json"), 2 * 1024 * 1024);
            var admitted = reservation.RootElement;
            if (admitted.GetProperty("schemaVersion").GetInt32() != 1
                || admitted.GetProperty("kind").GetString() != "ai-experiment-reservation"
                || admitted.GetProperty("lockSha256").GetString() != lockHash
                || admitted.GetProperty("jobId").GetString() != jobId
                || admitted.GetProperty("attemptId").GetString() != Path.GetFileName(directory)
                || admitted.GetProperty("reservationToken").GetString() != token)
                throw new InvalidDataException("The worker reservation does not match the locked job.");
            if (File.Exists(Path.Combine(directory, "result.json")) || File.Exists(Path.Combine(directory, "artifacts.json")))
                throw new InvalidDataException("An experiment attempt cannot overwrite a prior result.");
            ValidateArtifacts(root);
            captureBytes = root.GetProperty("limits").GetProperty("captureBytes").GetInt64();
            if (captureBytes < 1024 * 1024 || captureBytes > 1024L * 1024 * 1024)
                throw new InvalidDataException("Invalid bounded experiment capture allowance.");
            var admittedIdentity = new JsonObject {
                ["schemaVersion"] = 1, ["kind"] = "ai-experiment-result", ["lockSha256"] = lockHash,
                ["jobId"] = jobId, ["attemptId"] = Path.GetFileName(directory), ["reservationToken"] = token
            };
            // Even a duplicate spawn with the same valid reservation cannot
            // launch another game. A crashed claimant is never silently replaced.
            await using (var claim = new FileStream(Path.Combine(directory, "worker-claim.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] claimed = JsonSerializer.SerializeToUtf8Bytes(new { jobId, reservationToken = token,
                    processId = Environment.ProcessId, claimedAtUtc = DateTime.UtcNow }, Json);
                await claim.WriteAsync(claimed); claim.Flush(flushToDisk: true);
            }
            identity = admittedIdentity;
            var job = jobs[0];
            JsonObject payload;
            if (job.GetProperty("mode").GetString() == "replay") {
                var resolved = JsonNode.Parse(job.GetRawText())!.AsObject();
                string arm = job.GetProperty("arm").GetString()!;
                resolved["variant"] = JsonNode.Parse(root.GetProperty("variants").GetProperty(arm).GetRawText());
                payload = await AiExperimentReplay.RunAsync(JsonSerializer.SerializeToElement(resolved), root.GetProperty("limits"), directory);
            } else if (job.GetProperty("mode").GetString() == "natural") {
                naturalMayHaveStarted = true;
                payload = await AiExperimentNatural.RunAsync(root, job, directory);
            }
            else throw new InvalidDataException("Unsupported experiment worker mode.");
            string? status = payload["status"]?.GetValue<string>();
            if (status == null || !Statuses.Contains(status) || payload["started"] is not JsonValue
                || payload["criticalSafetyFailure"] is not JsonValue)
                throw new InvalidDataException("The experiment worker produced an incomplete result.");
            foreach (var field in payload.ToArray()) {
                if (identity.ContainsKey(field.Key)) throw new InvalidDataException("Worker payload cannot replace reservation identity.");
                identity[field.Key] = field.Value?.DeepClone();
            }
            await CommitAsync(directory, identity, captureBytes);
            return 0;
        } catch (Exception error) {
            // Only an authenticated, artifact-checked attempt may receive output.
            // Before that boundary a refusal must leave its reservation untouched.
            if (identity != null && directory != null) {
                identity["status"] = "evidence-failure"; identity["started"] = false;
                identity["criticalSafetyFailure"] = true;
                identity["details"] = new JsonObject { ["gameStartUncertain"] = naturalMayHaveStarted,
                    ["scope"] = "Unexpected worker failure; no known game start or terminal outcome is manufactured." };
                identity["error"] = error.GetType().Name + ": " + error.Message[..Math.Min(error.Message.Length, 2000)];
                try { await CommitAsync(directory, identity, Math.Max(captureBytes, 1024 * 1024)); }
                catch { /* The coordinator retains a missing/uncertain receipt. */ }
                return 3;
            }
            Console.Error.WriteLine("Experiment preflight refused: " + error.GetType().Name + ": " + error.Message);
            return 2;
        }
    }

    internal static JsonDocument Read(string path, int maximumBytes)
        => Parse(ReadBytes(path, maximumBytes));

    private static byte[] ReadBytes(string path, int maximumBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximumBytes) throw new InvalidDataException("Experiment JSON exceeds its bounded input size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Experiment input changed while reading.");
        return bytes;
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        try { Distinct(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void Distinct(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate experiment JSON property.");
                Distinct(property.Value);
            }
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Distinct(item);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out double number) || !double.IsFinite(number)))
            throw new InvalidDataException("Experiment numeric values must be finite.");
    }

    internal static void ValidateArtifacts(JsonElement locked)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in locked.GetProperty("artifacts").EnumerateArray()) {
            string path = Path.GetFullPath(artifact.GetProperty("path").GetString()!);
            if (!expected.Add(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(path).Length != artifact.GetProperty("bytes").GetInt64()
                || !Hash(path).Equals(artifact.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An experiment artifact changed or has ambiguous identity.");
            if (expected.Count > 250000) throw new InvalidDataException("Experiment artifact count is bounded.");
        }
        string driver = Path.GetFullPath(locked.GetProperty("runtime").GetProperty("driverPath").GetString()!);
        if (!driver.Equals(Path.GetFullPath(typeof(AiExperimentWorker).Assembly.Location), StringComparison.OrdinalIgnoreCase)
            || !expected.Contains(driver)) throw new InvalidDataException("The current worker is not the locked harness.");
        if (locked.GetProperty("runtime").TryGetProperty("sourceRevision", out var source)) {
            string version = typeof(AiExperimentWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            if (source.ValueKind != JsonValueKind.String || !version.EndsWith("+" + source.GetString(), StringComparison.Ordinal))
                throw new InvalidDataException("The native harness build does not match its asserted source revision.");
        }
        var roots = locked.GetProperty("closureRoots").EnumerateArray().Select(item => Path.GetFullPath(item.GetString()!)).ToArray();
        if (!roots.Contains(Path.GetDirectoryName(driver)!, StringComparer.OrdinalIgnoreCase)
            || locked.GetProperty("runtime").TryGetProperty("resourcesPath", out var resources)
                && !roots.Contains(Path.GetFullPath(resources.GetString()!), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The complete harness and resources directories must be pinned.");
        foreach (string root in roots)
            foreach (string file in Files(root))
                if (!expected.Contains(Path.GetFullPath(file))) throw new InvalidDataException("An unpinned artifact appeared in the experiment closure.");
    }

    private static IEnumerable<string> Files(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("An experiment directory cannot be a linked/reparse-point tree.");
        foreach (string file in Directory.EnumerateFiles(directory)) {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked experiment files are unsupported.");
            yield return file;
        }
        foreach (string child in Directory.EnumerateDirectories(directory)) foreach (string file in Files(child)) yield return file;
    }

    internal static async Task CommitAsync(string directory, JsonObject result, long maximumBytes)
    {
        string resultPath = Path.Combine(directory, "result.json");
        if (File.Exists(resultPath)) throw new IOException("A prior result cannot be overwritten.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("Worker result exceeded its bounded size.");
        var files = new List<object>();
        long total = bytes.LongLength;
        files.Add(new { path = "result.json", bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        foreach (string path in Files(directory)) {
            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (relative is "result.json" or "artifacts.json" || relative.EndsWith(".tmp", StringComparison.Ordinal)
                || Path.GetFileName(relative).StartsWith("coordinator-", StringComparison.Ordinal)) continue;
            long length = new FileInfo(path).Length;
            total = checked(total + length);
            if (total > maximumBytes || files.Count >= 10000) throw new IOException("Experiment evidence exceeded its capture allowance.");
            files.Add(new { path = relative, bytes = length, sha256 = Hash(path) });
        }
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "ai-experiment-artifacts", files }, Json);
        if (receipt.LongLength > 2 * 1024 * 1024 || total + receipt.LongLength > maximumBytes)
            throw new IOException("The artifact manifest cannot exceed its own or the total capture allowance.");
        await AtomicAsync(resultPath, bytes);
        await AtomicAsync(Path.Combine(directory, "artifacts.json"), receipt);
    }

    private static async Task AtomicAsync(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                await file.WriteAsync(bytes); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
