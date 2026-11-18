using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class LibraryAcquisitionWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"id":"library-choice","kind":"choice","task":"choice","context":"librarySearch","min":0,"max":2,
             "choices":[{"index":3,"label":"Forest","card":{"name":"Forest","type":"Basic Land - Forest"}},
                        {"index":8,"label":"Plains","card":{"name":"Plains","type":"Basic Land - Plains"}}],
             "libraryCards":[{"index":3,"label":"Forest","card":{"name":"Forest","type":"Basic Land - Forest"}},
                             {"index":8,"label":"Plains","card":{"name":"Plains","type":"Basic Land - Plains"}}],
             "libraryAcquisition":{"actorPlayerId":29,"libraryOwnerId":29,"recipientPlayerId":29,
               "stage":"select-bundle","currentDestination":"Library","additionalCostKnownAbsent":true,
               "plannedDestinations":[{"destination":"Battlefield","maximumCount":1},{"destination":"Hand","maximumCount":1}]}}
            """)!;
        Decision Decode(JsonNode node, int? audience = 29) => ForgeDecisionDecoder.Decode(JsonSerializer.SerializeToElement(node), new HashSet<int> { 29, 47 }, audience);
        bool Reject(JsonNode node, int? audience = 29) { try { Decode(node, audience); return false; } catch (IOException) { return true; } }
        var decision = Decode(wire);
        var options = (OptionsPayload)decision.Payload;
        check(options.LibraryAcquisition is { ActorPlayerId: 29, LibraryOwnerId: 29, RecipientPlayerId: 29,
                Stage: LibraryAcquisitionStage.SelectBundle, CurrentDestination: "Library", AdditionalCostKnownAbsent: true }
            && options.LibraryAcquisition.PlannedDestinations.SequenceEqual([new("Battlefield", 1), new("Hand", 1)])
            && options.Selection.Choices.Select(choice => choice.Index).SequenceEqual([3, 8]),
            "A library bundle retains its exact typed destinations and original offered indices without adding occurrence handles");
        var state = new GameSnapshot("library-wire", "playing", null, null, 29, 1, "Main", "MAIN1", 29, [], [], null, decision);
        check(GameCommandPermissions.Allows(state, new ChooseOptions(state.Id, decision.Id, [3, 8]))
            && GameCommandPermissions.Allows(state, new ChooseOptions(state.Id, decision.Id, []))
            && !GameCommandPermissions.Allows(state, new ChooseOptions(state.Id, decision.Id, [0]))
            && !GameCommandPermissions.Allows(state, new SelectCard(state.Id, decision.Id, "3"))
            && !GameCommandPermissions.Allows(state, new PayMana(state.Id, decision.Id)),
            "Acquisition context preserves the current optional choice domain and grants no card, payment or hidden-zone commands");
        check(Reject(wire, 47) && Reject(wire, null), "An otherwise valid self acquisition cannot cross the current viewer boundary or omit its audience");
        var reduced = wire.DeepClone(); reduced["max"] = 1;
        check(Decode(reduced).Payload is OptionsPayload { LibraryAcquisition.Stage: LibraryAcquisitionStage.SelectBundle },
            "Planned destination maxima remain upper bounds when fewer current choices may be taken");
        foreach (string stage in new[] { "direct", "distribute-bundle" }) {
            var direct = wire.DeepClone(); direct["max"] = 1;
            direct["libraryAcquisition"]!["stage"] = stage; direct["libraryAcquisition"]!["currentDestination"] = "Battlefield";
            direct["libraryAcquisition"]!["plannedDestinations"] = JsonNode.Parse("""[{"destination":"Battlefield","maximumCount":1}]""");
            check(Decode(direct).Payload is OptionsPayload { LibraryAcquisition.CurrentDestination: "Battlefield" },
                "Direct and remembered distribution stages describe only their current own acquisition destination");
        }
        foreach (var mutate in new Action<JsonNode>[] {
            node => node["libraryAcquisition"]!["actorPlayerId"] = 47,
            node => node["libraryAcquisition"]!["libraryOwnerId"] = 47,
            node => node["libraryAcquisition"]!["recipientPlayerId"] = 47,
            node => node["libraryAcquisition"]!["actorPlayerId"] = -1,
            node => node["libraryAcquisition"]!["stage"] = "Direct",
            node => node["libraryAcquisition"]!["stage"] = 0,
            node => node["libraryAcquisition"]!["currentDestination"] = "Exile",
            node => node["libraryAcquisition"]!["additionalCostKnownAbsent"] = false,
            node => node["libraryAcquisition"]!["additionalCostKnownAbsent"] = null,
            node => node["libraryAcquisition"]!["plannedDestinations"]![0]!["maximumCount"] = 0,
            node => node["libraryAcquisition"]!["plannedDestinations"]![0]!["maximumCount"] = 2,
            node => node["libraryAcquisition"]!["plannedDestinations"]![1]!["destination"] = "Battlefield",
            node => node["libraryAcquisition"]!["plannedDestinations"] = new JsonArray(),
            node => node["libraryAcquisition"]!["actionId"] = "invented",
            node => node["libraryAcquisition"]!["plannedDestinations"]![0]!["cardId"] = "invented",
            node => node["max"] = 3,
            node => node["ordered"] = true,
            node => node["purpose"] = "abilities",
            node => node["context"] = "unrelated",
            node => { node["kind"] = "input"; node["task"] = "priority"; },
            node => node["choices"]![0]!["cardId"] = "hidden-occurrence",
            node => node["choices"]![0]!["card"]!["visualId"] = "hidden-occurrence",
            node => node["choices"]![0]!["card"]!["combatId"] = "hidden-occurrence",
            node => node["choices"]![0]!["card"]!["key"] = "new-command",
            node => node["choices"]![0]!["mana"] = "{G}",
            node => node["choices"]![0]!["card"] = null,
            node => node["handReduction"] = JsonNode.Parse("""{"playerId":29,"handSize":3,"maximumHandSize":2}""")
        }) { var invalid = wire.DeepClone(); mutate(invalid); check(Reject(invalid), "Library context rejects foreign, malformed, conflicting or authority-bearing facts"); }
        var duplicate = wire.ToJsonString().Replace("\"actorPlayerId\":29", "\"actorPlayerId\":29,\"actorPlayerId\":29", StringComparison.Ordinal);
        bool duplicateRejected;
        try { ForgeDecisionDecoder.Decode(JsonDocument.Parse(duplicate).RootElement, new HashSet<int> { 29 }, 29); duplicateRejected = false; }
        catch (IOException) { duplicateRejected = true; }
        check(duplicateRejected, "Library facts reject duplicate keys rather than choosing one value");
        var ordinary = wire.DeepClone(); ((JsonObject)ordinary).Remove("libraryAcquisition");
        check(Decode(ordinary).Payload is OptionsPayload { LibraryAcquisition: null }, "Legacy library choices remain answerable without inferred acquisition facts");
        bool Conflict(Func<OptionsPayload> mutate) { try { mutate(); return false; } catch (ArgumentException) { return true; } }
        check(Conflict(() => options with { Selection = new(options.Selection.Choices, new(0, 3), options.Selection.Disclosures) })
            && Conflict(() => options with { ManaPaymentContext = new(29, [], null) })
            && Conflict(() => options with { HandReduction = new(29, 3, 2) }),
            "Native record copies cannot detach an acquisition from its bounded non-payment choice shape");
        var roundTrip = JsonSerializer.Deserialize<Decision>(JsonSerializer.Serialize(decision));
        check(roundTrip?.Payload is OptionsPayload { LibraryAcquisition: { Stage: LibraryAcquisitionStage.SelectBundle } },
            "Owned replay serialization retains the frozen acquisition contract without changing its option domain");
    }
}
