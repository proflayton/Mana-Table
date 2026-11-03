using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class ResourceEvidenceWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"resource-wire","status":"playing","viewerId":0,"activePlayerId":0,"turn":1,"phaseKey":"MAIN1",
             "players":[{"id":0,"name":"Viewer","life":40,"human":true,"lost":false,"mana":{},"zones":[]}],
             "stack":[],"activity":[{"id":1,"turn":1,"phaseKey":"MAIN1","kind":"cast","playerId":0,
              "message":"A spell was cast.","cardId":"spell","cardName":"Disclosed spell"}]}
            """)!;
        var legacy = Read(wire).Activity.Single();
        check(legacy.HistoricSpell == null, "Absent cast characteristics remain unknown on an older producer");
        foreach (bool historic in new[] { true, false }) {
            var known = wire.DeepClone();
            known["activity"]![0]!["historicSpell"] = historic;
            var activity = Read(known).Activity.Single();
            check(activity.HistoricSpell == historic, "Actual cast characteristic survives the live typed boundary: " + historic);
            var archived = History(activity);
            check(ForgeHistoryDecoder.Decode(JsonSerializer.SerializeToElement(archived)).Records[0].Activity?.HistoricSpell == historic,
                "The same event-time cast characteristic survives the existing history decoder: " + historic);
        }
        var wrongKind = wire.DeepClone();
        wrongKind["activity"]![0]!["kind"] = "draw";
        wrongKind["activity"]![0]!["historicSpell"] = true;
        check(Reject(wrongKind), "A non-cast event cannot certify a historic spell through live publication");
        check(RejectHistory(legacy with { Kind = "draw", HistoricSpell = true }),
            "A non-cast historic flag also fails the shared archival validator");
        var malformed = wire.DeepClone();
        malformed["activity"]![0]!["historicSpell"] = "false";
        check(Reject(malformed), "A malformed cast-characteristic string never becomes permissive false metadata");
        foreach (var values in new[] { (Before: 1, After: 0), (Before: 0, After: 1) }) {
            var activity = Tap(values.Before, values.After);
            check(Read(TapWire(activity)).Activity.Single().Change == activity.Change,
                "Actual tapped-state flip crosses live publication: " + values);
            check(ForgeHistoryDecoder.Decode(JsonSerializer.SerializeToElement(History(activity))).Records[0].Activity?.Change == activity.Change,
                "The same committed tapped-state flip crosses history: " + values);
        }
        foreach (var bad in new[] { Tap(0, 0), Tap(1, 1), Tap(2, 0), Tap(1, -1),
            Tap(1, 0) with { Kind = "counters" }, Tap(1, 0) with { Change = null } }) {
            check(Reject(TapWire(bad)) && RejectHistory(bad),
                "Live/history reject malformed tapped-state mutation: " + bad.Kind + "/" + bad.Change);
        }

        static GameSnapshot Read(JsonNode value) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(value))
            ?? throw new InvalidDataException("Missing wire fixture.");
        static HistoryBatch History(ActivityEntry activity) => new(1, "resource-wire", 0, 1, 1, 1, false,
            [new HistoryRecord(1, 1, "MAIN1", "public") { Activity = activity }]);
        static ActivityEntry Tap(int before, int after) => new(1, 1, "MAIN1", "tapped", 0, "A card changed state.", "physical", null) {
            Change = new("state", "tapped", before, after), InterruptsQuietYield = false
        };
        JsonNode TapWire(ActivityEntry activity)
        {
            var result = wire.DeepClone();
            result["activity"] = JsonSerializer.SerializeToNode(new[] { activity });
            return result;
        }
        static bool Reject(JsonNode value) { try { Read(value); return false; } catch (IOException) { return true; } }
        static bool RejectHistory(ActivityEntry value)
        {
            try { ForgeHistoryDecoder.Decode(JsonSerializer.SerializeToElement(History(value))); return false; }
            catch (IOException) { return true; }
        }
    }
}
