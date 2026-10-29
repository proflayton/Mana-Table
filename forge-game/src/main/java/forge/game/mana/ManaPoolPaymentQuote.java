package forge.game.mana;

import forge.card.mana.ManaCostShard;
import forge.card.mana.ManaAtom;
import forge.game.CardTraitBase;
import forge.game.card.Card;
import forge.game.cost.CostPayment;
import forge.game.spellability.AbilityManaPart;
import forge.game.spellability.SpellAbility;
import forge.game.staticability.StaticAbility;
import forge.game.staticability.StaticAbilityMode;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Set;

/** A read-time pool-only witness, never authority to spend mana or activate a source.
 *  Failure of this bounded ordinary payment walk is not a proof of unpayability. */
public final class ManaPoolPaymentQuote {
    public enum Knowledge { PAYABLE, UNKNOWN }
    private static final int MAX_UNITS = 128;
    private static final Set<ManaCostShard> FIXED = Set.of(ManaCostShard.GENERIC, ManaCostShard.WHITE,
            ManaCostShard.BLUE, ManaCostShard.BLACK, ManaCostShard.RED, ManaCostShard.GREEN, ManaCostShard.COLORLESS);

    private final Knowledge knowledge;
    private final List<Mana> selected;
    private final ManaCostBeingPaid remaining;
    private final ManaPaymentMatrix matrix;

    private ManaPoolPaymentQuote(Knowledge knowledge, List<Mana> selected,
            ManaCostBeingPaid remaining, ManaPaymentMatrix matrix) {
        this.knowledge = knowledge; this.selected = List.copyOf(selected);
        this.remaining = remaining == null ? null : new ManaCostBeingPaid(remaining); this.matrix = matrix;
    }

    public Knowledge knowledge() { return knowledge; }
    public boolean canPayEntireBill() { return knowledge == Knowledge.PAYABLE; }
    /** Exact unit occurrences in the witness; repeated references can represent multiple units. */
    public List<Mana> selectedUnits() { return selected; }
    /** A defensive residual only for a complete supported pool walk, or a complete success witness.
     *  Null means omitted/unsupported inputs; a partial residual never proves global unpayability. */
    public ManaCostBeingPaid remainingBill() { return remaining == null ? null : new ManaCostBeingPaid(remaining); }
    /** Consult the same immutable conversion matrix as this quote, after checking remainingBill(). */
    public boolean canUseColor(byte color) {
        if (remaining == null) throw new IllegalStateException("This quote has no supported remaining bill");
        return remaining.isAnyPartPayableWith(color, matrix);
    }

    static ManaPoolPaymentQuote capture(ManaPool pool, ManaCostBeingPaid requested, SpellAbility purpose) {
        if (requested == null || requested.getClass() != ManaCostBeingPaid.class
                || pool.getClass() != ManaPool.class || !fixed(requested) || !ordinaryPurpose(pool, purpose))
            return unknown();
        ManaCostBeingPaid bill = new ManaCostBeingPaid(requested);
        ManaPaymentMatrix matrix = pool.paymentSnapshot();
        List<Mana> units = new ArrayList<>();
        boolean supportedInventory = pool.totalMana() <= MAX_UNITS;
        int inspected = 0;
        for (Mana unit : pool) {
            if (++inspected > MAX_UNITS) break;
            if (!ordinaryUnit(unit)) { supportedInventory = false; continue; }
            units.add(unit);
        }
        List<Mana> selected = new ArrayList<>();
        var shards = bill.getUnpaidShards(); // Already bounded by distinct counted parts above.
        shards.sort(Comparator.naturalOrder());
        boolean converge = purpose.getHostCard().hasConverge();
        for (ManaCostShard shard : shards) {
            if (bill.isPaid()) break;
            var choices = CostPayment.unrestrictedManaChoices(units, matrix, shard, converge ? bill.getColorsPaid() : -1);
            if (choices.isEmpty()) continue;
            Mana chosen = choices.get(0); // A deterministic witness; never calls a controller for a tie.
            if (!bill.isNeeded(chosen, matrix)) continue;
            bill.payMana(chosen, matrix);
            selected.add(chosen);
            for (int i = 0; i < units.size(); i++) if (units.get(i) == chosen) { units.remove(i); break; }
        }
        return new ManaPoolPaymentQuote(bill.isPaid() ? Knowledge.PAYABLE : Knowledge.UNKNOWN, selected,
                bill.isPaid() || supportedInventory ? bill : null, matrix);
    }

    private static ManaPoolPaymentQuote unknown() {
        return new ManaPoolPaymentQuote(Knowledge.UNKNOWN, List.of(), null, null);
    }
    private static boolean fixed(ManaCostBeingPaid bill) {
        if (bill.getXcounter() != 0) return false;
        long count = 0;
        for (ManaCostShard shard : bill.getDistinctShards()) {
            int amount = bill.getUnpaidShards(shard);
            if (!FIXED.contains(shard) || amount <= 0 || (count += amount) > MAX_UNITS) return false;
        }
        return true;
    }
    private static boolean ordinaryUnit(Mana unit) {
        if (unit == null || unit.getClass() != Mana.class) return false;
        boolean color = false;
        for (byte type : ManaAtom.MANATYPES) if (type == unit.getColor()) { color = true; break; }
        return color && unit.getSourceCard() != null && unit.getSourceCard().getClass() == Card.class
                && (unit.getManaAbility() == null || unit.getManaAbility().getClass() == AbilityManaPart.class)
                && !unit.isRestricted();
    }
    private static boolean ordinaryPurpose(ManaPool pool, SpellAbility purpose) {
        if (purpose == null) return false;
        try {
            // A quote cannot execute custom eligibility/getter code retained by a purpose subclass.
            if (purpose.getClass().getMethod("getMapParams").getDeclaringClass() != CardTraitBase.class
                    || purpose.getClass().getMethod("hasParam", String.class).getDeclaringClass() != CardTraitBase.class
                    || purpose.getClass().getMethod("getParam", String.class).getDeclaringClass() != CardTraitBase.class
                    || purpose.getClass().getMethod("getHostCard").getDeclaringClass() != CardTraitBase.class
                    || purpose.getClass().getMethod("allowsPayingWithShard", Card.class, byte.class).getDeclaringClass() != SpellAbility.class)
                return false;
        } catch (ReflectiveOperationException unsupported) { return false; }
        var params = purpose.getMapParams();
        // The final getter can still expose a protected map replaced by a subclass.
        if (params == null || params.getClass() != HashMap.class || params.containsKey("ManaRestriction")) return false;
        Card source = purpose.getHostCard();
        if (source == null || source.getClass() != Card.class || source.getGame() != pool.paymentGame()) return false;
        int inspected = 0;
        for (StaticAbility ability : source.getStaticAbilities()) {
            if (++inspected > MAX_UNITS || ability.getClass() != StaticAbility.class
                    || ability.getMode().contains(StaticAbilityMode.ManaRestriction)) return false;
        }
        return true;
    }
}
