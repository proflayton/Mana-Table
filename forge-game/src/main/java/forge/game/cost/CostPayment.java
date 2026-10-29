/*
 * Forge: Play Magic: the Gathering.
 * Copyright (C) 2011  Forge Team
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
package forge.game.cost;

import com.google.common.collect.Lists;
import com.google.common.collect.Maps;
import forge.card.MagicColor;
import forge.card.mana.ManaCostShard;
import forge.game.Game;
import forge.game.ability.AbilityKey;
import forge.game.card.Card;
import forge.game.card.CardCollection;
import forge.game.card.CardZoneTable;
import forge.game.mana.*;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import org.apache.commons.lang3.tuple.Pair;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.Predicate;

/**
 * <p>
 * Cost_Payment class.
 * </p>
 * 
 * @author Forge
 * @version $Id$
 */
public class CostPayment extends ManaConversionMatrix {
    private final Cost cost;
    private Cost adjustedCost;
    private final SpellAbility ability;
    private final List<CostPart> paidCostParts = Lists.newArrayList();
    private Player payer;

    /** Current payment owner and remaining adjusted parts, for read-only
     * decision projection. No caller can mutate payment order or paid state. */
    public final Player getPayer() { return payer; }
    public final List<CostPart> getUnpaidCostParts() {
        return adjustedCost.getCostParts().stream().filter(part -> !paidCostParts.contains(part)).toList();
    }

    /**
     * <p>
     * Getter for the field <code>cost</code>.
     * </p>
     * 
     * @return a {@link forge.game.cost.Cost} object.
     */
    public final Cost getCost() {
        return this.cost;
    }

    public final SpellAbility getAbility() {
        return this.ability;
    }

    /**
     * <p>
     * Constructor for Cost_Payment.
     * </p>
     * 
     * @param cost
     *            a {@link forge.game.cost.Cost} object.
     * @param abil
     *            a {@link forge.game.spellability.SpellAbility} object.
     */
    public CostPayment(final Cost cost, final SpellAbility abil) {
        this.cost = cost;
        this.adjustedCost = cost;
        this.ability = abil;
        restoreColorReplacements();
    }

    /**
     * <p>
     * canPayAdditionalCosts.
     * </p>
     * 
     * @param cost
     *            a {@link forge.game.cost.Cost} object.
     * @param ability
     *            a {@link forge.game.spellability.SpellAbility} object.
     * @return a boolean.
     */
    public static boolean canPayAdditionalCosts(Cost cost, final SpellAbility ability, final boolean effect) {
        return canPayAdditionalCosts(cost, ability, effect, ability.getActivatingPlayer());
    }
    public static boolean canPayAdditionalCosts(Cost cost, final SpellAbility ability, final boolean effect, final Player payer) {
        if (cost == null) {
            return true;
        }

        cost = CostAdjustment.adjust(cost, ability, effect);
        return cost.canPay(ability, payer, effect);
    }

    /**
     * <p>
     * isAllPaid.
     * </p>
     * 
     * @return a boolean.
     */
    public final boolean isFullyPaid() {
        return paidCostParts.containsAll(adjustedCost.getCostParts());
    }

    /**
     * <p>
     * cancelPayment.
     * </p>
     */
    public final void refundPayment() {
        Card sourceCard = this.ability.getHostCard();
        for (final CostPart part : this.paidCostParts) {
            part.refund(sourceCard);
            // Clear lists to prevent accumulation across multiple cancelled activations
            if (part instanceof CostPartWithList) {
                ((CostPartWithList) part).resetLists();
            }
        }

        new ManaRefundService(this.ability).refundManaPaid();
    }

