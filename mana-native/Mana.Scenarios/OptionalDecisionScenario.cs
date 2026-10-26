using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

// Authored observations of distinct decisions. Stage navigation never searches,
// chooses a target or resolves a trigger; real rules coverage lives in forge-api.
public static class OptionalDecisionScenario
{
    public const string Id = "optional-decisions.v1";
    public static IReadOnlyList<ExperienceStage> Create()
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        var toothy = new Card {
            VisualId = "optional-toothy", CombatId = "optional-toothy",
            Name = "Toothy, Imaginary Friend", Type = "Legendary Creature — Illusion",
            ManaCost = "{3}{U}", Power = 1, Toughness = 1,
            Text = "Partner with Pir, Imaginative Rascal\nWhenever you draw a card, put a +1/+1 counter on Toothy, Imaginary Friend."
        };
        var pir = new Card {
            VisualId = "optional-pir", CombatId = "optional-pir", Name = "Pir, Imaginative Rascal",
            Type = "Legendary Creature — Human", ManaCost = "{2}{G}", Power = 1, Toughness = 1
        };
        var table = baseline with {
            Id = "optional-decisions-lab", ActivePlayerId = 0, PriorityPlayerId = null,
            DecisionOwnerId = 0, ActionClass = "selection",
            Players = baseline.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name switch {
                    "Battlefield" => zone with { Cards = [.. zone.Cards, toothy], Count = zone.Count + 1 },
                    "Command" => new Zone("Command", 1, [pir], null), _ => zone
                }).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = new("optional-target",
                new(Message: "Target player may search their library for Pir, Imaginative Rascal, reveal it, put it into their hand, then shuffle.",
                    Source: new(toothy.VisualId, toothy, 0, "Battlefield")),
                new TargetSelectionPayload(new("Confirm", false), new("Cancel", false), new(1, 1), [0, 1, 2],
                    Search: new("Hand", pir.Name)))
        };
        GameSnapshot Offer(int viewer, int owner) => table with {
            ViewerId = viewer, DecisionOwnerId = viewer, ActionClass = "confirm",
            // Changing viewers removes the previous viewer's authorized hand.
            Players = table.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" || player.Id == viewer ? zone
                    : zone with { Cards = [] }).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = new("optional-search-" + viewer,
                table.Decision!.Context with { Message = "You may search your library for Pir, Imaginative Rascal, reveal it, put it into your hand, then shuffle." },
                new ConfirmationPayload(new("Search library", true), new("Don't search", true), new(owner, "Hand", pir.Name)))
        };
        GameSnapshot Opening(bool free) => baseline with {
            Id = "optional-decisions-lab", Turn = 0, Phase = "Opening hand", PhaseKey = "",
            ActivePlayerId = null, PriorityPlayerId = null, DecisionOwnerId = 0, ActionClass = "confirm",
            Players = baseline.Players.Select(player => player with {
                Mana = ImmutableDictionary<string, int>.Empty,
                Zones = player.Zones.Select(zone => zone.Name is "Battlefield" or "Graveyard"
                    ? zone with { Count = 0, Cards = [], TopCard = null } : zone).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = new("optional-opening-" + free,
                new(Message: "Keep your opening hand or take a mulligan."),
                new OpeningHandPayload(new("Keep", true), new("Mulligan", true),
                    new(7, free ? 7 : 6, free ? 0 : 1, free)))
        };
        var crowded = ClutteredChangesScenario.Initial();
        crowded = crowded with {
            Id = table.Id, Decision = table.Decision, ActivePlayerId = 0,
            DecisionOwnerId = 0, PriorityPlayerId = null, ActionClass = "selection",
            Players = crowded.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name switch {
                    "Battlefield" => zone with { Cards = zone.Cards.SetItem(0, toothy) },
                    "Command" => new Zone("Command", 1, [pir], null), _ => zone
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        return [
            new("Partner: choose the target", "Real player targeting remains explicit; library gestures name the eligible owners. Pir is publicly in Command, never inferred to be absent from a hidden library.", table),
            new("Only one legal player target", "A sole legal target remains an explicit target choice. It is not silently submitted or confused with an automatic effect recipient.", table with {
                Decision = table.Decision! with { Id = "optional-sole-target",
                    Payload = ((TargetSelectionPayload)table.Decision.Payload) with { Players = [0] } }
            }),
            new("Partner: search or decline", "The targeted player can search and shuffle even when the partner is elsewhere. Neither option is a tiny cancel control; no library contents are disclosed yet.", Offer(0, 0)),
            new("Another player receives the offer", "The optional search belongs to the chosen player, independently of the source controller. Their library is private until the actual search authorizes its contents.", Offer(1, 1)),
            new("First multiplayer mulligan", "Read any hand card without committing. Keep and the free redraw are peer actions beside the hand.", Opening(true)),
            new("A paid mulligan", "The engine supplies the next hand size and required bottom-card count; the client does not derive Commander policy.", Opening(false)),
            new("Optional effect without a search", "An ordinary confirmation retains its source and two readable alternatives without acquiring library semantics.", table with {
                ActionClass = "confirm", Decision = new("optional-generic",
                    new(Message: "Use this optional effect?", Source: new(toothy.VisualId, toothy, 0, "Battlefield")),
                    new ConfirmationPayload(new("Yes", true), new("No", true)))
            }),
            new("Library choice on a crowded table", "The same authored search target over 480 permanents. All owners remain explicit; review readability and placement, not just counts.", crowded)
        ];
    }
}
