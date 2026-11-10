using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class SoloSetupWireChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var wire = JsonNode.Parse("""
            {"deckId":"human-document","name":"The human deck","maxPlayers":4,
             "opponents":[{"id":"first","name":"A saved deck","description":"Saved"},
                          {"id":"second","name":"A bundled deck","description":"Precon"},
                          {"id":"third","name":"Another deck","description":"Saved"}],
             "defaultOpponentIds":["third","first","second"]}
            """)!;
        for (int index = 0; index < 3; index++) wire["opponents"]![index]!["compositionId"] = new string((char)('a' + index), 64);
        SoloSetup Map(JsonNode value) => ForgeEngine.MapSoloSetup(JsonSerializer.SerializeToElement(value));
        bool RejectMap(JsonNode value) {
            try { Map(value); return false; }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IOException) { return true; }
        }
        bool RejectSelection(JsonNode value) {
            var setup = Map(value);
            try { SoloOpponentSelection.Prepare(setup, SoloOpponentSelection.Empty, 42); return false; }
            catch (InvalidOperationException) { return true; }
        }
        var setup = Map(wire);
        var selected = SoloOpponentSelection.Prepare(setup, SoloOpponentSelection.Empty, 42);
        check(setup.DeckId == "human-document" && setup.DeckName == "The human deck" && setup.MaxPlayers == 4
                && setup.Opponents[1].CompositionId == new string('b', 64)
                && setup.DefaultOpponentIds.SequenceEqual(["third", "first", "second"])
                && selected.Choices.Select(choice => choice.OpponentId).SequenceEqual(setup.DefaultOpponentIds),
            "The production Forge solo mapper preserves provider composition identities and exact current default opponent IDs");

        var legacy = wire.DeepClone(); ((JsonObject)legacy).Remove("defaultOpponentIds");
        foreach (var opponent in legacy["opponents"]!.AsArray()) ((JsonObject)opponent!).Remove("compositionId");
        var old = Map(legacy);
        check(old.DefaultOpponentIds.IsEmpty && old.Opponents.All(opponent => opponent.CompositionId == "")
                && SoloOpponentSelection.Prepare(old, SoloOpponentSelection.Empty, 42).Choices.Select(choice => choice.OpponentId).Distinct().Count() == 3,
            "The actual Forge mapper accepts older solo responses without either optional field and leaves fallback selection usable");
        var nullDefaults = wire.DeepClone(); nullDefaults["defaultOpponentIds"] = null;
        check(Map(nullDefaults).DefaultOpponentIds.IsEmpty
                && SoloOpponentSelection.Prepare(Map(nullDefaults), SoloOpponentSelection.Empty, 42).Choices.Length == 3,
            "A null optional default-opponent array means unsupported defaults rather than an unusable solo preparation");

        foreach (var mutate in new Action<JsonNode>[] {
            value => value["defaultOpponentIds"] = "first",
            value => value["defaultOpponentIds"] = new JsonObject { ["id"] = "first" },
            value => value["defaultOpponentIds"] = new JsonArray(7),
            value => value["opponents"] = new JsonObject(),
            value => value["opponents"]![0]!["compositionId"] = 64,
            value => value["opponents"]![0]!["id"] = false,
            value => value["maxPlayers"] = "four"
        }) {
            var malformed = wire.DeepClone(); mutate(malformed);
            check(RejectMap(malformed), "Malformed solo JSON field shapes or types fail the real Forge adapter mapping");
        }
        check(RejectMap(new JsonArray()), "A non-object response cannot be interpreted as a prepared Forge solo table");
        var foreign = wire.DeepClone(); foreign["defaultOpponentIds"] = new JsonArray("foreign-catalog-deck");
        var retired = wire.DeepClone(); retired["opponents"]!.AsArray().RemoveAt(2);
        check(RejectSelection(foreign) && RejectSelection(retired),
            "Mapped foreign or retired default handles are rejected against the exact current catalog before a roster can be selected");
        var malformedIdentity = wire.DeepClone(); malformedIdentity["opponents"]![0]!["compositionId"] = "invalid\nidentity";
        check(RejectSelection(malformedIdentity),
            "A decoded composition label still needs the semantic-identity contract before it can influence automatic seats");
    }
}
