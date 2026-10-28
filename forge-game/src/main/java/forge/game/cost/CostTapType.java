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

import forge.card.CardType;
import forge.game.ability.AbilityKey;
import forge.game.card.*;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.TriggerType;
import forge.game.zone.ZoneType;
import forge.util.Lang;
import forge.util.TextUtil;

import java.util.Map;

/**
 * The Class CostTapType.
 */
public class CostTapType extends CostPartWithList {

    /**
     * Serializables need a version ID.
     */
    private static final long serialVersionUID = 1L;
    public final boolean canTapSource;

    /**
     * Instantiates a new cost tap type.
     * 
     * @param amount
     *            the amount
     * @param type
     *            the type
     * @param description
     *            the description
     */
    public CostTapType(final String amount, final String type, final String description, boolean costHasTapSource) {
        super(amount, type, description);
        canTapSource  = !costHasTapSource;
    }

    @Override
    public Integer getMaxAmountX(SpellAbility ability, Player payer, final boolean effect) {
        return getEligibleCards(ability, payer).size();
    }

    /** Current physical resources shared by feasibility, input offers and payment.
     * The active adjusted payment owns any additional source {T} reservation. */
    public final CardCollection getEligibleCards(SpellAbility ability, Player payer) {
        final Card source = ability.getHostCard();
        boolean sourceReserved = !canTapSource;
        for (var entry : payer.getGame().costPaymentStack) {
            if (entry.payment().getAbility() != ability) continue;
            var unpaid = entry.payment().getUnpaidCostParts();
            // A paid {T} no longer reserves an untapped source. An untap cost
            // can also permit two distinct taps; leave that ordering available
            // and revalidate each action against its actual current resource.
            sourceReserved = unpaid.stream().anyMatch(part -> part instanceof CostTap)
                    && unpaid.stream().noneMatch(part -> part instanceof CostUntap || part instanceof CostUntapType);
        }
        String type = getType();
        final CardCollection candidates;
        if (type.equals("OriginalHost")) {
            candidates = new CardCollection();
            Card original = ability.getOriginalHost();
            if (original.isInZone(ZoneType.Battlefield)
                    && payer.getGame().getCardState(original, null) == original) candidates.add(original);
        } else {
            type = TextUtil.fastReplace(type, ".sharesCreatureTypeWith", "");
            if (type.contains("+withTotalPowerGE")) type = type.split("\\+withTotalPowerGE")[0];
            candidates = CardLists.getValidCards(payer.getCardsIn(ZoneType.Battlefield), type.split(";"), payer, source, ability);
        }
        if (sourceReserved) candidates.remove(source);
        return CardLists.filter(candidates, ability.isCrew() ? CardPredicates.CAN_CREW : CardPredicates.CAN_TAP);
    }

    @Override
    public boolean isReusable() { return true; }

    @Override
    public boolean isRenewable() { return true; }

    /*
     * (non-Javadoc)
     * 
     * @see forge.card.cost.CostPart#toString()
     */
    @Override
    public final String toString() {
        final StringBuilder sb = new StringBuilder();

        final String desc = this.getDescriptiveType();
        final String type = this.getType();
        final String amt = this.getAmount();

        if (type.contains("+withTotalPowerGE")) {
            String num = type.split("\\+withTotalPowerGE")[1];
            sb.append("Tap any number of untapped creatures you control other than CARDNAME with total power ");
            sb.append(num).append(" or greater");
            return sb.toString();
        }

        sb.append("Tap ");
        if (type.contains("Other")) {
            String rep = type.contains(".Other") ? ".Other" : "+Other";
            String descTrim = desc.replace(rep, "");
            if (CardType.CoreType.isValidEnum(descTrim)) {
                descTrim = descTrim.toLowerCase();
            }
            sb.append(amt.equals("1") ? "another untapped " + descTrim : 
                Lang.nounWithNumeral(amt, "other untapped " + descTrim));
            if (!descTrim.contains("you control")) {
                sb.append(" you control");
            }
        } else if (amt.equals("X")) {
            sb.append("any number of untapped ").append(desc).append("s you control");
        } else {
            sb.append(Lang.nounWithNumeralExceptOne(amt, "untapped " + desc)).append(" you control");
        }
        if (type.contains("sharesCreatureTypeWith")) {
            sb.append(" that share a creature type");
        }
        return sb.toString();
    }

