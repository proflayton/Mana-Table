using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Production IPC admission with authored envelopes. Real Forge extraction and
// resolution are independently exercised by the Java attachment journeys.
internal static class AttachmentCapabilityWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (string fixture in new[] { "rancor", "dead-weight", "mixed-unsupported", "conditional-unsupported" }) {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "AttachmentCapabilities", fixture + ".json")));
            var value = document.RootElement.GetProperty("attachment").Deserialize<AttachmentModifier>(new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
            })!;
            check(value.IsValid && value.Knowledge == (fixture is "rancor" or "dead-weight" ? SemanticKnowledge.Known : SemanticKnowledge.Unknown),
                "Native consumption agrees with Archive on the actual Forge definition export: " + fixture);
            var roundtrip = JsonSerializer.SerializeToNode(value, new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
            });
            check(JsonNode.DeepEquals(roundtrip, JsonNode.Parse(document.RootElement.GetProperty("attachment").GetRawText())),
                "Native attachment serialization retains the shared body without runtime-only fields: " + fixture);
            if (fixture == "rancor") check(value is { PowerDelta: 2, ToughnessDelta: 0 } && value.GrantedKeywords.SequenceEqual(["trample"]),
                "The Rancor export retains direct power and trample only, without crediting its future return trigger");
            if (fixture == "dead-weight") check(value is { PowerDelta: -2, ToughnessDelta: -2 } && value.GrantedKeywords.IsEmpty,
                "The negative Aura export retains its signs rather than inheriting positive attachment intent");
        }
        var original = JsonNode.Parse("""
            {"id":"attachment-wire","status":"playing","viewerId":0,"turn":3,"phaseKey":"MAIN1",
             "activePlayerId":0,"priorityPlayerId":0,
             "prompt":{"id":"attachment-priority","kind":"input","task":"priority","ok":"Pass","okEnabled":true},
             "players":[{"id":0,"name":"Actor","life":20,"mana":{},"zones":[
                {"name":"Battlefield","count":1,"cards":[{"name":"Own creature","type":"Creature","visualId":"own","combatId":"own","power":2,"toughness":2}]}]},
                {"id":1,"name":"Opponent","life":20,"mana":{},"zones":[]}],
             "stack":[{"id":"aura-stack","name":"Aura","text":"","ability":false,"kind":"spell","controllerId":0,
                "card":{"name":"Aura","type":"Enchantment - Aura","visualId":"aura","combatId":"aura"},
                "operationGraph":{"knowledge":"Unknown","rootId":0,"operations":[
                  {"id":0,"effect":"Unknown","knowledge":"Unknown","actorPlayerId":0,
                   "sources":{"knowledge":"Known","binding":"Source","entities":[{"id":"aura-source","kind":"Card","cardId":"aura","controllerId":0,"ownerId":0}]},
                   "recipients":{"knowledge":"Known","binding":"Targets","targetOperationId":0,"entities":[{"id":"own-target","kind":"Card","cardId":"own","controllerId":0,"ownerId":0}]},
                   "targets":{"knowledge":"Known","min":1,"max":1,"candidates":[{"id":"own-target","kind":"Card","cardId":"own","controllerId":0,"ownerId":0}]}}
                ]}}]}
            """)!;
        JsonNode Graph(JsonNode wire) => wire["stack"]![0]!["operationGraph"]!;
        JsonNode Operation(JsonNode wire) => Graph(wire)["operations"]![0]!;
        JsonNode Known()
        {
            var wire = original.DeepClone();
            Operation(wire)["attachment"] = JsonNode.Parse("""
                {"knowledge":"Known","powerDelta":2,"toughnessDelta":0,"grantedKeywords":["trample"]}
                """);
            return wire;
        }
        GameSnapshot Decode(JsonNode wire) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(wire))!;
        bool Reject(JsonNode wire) { try { Decode(wire); return false; } catch (IOException) { return true; } }

        var old = Decode(original);
        check(old.Stack.Single().OperationGraph is { Knowledge: SemanticKnowledge.Unknown }
            && old.Stack[0].OperationGraph.Operations[0] is { Effect: ActionEffectKind.Unknown, Attachment: null },
            "A legacy unknown operation without attachment metadata retains its original uncertainty");
        var known = Known(); var state = Decode(known); var graph = state.Stack.Single().OperationGraph;
        check(Operation(known)["effect"]!.GetValue<string>() == "Unknown"
            && graph.Knowledge == SemanticKnowledge.Known && graph.Operations[0] is {
                Effect: ActionEffectKind.Attach, Knowledge: SemanticKnowledge.Known,
                Attachment: { Knowledge: SemanticKnowledge.Known, PowerDelta: 2, ToughnessDelta: 0 } }
            && graph.Operations[0].Attachment!.GrantedKeywords.SequenceEqual(["trample"]),
            "The actual adapter promotes a legacy Unknown operator only from its bounded current attachment descriptor");
        check(!GameCommandPermissions.Allows(state, new SelectCard(state.Id, "invented", "own")),
            "Attachment metadata and a known pending target confer no invented selection permission");

        foreach (var change in new Action<JsonNode>[] {
            wire => Operation(wire)["attachment"]!["powerDelta"] = 1001,
            wire => Operation(wire)["attachment"]!["powerDelta"] = .5,
            wire => Operation(wire)["attachment"]!["powerDelta"] = null,
            wire => { Operation(wire)["attachment"]!["powerDelta"] = 0; Operation(wire)["attachment"]!["grantedKeywords"] = new JsonArray(); },
            wire => Operation(wire)["attachment"]!["grantedKeywords"] = new JsonArray("trample", "trample"),
            wire => Operation(wire)["attachment"]!["grantedKeywords"] = new JsonArray("cannot attack"),
            wire => Operation(wire)["attachment"]!["knowledge"] = 1,
            wire => Operation(wire)["attachment"]!["unmodeledRestriction"] = true,
            wire => Operation(wire)["attachment"]!["knowledge"] = "Unknown",
            wire => Operation(wire)["effect"] = "Damage",
            wire => Operation(wire)["sources"]!["entities"]![0]!["cardId"] = "hidden-library-card",
            wire => wire["stack"]![0]!["card"]!["faceDown"] = true
        }) { var invalid = Known(); change(invalid); check(Reject(invalid),
                "Attachment IPC rejects malformed, contradictory, unsupported or concealed mechanical claims"); }

        var unknown = Known(); Operation(unknown)["attachment"] = JsonNode.Parse("""
            {"knowledge":"Unknown","powerDelta":null,"toughnessDelta":null,"grantedKeywords":[]}
            """);
        var uncertain = Decode(unknown).Stack[0].OperationGraph;
        check(uncertain.Knowledge == SemanticKnowledge.Unknown && uncertain.Operations[0] is {
            Effect: ActionEffectKind.Attach, Knowledge: SemanticKnowledge.Unknown, Attachment.Knowledge: SemanticKnowledge.Unknown },
            "Recognizing an attachment operation does not manufacture knowledge of unsupported modifiers");
        var conditional = Known(); Operation(conditional)["conditional"] = true;
        check(Decode(conditional).Stack[0].OperationGraph.Knowledge == SemanticKnowledge.Unknown,
            "An unconditional modifier body cannot promote a conditional attachment into a known operation");
        var negative = Known(); Operation(negative)["attachment"]!["powerDelta"] = -2;
        Operation(negative)["attachment"]!["toughnessDelta"] = -2;
        Operation(negative)["attachment"]!["grantedKeywords"] = new JsonArray();
        check(Decode(negative).Stack[0].OperationGraph.Operations[0].Attachment is { PowerDelta: -2, ToughnessDelta: -2 },
            "The actual decoder preserves harmful signed modifiers instead of treating attachment as inherent benefit");

        JsonObject Envelope(JsonNode wire) => new() {
            ["state"] = wire.DeepClone(),
            ["domain"] = JsonNode.Parse("""{"version":1,"coverage":"Complete","sources":[],"actions":[]}"""),
            ["semantics"] = new JsonObject { ["operationGraph"] = Graph(wire).DeepClone(), ["operationId"] = 0 }
        };
        var owned = Envelope(Known());
        owned["domain"]!["actions"] = new JsonArray(JsonNode.Parse("""
            {"actionId":"current-action","effect":"Unknown","effects":["Unknown"],"costs":[],"affordability":"Unknown"}
            """));
        owned["domain"]!["actions"]![0]!["operationGraph"] = Graph(Known()).DeepClone();
        var observation = ForgeEngine.DecodeOwnedDecisionObservation(JsonSerializer.SerializeToElement(owned))!;
        check(observation.Semantics.OperationGraph.Operations[0].Effect == ActionEffectKind.Attach
            && observation.Domain.Actions.Single() is { Effect: ActionEffectKind.Attach, OperationGraph.Knowledge: SemanticKnowledge.Known },
            "Owned human and automated observation decoding shares attachment promotion for action and decision graphs");

        JsonNode PortraitEnvelope()
        {
            var value = Envelope(Known());
            value["state"]!["stack"] = new JsonArray();
            value["state"]!["prompt"] = JsonNode.Parse("""
                {"id":"current-targets","kind":"input","task":"selection","purpose":"targets",
                 "ok":"Done","okEnabled":false,"cancel":"Cancel","cancelEnabled":true,"min":1,"max":1,
                 "sourceControllerId":0,"sourceZone":"Stack",
                 "sourceCard":{"name":"Aura portrait","type":"Enchantment - Aura","visualId":"announcing","combatId":"announcing"}}
                """);
            var operation = value["semantics"]!["operationGraph"]!["operations"]![0]!;
            operation["sources"] = JsonNode.Parse("""{"knowledge":"Unknown","binding":"Unknown","entities":[]}""");
            operation["attachmentSourceCardId"] = "announcing";
            return value;
        }
        OwnedDecisionObservation ReadPortrait(JsonNode value)
            => ForgeEngine.DecodeOwnedDecisionObservation(JsonSerializer.SerializeToElement(value))!;
        var portrait = ReadPortrait(PortraitEnvelope());
        var portraitGraph = portrait.Semantics.OperationGraph;
        new ActionOperationValidation(portrait.State).Validate(portraitGraph, portrait.Semantics.OperationId);
        check(portraitGraph.Knowledge == SemanticKnowledge.Known && portraitGraph.Operations[0] is {
            Effect: ActionEffectKind.Attach, AttachmentSourceCardId: "announcing",
            Sources: { Knowledge: SemanticKnowledge.Known, Binding: EffectRecipientBinding.DecisionSource, Entities.Length: 0 } },
            "An actual announcing portrait can identify the active attachment source without pretending it is a zone occurrence");
        check(portrait.State.ActionSources.IsEmpty && portrait.State.Stack.IsEmpty
            && !CardViews.InZones(portrait.State).Any(row => CardViews.Identity(row.Card) == "announcing")
            && !GameCommandPermissions.Allows(portrait.State, new SelectCard(portrait.State.Id, "current-targets", "announcing")),
            "Read-only attachment source provenance adds no zone occurrence, action handle or target selection permission");
        foreach (var change in new Action<JsonNode>[] {
            value => value["semantics"]!["operationGraph"]!["operations"]![0]!["attachmentSourceCardId"] = "stale",
            value => value["semantics"]!["operationGraph"]!["operations"]![0]!["actorPlayerId"] = 1,
            value => value["semantics"]!["operationId"] = 1,
            value => value["state"]!["prompt"]!["sourceControllerId"] = 1,
            value => value["state"]!["prompt"]!["sourceCard"]!["faceDown"] = true,
            value => value["state"]!["prompt"]!["sourceCard"]!["selectable"] = true,
            value => value["state"]!["prompt"]!["sourceCard"]!["highlighted"] = true,
            value => value["state"]!["prompt"]!["sourceCard"]!["key"] = "fake-handle",
            value => value["state"]!["prompt"]!["sourceCardId"] = "different-portrait"
        }) {
            var value = PortraitEnvelope(); change(value);
            bool rejected = false; try { ReadPortrait(value); } catch (IOException) { rejected = true; }
            check(rejected, "Owned attachment provenance rejects stale, foreign, concealed or actionable source portraits");
        }
        bool InvalidGraph(ActionOperationGraph value, int? active = 0)
        {
            try { new ActionOperationValidation(portrait.State).Validate(value, active); return false; }
            catch (ArgumentException) { return true; }
        }
        check(InvalidGraph(portraitGraph, null), "Decision portrait provenance is unavailable to action offers, options and committed stack graphs");
        var portraitNode = portraitGraph.Operations[0];
        check(InvalidGraph(portraitGraph with { Operations = [portraitNode with { Recipients = portraitNode.Sources }] }),
            "A source portrait cannot be rebound as an effect recipient");
        var fakeTarget = new SemanticEntity("portrait-target", SemanticEntityKind.Card, "announcing", ControllerId: 0, OwnerId: 0);
        check(InvalidGraph(portraitGraph with { Operations = [portraitNode with {
            Targets = portraitNode.Targets! with { Candidates = [fakeTarget] } }] }),
            "The ordinary entity validator still rejects a portrait used as a target occurrence");
    }
}