    public boolean payCost(final CostDecisionMakerBase decisionMaker) {
        payer = decisionMaker.getPlayer();
        ability.getHostCard().getGame().getExecutions().step(ability, forge.game.ExecutionTracker.Step.COST);
        adjustedCost = CostAdjustment.adjust(cost, ability, decisionMaker.isEffect());
        List<CostPart> costParts = adjustedCost.getCostPartsWithZeroMana();

        if (adjustedCost.getCostParts().size() > 1) {
            // if mana part is shown here it wouldn't include reductions, but that's just a minor inconvenience
            costParts = decisionMaker.getPlayer().getController().orderCosts(costParts);
        }

        final Game game = decisionMaker.getPlayer().getGame();

        for (final CostPart part : costParts) {
            // Wrap the cost and push onto the cost stack
            try {
                game.costPaymentStack.push(part, this);

                PaymentDecision pd = part.accept(decisionMaker);

                // Right before we start paying as decided, we need to transfer the CostPayments matrix over?
                if (pd != null) {
                    pd.matrix = this;
                }

                if (pd == null || !part.payAsDecided(decisionMaker.getPlayer(), pd, ability, decisionMaker.isEffect())) {
                    return false;
                }
                this.paidCostParts.add(part);
            } finally {
                game.costPaymentStack.pop(); // cost is resolved
            }
        }

        // clear lists used for undo
        for (final CostPart part : this.paidCostParts) {
            if (part instanceof CostPartWithList listCost) {
                listCost.resetLists();
            }
        }

        return true;
    }

    public final boolean payComputerCosts(final CostDecisionMakerBase decisionMaker) {
        // Just in case it wasn't set, but honestly it shouldn't have gotten
        // here without being set
        if (this.ability.getActivatingPlayer() == null) {
            this.ability.setActivatingPlayer(decisionMaker.getPlayer());
        }

        Map<CostPart, PaymentDecision> decisions = Maps.newHashMap();
        // for Trinisphere make sure to include Zero
        List<CostPart> parts = CostAdjustment.adjust(cost, ability, decisionMaker.isEffect()).getCostPartsWithZeroMana();

        // Set all of the decisions before attempting to pay anything

        final Game game = decisionMaker.getPlayer().getGame();

        for (final CostPart part : parts) {
            PaymentDecision decision = part.accept(decisionMaker);
            if (null == decision) return false;

            try {
                // wrap the payment and push onto the cost stack
                game.costPaymentStack.push(part, this);
                if (decisionMaker.paysRightAfterDecision() && !part.payAsDecided(decisionMaker.getPlayer(), decision, ability, decisionMaker.isEffect())) {
                    return false;
                }
            } finally {
                game.costPaymentStack.pop(); // cost is either paid or deferred
            }
            decisions.put(part, decision);
        }

        for (final CostPart part : parts) {
            // wrap the payment and push onto the cost stack
            try {
                game.costPaymentStack.push(part, this);

                if (!part.payAsDecided(decisionMaker.getPlayer(), decisions.get(part), this.ability, decisionMaker.isEffect())) {
                    return false;
                }
                // abilities care what was used to pay for them
                if (part instanceof CostPartWithList) {
                    ((CostPartWithList) part).resetLists();
                }
            } finally {
                game.costPaymentStack.pop(); // cost is resolved
            }
        }
        return true;
    }

    /**
     * <p>
     * getManaFrom.
     * </p>
     *
     * @param saBeingPaidFor
     *            a {@link forge.game.spellability.SpellAbility} object.
     * @return a {@link forge.game.mana.Mana} object.
     */
    public static Mana getMana(final Player player, final ManaCostShard shard, final SpellAbility saBeingPaidFor,
            final byte colorsPaid, Map<String, Integer> xManaCostPaidByColor) {
        final List<Mana> manaChoices = bestManaChoices(selectManaToPayFor(player.getManaPool(), player.getManaPool(), shard,
                colorsPaid, xManaCostPaidByColor, mana ->
                    (mana.getManaAbility() == null || mana.getManaAbility().meetsSpellAndShardRestrictions(saBeingPaidFor, shard, mana.getColor()))
                        && saBeingPaidFor.allowsPayingWithShard(mana.getSourceCard(), mana.getColor())));

        // Exclude border case
        if (manaChoices.isEmpty()) {
            return null; // There is no matching mana in the pool
        }

        // got an only one best option?
        if (manaChoices.size() == 1) return manaChoices.get(0);
        // Only actual payment asks a controller to break a tie.
        return player.getController().chooseManaFromPool(manaChoices);
    }

