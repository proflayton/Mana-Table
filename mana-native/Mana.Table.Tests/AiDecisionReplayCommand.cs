using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;

namespace Mana.Table;

internal static class AiDecisionReplayCommand
{
    internal static async Task RunAsync(string path, string profile)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        json.Converters.Add(new JsonStringEnumConverter());
        var recorded = JsonSerializer.Deserialize<AiDecisionReplayCase>(await File.ReadAllTextAsync(path), json)
            ?? throw new InvalidDataException("The private decision replay is empty.");
        var result = await AiDecisionReplay.EvaluateAsync(recorded, new CasualCommanderPolicy());
        await File.WriteAllTextAsync(Path.Combine(profile, "ai-decision-replay.json"), JsonSerializer.Serialize(new {
            formatVersion = 1, audience = "host-private", caseId = result.CaseId,
            capturedPolicyId = recorded.PolicyId, evaluatedPolicyId = result.PolicyId,
            evaluatedPolicyAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CasualCommanderPolicy).Assembly.Location))),
            status = result.Status, proposal = result.Evidence?.AnswerEvidence, result.Evidence,
            executed = false
        }, json));
    }
}
