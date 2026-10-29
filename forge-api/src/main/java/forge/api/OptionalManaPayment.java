package forge.api;

import com.google.gson.Gson;
import forge.card.mana.ManaCostShard;
import forge.game.GameActionUtil;
import forge.game.card.Card;
import forge.game.cost.CostTap;
import forge.game.mana.Mana;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.util.Visitor;

import java.nio.charset.StandardCharsets;
import java.util.*;

/** Current optional mana only. A failed bounded witness is never a proof of absence. */
final class OptionalManaPayment {
    private static final int MAX_CARDS = 8192, MAX_SOURCES = 128, MAX_ROUTES = 32, MAX_UNITS = 128;
    private static final Gson JSON = new Gson();

    /** One ordinary gesture from a complete current funding witness. Never a retained action handle. */
    record Step(Byte poolColor, SpellAbility ability, Map<String, Integer> output) {
        boolean apply(InputPayMana input) {
            if (poolColor != null) {
                if (!input.getUsablePoolManaColors().contains(poolColor)) return false;
                input.useManaFromPool(poolColor); return true;
            }
            ability.setActivatingPlayer(input.getPayer());
            if (!ability.canPlay() || !ManaProductionProjection.simpleTapSourceIsStable(ability)) return false;
            var choices = ManaProductionProjection.outputChoices(ability, output);
            return choices != null && input.selectAbility(ability, choices);
        }
    }

    static Map<String, Object> describe(InputPayMana input, Map<String, Object> offer) {
        var original = input.getOriginalManaCost();
        if (offer == null || !input.isPaymentActive() || input.isPaymentMandatory()
                || !ResolutionBatchProjection.literalManaOnly(input.getPaidForAbility()) || !fixed(original)
                || !remainingSubset(original, input.getRemainingManaCost())) return null;
        String key = UUID.nameUUIDFromBytes(JSON.toJson(List.of(offer.get("groupKey"), offer.get("purpose"),
                offer.get("payerId"), ManaPaymentFacts.parts(original))).getBytes(StandardCharsets.UTF_8)).toString();
        Step witness = null;
        boolean absent = false;
        try { witness = witness(input); if (witness == null) absent = noFundingRoute(input); }
        catch (RuntimeException unsupported) { /* An unsupported rules probe leaves the question explicit. */ }
        return Map.of("policyKey", key, "originalManaParts", ManaPaymentFacts.parts(original),
                "availability", witness != null ? "Payable" : absent ? "Unpayable" : "Unknown",
                "canAutoPay", witness != null);
    }

    static Step witness(InputPayMana input) {
        if (!input.isPaymentActive() || input.isPaymentMandatory() || !fixed(input.getOriginalManaCost())
                || !fixed(input.getRemainingManaCost()) || input.getPayer() == null
                || !ResolutionBatchProjection.literalManaOnly(input.getPaidForAbility())
                || !remainingSubset(input.getOriginalManaCost(), input.getRemainingManaCost())) return null;
        var remaining = input.getRemainingManaCost();
        if (units(remaining) > MAX_UNITS) return null;
        var payer = input.getPayer();
        var paidFor = input.getPaidForAbility();
        Step first = null;
        int examined = 0;
        // Pool order must match the real color gesture: the first eligible unit
        // can differ in snow/restrictions from later units of the same color.
        var blockedColors = new HashSet<Byte>();
        for (Mana mana : payer.getManaPool()) {
            if (++examined > MAX_UNITS) return null;
            if (blockedColors.contains(mana.getColor()) || !mana.meetsManaRestrictions(paidFor)
                    || !paidFor.allowsPayingWithShard(mana.getSourceCard(), mana.getColor())) continue;
            if (!remaining.isNeeded(mana, payer.getManaPool())) { blockedColors.add(mana.getColor()); continue; }
            // Spending these units can itself need further decisions. The
            // ordinary manual route remains, but unattended funding is unknown.
            if (mana.triggersWhenSpent() || mana.addsCounters(paidFor) || mana.addsKeywords(paidFor)) return null;
            remaining.payMana(mana, payer.getManaPool());
            if (first == null) first = new Step(mana.getColor(), null, Map.of());
            if (remaining.isPaid()) return first;
        }
        int sources = 0;
        for (Card source : payer.getCardsIn(ZoneType.Battlefield)) {
            if (++sources > MAX_SOURCES) return null;
            if (source.isFaceDown() || !source.getView().canBeShownTo(payer.getView())) continue;
            int routes = 0;
            ManaCostBeingPaid best = remaining;
            Step chosen = null;
            for (SpellAbility original : source.getManaAbilities()) {
                if (++routes > MAX_ROUTES) return null;
                SpellAbility ability = original.copy(source, payer, true);
                if (!ability.canPlay() || !ManaProductionProjection.simpleTapSourceIsStable(ability)
                        || ability.getPayCosts().getCostParts().stream().filter(CostTap.class::isInstance)
                            .anyMatch(cost -> !cost.canPay(ability, payer, false))) continue;
                var production = ManaProductionProjection.describe(ability);
                if (!"Known".equals(production.get("knowledge"))) continue;
                @SuppressWarnings("unchecked") var alternatives = (List<Map<String, Object>>) production.get("alternatives");
                for (var alternative : alternatives) {
                    @SuppressWarnings("unchecked") var colors = (Map<String, Integer>) alternative.get("colors");
                    if (colors.values().stream().mapToLong(Integer::longValue).sum() > MAX_UNITS) continue;
                    var candidate = new ManaCostBeingPaid(remaining);
                    boolean supported = true;
                    for (var color : colors.entrySet()) {
                        byte mask = forge.card.mana.ManaAtom.fromName(color.getKey());
                        if (!paidFor.allowsPayingWithShard(source, mask) || !ability.isManaAbilityFor(paidFor, mask)) {
                            supported = false; break;
                        }
                        for (int unit = 0; unit < color.getValue() && !candidate.isPaid(); unit++)
                            candidate.ai_payMana(color.getKey(), payer.getManaPool());
                    }
                    if (supported && candidate.getConvertedManaCost() < best.getConvertedManaCost()) {
                        best = candidate; chosen = new Step(null, original, Map.copyOf(colors));
                    }
                }
            }
            if (chosen != null) {
                remaining = best;
                if (first == null) first = chosen;
                if (remaining.isPaid()) return first;
            }
        }
        return null;
    }

