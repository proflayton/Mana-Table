package forge.game.combat;

import forge.game.GameEntity;
import forge.game.card.Card;
import forge.game.player.Player;

import java.io.Serializable;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Host-computed legal combat pairs. Contains public battlefield IDs, never card identities or rules text. */
public record CombatInputState(boolean attacking, List<Target> defenders, Target selectedDefender,
                               Map<Integer, List<Target>> attackOptions, List<Integer> blockerCandidates,
                               Map<Integer, List<Integer>> blockOptions, String blockProblem) implements Serializable {
    public record Target(boolean player, int id, int playerId) implements Serializable {
        public static Target of(GameEntity entity) {
            if (entity instanceof Player player) return new Target(true, player.getId(), player.getId());
            Card card = (Card) entity;
            Player defending = card.isBattle() ? card.getProtectingPlayer() : card.getController();
            return new Target(false, card.getId(), defending.getId());
        }
    }

    public static CombatInputState attackers(Combat combat, Player player, GameEntity selected) {
        var defenders = new ArrayList<Target>();
        for (GameEntity defender : combat.getDefenders()) defenders.add(Target.of(defender));
        var options = new LinkedHashMap<Integer, List<Target>>();
        for (Card card : player.getCreaturesInPlay()) {
            var targets = new ArrayList<Target>();
            for (GameEntity defender : combat.getDefenders()) {
                if (combat.isAttacking(card, defender) || CombatUtil.canAttack(card, defender)) targets.add(Target.of(defender));
            }
            if (!targets.isEmpty()) options.put(card.getId(), List.copyOf(targets));
        }
        return new CombatInputState(true, List.copyOf(defenders), selected == null ? null : Target.of(selected),
                Map.copyOf(options), List.of(), Map.of(), null);
    }

    public static CombatInputState blockers(Combat combat, Player player) {
        var options = new LinkedHashMap<Integer, List<Integer>>();
        for (Card attacker : combat.getAttackers()) {
            var blockers = new ArrayList<Integer>();
            for (Card blocker : player.getCreaturesInPlay()) {
                if (combat.isBlocking(blocker, attacker) || CombatUtil.canBlock(attacker, blocker, combat)) blockers.add(blocker.getId());
            }
            options.put(attacker.getId(), List.copyOf(blockers));
        }
        return new CombatInputState(false, List.of(), null, Map.of(),
                player.getCreaturesInPlay().stream().map(Card::getId).toList(), Map.copyOf(options), CombatUtil.validateBlocks(combat, player));
    }
}
