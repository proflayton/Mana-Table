using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Offline local comparisons only. The coordinator owns lock/reservation/artifact
// identity and result publication; this helper cannot execute a rules command.
internal static class AiExperimentReplay
{
    internal const string Evaluator = "current-scoped-choice.v1";
    private const int MaximumInputBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<JsonObject> RunAsync(JsonElement job, JsonElement limits, string attemptDirectory)
    {
        AiDecisionReplayCase recorded;
        AiDecisionFrame frame;
        IAiPolicy policy;
        string kind, family, source, sourceHash, rawHash, historyBasis;
        bool certifiedUndo = false;
        string? capabilityContext = null;
        try {
            Unique(job); Unique(limits);
            policy = AiExperimentPolicyRegistry.Create(AiExperimentPolicyRegistry.Parse(job.GetProperty("variant")));
            var input = job.GetProperty("replay");
            source = input.GetProperty("path").GetString() ?? throw Invalid("Missing replay path.");
            if (!Path.IsPathFullyQualified(source) || source.StartsWith("\\\\", StringComparison.Ordinal)) throw Invalid("Replay paths must identify locked local files.");
            sourceHash = input.GetProperty("sha256").GetString() ?? "";
            if (sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit)) throw Invalid("Replay hash must be SHA256 hexadecimal.");
            family = input.GetProperty("family").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(family)) throw Invalid("A replay requires its declared family.");
            kind = input.GetProperty("expectation").GetProperty("kind").GetString() ?? "";
            if (kind is not ("target-self" or "target-opponent" or "cancel" or "complete" or "admissible" or "ungraded")) throw Invalid("Unsupported replay evaluator.");
            double milliseconds = limits.GetProperty("decisionMs").GetDouble();
            int work = limits.GetProperty("workUnits").GetInt32();
            if (!double.IsFinite(milliseconds) || milliseconds <= 0 || milliseconds > uint.MaxValue - 1 || work <= 0)
                throw Invalid("Invalid decision limits.");
            var allowance = new AiSeatLimits(TimeSpan.FromMilliseconds(milliseconds), work);
            if (new FileInfo(source).Length > MaximumInputBytes) throw Invalid("Replay input exceeds the supported byte limit.");
            byte[] stored = await File.ReadAllBytesAsync(source);
            if (stored.Length > MaximumInputBytes || !Hash(stored).Equals(sourceHash, StringComparison.OrdinalIgnoreCase)) throw Invalid("Replay bytes do not match their locked hash.");
            byte[] raw = await ExpandAsync(source, stored);
            rawHash = Hash(raw);
            using var document = JsonDocument.Parse(raw);
            Unique(document.RootElement);
            string? format = input.GetProperty("format").GetString();
            if (format == "replay") {
                recorded = document.RootElement.Deserialize<AiDecisionReplayCase>(Json) ?? throw Invalid("Empty captured replay.");
                if (allowance.DecisionTime < recorded.RemainingTime || allowance.WorkUnits < recorded.RemainingWork)
                    throw Invalid("Declared limits are lower than the captured allowance; v1 requires the exact original replay allowance.");
                historyBasis = "preserved captured history, plan, seed and remaining allowance";
            } else if (format == "owned") {
                if (input.TryGetProperty("certifiedUndo", out var undo)) {
                    if (undo.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("certifiedUndo must be an explicit boolean.");
                    certifiedUndo = undo.GetBoolean();
                }
                if (input.TryGetProperty("capabilityContext", out var context)) capabilityContext = context.GetString();
                bool hasUndo = document.RootElement.TryGetProperty("state", out var state)
                    && state.TryGetProperty("undo", out var undoState) && undoState.ValueKind != JsonValueKind.Null;
                if (hasUndo && (!certifiedUndo || string.IsNullOrWhiteSpace(capabilityContext)))
                    throw Invalid("Undo-bearing owned input requires an explicit certifiedUndo capability context; its presence is not a handshake.");
                var observation = ForgeEngine.DecodeOwnedDecisionObservation(document.RootElement, certifiedUndo)
                    ?? throw Invalid("Empty owned observation.");
                var seedText = job.GetProperty("seed").GetString() ?? "";
                if (!DecimalSeed(seedText, out long seed)) throw Invalid("Owned replay seed must be an exact signed 64-bit decimal string.");
                recorded = new(1, "owned-envelope-diagnostic", "owned-envelope-diagnostic.v1", observation.State,
                    observation.Domain, observation.Semantics, new([], []) {
                        RestorationVersion = observation.State.Undo?.RestorationVersion ?? 0
                    }, seed) { RemainingTime = allowance.DecisionTime, RemainingWork = allowance.WorkUnits };
                historyBasis = "empty authored diagnostic history; actual provider-owned frame";
            } else throw Invalid("Unsupported replay input format.");
            frame = new(recorded.State, domain: recorded.Domain, semantics: recorded.Semantics);
            if (kind != "ungraded" && !Eligible(frame, kind)) throw Invalid("The original frame has no legal opportunity for the declared evaluator.");
        } catch (Exception error) when (error is ArgumentException or JsonException or IOException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException) {
            return Incompatible(error.Message);
        }

