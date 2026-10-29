namespace Mana.Contracts;

/// <summary>Rules witnesses reconcile declarations through current commands;
/// they never create new card identities or additional blocking authority.</summary>
public static class CombatRepairFacts
{
    public static void Validate(GameSnapshot state)
    {
        if (state.Combat?.BlockRepair is not { } repair) return;
        var combat = state.Combat;
        var ownAssignments = combat.Attackers.SelectMany(attack => attack.BlockerIds
            .Where(id => CardViews.FindZoneOccurrence(state, id) is { Zone: "Battlefield" } occurrence
                && occurrence.PlayerId == state.ViewerId)
            .Select(id => (AttackerId: attack.CardId, BlockerId: id))).ToHashSet();
        if (state.Decision?.Payload is not BlockPayload || string.IsNullOrEmpty(combat.BlockProblem)
            || repair.IsDefault || repair.Length >= ownAssignments.Count
            || repair.Any(assignment => assignment == null || string.IsNullOrWhiteSpace(assignment.AttackerId)
                || string.IsNullOrWhiteSpace(assignment.BlockerId)
                || !ownAssignments.Contains((assignment.AttackerId, assignment.BlockerId)))
            || repair.Distinct().Count() != repair.Length)
            throw new ArgumentException("Block repairs must be a smaller complete subset of this seat's current assignments.", nameof(state));

        foreach (var assignment in ownAssignments) {
            if (repair.Any(retained => retained.AttackerId == assignment.AttackerId && retained.BlockerId == assignment.BlockerId)) continue;
            var attacker = CardViews.FindZoneOccurrence(state, assignment.AttackerId);
            var blocker = CardViews.FindZoneOccurrence(state, assignment.BlockerId);
            if (attacker is not { Zone: "Battlefield" } || blocker is not { Zone: "Battlefield" }
                || !GameCommandPermissions.Allows(state, new ToggleBlock(state.Id, state.Decision.Id, attacker.Value.Card.Key, blocker.Value.Card.Key)))
                throw new ArgumentException("Every removed block assignment must retain its exact current toggle authority.", nameof(state));
        }
    }
}