    /** Pure shared weighting for an already detached, unrestricted unit ledger.
     *  Purpose restrictions must have been excluded by the caller's closed quote boundary. */
    public static List<Mana> unrestrictedManaChoices(List<Mana> units, ManaPaymentMatrix matrix,
            ManaCostShard shard, byte colorsPaid) {
        return List.copyOf(bestManaChoices(selectManaToPayFor(units, matrix, shard, colorsPaid, null,
                mana -> !mana.isRestricted())));
    }

    private static List<Mana> bestManaChoices(List<Pair<Mana, Integer>> weightedOptions) {
        // select equal weight possibilities
        List<Mana> manaChoices = new ArrayList<>();
        int bestWeight = Integer.MIN_VALUE;
        for (Pair<Mana, Integer> option : weightedOptions) {
            int thisWeight = option.getRight();
            Mana thisMana = option.getLeft();

            if (thisWeight > bestWeight) {
                manaChoices.clear();
                bestWeight = thisWeight;
            }

            if (thisWeight == bestWeight) {
                // add only distinct Mana-s
                boolean haveDuplicate = false;
                for (Mana m : manaChoices) {
                    if (m.equals(thisMana)) {
                        haveDuplicate = true;
                        break;
                    }
                }
                if (!haveDuplicate) {
                    manaChoices.add(thisMana);
                }
            }
        }

        return manaChoices;
    }

    private static List<Pair<Mana, Integer>> selectManaToPayFor(final Iterable<Mana> units,
            final ManaPaymentMatrix matrix, final ManaCostShard shard,
            final byte colorsPaid, Map<String, Integer> xManaCostPaidByColor, Predicate<Mana> eligible) {
        final List<Pair<Mana, Integer>> weightedOptions = new ArrayList<>();
        for (final Mana thisMana : Lists.newArrayList(units)) {
            if (shard == ManaCostShard.COLORED_X && !ManaCostBeingPaid.canColoredXShardBePaidByColor(MagicColor.toShortString(thisMana.getColor()), xManaCostPaidByColor)) {
                continue;
            }

            if (!matrix.canPayForShardWithColor(shard, thisMana.getColor())) {
                continue;
            }

            if (shard.isSnow() && !thisMana.isSnow()) {
                continue;
            }

            if (!eligible.test(thisMana)) continue;

            int weight = 0;
            if (colorsPaid == -1) {
                // prefer colorless mana to spend
                weight += thisMana.isColorless() ? 5 : 0;
            } else {
                // get more colors for converge
                weight += (thisMana.getColor() | colorsPaid) != colorsPaid ? 5 : 0;
            }

            // prefer restricted mana to spend
            if (thisMana.isRestricted()) {
                weight += 2;
            }

            // Spend non-snow mana first
            if (!thisMana.isSnow()) {
                weight += 1;
            }

            weightedOptions.add(Pair.of(thisMana, weight));
        }
        return weightedOptions;
    }

    public static boolean handleOfferings(final SpellAbility sa, boolean test, boolean costIsPaid) {
        final Game game = sa.getHostCard().getGame();
        final CardZoneTable table = new CardZoneTable(game.getLastStateBattlefield(), game.getLastStateGraveyard());
        Map<AbilityKey, Object> params = AbilityKey.newMap();
        AbilityKey.addCardZoneTableParams(params, table);

        if (sa.isOffering()) {
            if (sa.getSacrificedAsOffering() == null) {
                return false;
            }
            final Card offering = sa.getSacrificedAsOffering();
            offering.setUsedToPay(false);
            if (test) {
                sa.resetSacrificedAsOffering();
            } else if (costIsPaid) {
                game.getAction().sacrifice(new CardCollection(offering), sa, false, params);
            }
        }
        if (sa.isEmerge()) {
            if (sa.getSacrificedAsEmerge() == null) {
                return false;
            }
            final Card emerge = sa.getSacrificedAsEmerge();
            emerge.setUsedToPay(false);
            if (test) {
                sa.resetSacrificedAsEmerge();
            } else if (costIsPaid) {
                game.getAction().sacrifice(new CardCollection(emerge), sa, false, params);
                sa.setSacrificedAsEmerge(game.getChangeZoneLKIInfo(emerge));
            }
        }
        if (!table.isEmpty()) {
            table.triggerChangesZoneAll(sa.getHostCard().getGame(), sa);
        }
        return true;
    }
}