    /*
     * (non-Javadoc)
     * 
     * @see forge.card.cost.CostPart#refund(forge.Card)
     */
    @Override
    public final void refund(final Card source) {
        for (final Card c : cardList) {
            c.setTapped(false);
        }
        cardList.clear();
    }

    /*
     * (non-Javadoc)
     * 
     * @see
     * forge.card.cost.CostPart#canPay(forge.card.spellability.SpellAbility,
     * forge.Card, forge.Player, forge.card.cost.Cost)
     */
    @Override
    public final boolean canPay(final SpellAbility ability, final Player payer, final boolean effect) {
        String type = this.getType();
        boolean sameType = false;

        if (type.equals("OriginalHost")) {
            return !getEligibleCards(ability, payer).isEmpty();
        }

        if (type.contains(".sharesCreatureTypeWith")) {
            sameType = true;
            type = TextUtil.fastReplace(type, ".sharesCreatureTypeWith", "");
        }
        boolean totalPower = false;
        String totalP = "";
        if (type.contains("+withTotalPowerGE")) {
            totalPower = true;
            totalP = type.split("withTotalPowerGE")[1];
            type = TextUtil.fastReplace(type, TextUtil.concatNoSpace("+withTotalPowerGE", totalP), "");
        }

        CardCollection typeList = getEligibleCards(ability, payer);

        if (sameType) {
            for (final Card card : typeList) {
                if (CardLists.count(typeList, CardPredicates.sharesCreatureTypeWith(card)) > 1) {
                    return true;
                }
            }
            return false;
        }

        if (totalPower) {
            final int i = Integer.parseInt(totalP);
            return CardLists.getTotalPower(typeList, ability) >= i;
        }

        final int amount = this.getAbilityAmount(ability);
        return typeList.size() >= amount;
    }

    @Override
    public boolean payAsDecided(Player payer, PaymentDecision decision, SpellAbility ability, final boolean effect) {
        if (decision == null) return false;
        var eligible = getEligibleCards(ability, payer);
        // Check the entire selection before tapping any card. Equality by card
        // ID alone must not turn an old zone occurrence into a current resource.
        for (Card chosen : decision.cards) {
            if (eligible.stream().noneMatch(current -> current == chosen)) return false;
        }
        String type = getType();
        if (type.contains("+withTotalPowerGE")) {
            int requiredPower = Integer.parseInt(type.split("withTotalPowerGE")[1]);
            if (CardLists.getTotalPower(decision.cards, ability) < requiredPower) return false;
        } else if (!getAmount().equals("Any") && decision.cards.size() != getAbilityAmount(ability)) {
            return false;
        }
        if (type.contains(".sharesCreatureTypeWith")) {
            for (Card chosen : decision.cards) {
                if (decision.cards.stream().anyMatch(other -> other != chosen && !chosen.sharesCreatureTypeWith(other))) return false;
            }
        }
        return super.payAsDecided(payer, decision, ability, effect);
    }

    /* (non-Javadoc)
     * @see forge.card.cost.CostPartWithList#executePayment(forge.card.spellability.SpellAbility, forge.Card)
     */
    @Override
    protected Card doPayment(Player payer, SpellAbility ability, Card targetCard, final boolean effect) {
        targetCard.tap(true, ability, payer);
        return targetCard;
    }

    @Override
    protected boolean canPayListAtOnce() {
        return true;
    }

    @Override
    protected CardCollectionView doListPayment(Player payer, SpellAbility ability, CardCollectionView targetCards, final boolean effect) {
        CardCollection tapped = new CardCollection();
        for (Card c : targetCards) {
            if (c.tap(true, ability, payer)) tapped.add(c);
        }

        final Map<AbilityKey, Object> runParams = AbilityKey.newMap();
        runParams.put(AbilityKey.Cards, tapped);
        payer.getGame().getTriggerHandler().runTrigger(TriggerType.TapAll, runParams, false);
        return targetCards;
    }

    /* (non-Javadoc)
     * @see forge.card.cost.CostPartWithList#getHashForList()
     */
    @Override
    public String getHashForLKIList() {
        return "Tapped";
    }
    @Override
    public String getHashForCardList() {
    	return "TappedCards";
    }

    // Inputs
    public <T> T accept(ICostVisitor<T> visitor) {
        return visitor.visit(this);
    }

}
