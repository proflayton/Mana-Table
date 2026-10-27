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
        foreach (var item in observations)
            foreach (var player in item.State.Players)
                foreach (var card in player.Zone("Battlefield").Cards) {
                    if (!string.IsNullOrEmpty(card.VisualId) || !string.IsNullOrEmpty(card.CombatId)) continue;
                    if ((item.State.Status is "playing" or "resolving") && !player.Eliminated
                        || !string.IsNullOrEmpty(card.Key) || card.Selectable || card.Highlighted)
                        throw new InvalidDataException("Forge published a current or actionable battlefield face without an occurrence identity.");
                }
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
        AutomatedSeatObservation MissingIdentity(AutomatedSeatObservation observation, string status, bool eliminated, bool selectable = false) {
            var owner = observation.State.Players[0];
            var face = owner.Zone("Battlefield").Cards[0] with { VisualId = null!, CombatId = null!, Selectable = selectable };
            owner = owner with { Eliminated = eliminated, Zones = [new("Battlefield", 1, [face], null), owner.Zone("Hand")] };
            return observation with { State = observation.State with { Status = status,
                Players = [owner, observation.State.Players[1]] } };
        }
        bool MissingRejected(params AutomatedSeatObservation[] observations) {
            try { ValidateBatch(observations.ToImmutableArray(), directory); return false; }
            catch (InvalidDataException) { return true; }
        }
        foreach (string status in new[] { "playing", "resolving" })
            check(MissingRejected(MissingIdentity(first, status, false), MissingIdentity(other, status, false)),
                "Matching empty identities across all Forge audiences cannot certify a current live battlefield occurrence");
        var anonymous = MissingIdentity(first, "playing", false);
        var anonymousOwner = anonymous.State.Players[0];
        anonymousOwner = anonymousOwner with { Zones = [new("Battlefield", 1,
            [anonymousOwner.Zone("Battlefield").Cards[0] with { CombatId = "anonymous-public-occurrence", FaceDown = true }], null),
            anonymousOwner.Zone("Hand")] };
        check(ValidateBatch([anonymous with { State = anonymous.State with { Players = [anonymousOwner, anonymous.State.Players[1]] } }], directory) == 0,
            "A live concealed battlefield face may retain its anonymous combat occurrence without a readable visual identity");
        check(ValidateBatch([MissingIdentity(first, "finished", true), MissingIdentity(other, "finished", true)], directory) == 1
                && ValidateBatch([MissingIdentity(first, "playing", true), MissingIdentity(other, "playing", true)], directory) == 1,
            "Terminal or explicitly retired read-only faces compare without fabricating current identities or throwing on legacy null fields");
        check(MissingRejected(MissingIdentity(first, "finished", true, selectable: true)),
            "An identity-free retired face cannot regain target-selection authority through its display flags");
    }
}
