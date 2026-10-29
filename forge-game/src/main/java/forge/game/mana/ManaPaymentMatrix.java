package forge.game.mana;

import forge.card.mana.ManaCostShard;

/** Read-only conversion facts needed to apply a mana unit to a detached bill. */
public interface ManaPaymentMatrix {
    byte getPossibleColorUses(byte color);
    boolean canPayForShardWithColor(ManaCostShard shard, byte color);
    boolean isSnowForColor();
}
