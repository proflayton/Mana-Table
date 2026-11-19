using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Diagnostic acceptance of actual provider-owned envelopes. Fixture IDs select
// independent expectations; card names and display text supply no semantics.
internal static class AttachmentOwnedChecks
{
    // The Java journeys call the current provider in-process rather than
    // capturing a transport hello. That same provider's DesktopEngine ready
    // declaration includes certified-one-step-undo and owned-decision-observation.
    private const string CapabilityBasis = "Direct in-process provider fixtures; same-provider DesktopEngine.java:96 declares certified-one-step-undo and owned-decision-observation. No transport handshake was observed by this runner.";
    private enum Expected { Friendly, Opposing, Cancel }
    private static readonly (string Id, Expected Choice)[] Cases = [
        ("attachment-rancor", Expected.Friendly),
        ("attachment-renamed-positive", Expected.Friendly),
        ("attachment-dead-weight", Expected.Opposing),
        ("attachment-mixed", Expected.Cancel),
        ("attachment-conditional", Expected.Cancel)
    ];
    private static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void RunPinned(string profile, Action<bool, string> check)
        => RunAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AttachmentOwned"),
            Path.Combine(profile, "attachment-owned"), check).GetAwaiter().GetResult();

    internal static async Task RunAsync(string directory, string profile, Action<bool, string>? observer = null)
    {
        directory = Path.GetFullPath(directory); profile = Path.GetFullPath(profile);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (Path.TrimEndingDirectorySeparator(directory).Equals(Path.TrimEndingDirectorySeparator(profile), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a separate explicit output profile so the provider capture directory stays unchanged.");
        Directory.CreateDirectory(profile);
        int checks = 0;
        var results = new List<object>();
        string? failure = null;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidDataException(description);
            checks++;
            observer?.Invoke(true, description);
        }
        try {
            foreach (var fixture in Cases) {
                string source = Path.Combine(directory, fixture.Id + ".json");
                if (!File.Exists(source)) source += ".gz";
                byte[] stored = await File.ReadAllBytesAsync(source);
                byte[] raw = await RawEnvelopeAsync(source, stored);
                string hash = Convert.ToHexString(SHA256.HashData(raw));
                string storedHash = Convert.ToHexString(SHA256.HashData(stored));
                string output = Path.Combine(profile, fixture.Id);
                Directory.CreateDirectory(output);
                await File.WriteAllBytesAsync(Path.Combine(output, "provider-owned-envelope.json"), raw);
                await File.WriteAllTextAsync(Path.Combine(output, "source-provenance.json"), JsonSerializer.Serialize(new {
                    fixture.Id, source, sha256 = hash, storedSha256 = storedHash,
                    certifiedUndo = true, capabilityBasis = CapabilityBasis, observedHandshake = false
                }, Json));
                using var document = JsonDocument.Parse(raw);
                bool unnegotiatedRejected = false;
                try { ForgeEngine.DecodeOwnedDecisionObservation(document.RootElement, certifiedUndo: false); }
                catch (IOException) { unnegotiatedRejected = true; }
                Check(unnegotiatedRejected, fixture.Id + ": the actual Undo-bearing envelope is rejected without the provider capability context.");
                var observation = ForgeEngine.DecodeOwnedDecisionObservation(document.RootElement, certifiedUndo: true)
                    ?? throw new InvalidDataException(fixture.Id + " did not contain an owned observation.");
                var frame = Frame(observation);
                var semantics = observation.Semantics;
                var node = semantics.OperationGraph.Operations.Single(operation => operation.Id == semantics.OperationId);
                Check(node.Effect == ActionEffectKind.Attach && node.Attachment is { IsValid: true }
                    && node.Sources is { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.DecisionSource, Entities.Length: 0 }
                    && AttachmentModifier.IsCurrentDecisionSource(observation.State, node.AttachmentSourceCardId, node.ActorPlayerId),
                    fixture.Id + ": native admission binds the actual active attachment to its read-only decision portrait.");
                Check(!frame.Allows(new SelectCard(frame.State.Id, frame.State.Decision!.Id, node.AttachmentSourceCardId)),
                    fixture.Id + ": source provenance confers no target selection handle.");
                Check(CardViews.InZones(frame.State).Any(row => row.PlayerId == frame.State.ViewerId && row.Card.Selectable)
                        && CardViews.InZones(frame.State).Any(row => PlayerRelations.ForViewer(frame.State, row.PlayerId) == PlayerRelation.Opponent && row.Card.Selectable),
                    fixture.Id + ": actual friendly and opposing recipients are both currently offered.");
                bool known = fixture.Choice != Expected.Cancel;
                Check(node.Attachment!.Knowledge == (known ? SemanticKnowledge.Known : SemanticKnowledge.Unknown)
                        && semantics.OperationGraph.Knowledge == (known ? SemanticKnowledge.Known : SemanticKnowledge.Unknown),
                    fixture.Id + ": mechanical coverage retains the independently expected supported or unsupported scope.");

                var policy = new CasualCommanderPolicy();
                // This is an actual admitted frame with explicitly empty authored
                // diagnostic history, not a captured campaign policy request.
                var recorded = new AiDecisionReplayCase(1, fixture.Id, policy.Descriptor.Id,
                    observation.State, observation.Domain, semantics,
                    new([], []) { RestorationVersion = observation.State.Undo?.RestorationVersion ?? 0 }, 17L) {
                        RemainingTime = TimeSpan.FromSeconds(3), RemainingWork = 4096
                    };
                string replayPath = Path.Combine(output, "admitted-replay.json");
                await File.WriteAllTextAsync(replayPath, JsonSerializer.Serialize(recorded, Json));
                var result = await AiDecisionReplay.EvaluateAsync(recorded, policy);
                await File.WriteAllTextAsync(Path.Combine(output, "policy-result.json"), JsonSerializer.Serialize(new {
                    result.CaseId, result.PolicyId, result.Status, command = result.Proposal?.GetType().Name,
                    proposal = result.Evidence?.AnswerEvidence, result.Evidence, executed = false
                }, Json));
                Check(result.Status == AiSeatStatus.Ready && result.Proposal != null && frame.Allows(result.Proposal),
                    fixture.Id + ": the bounded policy proposes an exact currently authorized command.");
                int? recipient = null;
                if (fixture.Choice == Expected.Cancel) {
                    Check(result.Proposal is CancelSelection && frame.State.Decision!.Payload is TargetSelectionPayload { Cancel.Enabled: true },
                        fixture.Id + ": an unsupported attachment uses the actual offered cancellation permission.");
                } else {
                    Check(result.Proposal is SelectCard, fixture.Id + ": a supported attachment chooses a current card target.");
                    var selected = (SelectCard)result.Proposal!;
                    var recipients = CardViews.InZones(frame.State).Where(row => row.Card.Key == selected.Card).ToArray();
                    Check(recipients.Length == 1, fixture.Id + ": the proposed key identifies exactly one current recipient.");
                    recipient = recipients[0].PlayerId;
                    Check(fixture.Choice == Expected.Friendly ? recipient == frame.State.ViewerId
                            : PlayerRelations.ForViewer(frame.State, recipient.Value) == PlayerRelation.Opponent,
                        fixture.Id + ": the actual recipient controller agrees with the independent fixture expectation.");
                }
                var rejected = RejectionControls(raw, fixture.Id, Check);
                byte[] retained = await File.ReadAllBytesAsync(source);
                Check(Convert.ToHexString(SHA256.HashData(retained)) == storedHash
                        && Convert.ToHexString(SHA256.HashData(await RawEnvelopeAsync(source, retained))) == hash,
                    fixture.Id + ": source envelope bytes remain unchanged after admission and diagnostic controls.");
                results.Add(new { fixture.Id, source, sha256 = hash, storedSha256 = storedHash, replay = replayPath,
                    historyBasis = "empty authored diagnostic history", seed = 17L,
                    result.Status, command = result.Proposal!.GetType().Name, recipientController = recipient,
                    expected = fixture.Choice, rejected, executed = false });
            }
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally {
            await File.WriteAllTextAsync(Path.Combine(profile, "attachment-owned-results.json"), JsonSerializer.Serialize(new {
                formatVersion = 1, audience = "host-private", passed = failure == null, checks,
                sourceDirectory = directory, policy = new CasualCommanderPolicy().Descriptor.Id,
                certifiedUndo = true, capabilityBasis = CapabilityBasis, observedHandshake = false,
                policyAssemblySha256 = Hash(typeof(CasualCommanderPolicy).Assembly.Location),
                adapterAssemblySha256 = Hash(typeof(ForgeEngine).Assembly.Location), results, failure,
                executed = false,
                limits = "Actual provider envelopes and bounded offline proposals; empty diagnostic history. No engine command, receipt or resolution is produced by this runner."
            }, Json));
        }
        Console.WriteLine($"Passed {checks} actual owned attachment checks.");
    }

    private static string[] RejectionControls(byte[] raw, string id, Action<bool, string> check)
    {
        var rejected = new List<string>();
        foreach (string control in new[] { "wrong-source", "foreign-actor", "recipient-binding" }) {
            var changed = JsonNode.Parse(raw)!;
            var semantics = changed["semantics"]!;
            int active = semantics["operationId"]!.GetValue<int>();
            var node = semantics["operationGraph"]!["operations"]!.AsArray().Single(operation => operation!["id"]!.GetValue<int>() == active)!;
            if (control == "wrong-source") node["attachmentSourceCardId"] = "unrelated-source";
            else if (control == "foreign-actor") {
                int viewer = changed["state"]!["viewerId"]!.GetValue<int>();
                int foreign = changed["state"]!["players"]!.AsArray().Select(player => player!["id"]!.GetValue<int>()).First(player => player != viewer);
                node["actorPlayerId"] = foreign;
            } else node["recipients"] = JsonNode.Parse("""{"knowledge":"Known","binding":"DecisionSource","entities":[]}""");
            bool denied = false;
            try {
                var admitted = ForgeEngine.DecodeOwnedDecisionObservation(JsonSerializer.SerializeToElement(changed), certifiedUndo: true);
                if (admitted != null) _ = Frame(admitted);
            } catch (Exception error) when (error is IOException or ArgumentException) { denied = true; }
            check(denied, id + ": native admission rejects the isolated " + control + " mutation.");
            rejected.Add(control);
        }
        return rejected.ToArray();
    }

    private static AiDecisionFrame Frame(OwnedDecisionObservation observation)
        => new(observation.State, domain: observation.Domain, semantics: observation.Semantics);
    private static async Task<byte[]> RawEnvelopeAsync(string source, byte[] stored)
    {
        if (!source.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return stored;
        using var input = new MemoryStream(stored);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await gzip.CopyToAsync(output);
        return output.ToArray();
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
