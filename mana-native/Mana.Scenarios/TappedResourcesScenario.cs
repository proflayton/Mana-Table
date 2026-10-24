using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Authored public tap states; stage navigation never activates a mana ability.</summary>
public static class TappedResourcesScenario
{
    public const string Id = "tapped-resources.v1";
    public static IReadOnlyList<ExperienceStage> Create()
    {
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        GameSnapshot State(int ready, bool copies = false) => baseline with {
            Players = baseline.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = [.. zone.Cards.Where(card => !card.Type.Contains("Land", StringComparison.Ordinal)),
                        .. new[] { "Forest", "Mountain", "Plains" }.SelectMany((name, index) => Enumerable.Range(0, copies ? 3 : 1)
                            .Select(copy => new Card {
                                VisualId = $"tap-land-{player.Id}-{index}-{copy}", CombatId = $"tap-land-{player.Id}-{index}-{copy}",
                                Name = name, ArtName = name, Type = "Basic Land — " + name,
                                Tapped = copies ? copy != 0 : index >= ready
                            }))]
                }).Select(zone => zone.Name == "Battlefield" ? zone with { Count = zone.Cards.Length } : zone).ToImmutableArray()
            }).ToImmutableArray()
        };
        return [new("Three tapped lands", "Forest, Mountain and Plains are tapped at every seat.", State(0)),
            new("One untapped land", "Only Forest is untapped; the same cards keep their places.", State(1)),
            new("All untapped", "The same three resources are untapped at every seat.", State(3)),
            new("Mixed matching lands", "Each land family has one untapped and two tapped occurrences.", State(0, true))];
    }
}
