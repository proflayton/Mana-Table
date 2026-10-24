using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

public static partial class ExperienceScenarios
{
    private static IReadOnlyList<ExperienceStage> CardPlay()
    {
        var land = Body("play-forest", "Forest", "Basic Land — Forest", null, null);
        var bear = Body("play-bears", "Grizzly Bears") with { Text = "", ManaCost = "{1}{G}" };
        var ring = Body("play-ring", "Sol Ring", "Artifact", null, null) with { Text = "{T}: Add {C}{C}.", ManaCost = "{1}" };
        var initial = Table("card-play", [], [land, bear]) with { Decision = new() {
            Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true, Ok = "Pass" } };
        GameSnapshot Move(GameSnapshot state, int actor, Card card, string destination) => state with {
            Players = state.Players.Select(player => player.Id != actor ? player : player with {
                Zones = player.Zones.Select(zone => {
                    var cards = zone.Cards.Where(current => current.VisualId != card.VisualId).ToImmutableArray();
                    if (zone.Name == destination) cards = cards.Add(card);
                    return zone with { Cards = cards, Count = zone.Cards.Length > 0 || zone.Name == destination ? cards.Length : zone.Count };
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        ActivityEntry Event(int id, string kind, int actor, Card card) => new(id, 4, "MAIN1", kind, actor,
            card.Name, card.VisualId, card.Name);
        var landPlayed = Move(initial, 0, land, "Battlefield") with { Activity = [Event(1, "land", 0, land)] };
        var casting = Move(landPlayed, 0, bear, "Stack") with {
            Stack = [new(bear.Name, bear.Text, false, bear) { Id = "play-bear-spell", ControllerId = 0 }],
            Activity = [.. landPlayed.Activity, Event(2, "cast", 0, bear)]
        };
        var resolved = Move(casting, 0, bear, "Battlefield") with { Stack = [] };
        var opponentCasting = resolved with {
            Stack = [new(ring.Name, ring.Text, false, ring) { Id = "play-ring-spell", ControllerId = 1 }],
            Activity = [.. casting.Activity, Event(3, "cast", 1, ring)]
        };
        var opponentResolved = Move(opponentCasting, 1, ring, "Battlefield") with { Stack = [] };
        var ability = opponentResolved with {
            Stack = [new(ring.Name, "Add {C}{C}.", true, ring) { Id = "play-ring-ability", ControllerId = 1 }],
            Activity = [.. opponentResolved.Activity, Event(4, "ability", 1, ring)]
        };
        return [
            new("Before playing", "Authored hand. Inspect either card; continue to observe a land play.", initial),
            new("Land enters", "The same Forest moves from your hand into its actual battlefield slot.", landPlayed),
            new("Your spell", "Grizzly Bears leaves your hand, is shown, then rests on the stack.", casting),
            new("Spell resolves", "That same physical Bears card travels from the stack into your battlefield.", resolved),
            new("Opponent casts", "Sol Ring becomes visible only after this authored public cast.", opponentCasting),
            new("Opponent resolves", "The public spell travels into its controller's battlefield.", opponentResolved),
            new("Ability source remains", "An ability refers to Sol Ring without moving or replaying the permanent.", ability)
        ];
    }
}
