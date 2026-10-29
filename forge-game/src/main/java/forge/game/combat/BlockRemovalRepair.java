package forge.game.combat;

import forge.game.card.Card;
import forge.game.player.Player;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;

/** A bounded rules witness for repairing a staged declaration by removing own
 * assignments. This is not a tactical planner or a complete blocking solver. */
public final class BlockRemovalRepair {
    private BlockRemovalRepair() { }

    public record Assignment(Card attacker, Card blocker) { }
    /** null assignments means no witness was certified; an empty list certifies
     * clearing this defender's blocks. Other players' assignments stay fixed. */
    public record Result(List<Assignment> assignments, int candidatesTried) { }

    public static Result find(Combat combat, Player defending, int maxCandidates) {
        if (maxCandidates < 0) throw new IllegalArgumentException("A repair budget cannot be negative");
        if (maxCandidates == 0 || CombatUtil.validateBlocks(combat, defending) == null)
            return new Result(null, 0);

        // One blocker assignment belongs to the entire attacking band. Search
        // each physical toggle once, then expand the witness back to its pairs.
        var toggles = new ArrayList<Assignment>();
        var all = new ArrayList<Assignment>();
        var seen = new HashSet<java.util.Map.Entry<AttackingBand, Card>>();
        for (Card attacker : combat.getAttackers().stream().sorted(Comparator.comparingInt(Card::getId)).toList()) {
            for (Card blocker : combat.getBlockers(attacker).stream().filter(card -> card.getController() == defending)
                    .sorted(Comparator.comparingInt(Card::getId)).toList()) {
                var assignment = new Assignment(attacker, blocker);
                all.add(assignment);
                if (seen.add(java.util.Map.entry(combat.getBandOfAttacker(attacker), blocker))) toggles.add(assignment);
            }
        }
        if (toggles.isEmpty()) return new Result(null, 0);

        int tried = 0;
        // Increasing-size removal subsets preserve the most existing choices.
        // Reserve the final trial for clearing own blocks; this can cheaply
        // certify larger optional declarations without exponential search.
        for (int count = 1; count < toggles.size() && tried < maxCandidates - 1; count++) {
            int[] indexes = new int[count];
            for (int i = 0; i < count; i++) indexes[i] = i;
            while (tried < maxCandidates - 1) {
                var removed = new ArrayList<Assignment>(count);
                for (int index : indexes) removed.add(toggles.get(index));
                Combat candidate = combat.copyRemovingBlockAssignments(removed);
                tried++;
                if (CombatUtil.validateBlocks(candidate, defending) == null)
                    return new Result(retained(candidate, all), tried);
                int next = count - 1;
                while (next >= 0 && indexes[next] == toggles.size() - count + next) next--;
                if (next < 0) break;
                indexes[next]++;
                for (int i = next + 1; i < count; i++) indexes[i] = indexes[i - 1] + 1;
            }
        }
        Combat cleared = combat.copyRemovingBlockAssignments(toggles);
        tried++;
        return new Result(CombatUtil.validateBlocks(cleared, defending) == null ? List.of() : null, tried);
    }

    private static List<Assignment> retained(Combat candidate, List<Assignment> original) {
        return original.stream().filter(pair -> candidate.isBlocking(pair.blocker(), pair.attacker())).toList();
    }
}
