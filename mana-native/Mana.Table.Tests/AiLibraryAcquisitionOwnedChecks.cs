using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.AI;

namespace Mana.Table;

// Genuine owned envelopes from synthetic production-rules journeys. These are
// development regression checks; separate live continuations verify resolution.
internal static class AiLibraryAcquisitionOwnedChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        string output = Path.GetFullPath(Path.Combine(profile, "library-owned-replays"));
        int attempt = 0;
        foreach (string id in new[] { "acquisition-direct-battlefield", "acquisition-direct-hand", "acquisition-bundle", "acquisition-renamed-bundle" }) {
            string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LibraryAcquisitionOwned", id + ".json.gz");
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            async Task<JsonObject> Run(string factory) => await AiExperimentReplay.RunAsync(JsonSerializer.SerializeToElement(new {
                seed = "2668620592559112008", variant = new { factoryId = factory, attachmentEvaluation = true },
                replay = new { path = source, sha256 = hash, format = "owned", family = id, expectation = new { kind = "acquire-basic-lands" },
                    certifiedUndo = true, capabilityContext = "Authentic in-process provider fixture; same provider declares certified-one-step-undo; no observed transport handshake." }
            }), JsonSerializer.SerializeToElement(new { decisionMs = 3000, workUnits = 4096, captureBytes = 16777216 }),
                Path.Combine(output, "attempt-" + ++attempt));
            var baseline = await Run(AiExperimentPolicyRegistry.CasualCommanderFactoryId);
            var candidate = await Run(AiExperimentPolicyRegistry.LibraryAcquisitionFactoryId);
            var repeated = await Run(AiExperimentPolicyRegistry.LibraryAcquisitionFactoryId);
            check(baseline["status"]!.ToString() == "completed" && baseline["replay"]!["grade"]!.ToString() == "fail"
                    && baseline["replay"]!["eligible"]!.GetValue<bool>() && baseline["replay"]!["admissible"]!.GetValue<bool>(),
                "The real owned acquisition opportunity preserves frozen v6's legal empty-answer failure: " + id);
            check(candidate["status"]!.ToString() == "completed" && candidate["replay"]!["grade"]!.ToString() == "pass"
                    && candidate["replay"]!["admissible"]!.GetValue<bool>() && !candidate["started"]!.GetValue<bool>()
                    && !candidate["details"]!["observedHandshake"]!.GetValue<bool>(),
                "The real owned capture receives a useful scoped basic-land proposal without claiming execution or handshake: " + id);
            check(JsonNode.DeepEquals(candidate["details"]!["proposal"], repeated["details"]!["proposal"])
                    && candidate["details"]!["admittedReplaySha256"]!.ToString() == baseline["details"]!["admittedReplaySha256"]!.ToString(),
                "A/A candidate proposals agree while A/B retains the exact same admitted provider facts: " + id);
        }
    }
}
