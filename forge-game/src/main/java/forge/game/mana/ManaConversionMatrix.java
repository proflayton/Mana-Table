package forge.game.mana;

import forge.card.mana.ManaAtom;
import forge.card.mana.ManaCostShard;

import java.util.Arrays;

public class ManaConversionMatrix implements ManaPaymentMatrix {
    static byte[] identityMatrix = { ManaAtom.WHITE, ManaAtom.BLUE, ManaAtom.BLACK, ManaAtom.RED, ManaAtom.GREEN, ManaAtom.COLORLESS };

    // Conversion matrix ORs byte values to make mana more payable
    // Restrictive matrix ANDs byte values to make mana less payable
    protected byte[] colorConversionMatrix = new byte[ManaAtom.MANATYPES.length];
    protected byte[] colorRestrictionMatrix = new byte[ManaAtom.MANATYPES.length];

    protected boolean snowForColor = false;

    public boolean isSnowForColor() {
        return snowForColor;
    }
    public void setSnowForColor(boolean value) {
        snowForColor = value;
    }

    public byte getPossibleColorUses(byte color) {
        // Take the current conversion value, AND with restrictions to get mana usage
        int rowIdx = ManaAtom.getIndexOfFirstManaType(color);
        int matrixIdx = rowIdx < 0 ? identityMatrix.length - 1 : rowIdx;

        byte colorUse = colorConversionMatrix[matrixIdx];
        colorUse &= colorRestrictionMatrix[matrixIdx];
        return colorUse;
    }

    public boolean canPayForShardWithColor(ManaCostShard shard, byte color) {
        return canPayForShard(this, shard, color);
    }

    private static boolean canPayForShard(ManaPaymentMatrix matrix, ManaCostShard shard, byte color) {
        if (shard.isOfKind(ManaAtom.COLORLESS) && color == ManaAtom.GENERIC) {
            return false; // Preserve the existing generic/colorless distinction.
        }
        byte line = matrix.getPossibleColorUses(color);
        for (byte outColor : ManaAtom.MANATYPES) {
            if ((line & outColor) != 0 && shard.canBePaidWithManaOfColor(outColor)) return true;
        }
        return shard.canBePaidWithManaOfColor((byte) 0);
    }

    /** Exact detached arrays, with no operations that can change the live matrix. */
    public ManaPaymentMatrix paymentSnapshot() {
        return new PaymentSnapshot(colorConversionMatrix.clone(), colorRestrictionMatrix.clone(), snowForColor);
    }

    private record PaymentSnapshot(byte[] conversions, byte[] restrictions, boolean snowForColor) implements ManaPaymentMatrix {
        @Override public boolean isSnowForColor() { return snowForColor; }
        @Override public byte getPossibleColorUses(byte color) {
            int row = ManaAtom.getIndexOfFirstManaType(color);
            if (row < 0) row = identityMatrix.length - 1;
            return (byte) (conversions[row] & restrictions[row]);
        }
        @Override public boolean canPayForShardWithColor(ManaCostShard shard, byte color) {
            return canPayForShard(this, shard, color);
        }
    }

    public void adjustColorReplacement(byte originalColor, byte replacementColor, boolean additive) {
        // Fix the index without hardcodes
        int rowIdx = ManaAtom.getIndexOfFirstManaType(originalColor);
        rowIdx = rowIdx < 0 ? identityMatrix.length - 1 : rowIdx;
        if (additive) {
            colorConversionMatrix[rowIdx] |= replacementColor;
        } else {
            colorRestrictionMatrix[rowIdx] &= replacementColor;
        }
    }

    public void applyCardMatrix(ManaConversionMatrix extraMatrix) {
        for (int i = 0; i < colorConversionMatrix.length; i++) {
            colorConversionMatrix[i] |= extraMatrix.colorConversionMatrix[i];
        }

        for (int i = 0; i < colorRestrictionMatrix.length; i++) {
            colorRestrictionMatrix[i] &= extraMatrix.colorRestrictionMatrix[i];
        }
        setSnowForColor(extraMatrix.isSnowForColor());
    }

    public void restoreColorReplacements() {
        // By default each color can only be paid by itself ( {G} -> {G}, {C} -> {C}
        System.arraycopy(identityMatrix, 0, colorConversionMatrix, 0, colorConversionMatrix.length);
        // By default all mana types are unrestricted
        Arrays.fill(colorRestrictionMatrix, ManaAtom.ALL_MANA_TYPES);
        snowForColor = false;
    }
}
