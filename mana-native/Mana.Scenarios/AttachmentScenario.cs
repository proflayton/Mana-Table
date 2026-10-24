using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Authored public snapshots, not a simulation of attachment rules.</summary>
public static class AttachmentScenario
{
    public const string Id = "attachments.v1";
    public const string Host = FourPlayerTableScenario.TargetId;
    public const string Boots = "attachments-boots", Aura = "attachments-rancor", Foreign = "attachments-pacifism", Jitte = "attachments-jitte";
    public static IReadOnlyList<ExperienceStage> Create()
    {
        var initial = FourPlayerTableScenario.Create()[0].Snapshot;
        var other = initial.Players[1].Zone("Battlefield").Cards.First(card => card.Type.Contains("Creature"));
        var additions = new[] {
            Face(Boots, "Swiftfoot Boots", "Artifact — Equipment", Host, "Tireless Tracker"),
            Face(Aura, "Rancor", "Enchantment — Aura", Host, "Tireless Tracker"),
            Face(Jitte, "Umezawa's Jitte", "Legendary Artifact — Equipment", Host, "Tireless Tracker") with { Counters = ImmutableDictionary<string, int>.Empty.Add("Charge", 2) },
            Face(Foreign, "Pacifism", "Enchantment — Aura", other.VisualId, other.Name)
        };
        var attached = initial with { Id = Id, Players = initial.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = [.. zone.Cards.Select(card => card.VisualId == Host ? card with {
                    Power = 7, Toughness = 4, CombatKeywords = ["trample", "haste", "hexproof"],
                    Attachments = [.. card.Attachments, .. additions.Take(3).Select(item => new CardAttachment(item.VisualId, item.Name, item.Type))]
                } : card.VisualId == other.VisualId ? card with { Attachments = [new(Foreign, "Pacifism", "Enchantment — Aura")] } : card),
                    .. player.Id == 0 ? additions : Array.Empty<Card>()],
                Count = zone.Cards.Length + (player.Id == 0 ? additions.Length : 0)
            }).ToImmutableArray()
        }).ToImmutableArray() };
        var nextHost = attached.Viewer!.Zone("Battlefield").Cards.First(card => card.Type.Contains("Creature") && card.VisualId != Host);
        var moved = Map(attached, card => card.VisualId == Boots ? card with { AttachedTo = new(nextHost.VisualId, nextHost.Name, nextHost.Type) }
            : card.VisualId == Host ? card with { Attachments = card.Attachments.Where(item => item.Id != Boots).ToImmutableArray(), CombatKeywords = ["trample"] }
            : card.VisualId == nextHost.VisualId ? card with { Attachments = [new(Boots, "Swiftfoot Boots", "Artifact — Equipment")], CombatKeywords = ["haste", "hexproof"] } : card);
        var detached = Map(moved, card => card.VisualId == Boots ? card with { AttachedTo = null }
            : card.VisualId == nextHost.VisualId ? card with { Attachments = [], CombatKeywords = [] } : card);
        var tapped = Map(attached, card => card.VisualId == Host ? card with { Tapped = true } : card);
        var clutter = ClutteredChangesScenario.Initial();
        var crowded = attached with { Players = attached.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = [.. zone.Cards, .. clutter.Players.Single(item => item.Id == player.Id).Zone("Battlefield").Cards.Take(120 - zone.Cards.Length)
                    .Select(card => card.Name == "Cat Beast Token" ? card with {
                        GamePieceType = "token", ArtName = "Cat Beast", ArtSet = "tfdn", Edition = "FDN", CollectorNumber = "28"
                    } : card)], Count = 120
            }).ToImmutableArray()
        }).ToImmutableArray() };
        return [new("Auras and Equipment", "Four attachments beneath Tracker, including Jitte with two counters; your Pacifism stays beneath an opponent's permanent. Read each exposed edge.", attached),
            new("Re-equip Boots", "The same Boots occurrence moves to the other creature; no decision is submitted by stage navigation.", moved),
            new("Equipment detached", "Boots returns to its controller's independent support permanents.", detached),
            new("Tapped host", "Tracker taps; its attachments retain their own independent untapped states.", tapped),
            new("480 permanents", "The same attachment relationships amid 480 visible permanents. Inspect a player to move closer.", crowded)];
    }
    private static Card Face(string id, string name, string type, string host, string hostName) => new() {
        Key = "p:" + id, VisualId = id, CombatId = id, Name = name, ArtName = name, Type = type,
        AttachedTo = new(host, hostName, "Permanent")
    };
    private static GameSnapshot Map(GameSnapshot state, Func<Card, Card> transform) => state with { Revision = state.Revision + 1,
        Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone
            : zone with { Cards = zone.Cards.Select(transform).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray()
    };
}
