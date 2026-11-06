using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class TableChoiceWireChecks
{
    internal static JsonNode Frame() => JsonNode.Parse("""
        {"gameId":"presence-wire-game","boardRevision":10,"revision":1,"cues":[],"choices":[
        {"playerId":29,"contextId":"choice-context","preview":{"source":{"kind":"card","id":"public-card"},
        "target":{"kind":"player","id":"47"}},"remainingMilliseconds":1500}]}
        """)!;
    internal static void Run(Action<bool, string> check)
    {
        var good = Frame();
        TablePresenceFrame Decode(JsonNode node) => ForgeEngine.DecodeTablePresence(JsonSerializer.SerializeToElement(node), "presence-wire-game");
        check(Decode(good).Choices is [{ Preview.Target.Kind: TableChoiceTargetKind.Player }],
            "A closed public player endpoint decodes from the actual choice-presence wire");
        foreach (string kind in new[] { "card", "stack" }) {
            var variant = good.DeepClone(); variant["choices"]![0]!["preview"]!["target"]!["kind"] = kind;
            check(Decode(variant).Choices.Length == 1, "Public choice codec retains the closed endpoint kind: " + kind);
        }
        var sourceOnly = good.DeepClone(); sourceOnly["choices"]![0]!["preview"]!["target"] = null;
        check(Decode(sourceOnly).Choices.Single().Preview.Target == null, "Picking up a public source does not invent an intended recipient");
        var older = good.DeepClone(); ((JsonObject)older).Remove("choices");
        check(Decode(older).Choices.IsEmpty, "An older host's absent optional choices array remains a valid inspection frame");
        foreach (Action<JsonNode> corrupt in new Action<JsonNode>[] {
            node => node["choices"]![0]!["preview"]!["source"]!["kind"] = "hand",
            node => node["choices"]![0]!["preview"]!["target"]!["kind"] = "library",
            node => node["choices"]![0]!["preview"]!["target"]!["kind"] = 2,
            node => ((JsonObject)node["choices"]![0]!["preview"]!).Remove("source"),
            node => node["choices"]![0]!["preview"]!["source"] = null,
            node => node["choices"]![0]!["preview"]!["target"]!["id"] = "047",
            node => node["choices"]![0]!["preview"]!["target"]!["id"] = "-1",
            node => node["choices"]![0]!["preview"]!["target"]!["id"] = new string('x', 129),
            node => node["choices"]![0]!["remainingMilliseconds"] = 1501,
            node => ((JsonArray)node["choices"]!).Add(node["choices"]![0]!.DeepClone()),
            node => ((JsonObject)node["choices"]![0]!).Remove("contextId")
        }) {
            var invalid = good.DeepClone(); corrupt(invalid); bool rejected = false;
            try { Decode(invalid); } catch (IOException) { rejected = true; }
            check(rejected, "Malformed, hidden-zone, duplicate or unbounded choice-presence data cannot enter a decoded frame");
        }
        var state = ConfiguredSimulationWireChecks.State();
        state["id"] = "choice-snapshot"; state["viewerId"] = 29; state["boardRevision"] = 10;
        state["decisionOwnerId"] = 29; state["actionClass"] = "attack";
        state["publicChoice"] = new JsonObject { ["id"] = "CHOICE-NONCE-CANARY", ["boardRevision"] = 10, ["playerId"] = 29, ["kind"] = "attack" };
        state["prompt"] = new JsonObject { ["id"] = "attack", ["kind"] = "input", ["task"] = "attack", ["okEnabled"] = true };
        check(PublicChoiceFacts.IsCurrent(ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(state))!),
            "An acting combat snapshot retains choice authority independently of priority ownership");
        var observing = state.DeepClone(); observing["viewerId"] = 47; observing["prompt"] = null;
        check(PublicChoiceFacts.IsCurrent(ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(observing))!),
            "An observing audience retains the public combat context without receiving the actor's decision");
        var targeting = state.DeepClone(); targeting["actionClass"] = "selection";
        targeting["publicChoice"]!["kind"] = "target";
        targeting["publicChoice"]!["source"] = new JsonObject { ["kind"] = "card", ["id"] = "public-source" };
        ((JsonArray)targeting["players"]![3]!["zones"]!).Add(new JsonObject { ["name"] = "Battlefield", ["count"] = 1,
            ["cards"] = new JsonArray(new JsonObject { ["visualId"] = "public-source", ["name"] = "Public source", ["faceDown"] = false }) });
        targeting["prompt"] = new JsonObject { ["id"] = "targeting", ["kind"] = "input", ["task"] = "selection", ["purpose"] = "targets",
            ["okEnabled"] = true, ["cancelEnabled"] = true, ["min"] = 0, ["max"] = 1, ["playerChoices"] = new JsonArray(47) };
        check(PublicChoiceFacts.IsCurrent(ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(targeting))!),
            "An explicit target decision shares only its already-public authority-authored source");
        var secret = targeting.DeepClone(); secret["prompt"]!["librarySearch"] = new JsonObject { ["libraryOwnerId"] = null,
            ["destination"] = "Hand", ["cardName"] = "SECRET-SEARCH-CANARY" };
        string? secretError = null;
        try { ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(secret)); } catch (IOException error) { secretError = error.Message; }
        check(secretError == "The rules provider published an invalid match snapshot (public choice: MissingLocalChoice).",
            "A library-search target prompt cannot borrow a public source to expose its secret consideration");
        foreach (var (reason, corrupt) in new (PublicChoiceViolation, Action<JsonNode>)[] {
            (PublicChoiceViolation.InvalidContext, node => node["publicChoice"]!["id"] = "CHOICE-NONCE-CANARY\n"),
            (PublicChoiceViolation.BoardRevisionMismatch, node => node["publicChoice"]!["boardRevision"] = 11),
            (PublicChoiceViolation.UnknownPlayer, node => node["publicChoice"]!["playerId"] = 999),
            (PublicChoiceViolation.DecisionOwnerMismatch, node => { node["prompt"] = null; node["decisionOwnerId"] = 47; }),
            (PublicChoiceViolation.NonChoiceAction, node => node["actionClass"] = "payment"),
            (PublicChoiceViolation.NotPlaying, node => { node["prompt"] = null; node["status"] = "resolving"; }),
            (PublicChoiceViolation.MissingLocalChoice, node => node["prompt"] = null),
            (PublicChoiceViolation.InvalidSource, node => node["publicChoice"]!["source"] = new JsonObject { ["kind"] = "card", ["id"] = "private-card" })
        }) {
            var invalid = state.DeepClone(); corrupt(invalid); string? message = null;
            try { ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(invalid)); } catch (IOException error) { message = error.Message; }
            check(message == "The rules provider published an invalid match snapshot (public choice: " + reason + ").",
                "Invalid choice capability reports only its fixed non-identifying reason: " + reason);
        }
    }
}
