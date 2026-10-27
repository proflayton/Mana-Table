using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Forge;

namespace Mana.Table;

internal static class AutomatedWireFailureChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
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