        byte[] admitted = JsonSerializer.SerializeToUtf8Bytes(recorded, Json);
        if (limits.TryGetProperty("captureBytes", out var capture) && admitted.LongLength > capture.GetInt64())
            return Incompatible("The declared evidence allowance cannot retain the admitted replay.");
        Directory.CreateDirectory(attemptDirectory);
        await File.WriteAllBytesAsync(Path.Combine(attemptDirectory, "replay-input.json"), admitted);
        var timer = Stopwatch.StartNew();
        AiDecisionReplayResult result;
        try { result = await AiDecisionReplay.EvaluateAsync(recorded, policy); }
        catch (ArgumentException error) { return Incompatible(error.Message); }
        timer.Stop();
        var grade = EvaluateGrade(frame, kind, result);
        bool admissible = grade["admissible"]!.GetValue<bool>();
        bool invalid = result.Status == AiSeatStatus.Invalid || result.Proposal != null && !frame.Allows(result.Proposal);
        return new JsonObject {
            ["status"] = admissible ? "completed" : "policy-failure", ["started"] = false,
            ["criticalSafetyFailure"] = invalid, ["replay"] = grade,
            ["metrics"] = new JsonObject {
                ["evaluationWallMs"] = timer.Elapsed.TotalMilliseconds,
                ["episodeElapsedMs"] = result.Evidence?.Elapsed.TotalMilliseconds,
                ["grantedWorkUnits"] = recorded.RemainingWork,
                ["remainingWorkUnits"] = result.Evidence?.RemainingWork,
                ["grantedDecisionMs"] = recorded.RemainingTime.TotalMilliseconds,
                ["spentWorkUnits"] = result.Evidence is { } evidence ? recorded.RemainingWork - evidence.RemainingWork : null
            },
            ["details"] = new JsonObject {
                ["incompatible"] = false, ["family"] = family, ["expectation"] = kind,
                ["source"] = source, ["sourceSha256"] = sourceHash.ToUpperInvariant(), ["rawSha256"] = rawHash,
                ["admittedReplaySha256"] = Hash(admitted), ["historyBasis"] = historyBasis,
                ["seed"] = recorded.Seed.ToString(CultureInfo.InvariantCulture),
                ["capturedPolicyId"] = recorded.PolicyId, ["evaluatedPolicyId"] = result.PolicyId,
                ["policyStatus"] = result.Status.ToString(), ["command"] = result.Proposal?.GetType().Name,
                ["proposal"] = result.Evidence?.AnswerEvidence is { } answer ? JsonNode.Parse(answer.GetRawText()) : null,
                ["failure"] = JsonSerializer.SerializeToNode(result.Evidence?.Failure, Json),
                ["certifiedUndoContext"] = certifiedUndo, ["capabilityContext"] = capabilityContext,
                ["observedHandshake"] = false, ["executed"] = false
            }
        };
    }

    internal static JsonObject EvaluateGrade(AiDecisionFrame frame, string kind, AiDecisionReplayResult result)
    {
        bool eligible = Eligible(frame, kind);
        bool admissible = result.Status == AiSeatStatus.Ready && result.Proposal != null && frame.Allows(result.Proposal);
        bool passes = admissible && kind switch {
            "admissible" => true,
            "cancel" => result.Proposal is CancelSelection or CancelPayment,
            "complete" => result.Proposal is CompleteSelection,
            "target-self" => Controller(frame.State, result.Proposal) == frame.State.ViewerId,
            "target-opponent" => Controller(frame.State, result.Proposal) is { } player
                && PlayerRelations.ForViewer(frame.State, player) == PlayerRelation.Opponent,
            _ => false
        };
        return new() { ["grade"] = !eligible ? "ungraded" : passes ? "pass" : "fail", ["eligible"] = eligible,
            ["admissible"] = admissible, ["evaluator"] = Evaluator };
    }

    private static bool Eligible(AiDecisionFrame frame, string kind)
    {
        string game = frame.State.Id, decision = frame.State.Decision!.Id;
        if (kind == "ungraded") return false;
        if (kind == "admissible") return true;
        if (kind == "cancel") return frame.Allows(new CancelSelection(game, decision)) || frame.Allows(new CancelPayment(game, decision));
        if (kind == "complete") return frame.Allows(new CompleteSelection(game, decision));
        if (kind is not ("target-self" or "target-opponent")) return false;
        bool Matches(int player) => kind == "target-self" ? player == frame.State.ViewerId
            : PlayerRelations.ForViewer(frame.State, player) == PlayerRelation.Opponent;
        if (CardViews.InZones(frame.State).Any(row => !row.Card.Highlighted && Matches(row.PlayerId)
                && frame.Allows(new SelectCard(game, decision, row.Card.Key)))) return true;
        return frame.State.Players.Any(player => !frame.Semantics.SelectedPlayers.Contains(player.Id) && Matches(player.Id)
            && frame.Allows(new SelectPlayer(game, decision, player.Id)));
    }

    private static int? Controller(GameSnapshot state, DecisionCommand? command)
    {
        if (command is SelectPlayer player) return player.Player;
        if (command is not SelectCard card) return null;
        var matches = CardViews.InZones(state).Where(row => row.Card.Key == card.Card).ToArray();
        return matches.Length == 1 ? matches[0].PlayerId : null;
    }

    private static JsonObject Incompatible(string reason) => new() {
        ["status"] = "setup-failure", ["started"] = false, ["criticalSafetyFailure"] = false,
        ["replay"] = new JsonObject { ["grade"] = "ungraded", ["eligible"] = false, ["admissible"] = false, ["evaluator"] = Evaluator },
        ["metrics"] = new JsonObject(),
        ["details"] = new JsonObject { ["incompatible"] = true, ["executed"] = false }, ["error"] = reason
    };

    private static bool DecimalSeed(string value, out long seed)
    {
        seed = 0;
        int start = value.StartsWith('-') ? 1 : 0;
        return value.Length > start && value[start..].All(character => character is >= '0' and <= '9')
            && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed);
    }

    private static async Task<byte[]> ExpandAsync(string path, byte[] stored)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return stored;
        using var source = new MemoryStream(stored);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await gzip.ReadAsync(buffer)) > 0) {
            if (output.Length + read > MaximumInputBytes) throw Invalid("Expanded replay exceeds the supported byte limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) {
                if (!names.Add(property.Name)) throw Invalid("Duplicate replay or job property.");
                Unique(property.Value);
            }
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static ArgumentException Invalid(string reason) => new(reason);
}
