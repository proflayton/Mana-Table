using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;

internal static class AllocationRegression
{
    public static void Run(Action<bool, string> check)
    {
        var source = new Card { Name = "Combat source", VisualId = "source", Power = 9, Toughness = 9, CombatKeywords = ["trample", "deathtouch"] };
        var first = new Card { Name = "Runeclaw Bear", VisualId = "bear-a", Type = "Creature — Bear", Power = 3, Toughness = 4, Damage = 2 };
        var second = first with { VisualId = "bear-b", Power = 2, Toughness = 2, Damage = 0 };
        var decision = new Decision { Id = "allocation-prompt", Task = DecisionTask.Allocation, AllocationKind = "combat", AmountLabel = "combat damage",
            SourceCard = source, Amount = 7, Choices = [
                new(0, first.Name, first) { Id = "target-a", CardId = first.VisualId, ControllerId = 1, OwnerId = 0, Minimum = 0, Maximum = 7, Lethal = 1 },
                new(1, second.Name, second) { Id = "target-b", CardId = second.VisualId, ControllerId = 1, Minimum = 0, Maximum = 7, Lethal = 1 },
                new(2, "Alex", null) { Id = "target-player", PlayerId = 1, IsDefender = true, Minimum = 0, Maximum = 7 }
            ] };
        var state = new GameSnapshot("allocation-game", "playing", null, null, 0, 1, "Combat damage", "COMBAT_DAMAGE", 0,
            [new(0, "You", 40, false, false, ImmutableDictionary<string, int>.Empty, []), new(1, "Alex", 37, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null, decision);

        check(AllocationGuide.Stats(first) == "3/4" && AllocationGuide.MarkedDamage(first) == "2 damage marked",
            "Current power/toughness stays separate from marked damage rather than subtracting it from toughness");
        check(AllocationGuide.Unit(decision) == "combat damage" && decision.Amount != source.Power,
            "Assignment uses the engine's damage amount rather than deriving it from source power");
        check(AllocationGuide.Seat(state, decision.Choices[0]) == "Alex · Seat 2",
            "Recipient context follows its controller instead of the card's owner");
        check(AllocationGuide.Seat(state, decision.Choices[2]) == "Alex · Seat 2" && AllocationGuide.Stats(null) == "",
            "Player recipients have a seat without invented card stats");
        check(AllocationGuide.Problem(decision, [1, 1, 5]) == null && AllocationGuide.ThresholdLabel(decision, decision.Choices[0]) == "1 required before defender",
            "The source-specific engine threshold is used instead of guessing from current toughness and marked damage");
        check(AllocationGuide.Problem(decision, [0, 1, 6]) is { } requirement && requirement.Contains("before the defender", StringComparison.Ordinal)
            && requirement.Contains("(recipient 1)", StringComparison.Ordinal),
            "An assignment to the defender stays invalid until every supplied blocker threshold is satisfied");
        check(AllocationGuide.Problem(decision, [0, 7, 0]) == null,
            "Without assigning to the defender, no blocker ordering or inferred kill-first rule is imposed");
        check(AllocationGuide.Problem(decision with { Choices = decision.Choices.Select(c => c with { IsDefender = false }).ToImmutableArray() }, [0, 1, 6]) == null,
            "An unrestricted engine assignment does not gain a defender restriction from text or source keywords");
        check(AllocationGuide.Problem(decision, [1, 1, 4]) == "1 left to assign."
            && AllocationGuide.Problem(decision, [1, 1, 6]) == "1 too many assigned.",
            "The assignment explains both unassigned and excess amounts before confirmation");
        check(AllocationGuide.Problem(decision, [1, 6]) != null && AllocationGuide.Problem(decision, [-1, 1, 7]) != null,
            "Missing recipients and negative values cannot produce allocation commands");
        int[] staged = [1, 1, 5];
        var reply = AllocationGuide.Reply(state, staged);
        staged[0] = 7;
        check(reply is AllocateAmounts { GameId: "allocation-game", DecisionId: "allocation-prompt" }
            && reply.Amounts!.SequenceEqual([1, 1, 5]), "Confirmation snapshots the exact ordered recipient amounts with the current game and prompt scope");
        check(AllocationGuide.Reply(state, [0, 1, 6]) == null && AllocationGuide.Reply(state with { Status = "finished" }, [1, 1, 5]) == null
            && AllocationGuide.Reply(state with { Decision = decision with { Id = "" } }, [1, 1, 5]) == null,
            "Illegal, ended and unscoped allocations cannot become commands");

        var generic = decision with { AllocationKind = "generic", AmountLabel = "+1/+1 counters", Amount = 5, AtLeastOne = true, Limits = [2, 4],
            Choices = [new(7, first.Name, first), new(42, second.Name, second)] };
        check(AllocationGuide.Minimum(generic, 0) == 1 && AllocationGuide.Maximum(generic, 0) == 2
            && AllocationGuide.Problem(generic, [2, 3]) == null, "Legacy per-recipient limits and minimum-one rules remain authoritative for generic allocations");
        check(AllocationGuide.Problem(generic, [0, 5]) != null && AllocationGuide.Problem(generic, [3, 2]) != null,
            "Generic allocation rejects missing required amounts and target-specific cap violations");
        check(AllocationGuide.Unit(generic) == "+1/+1 counters" && AllocationGuide.ThresholdLabel(generic, decision.Choices[0]) == "",
            "Counter, mana and prevention labels do not become combat-damage instructions");
        var specific = generic with { Choices = [generic.Choices[0] with { Minimum = 2, Maximum = 3 }, generic.Choices[1] with { Minimum = 0, Maximum = 5 }] };
        check(AllocationGuide.Minimum(specific, 0) == 2 && AllocationGuide.Maximum(specific, 0) == 3
            && AllocationGuide.Minimum(specific, 1) == 0, "New explicit bounds override the older generic defaults by recipient position");
        var large = generic with { Amount = int.MaxValue, AtLeastOne = false, Limits = [] };
        check(AllocationGuide.Total([int.MaxValue, int.MaxValue]) == 4294967294L
            && AllocationGuide.Problem(large, [int.MaxValue, int.MaxValue]) != null, "Large allocations cannot overflow their displayed or validated total");
        check(AllocationGuide.Stats(new Card { FaceDown = true }) == ""
            && AllocationGuide.Stats(new Card { Name = "Face-down permanent", FaceDown = true, Power = 2, Toughness = 3 }) == "2/3",
            "Anonymous cards show only supplied public stats and never assume a default hidden creature size");
        var planeswalker = new Card { Type = "Legendary Planeswalker", Power = 0, Toughness = 0, Counters = ImmutableDictionary<string, int>.Empty.Add("loyalty", 5) };
        check(AllocationGuide.Stats(planeswalker) == "" && AllocationGuide.Counters(planeswalker) == "loyalty: 5",
            "Noncreature recipients show their public counters instead of incidental zero power/toughness fields");
        check(AllocationGuide.Counters(first with { Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2).Add("shield", 1) }).Contains("shield: 1", StringComparison.Ordinal)
            && AllocationGuide.Abilities(source).Contains("Deathtouch", StringComparison.Ordinal), "Relevant public counters and source abilities remain visible without predicting outcomes");
        var decoded = JsonSerializer.Deserialize<Decision>("""
            {"id":"wire","task":"Allocation","allocationKind":"combat","amountLabel":"combat damage","amount":3,
             "sourceControllerId":0,"sourceCardId":"opaque-source","choices":[
               {"index":0,"label":"Face-down permanent","id":"wire:0","cardId":"opaque-recipient","controllerId":1,"minimum":0,"maximum":3,"lethal":2,
                "card":{"name":"Face-down permanent","faceDown":true,"power":2,"toughness":2,"damage":1}},
               {"index":1,"label":"Alex","playerId":1,"isDefender":true}]}
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        check(decoded.SourceControllerId == 0 && decoded.Choices[0] is { Id: "wire:0", CardId: "opaque-recipient", ControllerId: 1, Lethal: 2, Card.FaceDown: true }
            && decoded.Choices[1].PlayerId == 1 && AllocationGuide.Problem(decoded, [2, 1]) == null,
            "The additive allocation wire fields deserialize with opaque identities and anonymous public stats intact");
    }
}
