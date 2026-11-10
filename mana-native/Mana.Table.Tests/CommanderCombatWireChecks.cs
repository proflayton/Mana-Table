using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Exercise the actual provider mapper and command owner. Lineage is public
// rules evidence, not a current source occurrence or selectable action key.
internal static class CommanderCombatWireChecks
{
    private const string Alpha = "commander:opaque-alpha", Beta = "commander:opaque-beta";
    private static JsonNode Wire() => JsonNode.Parse("""
        {"id":"commander-wire","status":"playing","viewerId":0,"revision":4,"boardRevision":3,
         "turn":6,"phaseKey":"MAIN1","activePlayerId":0,"decisionOwnerId":0,"actionClass":"selection",
         "commanderCombat":{"damageLimit":21},"stack":[],
         "players":[{"id":0,"name":"Controller","life":40,"mana":{},
           "commanderDamage":[{"name":"Old printed alpha","ownerId":1,"owner":"Owner","damage":20,"commanderId":"commander:opaque-alpha"},
             {"name":"Old printed beta","ownerId":1,"owner":"Owner","damage":2,"commanderId":"commander:opaque-beta"}],
           "zones":[{"name":"Battlefield","count":2,"cards":[
             {"name":"Renamed leader","type":"Creature","visualId":"stolen-current","combatId":"stolen-combat",
              "key":"blocked-current-key","commanderId":"commander:opaque-alpha","commanderTax":8,"power":4,"toughness":4},
             {"name":"Renamed leader","type":"Creature","visualId":"same-name-copy","combatId":"copy-combat",
              "key":"legal-copy-key","selectable":true,"power":4,"toughness":4}]}]},
           {"id":1,"name":"Owner","life":40,"mana":{},
            "commanderDamage":[{"name":"Different display alpha","ownerId":1,"owner":"Owner","damage":0,"commanderId":"commander:opaque-alpha"},
              {"name":"Different display beta","ownerId":1,"owner":"Owner","damage":20,"commanderId":"commander:opaque-beta"}],
            "zones":[{"name":"Command","count":1,"cards":[
              {"name":"Partner leader","type":"Legendary Creature","visualId":"partner-current","combatId":"partner-combat",
               "key":"partner-current-key","commanderId":"commander:opaque-beta","commanderTax":4}]}]}],
         "prompt":{"id":"choose-current-target","kind":"input","task":"selection","purpose":"targets",
           "min":1,"max":1,"okEnabled":false,"cancelEnabled":true,"playerChoices":[]}}
        """)!;
    private static JsonNode Card(JsonNode wire) => wire["players"]![0]!["zones"]![0]!["cards"]![0]!;
    private static JsonArray Rows(JsonNode wire, int player = 0) => wire["players"]![player]!["commanderDamage"]!.AsArray();
    private static GameSnapshot Decode(JsonNode wire) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(wire))!;
    private static bool Reject(JsonNode wire) { try { Decode(wire); return false; } catch (IOException) { return true; } }

    internal static void Run(Action<bool, string> check)
    {
        var state = Decode(Wire());
        var stolen = state.Viewer!.Zone("Battlefield").Cards[0];
        var copy = state.Viewer.Zone("Battlefield").Cards[1];
        var partner = state.Players[1].Zone("Command").Cards[0];
        check(state.CommanderCombat is { DamageLimit: 21 } && stolen.CommanderId == Alpha && partner.CommanderId == Beta
                && state.Players[0].CommanderDamage.Select(row => (row.CommanderId, row.Damage)).SequenceEqual([(Alpha, 20), (Beta, 2)])
                && state.Players[1].CommanderDamage.Select(row => (row.CommanderId, row.Damage)).SequenceEqual([(Alpha, 0), (Beta, 20)]),
            "The shipping decoder preserves each defender's separate partner totals and the provider's actual commander-damage threshold");
        check(stolen.Name == copy.Name && copy.CommanderId.Length == 0
                && state.Players[0].CommanderDamage.All(row => row.OwnerId == 1)
                && state.Players[0].Zone("Battlefield").Cards.Contains(stolen) && stolen.CommanderTax == 8
                && state.Players[0].CommanderDamage[0].Name != stolen.Name,
            "A stolen renamed commander retains original lineage/owner while a same-named copy, current controller, display name and tax cannot invent a binding");

        foreach (bool explicitNull in new[] { false, true }) {
            var legacy = Legacy();
            if (explicitNull) legacy["commanderCombat"] = null; else legacy.AsObject().Remove("commanderCombat");
            var old = Decode(legacy);
            check(old.CommanderCombat == null && old.Players[0].CommanderDamage[0] is { CommanderId.Length: 0, Damage: 20 }
                    && old.Players[0].Zone("Battlefield").Cards[0].CommanderId.Length == 0,
                "Missing or explicitly null legacy rules/lineage remain unknown while old presentation damage rows stay readable");
        }
        var disabled = Wire(); disabled["commanderCombat"]!["damageLimit"] = null;
        check(Decode(disabled).CommanderCombat is { DamageLimit: null },
            "An explicit null damage limit is known inapplicable, distinct from a missing rules object");
        var variant = Wire(); variant["commanderCombat"]!["damageLimit"] = 17;
        check(Decode(variant).CommanderCombat?.DamageLimit == 17,
            "The decoder preserves a provider-owned format threshold rather than hardcoding21");
        foreach (bool explicitNull in new[] { false, true }) {
            var partial = Wire();
            if (explicitNull) { Card(partial)["commanderId"] = null; Rows(partial)[0]!["commanderId"] = null; }
            else { Card(partial).AsObject().Remove("commanderId"); Rows(partial)[0]!.AsObject().Remove("commanderId"); }
            var decoded = Decode(partial);
            check(decoded.Viewer!.Zone("Battlefield").Cards[0].CommanderId.Length == 0
                    && decoded.Viewer.CommanderDamage[0].CommanderId.Length == 0,
                "Absent/null card and damage-row IDs normalize to empty unknown facts without joining by their names");
        }
        var partialRows = Wire(); Rows(partialRows).RemoveAt(1); Rows(partialRows, 1).Clear();
        var partialState = Decode(partialRows);
        check(partialState.Viewer!.CommanderDamage.Length == 1 && partialState.Players[1].CommanderDamage.IsEmpty,
            "Partial defender rows remain partial; missing partner/defender totals never become invented zero-damage rows");

        var malformed = new List<(string Name, Action<JsonNode> Change)> {
            ("missing required damage limit", node => node["commanderCombat"] = new JsonObject()),
            ("lineage without rules", node => node.AsObject().Remove("commanderCombat")),
            ("lineage with null rules", node => node["commanderCombat"] = null),
            ("negative damage", node => Rows(node)[0]!["damage"] = -1),
            ("null damage row", node => Rows(node).Add((JsonNode?)null)),
            ("null damage array", node => node["players"]![0]!["commanderDamage"] = null),
            ("unregistered lineage owner", node => Rows(node)[0]!["ownerId"] = 9),
            ("duplicate lineage for one defender", node => Rows(node).Add(Rows(node)[0]!.DeepClone())),
            ("contradictory lineage owners across defenders", node => Rows(node, 1)[0]!["ownerId"] = 0),
            ("contradictory current visual binding", node => node["players"]![0]!["zones"]![0]!["cards"]!.AsArray()
                .Add(new JsonObject { ["visualId"] = "stolen-current", ["commanderId"] = Beta })),
            ("face-down lineage", node => Card(node)["faceDown"] = true),
            ("combat-only identity", node => Card(node)["visualId"] = ""),
            ("missing current visual identity", node => Card(node).AsObject().Remove("visualId"))
        };
        foreach (var (name, change) in malformed) { var node = Wire(); change(node); check(Reject(node), "Commander-combat admission rejects " + name); }
        foreach (var bad in new JsonNode[] { JsonValue.Create(0)!, JsonValue.Create(-1)!, JsonValue.Create(21.5)!,
            JsonValue.Create("21")!, JsonValue.Create(true)!, JsonValue.Create(2147483648L)!, new JsonArray() }) {
            var node = Wire(); node["commanderCombat"]!["damageLimit"] = bad.DeepClone();
            check(Reject(node), "A malformed or nonpositive provider damage limit fails actual wire admission");
        }
        foreach (var bad in new JsonNode[] { JsonValue.Create(21)!, JsonValue.Create(true)!, JsonValue.Create("rules")!, new JsonArray() }) {
            var node = Wire(); node["commanderCombat"] = bad.DeepClone();
            check(Reject(node), "The optional commander-combat rules object cannot decode from another JSON type");
        }
        foreach (var bad in new JsonNode[] { JsonValue.Create(" ")!, JsonValue.Create("bad\u0001id")!, JsonValue.Create(new string('x', 129))!,
            JsonValue.Create(7)!, JsonValue.Create(true)!, new JsonArray(), new JsonObject() }) {
            var card = Wire(); Card(card)["commanderId"] = bad.DeepClone();
            var row = Wire(); Rows(row)[0]!["commanderId"] = bad.DeepClone();
            check(Reject(card) && Reject(row), "Both current-card and historical-row lineage IDs reject invalid types, controls, blank or oversized values");
        }
        foreach (string bad in new[] { " ", "bad\u0001visual", new string('v', 129) }) {
            var node = Wire(); Card(node)["visualId"] = bad;
            check(Reject(node), "A public lineage requires its separately valid bounded current visual identity");
        }
        var boundary = Wire(); string longest = new('x', 128); Card(boundary)["commanderId"] = longest;
        foreach (int player in new[] { 0, 1 }) Rows(boundary, player)[0]!["commanderId"] = longest;
        check(Decode(boundary).Viewer!.Zone("Battlefield").Cards[0].CommanderId == longest,
            "A valid128-character opaque lineage survives actual wire mapping without truncation or name parsing");
        var maxRows = Wire(); Rows(maxRows).Clear();
        for (int row = 0; row < 64; row++) Rows(maxRows).Add(new JsonObject {
            ["name"] = "Historical commander", ["ownerId"] = 1, ["owner"] = "Owner", ["damage"] = 0, ["commanderId"] = "historical-" + row });
        check(Decode(maxRows).Viewer!.CommanderDamage.Length == 64,
            "The decoder admits the bounded64-row historical commander set without requiring those sources to be in play");
        Rows(maxRows).Add(Rows(maxRows)[0]!.DeepClone()); Rows(maxRows)[64]!["commanderId"] = "overflow";
        check(Reject(maxRows), "A65th damage row fails the bounded provider contract even when its identity is otherwise unique");

        foreach (string zone in new[] { "Hand", "Library", "Graveyard", "Exile", "Stack", "Sideboard" }) {
            var node = Wire(); node["players"]![0]!["zones"]![0]!["name"] = zone;
            check(Reject(node), "Current commander lineage cannot escape its public Battlefield/Command binding through " + zone);
        }
        var hidden = Wire(); Card(hidden).AsObject().Remove("commanderId"); Card(hidden)["faceDown"] = true;
        Card(hidden)["name"] = "Face-down card"; Card(hidden)["visualId"] = "";
        check(Decode(hidden).Viewer!.Zone("Battlefield").Cards[0] is { FaceDown: true, CommanderId.Length: 0 },
            "An anonymous battlefield occurrence stays usable without exposing or inferring a hidden commander's lineage");
        var top = Wire(); var topZone = top["players"]![0]!["zones"]![0]!;
        var topCard = Card(top).DeepClone(); topZone["cards"] = new JsonArray(); topZone["name"] = "Library"; topZone["topCard"] = topCard;
        check(Reject(top), "A disclosed library top cannot borrow a public current commander binding");
        Portraits(check);
        Authority(check);
    }

    private static JsonNode Legacy()
    {
        var node = Wire();
        foreach (var player in node["players"]!.AsArray()) {
            foreach (var row in player!["commanderDamage"]!.AsArray()) row!.AsObject().Remove("commanderId");
            foreach (var zone in player["zones"]!.AsArray()) foreach (var card in zone!["cards"]!.AsArray()) card!.AsObject().Remove("commanderId");
        }
        return node;
    }
    private static void Portraits(Action<bool, string> check)
    {
        var portrait = Wire();
        portrait["prompt"]!["sourceCard"] = Card(portrait).DeepClone();
        portrait["prompt"]!["sourceCard"]!.AsObject().Remove("key");
        portrait["prompt"]!["sourceCardId"] = "stolen-current";
        portrait["prompt"]!["sourceControllerId"] = 0; portrait["prompt"]!["sourceZone"] = "Battlefield";
        check(Decode(portrait).Decision!.Context.Source!.Card!.CommanderId == Alpha,
            "A current disclosed source portrait may repeat lineage already bound to the exact public zoned visual");
        foreach (var (field, value) in new[] { ("visualId", "departed-visual"), ("commanderId", Beta) }) {
            var node = portrait.DeepClone(); node["prompt"]!["sourceCard"]![field] = value;
            check(Reject(node), "A source portrait cannot create a commander binding with mismatched " + field);
        }
        var concealed = portrait.DeepClone(); concealed["prompt"]!["sourceCard"]!["faceDown"] = true;
        check(Reject(concealed), "A concealed decision portrait cannot launder a current public lineage into private reading");
        var external = Wire(); external["prompt"] = JsonNode.Parse("""{"id":"priority","kind":"input","task":"priority","okEnabled":true}""");
        external["actionSources"] = JsonNode.Parse("""
            [{"decisionId":"priority","zone":"Sideboard","playerId":0,
              "card":{"name":"Authorized external source","key":"external-key","visualId":"external-visual"}}]
            """);
        check(Decode(external).ActionSources.Single().Card.CommanderId.Length == 0,
            "An independently authorized external source remains usable with unknown commander lineage");
        external["actionSources"]![0]!["card"]!["commanderId"] = Alpha;
        check(Reject(external), "External action-source authority cannot invent a public zoned commander binding");
    }
    private static void Authority(Action<bool, string> check)
    {
        var scope = new ForgeCommandScope();
        var state = scope.ObserveRaw(JsonSerializer.SerializeToElement(Wire()))!;
        bool RejectCommand(GameCommand command) { try { scope.Encode(command); return false; } catch (EngineCommandRejectedException) { return true; } }
        check(RejectCommand(new SelectCard(state.Id, state.Decision!.Id, Alpha))
                && RejectCommand(new SelectCard(state.Id, state.Decision.Id, "blocked-current-key"))
                && RejectCommand(new PlayCard(state.Id, state.Decision.Id, Alpha))
                && RejectCommand(new UseOfferedAction(state.Id, state.Decision.Id, Alpha, Alpha))
                && !scope.CanReadSource(state.Id, state.Decision.Id, Alpha),
            "The real command owner grants no selection, cast, offered action or source query from commander lineage or tax");
        check((string)scope.Encode(new SelectCard(state.Id, state.Decision.Id, "legal-copy-key"))["key"]! == "legal-copy-key",
            "A same-named noncommander copy retains only its independently offered exact current selection key");
        var malformed = Wire(); malformed["commanderCombat"] = new JsonObject();
        bool rejected = false; try { scope.ObserveRaw(JsonSerializer.SerializeToElement(malformed)); } catch (IOException) { rejected = true; }
        check(rejected && RejectCommand(new SelectCard(state.Id, state.Decision.Id, "legal-copy-key")),
            "Malformed commander facts retire prior command authority at the existing snapshot boundary rather than leaving an old selection live");
    }
}
