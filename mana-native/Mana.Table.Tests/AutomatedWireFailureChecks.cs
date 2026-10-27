using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Forge;
using Mana.Contracts;

namespace Mana.Table;

internal static class AutomatedWireFailureChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        const string staleMessage = "The automated input changed during discovery";
        using (var typed = JsonDocument.Parse(JsonSerializer.Serialize(new { error = staleMessage, errorCode = "observation-superseded" }))) {
            check(ForgeEngine.RequestFailure("automatedState", typed.RootElement) is EngineObservationSupersededException
                && ForgeEngine.RequestFailure("automatedAction", typed.RootElement) is EngineCommandRejectedException,
                "Only the exact typed read-only observation result permits discarding a stale AI discovery batch; action receipts remain failures");
        }
        using (var untyped = JsonDocument.Parse(JsonSerializer.Serialize(new { error = staleMessage })))
            check(ForgeEngine.RequestFailure("automatedState", untyped.RootElement) is EngineCommandRejectedException,
                "An identical untyped exception message is never interpreted as permission to hide a provider failure");
        const string privateMarker = "PRIVATE_WIRE_FACE";
        const string valid = """
            [{"seatId":0,"state":{"id":"wire-failure-fixture","status":"playing","viewerId":0,
              "players":[{"id":0,"name":"Viewer","life":40,"human":false,"lost":false,"mana":{},
                "zones":[{"name":"Hand","count":1,"cards":[{"name":"PRIVATE_WIRE_FACE","visualId":"own-face"}]}]}],"stack":[]},
              "domain":{"version":0,"coverage":"NotApplicable","sources":[],"actions":[]},"semantics":{}}]
            """;
        using var accepted = JsonDocument.Parse(valid);
        var last = ForgeEngine.DecodeAutomatedObservations(accepted.RootElement);
        check(last is [{ SeatId: 0, State.Id: "wire-failure-fixture" }],
            "The production automated wire decoder retains a fully decoded batch independently of later rejected input");
        var bad = JsonNode.Parse(valid)!;
        bad[0]!["state"]!["playAccesses"] = JsonNode.Parse("""
            [{"playerId":0,"zone":"Library","cardId":"not-published","routes":[
              {"id":"unusable-route","kind":0,"sourceCardId":"","sourceName":"","detail":"","canStart":false}]}]
            """);
        ForgeAutomatedObservationException? rejected = null;
        using (var document = JsonDocument.Parse(bad.ToJsonString())) {
            try { ForgeEngine.DecodeAutomatedObservations(document.RootElement); }
            catch (ForgeAutomatedObservationException error) { rejected = error; }
        }
        check(rejected is { InnerException: IOException }
            && rejected.RejectedWire[0].GetProperty("state").GetProperty("playAccesses")[0].GetProperty("cardId").GetString() == "not-published"
            && rejected.RejectedWire.GetRawText().Contains(privateMarker, StringComparison.Ordinal),
            "A pre-decode visibility rejection preserves the exact immutable raw batch after its original JSON document is disposed");
        check(!rejected!.ToString().Contains(privateMarker, StringComparison.Ordinal)
            && rejected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).All(property => property.Name != "RejectedWire"),
            "Rejected private faces do not enter public exception text or publicly accessible properties");
        string productionEvidence = Path.Combine(profile, "adapter-production-failure");
        ForgeEngine.PreserveRejectedAutomatedObservation(productionEvidence, rejected.RejectedWire, rejected);
        string privateFailurePath = Path.Combine(productionEvidence, "diagnostics", "private-rejected-ai-observation.json");
        using (var production = JsonDocument.Parse(File.ReadAllText(privateFailurePath))) {
            check(production.RootElement.GetProperty("authority").GetString() == "none"
                && production.RootElement.GetProperty("privacy").GetString()!.Contains("Private bot observations")
                && JsonNode.DeepEquals(JsonNode.Parse(production.RootElement.GetProperty("raw").GetRawText()), bad),
                "The shipping adapter quarantines the exact failed private batch locally so a provider-failed report is diagnosable");
        }
        ForgeEngine.PreserveLastAutomatedObservation(productionEvidence, accepted.RootElement, "wire-failure-fixture", 0, "provider-failed");
        string lastFailurePath = Path.Combine(productionEvidence, "diagnostics", "private-last-ai-observation.json");
        using (var production = JsonDocument.Parse(File.ReadAllText(lastFailurePath)))
            check(production.RootElement.GetProperty("phase").GetString() == "last-decoded-observation"
                && JsonNode.DeepEquals(JsonNode.Parse(production.RootElement.GetProperty("raw").GetRawText()), JsonNode.Parse(valid)),
                "A host failure after successful decoding retains that original private frame as evidence with no action authority");
        ForgeEngine.PreserveLastAutomatedObservation(productionEvidence, accepted.RootElement, "another-game", 0, "provider-failed");
        using (var production = JsonDocument.Parse(File.ReadAllText(lastFailurePath)))
            check(production.RootElement.GetProperty("phase").GetString() == "no-matching-decoded-observation"
                && production.RootElement.GetProperty("raw").ValueKind == JsonValueKind.Null,
                "A new game's fault cannot relabel an earlier game's private frame or leave it looking like current incident evidence");
        string directory = Path.Combine(profile, "adapter-rejected-wire");
        Directory.CreateDirectory(directory);
        using (var evidence = new AiCampaignDiagnostics(directory)) {
            evidence.PreserveObservations(last);
            evidence.PreserveFailure(rejected, "observe-automated", 1, last);
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "private-failure-manifest.json")));
        using var retained = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "private-rejected-observation.json")));
        check(manifest.RootElement.GetProperty("rejectedWireObservation").GetProperty("captured").GetBoolean()
            && manifest.RootElement.GetProperty("rawObservation").GetProperty("scopes")[0].GetProperty("gameId").GetString() == last[0].State.Id
            && retained.RootElement.GetProperty("authority").GetString() == "none"
            && JsonNode.DeepEquals(JsonNode.Parse(retained.RootElement.GetProperty("raw").GetRawText()), JsonNode.Parse(rejected.RejectedWire.GetRawText())),
            "Failure evidence distinguishes the rejected raw wire from the last valid decoded scope without manufacturing a policy replay");
    }
}
