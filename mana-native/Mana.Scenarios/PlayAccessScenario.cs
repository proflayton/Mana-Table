using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Authored permissions and physical occurrences, not a rules simulation. Route
// metadata comes from the fixture's explicit grant, never from parsing card text.
public static class PlayAccessScenario
{
    public const string Id = "play-access.v1";
    public const string ExileSpellId = "access-chandra-bears";
    public const string ExileLandId = "access-iteration-forest";
    public const string FlashbackId = "access-think-twice";
    public const string BorrowedId = "access-opponent-solemn";
    public const string PreparedId = "access-prepared-recall";
    public const string ChandraId = "access-chandra";
    public const string IterationId = "access-iteration";
    public const string HostageId = "access-hostage-taker";
    public const string MaskedExileId = "public-exile-masked";

    public static IReadOnlyList<ExperienceStage> Create()
    {
        var initial = Initial();
        var offTurn = RetainWithoutInitiation(initial) with {
            ActivePlayerId = 1, PriorityPlayerId = 1, DecisionOwnerId = 1,
            Decision = null, ActionClass = "priority"
        };
        var required = RetainWithoutInitiation(initial) with {
            PriorityPlayerId = null, ActionClass = "selection",
            Decision = new("access-required", new(Message: "Choose one card from your hand."),
                new EntitySelectionPayload(new("Confirm", false), new("Cancel", false), [], new(1, 1)))
        };
        required = required with { Players = required.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                Cards = zone.Cards.Select(card => card with { Selectable = true }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() };
        var removed = initial with {
            PlayAccesses = initial.PlayAccesses.Where(access => access.CardId != ExileSpellId).ToImmutableArray(),
            Players = initial.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone with {
                    Cards = zone.Cards.Select(card => card.VisualId == ExileSpellId ? card with { Selectable = false } : card).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var duplicate = initial with {
            PlayAccesses = initial.PlayAccesses.Select(access => access.CardId != FlashbackId ? access
                : new PlayAccess(access.PlayerId, access.Zone, access.CardId,
                    [.. access.Routes, new("snapcaster", PlayAccessKind.Cast, "access-snapcaster", "Snapcaster Mage", "Flashback {2}{U}", true)]))
                .ToImmutableArray()
        };
        var crowded = Many(initial);
        var pending = Cast(initial, ExileSpellId);
        var resolved = Resolve(pending, ExileSpellId, "Battlefield");
        var flashbackPending = Cast(initial, FlashbackId);
        var flashbackResolved = Resolve(flashbackPending, FlashbackId, "Exile");
        var landPlayed = Land(initial, ExileLandId);
        var publicZones = PublicZones(initial);
        var publicWithdrawal = publicZones with {
            PlayAccesses = publicZones.PlayAccesses.Where(access => access.CardId != FlashbackId).ToImmutableArray(),
            Players = publicZones.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Graveyard" ? zone : zone with {
                    Cards = zone.Cards.Where(card => card.VisualId != FlashbackId).ToImmutableArray(), Count = zone.Count - 1
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var publicRequired = RetainWithoutInitiation(publicZones) with {
            Decision = required.Decision, PriorityPlayerId = null, DecisionOwnerId = 0, ActionClass = "selection"
        };
        publicRequired = publicRequired with { Players = publicRequired.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                Cards = zone.Cards.Select(card => card with { Selectable = true }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() };
        return [
            new("Hand and retained access", "One hand, separate exile grants, flashback and an opponent-owned exiled card. The prepared copy stays linked to Emeritus.", initial),
            new("Another player's priority", "The grants remain visible while their routes cannot start. Reading does not claim priority.", offTurn),
            new("A required choice", "The current hand choice owns input. Retained permissions remain visible without becoming answers to that choice.", required),
            new("One grant expires", "Grizzly Bears remains a public exiled card, but its expired permission leaves the adjacent fan.", removed),
            new("Two routes, one physical card", "Think Twice has its own flashback and an authored Snapcaster grant. Read one occurrence to choose an exact current offer.", duplicate),
            new("Eleven in hand and many grants", "An eleven-card hand and twenty additional exiled occurrences retain their own hand and source packets. Unfold a packet to inspect its actual cards.", crowded),
            new("Spell on the stack", "The same physical spell leaves its actual source zone for the stack. This checkpoint starts with the exiled Grizzly Bears.", pending),
            new("Spell resolves", "The same physical card reaches its authored destination; its old permission does not return. This checkpoint puts Grizzly Bears on the battlefield.", resolved),
            new("Flashback on the stack", "The same Think Twice occurrence leaves the graveyard for the stack.", flashbackPending),
            new("Flashback resolves to exile", "The authored flashback result moves Think Twice to exile without retaining a play permission.", flashbackResolved),
            new("Land enters", "The exact land enters from its actual source zone without using the stack. This checkpoint plays the Forest granted by Expressive Iteration.", landPlayed),
            new("Thirty-seven cards in hand", "The same private hand unfolds into a physical spread. Inspect its first, middle and last occurrences without changing the hand or casting.", Many(initial, 37, 0)),
            new("Sixty cards playable elsewhere", "A thirty-seven-card hand and sixty granted occurrences remain distinct physical collections. Unfold a source packet and move across its spread.", Many(initial, 37, 56)),
            new("Two hundred cards playable elsewhere", "A large authored permission set for reviewing source packets and spatial navigation. All faces are already disclosed; no hidden cards or rules outcomes are inferred.", Many(initial, 37, 196)),
            new("Inspect actual public piles", "Large graveyards and exile contain mostly unplayable cards. Open a player's actual pile to inspect its current disclosed occurrences. Masked exile and count-only zones never disclose extra faces.", publicZones),
            new("A public face leaves", "Think Twice is no longer disclosed in the graveyard and its flashback grant is gone. A focused public-zone inspection must release its old face and action immediately.", publicWithdrawal),
            new("A required decision interrupts inspection", "The same large public piles remain, but a new hand choice owns input. Public inspection does not become an answer to that required choice.", publicRequired)
        ];
    }

    private static GameSnapshot Initial()
    {
        var bears = Face(ExileSpellId, "Grizzly Bears", "Creature — Bear", "{1}{G}", "", 2, 2) with { Selectable = true };
        var forest = Face(ExileLandId, "Forest", "Basic Land — Forest", "", "{T}: Add {G}.") with { Selectable = true };
        var think = Face(FlashbackId, "Think Twice", "Instant", "{1}{U}", "Draw a card.\nFlashback {2}{U}") with { Selectable = true };
        var solemn = Face(BorrowedId, "Solemn Simulacrum", "Artifact Creature — Golem", "{4}", "", 2, 2) with { Selectable = true };
        var recall = Face(PreparedId, "Ancestral Recall", "Instant", "{U}", "Target player draws three cards.") with { GamePieceType = "copied_spell" };
        Card[] local = [
            Face(ChandraId, "Chandra, Torch of Defiance", "Legendary Planeswalker — Chandra", "{2}{R}{R}", ""),
            Face(HostageId, "Hostage Taker", "Creature — Human Pirate", "{2}{U}{B}", "", 2, 3),
            Face("access-emeritus", "Emeritus of Ideation", "Creature — Human Wizard", "{3}{U}{U}", "", 5, 5) with {
                States = [new("prepared", "Prepared", "status", null, null, "Ancestral Recall {U}", "prepared")]
            },
            Face("access-island-a", "Island", "Basic Land — Island", "", "{T}: Add {U}."),
            Face("access-island-b", "Island", "Basic Land — Island", "", "{T}: Add {U}."),
            Face("access-mountain", "Mountain", "Basic Land — Mountain", "", "{T}: Add {R}.")
        ];
        var hand = Hand(7);
        var players = Enumerable.Range(0, 4).Select(seat => new Player(seat, seat == 0 ? "You" : "Opponent " + seat,
            40, seat == 0, false, ImmutableDictionary<string, int>.Empty, [
                new("Hand", seat == 0 ? hand.Length : 7, seat == 0 ? hand : [], null),
                new("Battlefield", seat == 0 ? local.Length : 1, seat == 0 ? local.ToImmutableArray()
                    : [Face("access-opponent-bear-" + seat, "Grizzly Bears", "Creature — Bear", "{1}{G}", "", 2, 2)], null),
                new("Command", 0, [], null), new("Library", 79, [], null),
                new("Graveyard", seat == 0 ? 2 : 0, seat == 0 ? [think, Face(IterationId, "Expressive Iteration", "Sorcery", "{U}{R}", "")] : [], null),
                new("Exile", seat == 0 ? 3 : seat == 1 ? 1 : 0,
                    seat == 0 ? [bears, forest, recall] : seat == 1 ? [solemn] : [], null)
            ])).ToImmutableArray();
        return new(Id, "playing", null, null, 0, 8, "Main phase", "MAIN1", 0, players, [], null, Priority()) {
            PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            PlayAccesses = [
                new(0, "Exile", ExileSpellId, [new("chandra", PlayAccessKind.Cast, ChandraId, "Chandra, Torch of Defiance", "Cast {1}{G}", true)]),
                new(0, "Exile", ExileLandId, [new("iteration", PlayAccessKind.PlayLand, IterationId, "Expressive Iteration", "Play land", true)]),
                new(0, "Graveyard", FlashbackId, [new("flashback", PlayAccessKind.Cast, FlashbackId, "Think Twice", "Flashback {2}{U}", true)]),
                new(1, "Exile", BorrowedId, [new("hostage", PlayAccessKind.Cast, HostageId, "Hostage Taker", "Cast {4}", true)])
            ]
        };
    }

    private static ImmutableArray<Card> Hand(int count)
    {
        (string Name, string Type, string Cost, int? Power, int? Toughness)[] faces = [
            ("Island", "Basic Land — Island", "", null, null), ("Mountain", "Basic Land — Mountain", "", null, null),
            ("Grizzly Bears", "Creature — Bear", "{1}{G}", 2, 2), ("Opt", "Instant", "{U}", null, null),
            ("Serra Angel", "Creature — Angel", "{3}{W}{W}", 4, 4), ("Llanowar Elves", "Creature — Elf Druid", "{G}", 1, 1),
            ("Forest", "Basic Land — Forest", "", null, null), ("Swamp", "Basic Land — Swamp", "", null, null),
            ("Plains", "Basic Land — Plains", "", null, null), ("Sol Ring", "Artifact", "{1}", null, null),
            ("Wall of Blossoms", "Creature — Plant Wall", "{1}{G}", 0, 4)
        ];
        return Enumerable.Range(0, count).Select(index => {
            var face = faces[index % faces.Length];
            return Face("access-hand-" + index, face.Name, face.Type, face.Cost, "", face.Power, face.Toughness)
                with { Selectable = true };
        }).ToImmutableArray();
    }

    private static GameSnapshot RetainWithoutInitiation(GameSnapshot state) => state with {
        PlayAccesses = state.PlayAccesses.Select(access => new PlayAccess(access.PlayerId, access.Zone, access.CardId,
            access.Routes.Select(route => route with { CanStart = false }).ToImmutableArray())).ToImmutableArray(),
        Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
            Cards = zone.Cards.Select(card => card with { Selectable = false }).ToImmutableArray()
        }).ToImmutableArray() }).ToImmutableArray()
    };

    private static GameSnapshot Many(GameSnapshot state, int handCount = 11, int additionalAccess = 20)
    {
        (string Name, string Type, string Cost)[] faces = [
            ("Opt", "Instant", "{U}"), ("Consider", "Instant", "{U}"),
            ("Lightning Strike", "Instant", "{1}{R}"), ("Abrade", "Instant", "{1}{R}"),
            ("Ponder", "Sorcery", "{U}"), ("Preordain", "Sorcery", "{U}"),
            ("Brainstorm", "Instant", "{U}"), ("Faithless Looting", "Sorcery", "{R}"),
            ("Shock", "Instant", "{R}"), ("Impulse", "Instant", "{1}{U}")
        ];
        var extra = Enumerable.Range(0, additionalAccess).Select(index => Face("access-overflow-" + index.ToString("D2"),
                faces[index % faces.Length].Name, faces[index % faces.Length].Type, faces[index % faces.Length].Cost, "")
            with { Selectable = true }).ToImmutableArray();
        return state with {
            Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name switch {
                    "Hand" => zone with { Cards = Hand(handCount), Count = handCount },
                    "Exile" => zone with { Cards = [.. zone.Cards, .. extra], Count = zone.Count + extra.Length }, _ => zone
                }).ToImmutableArray()
            }).ToImmutableArray(),
            PlayAccesses = [.. state.PlayAccesses, .. extra.Select(card => new PlayAccess(0, "Exile", card.VisualId,
                [new("overflow-" + card.VisualId, PlayAccessKind.Cast, ChandraId, "Chandra, Torch of Defiance", "Cast " + card.ManaCost, true)]))]
        };
    }

    private static GameSnapshot PublicZones(GameSnapshot state)
    {
        var masked = new Card { VisualId = MaskedExileId, CombatId = MaskedExileId, FaceDown = true, States = [] };
        return state with {
            Players = state.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => (player.Id, zone.Name) switch {
                    (0, "Graveyard") => zone with { Cards = [.. zone.Cards, .. PublicHistory("public-local-grave", 35)], Count = 37 },
                    (0, "Exile") => zone with { Cards = [.. zone.Cards, .. PublicHistory("public-local-exile", 33), masked], Count = 37 },
                    (1, "Graveyard") => zone with { Cards = PublicHistory("public-opponent-grave", 11), Count = 11 },
                    (1, "Exile") => zone with { Cards = [.. zone.Cards, .. PublicHistory("public-opponent-exile", 36)], Count = 37 },
                    (2, "Exile") => zone with { Cards = [], Count = 6 },
                    _ => zone
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static ImmutableArray<Card> PublicHistory(string prefix, int count)
    {
        (string Name, string Type, string Cost)[] faces = [
            ("Swords to Plowshares", "Instant", "{W}"), ("Cultivate", "Sorcery", "{2}{G}"),
            ("Counterspell", "Instant", "{U}{U}"), ("Nature's Lore", "Sorcery", "{1}{G}"),
            ("Arcane Signet", "Artifact", "{2}"), ("Beast Within", "Instant", "{2}{G}"),
            ("Farseek", "Sorcery", "{1}{G}"), ("Lightning Bolt", "Instant", "{R}"),
            ("Command Tower", "Land", ""), ("Path to Exile", "Instant", "{W}")
        ];
        return Enumerable.Range(0, count).Select(index => {
            var face = faces[index % faces.Length];
            return Face(prefix + "-" + index.ToString("D2"), face.Name, face.Type, face.Cost, "");
        }).ToImmutableArray();
    }

    internal static Decision Priority() => new("access-priority", new(Message: "You have priority."), new PriorityPayload(new("Pass", true)));

    internal static GameSnapshot Cast(GameSnapshot state, string cardId)
    {
        var access = state.PlayAccesses.Single(item => item.CardId == cardId);
        var card = state.Players.Single(player => player.Id == access.PlayerId).Zone(access.Zone).Cards.Single(item => item.VisualId == cardId);
        return CastFrom(state, access.PlayerId, access.Zone, card);
    }

    internal static GameSnapshot CastFrom(GameSnapshot state, int owner, string zone, Card card)
    {
        var next = Remove(state, owner, zone, card.VisualId);
        return next with {
            Stack = [new(card.Name, card.Text, false, card) { Id = "access-stack-" + card.VisualId, ControllerId = 0 }],
            Activity = [.. state.Activity, new(state.Activity.Length + 1, state.Turn, state.PhaseKey, "cast", 0, card.Name, card.VisualId, card.Name)],
            Decision = Priority(), DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "priority"
        };
    }

    internal static GameSnapshot Resolve(GameSnapshot state, string cardId, string destination)
    {
        var card = state.Stack.Single(item => item.Card?.VisualId == cardId).Card! with { Selectable = false };
        // The authored borrowed permanent is controlled locally; ordinary spells
        // and this flashback result are owned locally in this fixture.
        return Add(state with { Stack = [], Decision = Priority() }, 0, destination, card);
    }

    internal static GameSnapshot Land(GameSnapshot state, string cardId)
    {
        var access = state.PlayAccesses.Single(item => item.CardId == cardId);
        var card = state.Players.Single(player => player.Id == access.PlayerId).Zone(access.Zone).Cards.Single(item => item.VisualId == cardId);
        return LandFrom(state, access.PlayerId, access.Zone, card);
    }

    internal static GameSnapshot LandFrom(GameSnapshot state, int owner, string zone, Card card)
    {
        return Add(Remove(state, owner, zone, card.VisualId), 0, "Battlefield", card with { Selectable = false }) with {
            Activity = [.. state.Activity, new(state.Activity.Length + 1, state.Turn, state.PhaseKey, "land", 0, card.Name, card.VisualId, card.Name)]
        };
    }

    private static GameSnapshot Remove(GameSnapshot state, int owner, string source, string id) => state with {
        PlayAccesses = state.PlayAccesses.Where(access => access.CardId != id).ToImmutableArray(),
        Players = state.Players.Select(player => player.Id != owner ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != source ? zone : zone with {
                Cards = zone.Cards.Where(card => card.VisualId != id).ToImmutableArray(), Count = zone.Count - 1
            }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static GameSnapshot Add(GameSnapshot state, int owner, string destination, Card card) => state with {
        Players = state.Players.Select(player => player.Id != owner ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != destination ? zone : zone with {
                Cards = [.. zone.Cards, card], Count = zone.Count + 1
            }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static Card Face(string id, string name, string type, string cost, string text, int? power = null, int? toughness = null) => new() {
        VisualId = id, CombatId = id, Name = name, ArtName = name, Type = type, ManaCost = cost, Text = text,
        Power = power, Toughness = toughness, States = []
    };
}
