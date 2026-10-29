package forge.api;

import forge.game.combat.CombatInputState;

/** One engine-published combat domain, never a cache across input mutations. */
final class CombatChoiceScope {
    private Object input;
    private Integer owner;
    private long sequence = -1, rulesVersion = -1;
    private CombatInputState choices;

    void bind(Object current, Integer player, long inputSequence) {
        input = current; owner = player; sequence = inputSequence; choices = null; rulesVersion = -1;
    }
    boolean publish(Object current, Integer player, long inputSequence, long version, CombatInputState offered) {
        if (current == null || current != input || player == null || !player.equals(owner) || sequence != inputSequence || offered == null) return false;
        choices = offered; rulesVersion = version; return true;
    }
    CombatInputState current(Object current, Integer player, long inputSequence, long version) {
        return current != null && current == input && player != null && player.equals(owner) && sequence == inputSequence && rulesVersion == version ? choices : null;
    }
    void invalidate() { choices = null; rulesVersion = -1; }
    void clear() { bind(null, null, -1); }
}
