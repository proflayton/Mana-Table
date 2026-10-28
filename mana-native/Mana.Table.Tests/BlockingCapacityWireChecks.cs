using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class BlockingCapacityWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"blocking-capacity","status":"playing","viewerId":0,"turn":7,"phaseKey":"MAIN1",
             "players":[{"id":0,"name":"Actor","life":40,"mana":{},"zones":[
                 {"name":"Battlefield","count":1,"cards":[{"name":"Public creature","type":"Creature",
                  "visualId":"creature","combatId":"creature","power":2,"toughness":2}]}]},
                {"id":1,"name":"Opponent","life":40,"mana":{},"zones":[]}],"stack":[]}
            """)!;
        GameSnapshot Decode(JsonNode node) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(node))!;
        JsonNode Face(JsonNode node) => node["players"]![0]!["zones"]![0]!["cards"]![0]!;
        JsonNode With(JsonNode? capacity) {
            var changed = wire.DeepClone(); Face(changed)["blockingCapacity"] = capacity; return changed;
        }
        bool Reject(JsonNode changed) { try { Decode(changed); return false; } catch (IOException) { return true; } }
        check(Decode(wire).Viewer!.Zone("Battlefield").Cards[0].BlockingCapacity == null
                && Decode(With(null)).Viewer!.Zone("Battlefield").Cards[0].BlockingCapacity == null,
            "Absent or explicitly null legacy blocking capacity stays unknown instead of assuming a one-creature limit");
        foreach (int maximum in new[] { 0, 1, 4 }) {
            var decoded = Decode(With(new JsonObject { ["maximum"] = maximum }));
            check(decoded.Viewer!.Zone("Battlefield").Cards[0].BlockingCapacity == new BlockingCapacity(maximum),
                "A finite public upper bound preserves zero and additional blocks without implying current block permission");
        }
        check(Decode(With(new JsonObject { ["maximum"] = null, ["unlimited"] = true }))
                .Viewer!.Zone("Battlefield").Cards[0].BlockingCapacity == new BlockingCapacity(null, true),
            "Explicit unlimited blocking remains distinct from missing or finite capability knowledge");
        foreach (var malformed in new JsonNode[] {
            new JsonObject { ["maximum"] = -1 }, new JsonObject { ["maximum"] = null },
            new JsonObject { ["unlimited"] = true, ["maximum"] = 2 },
            new JsonObject { ["maximum"] = "two" }, new JsonObject { ["maximum"] = 2, ["unlimited"] = "yes" }
        }) check(Reject(With(malformed)), "Malformed or contradictory blocking-capacity wire facts fail before publication");

        var hidden = With(new JsonObject { ["maximum"] = 4 }); Face(hidden)["faceDown"] = true;
        Face(hidden)["name"] = "Face-down card"; Face(hidden)["visualId"] = "";
        check(Reject(hidden), "A masked face cannot disclose the blocking capacity of its hidden original");
        Face(hidden)["blockingCapacity"] = null;
        check(Decode(hidden).Viewer!.Zone("Battlefield").Cards[0] is { FaceDown: true, BlockingCapacity: null },
            "An anonymous current battlefield face remains readable with explicitly unknown blocking capacity");
        foreach (string zone in new[] { "Hand", "Library", "Graveyard", "Exile", "Command" }) {
            var moved = With(new JsonObject { ["maximum"] = 1 });
            moved["players"]![0]!["zones"]![0]!["name"] = zone;
            check(Reject(moved), "A nonbattlefield occurrence cannot borrow a current battlefield blocking-capacity fact");
        }
        var top = wire.DeepClone();
        var topFace = Face(top).DeepClone(); topFace["blockingCapacity"] = new JsonObject { ["maximum"] = 1 };
        var library = top["players"]![0]!["zones"]![0]!;
        library["name"] = "Library"; library["cards"] = new JsonArray(); library["topCard"] = topFace;
        check(Reject(top), "A disclosed library top cannot become a current blocker through its top-card projection");
        var additional = wire.DeepClone();
        additional["prompt"] = JsonNode.Parse("""
            {"id":"source-input","kind":"input","task":"priority","okEnabled":true}
            """);
        additional["actionSources"] = JsonNode.Parse("""
            [{"decisionId":"source-input","zone":"Sideboard","playerId":0,
              "card":{"key":"sideboard-key","visualId":"sideboard","combatId":"sideboard",
                      "name":"Authorized external source","blockingCapacity":{"maximum":1}}}]
            """);
        check(Reject(additional), "A current external action source cannot import a battlefield-only blocking bound");
        additional["actionSources"]![0]!["card"]!["blockingCapacity"] = null;
        check(Decode(additional).ActionSources.Single().Card.BlockingCapacity == null,
            "An authorized external source remains available when its blocking capacity is correctly unknown");
        var portrait = wire.DeepClone();
        portrait["prompt"] = JsonNode.Parse("""
            {"id":"read-hidden-source","kind":"input","task":"confirmation","okEnabled":true,
             "sourceCard":{"name":"Face-down card","faceDown":true,"blockingCapacity":{"maximum":4}}}
            """);
        check(Reject(portrait), "A concealed decision portrait cannot bypass blocking-capacity privacy validation through the reader path");
        portrait["prompt"]!["sourceCard"]!["blockingCapacity"] = null;
        var readable = Decode(portrait);
        check(CardViews.Source(readable, readable.Decision!) is { FaceDown: true, BlockingCapacity: null },
            "A read-only concealed decision portrait still opens without disclosing a blocking bound");
    }
}