    private static boolean noFundingRoute(InputPayMana input) {
        if (!input.isPaymentActive() || input.getPayer() == null || !fixed(input.getRemainingManaCost())
                || input.getPayer().hasKeyword("PayLifeInsteadOf:B") && input.getRemainingManaCost().hasAnyKind(forge.card.mana.ManaAtom.BLACK)
                || !input.getUsablePoolManaColors().isEmpty()) return false;
        var payer = input.getPayer();
        // Covers hand/exile/command/library routes as well as battlefield
        // sources, including externally usable and alternative activations.
        // Any startable route is uncertainty: it may fund a conversion chain,
        // have restricted output, or require a deliberate nonmana commitment.
        var absence = new boolean[] {true};
        payer.getGame().forEachCardInGame(new Visitor<Card>() {
            private int cards, routes;
            @Override public boolean visit(Card source) {
                if (++cards > MAX_CARDS) return stop();
                for (SpellAbility original : source.getManaAbilities()) {
                    if (++routes > MAX_CARDS) return stop();
                    SpellAbility ability = original.copy(source, payer, true);
                    if (ability.canPlay(true)) return stop();
                    var alternatives = GameActionUtil.getAlternativeCosts(ability, payer, false);
                    if (alternatives.size() > MAX_ROUTES) return stop();
                    for (SpellAbility alternative : alternatives) if (alternative.canPlay(true)) return stop();
                }
                return true;
            }
            private boolean stop() { absence[0] = false; return false; }
        }, true); // The real input also permits supported outside-game sources.
        return absence[0];
    }

    private static boolean fixed(ManaCostBeingPaid cost) {
        if (cost == null || cost.isPaid() || cost.getXcounter() != 0) return false;
        for (var shard : cost.getDistinctShards())
            if (!Set.of(ManaCostShard.GENERIC, ManaCostShard.WHITE, ManaCostShard.BLUE, ManaCostShard.BLACK,
                    ManaCostShard.RED, ManaCostShard.GREEN, ManaCostShard.COLORLESS).contains(shard)) return false;
        return true;
    }
    private static long units(ManaCostBeingPaid cost) {
        long count = 0;
        for (var shard : cost.getDistinctShards()) count += cost.getUnpaidShards(shard);
        return count;
    }
    private static boolean remainingSubset(ManaCostBeingPaid original, ManaCostBeingPaid remaining) {
        if (!fixed(remaining)) return false;
        for (var shard : remaining.getDistinctShards())
            if (remaining.getUnpaidShards(shard) > original.getUnpaidShards(shard)) return false;
        return true;
    }
    private OptionalManaPayment() { }
}
