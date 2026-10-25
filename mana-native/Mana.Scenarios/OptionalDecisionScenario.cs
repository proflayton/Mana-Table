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
            Decision = new() {
                Id = "optional-target", Task = DecisionTask.TargetSelection,
                SourceCard = toothy, SourceCardId = toothy.VisualId, SourceControllerId = 0, SourceZone = "Battlefield",
                PlayerChoices = [0, 1, 2], Min = 1, Max = 1,
                Message = "Target player may search their library for Pir, Imaginative Rascal, reveal it, put it into their hand, then shuffle.",
                LibrarySearch = new(null, "Hand", pir.Name)
            }
        };
        GameSnapshot Offer(int viewer, int owner) => table with {
            ViewerId = viewer, DecisionOwnerId = viewer, ActionClass = "confirm",
            // Changing viewers removes the previous viewer's authorized hand.
            Players = table.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" || player.Id == viewer ? zone
                    : zone with { Cards = [] }).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = table.Decision! with {
                Id = "optional-search-" + viewer, Task = DecisionTask.Confirmation,
                PlayerChoices = [], Min = 0, Max = 0, OkEnabled = true, CancelEnabled = true,
                Ok = "Search library", Cancel = "Don't search", LibrarySearch = new(owner, "Hand", pir.Name),
                Message = "You may search your library for Pir, Imaginative Rascal, reveal it, put it into your hand, then shuffle."
            }
        };
        GameSnapshot Opening(bool free) => baseline with {
            Id = "optional-decisions-lab", Turn = 0, Phase = "Opening hand", PhaseKey = "",
            ActivePlayerId = null, PriorityPlayerId = null, DecisionOwnerId = 0, ActionClass = "confirm",
            Players = baseline.Players.Select(player => player with {
                Mana = ImmutableDictionary<string, int>.Empty,
                Zones = player.Zones.Select(zone => zone.Name is "Battlefield" or "Graveyard"
                    ? zone with { Count = 0, Cards = [], TopCard = null } : zone).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = new() {
                Id = "optional-opening-" + free, Task = DecisionTask.OpeningHand,
                Ok = "Keep", Cancel = "Mulligan", OkEnabled = true, CancelEnabled = true,
                OpeningHand = new(7, free ? 7 : 6, free ? 0 : 1, free),
                Message = "Keep your opening hand or take a mulligan."
            }
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
                Decision = table.Decision! with { Id = "optional-sole-target", PlayerChoices = [0] }
            }),
            new("Partner: search or decline", "The targeted player can search and shuffle even when the partner is elsewhere. Neither option is a tiny cancel control; no library contents are disclosed yet.", Offer(0, 0)),
            new("Another player receives the offer", "The optional search belongs to the chosen player, independently of the source controller. Their library is private until the actual search authorizes its contents.", Offer(1, 1)),
            new("First multiplayer mulligan", "Read any hand card without committing. Keep and the free redraw are peer actions beside the hand.", Opening(true)),
            new("A paid mulligan", "The engine supplies the next hand size and required bottom-card count; the client does not derive Commander policy.", Opening(false)),
            new("Optional effect without a search", "An ordinary confirmation retains its source and two readable alternatives without acquiring library semantics.", table with {
                ActionClass = "confirm", Decision = new() {
                    Id = "optional-generic", Task = DecisionTask.Confirmation, SourceCard = toothy,
                    SourceCardId = toothy.VisualId, SourceControllerId = 0, SourceZone = "Battlefield",
                    Ok = "Yes", Cancel = "No", OkEnabled = true, CancelEnabled = true,
                    Message = "Use this optional effect?"
                }
            }),
            new("Library choice on a crowded table", "The same authored search target over 480 permanents. All owners remain explicit; review readability and placement, not just counts.", crowded)
        ];
    }
}
