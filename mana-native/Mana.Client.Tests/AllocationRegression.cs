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
        var decision = new Decision("allocation-prompt", new DecisionContext(Source: new DecisionSource(Card: source)), new AllocationPayload(7, AllocationKind.Combat, "combat damage", false, [
new AllocationRecipient(0, "target-a", first.Name, first, first.VisualId, null, 1, 0, Math.Max(0, 0), Math.Max(0, Math.Min(7, 7)), 1, false),
new AllocationRecipient(1, "target-b", second.Name, second, second.VisualId, null, 1, null, Math.Max(0, 0), Math.Max(0, Math.Min(7, 7)), 1, false),
new AllocationRecipient(2, "target-player", "Alex", null, "", 1, null, null, Math.Max(0, 0), Math.Max(0, Math.Min(7, 7)), null, true)            ]));
        var allocation = (AllocationPayload)decision.Payload;
        var state = new GameSnapshot("allocation-game", "playing", null, null, 0, 1, "Combat damage", "COMBAT_DAMAGE", 0,
            [new(0, "You", 40, false, false, ImmutableDictionary<string, int>.Empty, []), new(1, "Alex", 37, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null, decision);

        check(AllocationGuide.Stats(first) == "3/4" && AllocationGuide.MarkedDamage(first) == "2 damage marked",
            "Current power/toughness stays separate from marked damage rather than subtracting it from toughness");
        check(AllocationGuide.Unit(allocation) == "combat damage" && allocation.Amount != source.Power,
            "Assignment uses the engine's damage amount rather than deriving it from source power");
        check(AllocationGuide.Seat(state, allocation.Recipients[0]) == "Alex · Seat 2",
            "Recipient context follows its controller instead of the card's owner");
        check(AllocationGuide.Seat(state, allocation.Recipients[2]) == "Alex · Seat 2" && AllocationGuide.Stats(null) == "",
            "Player recipients have a seat without invented card stats");
        check(AllocationGuide.Problem(allocation, [1, 1, 5]) == null && AllocationGuide.ThresholdLabel(allocation, allocation.Recipients[0]) == "1 required before defender",
            "The source-specific engine threshold is used instead of guessing from current toughness and marked damage");
        check(AllocationGuide.Problem(allocation, [0, 1, 6]) is { } requirement && requirement.Contains("before the defender", StringComparison.Ordinal)
            && requirement.Contains("(recipient 1)", StringComparison.Ordinal),
            "An assignment to the defender stays invalid until every supplied blocker threshold is satisfied");
        check(AllocationGuide.Problem(allocation, [0, 7, 0]) == null,
            "Without assigning to the defender, no blocker ordering or inferred kill-first rule is imposed");
        check(AllocationGuide.Problem(new AllocationPayload(7, AllocationKind.Combat, "combat damage", false, allocation.Recipients.Select(c => Copy(c, defender: false)).ToImmutableArray()), [0, 1, 6]) == null,
            "An unrestricted engine assignment does not gain a defender restriction from text or source keywords");
        check(AllocationGuide.Problem(allocation, [1, 1, 4]) == "1 left to assign."
            && AllocationGuide.Problem(allocation, [1, 1, 6]) == "1 too many assigned.",
            "The assignment explains both unassigned and excess amounts before confirmation");
        check(AllocationGuide.Problem(allocation, [1, 6]) != null && AllocationGuide.Problem(allocation, [-1, 1, 7]) != null,
            "Missing recipients and negative values cannot produce allocation commands");
        int[] staged = [1, 1, 5];
        var reply = AllocationGuide.Reply(state, staged);
        staged[0] = 7;
        check(reply is AllocateAmounts { GameId: "allocation-game", DecisionId: "allocation-prompt" }
            && reply.Amounts!.SequenceEqual([1, 1, 5]), "Confirmation snapshots the exact ordered recipient amounts with the current game and prompt scope");
        check(AllocationGuide.Reply(state, [0, 1, 6]) == null && AllocationGuide.Reply(state with { Status = "finished" }, [1, 1, 5]) == null
            && AllocationGuide.Reply(state with { Decision = decision with { Id = "" } }, [1, 1, 5]) == null,
            "Illegal, ended and unscoped allocations cannot become commands");

        var generic = new AllocationPayload(5, AllocationKind.Generic, "+1/+1 counters", false, [
            new(7, "first", first.Name, first, first.VisualId, null, null, null, 1, 2),
            new(42, "second", second.Name, second, second.VisualId, null, null, null, 1, 4)]);
        check(AllocationGuide.Minimum(generic, 0) == 1 && AllocationGuide.Maximum(generic, 0) == 2
            && AllocationGuide.Problem(generic, [2, 3]) == null, "Normalized per-recipient limits and minimum-one rules remain authoritative for generic allocations");
        check(AllocationGuide.Problem(generic, [0, 5]) != null && AllocationGuide.Problem(generic, [3, 2]) != null,
            "Generic allocation rejects missing required amounts and target-specific cap violations");
        check(AllocationGuide.Unit(generic) == "+1/+1 counters" && AllocationGuide.ThresholdLabel(generic, allocation.Recipients[0]) == "",
            "Counter, mana and prevention labels do not become combat-damage instructions");
        var specific = new AllocationPayload(5, AllocationKind.Generic, "+1/+1 counters", false, [Copy(generic.Recipients[0], minimum: 2, maximum: 3), Copy(generic.Recipients[1], minimum: 0, maximum: 5)]);
        check(AllocationGuide.Minimum(specific, 0) == 2 && AllocationGuide.Maximum(specific, 0) == 3
            && AllocationGuide.Minimum(specific, 1) == 0, "Each typed recipient carries its independent normalized bounds");
        var large = new AllocationPayload(int.MaxValue, AllocationKind.Generic, "amount", false, [Copy(generic.Recipients[0], minimum: 0, maximum: int.MaxValue), Copy(generic.Recipients[1], minimum: 0, maximum: int.MaxValue)]);
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
            {"id":"wire","context":{"source":{"controllerId":0,"cardId":"opaque-source"}},
             "payload":{"type":"allocation","kind":1,"amountLabel":"combat damage","amount":3,"maySkip":false,"recipients":[
               {"index":0,"label":"Face-down permanent","id":"wire:0","cardId":"opaque-recipient","controllerId":1,"minimum":0,"maximum":3,"lethal":2,
                "card":{"name":"Face-down permanent","faceDown":true,"power":2,"toughness":2,"damage":1}},
               {"index":1,"id":"player","label":"Alex","cardId":"","playerId":1,"minimum":0,"maximum":3,"isDefender":true}]}}
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        check(decoded.Context.Source?.ControllerId == 0 && ((AllocationPayload)decoded.Payload).Recipients[0] is { Id: "wire:0", CardId: "opaque-recipient", ControllerId: 1, Lethal: 2, Card.FaceDown: true }
            && ((AllocationPayload)decoded.Payload).Recipients[1].PlayerId == 1 && AllocationGuide.Problem((AllocationPayload)decoded.Payload, [2, 1]) == null,
            "Typed allocation JSON deserializes with opaque identities and anonymous public stats intact");
        bool invalidRecipient = false, invalidAllocation = false;
        try { _ = Copy(generic.Recipients[0], minimum: 3, maximum: 2); } catch (ArgumentException) { invalidRecipient = true; }
        try { _ = new AllocationPayload(1, AllocationKind.Generic, "amount", false, generic.Recipients); } catch (ArgumentException) { invalidAllocation = true; }
        check(invalidRecipient && invalidAllocation, "Contradictory allocation bounds are rejected before a frame can be published");
        foreach (int[] amounts in new[] { new[] { 2, 3 }, [0, 5], [3, 2], [2], [-1, 6] }) {
            var frame = state with { Decision = decision with { Payload = generic } };
            check((AllocationGuide.Problem(generic, amounts) == null) == GameCommandPermissions.Allows(frame,
                new AllocateAmounts(frame.Id, frame.Decision!.Id, amounts.ToImmutableArray())),
                "Allocation feedback and command authority share the normalized constraints");
        }
    }
    private static AllocationRecipient Copy(AllocationRecipient recipient, int? minimum = null, int? maximum = null, bool? defender = null)
        => new(recipient.Index, recipient.Id, recipient.Label, recipient.Card, recipient.CardId, recipient.PlayerId,
            recipient.ControllerId, recipient.OwnerId, minimum ?? recipient.Minimum, maximum ?? recipient.Maximum, recipient.Lethal, defender ?? recipient.IsDefender);
}
