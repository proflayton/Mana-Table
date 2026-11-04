using System.Collections.Immutable;
using System.Security.Cryptography;
using Mana.Contracts;

namespace Mana.Table;

internal static class ForgePublicProjectionFactChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "expected-public-fact-rejections");
        Directory.CreateDirectory(directory);
        var state = Fixture();
        var mutations = new (string Name, Func<GameSnapshot, GameSnapshot> Change)[] {
            ("turn", value => value with { Turn = value.Turn + 1 }),
            ("phase", value => value with { PhaseKey = "COMBAT_DECLARE_BLOCKERS" }),
            ("active player", value => value with { ActivePlayerId = 2 }),
            ("stack count", value => value with { Stack = value.Stack.RemoveAt(0) }),
            ("stack order", value => value with { Stack = value.Stack.Reverse().ToImmutableArray() }),
            ("stack occurrence", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { Id = "replacement-trigger" }) }),
            ("stack execution", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { ExecutionId = "replacement-execution" }) }),
            ("stack controller", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { ControllerId = 3 }) }),
            ("spell versus ability", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with { Kind = StackActionKind.Spell }) }),
            ("public card target", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with {
                Targets = value.Stack[0].Targets.SetItem(0, new("card", "c", "Another public creature", 2)) }) }),
            ("player target", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with {
                Targets = value.Stack[0].Targets.SetItem(1, new("player", "2", "Another player", 2)) }) }),
            ("public spell-card target", value => value with { Stack = value.Stack.SetItem(0, value.Stack[0] with {
                Targets = value.Stack[0].Targets.RemoveAt(2) }) }),
            ("attacker occurrence", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { CardId = "c" }) } }),
            ("blocker topology", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { BlockerIds = ["c"] }) } }),
            ("defending player", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { DefendingPlayerId = 2 }) } }),
            ("defending permanent", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(1, value.Combat.Attackers[1] with { Defender = new("card", "b", 1, "Other defender") }) } }),
            ("departed defender", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(1, value.Combat.Attackers[1] with { HasDefender = false, Defender = null }) } }),
            ("blocked fact", value => value with { Combat = value.Combat! with {
                Attackers = value.Combat.Attackers.SetItem(0, value.Combat.Attackers[0] with { Blocked = false }) } }),
            ("combat presence", value => value with { Combat = null }),
            ("permanent printed identity", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { Name = "Different creature" })),
            ("permanent current face", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { ArtFace = "back" })),
            ("permanent concealed state", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { FaceDown = true })),
            ("permanent controller", MoveController),
            ("attachment parent", value => ChangeCard(value, 0, "Battlefield", "equipment", card => card with { AttachedTo = new("c", "Other", "Creature") })),
            ("attached occurrence", value => ChangeCard(value, 0, "Battlefield", "a", card => card with { Attachments = [] })),
            ("public graveyard identity", value => ChangeCard(value, 0, "Graveyard", "grave-0-0", card => card with { Name = "Different graveyard card" })),
            ("graveyard order", value => ChangeZone(value, 0, "Graveyard", zone => zone with { Cards = zone.Cards.Reverse().ToImmutableArray() })),
            ("public exile identity", value => ChangeCard(value, 0, "Exile", "exile-0", card => card with { Name = "Different exiled card" })),
            ("command identity", value => ChangeCard(value, 0, "Command", "commander-0", card => card with { Name = "Different commander" })),
            ("player resource", value => ChangePlayer(value, 0, player => player with { Counters = player.Counters.SetItem("energy", 8) })),
            ("land allowance", value => ChangePlayer(value, 0, player => player with { LandPlaysUsed = 1 })),
            ("hand limit", value => ChangePlayer(value, 0, player => player with { MaxHandSize = null })),
            ("effective public ability", value => ChangeCard(value, 0, "Battlefield", "a", card => card with {
                States = [new("flying", "Flying", "keyword", null, null, "Public description", "Flying")] }))
        };
        foreach (var (name, change) in mutations)
            check(!AiWholeSeatEngineCheck.ComparePublicProjection(state, change(state), directory),
                "Same-publication audience agreement rejects a changed " + name + " without relying on equal life totals or zone counts");

        var perspective = Perspective(state);
        check(AiWholeSeatEngineCheck.ComparePublicProjection(state, perspective, directory),
            "The same public game tolerates other viewers' private hand/library/face-down disclosures, opaque keys, priority and decision permissions, localized prose and table ordering");
        check(AiWholeSeatEngineCheck.ComparePublicProjection(perspective, state, directory),
            "Public agreement is symmetric when neither audience is a spectator and each retains different private knowledge");

        bool Refuses(GameSnapshot other) {
            try { AiWholeSeatEngineCheck.ComparePublicProjection(state, other, directory); return false; }
            catch (ArgumentException) { return true; }
        }
        check(Refuses(state with { Id = "replacement-game" }) && Refuses(state with { BoardRevision = state.BoardRevision + 1 }),
            "The shared oracle refuses cross-game and cross-publication comparisons instead of labelling sequential frames a desync");

        string receipt = Path.Combine(directory, "whole-seat-ai-projection-mismatch.json");
        string firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receipt)));
        _ = AiWholeSeatEngineCheck.ComparePublicProjection(state, state with { Turn = 99 }, directory);
        check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receipt))) == firstHash
                && !File.ReadAllText(receipt).Contains("PRIVATE_NOT_A_PUBLIC_FACT", StringComparison.Ordinal),
            "Expected mismatch evidence preserves its first observation and excludes private canary faces and action keys");
    }

    private static GameSnapshot Fixture()
    {
        Card Face(string id, string name, string type = "Creature") => new() {
            VisualId = id, CombatId = id, Name = name, Type = type, Power = 2, Toughness = 2,
            States = [], Counters = ImmutableDictionary<string, int>.Empty
        };
        var players = Enumerable.Range(0, 4).Select(id => new Player(id, "Player " + id, 40, false, false,
            ImmutableDictionary<string, int>.Empty.Add("U", 1), [
                new("Battlefield", 0, [], null),
                new("Graveyard", 2, [Face("grave-" + id + "-0", "First grave card"), Face("grave-" + id + "-1", "Second grave card")], null),
                new("Exile", 2, [Face("exile-" + id, "Public exile card")], null),
                new("Command", 1, [Face("commander-" + id, "Commander " + id)], null),
                new("Hand", 3, [], null), new("Library", 40, [], null)
            ]) { TeamId = id, LandPlaysUsed = 0, LandPlayLimit = 1,
                Counters = ImmutableDictionary<string, int>.Empty.Add("energy", 2),
                States = [new("energy", "Energy", "resource", 2, null, "Public resource text")] }).ToImmutableArray();
        var state = new GameSnapshot("public-facts-game", "playing", null, null, 0, 12, "Main phase", "MAIN1", 0,
            players, [
                new("Public source", "A trigger", StackActionKind.TriggeredAbility, Face("departed-portrait", "Historical source")) {
                    Id = "stack-trigger", ExecutionId = "execution-trigger", ControllerId = 0,
                    Targets = [new("card", "b", "Blocker", 1), new("player", "1", "Player 1", 1), new("card", "spell-portrait", "A public spell", 2)] },
                new("Public spell", "Spell prose", StackActionKind.Spell, Face("spell-portrait", "Public spell", "Instant")) {
                    Id = "stack-spell", ExecutionId = "execution-spell", ControllerId = 2, Targets = [new("card", "walker", "Walker", 1)] }
            ], new([
                new("a", 1, ["b"], ["b"]) { Blocked = true, Defender = new("player", "1", 1, "Player 1") },
                new("c", 1, [], []) { Defender = new("card", "walker", 1, "Walker") }
            ], [], null) { DeclarationId = "viewer-0-declaration" }, null) { BoardRevision = 42, Revision = 100 };
        state = ChangeZone(state, 0, "Battlefield", zone => zone with { Count = 3, Cards = [
            Face("a", "Attacker") with { Attacking = true, Attachments = [new("equipment", "Equipment", "Artifact")] },
            Face("equipment", "Equipment", "Artifact Equipment") with { AttachedTo = new("a", "Attacker", "Creature") },
            Face("morph", "Face-down card") with { FaceDown = true }
        ] });
        state = ChangeZone(state, 1, "Battlefield", zone => zone with { Count = 2, Cards = [Face("b", "Blocker") with { Blocking = true }, Face("walker", "Walker", "Planeswalker")] });
        state = ChangeZone(state, 2, "Battlefield", zone => zone with { Count = 1, Cards = [Face("c", "Other attacker") with { Attacking = true }] });
        return ChangeZone(state, 0, "Hand", zone => zone with {
            Cards = [Face("PRIVATE_NOT_A_PUBLIC_FACT", "PRIVATE_NOT_A_PUBLIC_FACT") with { Key = "PRIVATE_NOT_A_PUBLIC_FACT" }] });
    }

    private static GameSnapshot Perspective(GameSnapshot state)
    {
        state = state with { ViewerId = 1, Revision = 700, Status = "resolving", Phase = "Localized phase",
            PriorityPlayerId = 3, DecisionOwnerId = 2, Notices = ["A local notice"],
            Stack = state.Stack.Select(item => item with { Name = "Localized source", Text = "Localized stack prose",
                Card = item.Card! with { Name = "A private historical portrait", Key = "viewer-private-key" },
                Targets = item.Targets.Reverse().Select(target => target with { Name = "You" }).ToImmutableArray() }).ToImmutableArray(),
            Combat = state.Combat! with { DeclarationId = "viewer-1-declaration", AttackValid = false,
                BlockProblem = "Another audience's problem", AttackOptions = [new("c", [new("player", "3", 3, "You")])],
                Attackers = state.Combat.Attackers.Reverse().Select(attack => attack with {
                    EligibleBlockerIds = ["private-option"], Defender = attack.Defender! with { Name = "You" }
                }).ToImmutableArray() }
        };
        state = state with { Players = state.Players.Reverse().Select(player => player with {
            Name = player.Id == 1 ? "You" : "Localized player", Priority = player.Id == 3,
            States = player.States.Select(value => value with { Label = "Localized label", Detail = "Localized resource text" }).ToImmutableArray(),
            Zones = player.Zones.Reverse().Select(zone => zone.Name switch {
                "Hand" => zone with { Cards = player.Id == 1 ? [new Card { Name = "PRIVATE_NOT_A_PUBLIC_FACT", Key = "private-hand" }] : [] },
                "Library" => zone with { Cards = [new Card { Name = "PRIVATE_NOT_A_PUBLIC_FACT" }], TopCard = new() { Name = "PRIVATE_NOT_A_PUBLIC_FACT" } },
                "Exile" => zone with { Cards = zone.Cards.Add(new() { FaceDown = true, Name = "PRIVATE_NOT_A_PUBLIC_FACT", VisualId = "private-exile" }) },
                "Battlefield" => zone with { Cards = zone.Cards.Reverse().Select(card => card with {
                    Key = "different-opaque-key", Selectable = true, Highlighted = true, Text = "Localized rules prose",
                    Name = card.FaceDown ? "PRIVATE_NOT_A_PUBLIC_FACT" : card.Name,
                    Type = card.FaceDown ? "Secret printed type" : card.Type,
                    ManaCost = card.FaceDown ? "{8}" : card.ManaCost,
                    VisualId = card.FaceDown ? "private-morph-visual" : card.VisualId,
                    OtherFace = card.FaceDown ? new("Secret back", "Secret type", "{9}", "Secret text", 9, 9, "Secret art", "back") : card.OtherFace,
                    AttachedTo = card.AttachedTo is { } parent ? parent with { Name = "Localized parent", Type = "Localized type" } : null,
                    Attachments = card.Attachments.Select(value => value with { Name = "Localized child", Type = "Localized type" }).ToImmutableArray()
                }).ToImmutableArray() },
                _ => zone
            }).ToImmutableArray()
        }).ToImmutableArray() };
        return state;
    }

    private static GameSnapshot ChangePlayer(GameSnapshot state, int id, Func<Player, Player> change) => state with {
        Players = state.Players.Select(player => player.Id == id ? change(player) : player).ToImmutableArray()
    };
    private static GameSnapshot ChangeZone(GameSnapshot state, int id, string name, Func<Zone, Zone> change) => ChangePlayer(state, id,
        player => player with { Zones = player.Zones.Select(zone => zone.Name == name ? change(zone) : zone).ToImmutableArray() });
    private static GameSnapshot ChangeCard(GameSnapshot state, int id, string zone, string cardId, Func<Card, Card> change) => ChangeZone(state, id, zone,
        value => value with { Cards = value.Cards.Select(card => card.CombatId == cardId ? change(card) : card).ToImmutableArray() });
    private static GameSnapshot MoveController(GameSnapshot state)
    {
        var card = state.Players.Single(player => player.Id == 0).Zone("Battlefield").Cards.Single(value => value.CombatId == "a");
        state = ChangeZone(state, 0, "Battlefield", zone => zone with { Count = zone.Count - 1, Cards = zone.Cards.Remove(card) });
        return ChangeZone(state, 2, "Battlefield", zone => zone with { Count = zone.Count + 1, Cards = zone.Cards.Add(card) });
    }
}
