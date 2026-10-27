using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

// Forge publishes one shared public occurrence generation to every audience.
// This is an adapter publication invariant, not a requirement that unrelated
// providers share card IDs. Compare current public zones only: historical stack
// portraits never establish current occurrences by matching printed names.
internal static class ForgePublicProjectionChecks
{
    internal static int ValidateBatch(ImmutableArray<AutomatedSeatObservation> observations, string profile)
    {
        int comparisons = 0;
        foreach (var group in observations.GroupBy(item => (item.State.Id, item.State.BoardRevision))) {
            var first = group.First().State;
            foreach (var item in group.Skip(1)) {
                if (!AiWholeSeatEngineCheck.ComparePublicProjection(first, item.State, profile))
                    throw new InvalidDataException("Forge published inconsistent same-revision public occurrences across its automated audiences.");
                comparisons++;
            }
        }
        return comparisons;
    }

    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "expected-public-projection-rejections");
        Directory.CreateDirectory(directory);
        AutomatedSeatObservation Audience(int seat, long board = 20) {
            var players = Enumerable.Range(0, 2).Select(player => new Player(player, "Player " + player, 40, false, false,
                ImmutableDictionary<string, int>.Empty, [
                    new("Battlefield", 1, [new Card { Name = "Same printed creature", VisualId = "public-" + player,
                        CombatId = "public-" + player, Type = "Creature", Power = 2, Toughness = 2 }], null),
                    new("Hand", 1, player == seat ? [new Card { Name = "Private face " + seat,
                        VisualId = "private-" + seat }] : [], null)
                ])).ToImmutableArray();
            return new(seat, new("shared-publication", "playing", null, null, seat, 1, "Main", "MAIN1", 0,
                players, [], null, null) { BoardRevision = board, Revision = seat + 1 }, DecisionActionDomain.None, new());
        }
        var first = Audience(0);
        var other = Audience(1);
        check(ValidateBatch([first, other], directory) == 1,
            "Forge same-revision public occurrence comparison permits each audience's different private hand and decision revision");
        var changedCard = other.State.Players[0].Zone("Battlefield").Cards[0] with {
            VisualId = "rotated-public-id", CombatId = "rotated-public-id"
        };
        var changedPlayer = other.State.Players[0] with { Zones = [new("Battlefield", 1, [changedCard], null),
            other.State.Players[0].Zone("Hand")] };
        var inconsistent = other with { State = other.State with { Players = [changedPlayer, other.State.Players[1]] } };
        bool rejected = false;
        try { ValidateBatch([first, inconsistent], directory); } catch (InvalidDataException) { rejected = true; }
        check(rejected,
            "Forge rejects changed public occurrence IDs before policy evaluation even when printed names and all public statistics match");
        check(ValidateBatch([first, inconsistent with { State = inconsistent.State with { BoardRevision = 21 } }], directory) == 0
                && ValidateBatch([first, inconsistent with { State = inconsistent.State with { Id = "another-game" } }], directory) == 0,
            "Public occurrence comparison never combines different game or board revisions");
        var historical = other with { State = other.State with { Stack = [new("Same printed creature", "Historical ability",
            StackActionKind.TriggeredAbility, new Card { Name = "Same printed creature", VisualId = "departed-read-only-portrait" }) {
            Id = "historical-trigger"
        }] } };
        check(ValidateBatch([first, historical], directory) == 1,
            "Historical stack portraits are not joined to current public permanents by matching printed names");
    }
}
