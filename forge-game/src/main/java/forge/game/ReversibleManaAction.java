package forge.game;

import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.card.CardStateName;
import forge.game.cost.CostPartMana;
import forge.game.cost.CostTap;
import forge.game.event.*;
import forge.game.mana.Mana;
import forge.game.phase.PhaseType;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.MagicStack;
import forge.game.zone.Zone;
import forge.game.zone.ZoneType;
import forge.game.event.Event;

import java.util.*;

/**
 * One opt-in, closed tap-for-literal-mana reversal. This is not a game snapshot,
 * a cost refund, or an event replay. All calls and administrative writes share
 * the game's state boundary. The adapter additionally retires it before commands
 * and controller choices; no opaque record or hidden guard leaves the provider.
 */
public final class ReversibleManaAction {
    private static final int MAX_CARDS = 1024, MAX_MANA = 1024, MAX_HISTORY = 1024;
    private static final int MAX_GUARD_ENTRIES = 16384, MAX_GUARD_TEXT = 1048576, MAX_TEXT = 8192;
    private static final Set<String> PARAMS = Set.of("AB", "Cost", "Produced", "Amount", "Defined",
            "SpellDescription", "StackDescription", "Description", "IsSecondary", "Secondary");
    public enum Result { APPLIED, REJECTED }
    public record Offer(String id, String actionId, String executionId, int actorId, int sourceId, long barrierVersion) { }
    public record Restoration(long version, String actionId, String executionId, int actorId) { }

    public static final class Capture {
        private final String id = UUID.randomUUID().toString(), actionId = UUID.randomUUID().toString();
        private final Player actor;
        private final SpellAbility ability;
        private final Card source;
        private final long barrier;
        private final PhaseType phase;
        private final int turn;
        private final long timestamp;
        private final Map<Byte, Integer> output;
        private final Card.ManaActivationState card;
        private final SpellAbility.ManaActivationState abilityState;
        private final List<Mana> pool, production;
        private final MagicStack.ManaActivationHistory history;
        private final Game.ManaLkiState lki;
        private final StaticEffects.ReversiblePermissions permissions;
        private final boolean tappedLand;
        private final List<CardGuard> cards;
        private final List<PlayerGuard> players;
        private List<Mana> resultingPool, resultingProduction;
        private Card.ManaActivationState resultingCard;
        private SpellAbility.ManaActivationState resultingAbility;
        private MagicStack.ManaActivationHistory resultingHistory;
        private String executionId;
        private boolean settled;
        private int tapEvents, manaEvents;

        private Capture(Game game, Player actor, SpellAbility ability, long barrier, Map<Byte, Integer> output) {
            this.actor = actor; this.ability = ability; source = ability.getHostCard(); this.barrier = barrier;
            this.output = output; phase = game.getPhaseHandler().getPhase(); turn = game.getPhaseHandler().getTurn();
            timestamp = game.getTimestamp(); card = source.captureManaActivationState();
            abilityState = ability.captureManaActivationState(); pool = actor.getManaPool().captureReversibleUnits();
            production = ability.getManaPart().captureReversibleProduction();
            history = game.getStack().captureManaActivationHistory(); lki = game.captureManaLkiState();
            permissions = game.getStaticEffects().captureReversiblePermissions(game);
            tappedLand = actor.hasTappedLandForManaThisTurn();
            cards = guards(game, source); players = playerGuards(game, actor);
        }
    }

    private record TraitGuard(Object trait, Map<String, String> parameters) { }
    private record CardGuard(Card card, Zone zone, long timestamp, Object state, Player owner, Player controller,
                             com.google.common.collect.Multiset<?> counters, int damage, Map<String, String> variables,
                             Card.ManaActivationState activation, Object attachedTo, List<Card> attachments,
                             List<TraitGuard> traits, String type, int power, int toughness, List<String> keywords) {
        static CardGuard of(Card c, Card source) {
            var traits = new ArrayList<TraitGuard>();
            for (var trait : c.getAllSpellAbilities()) traits.add(new TraitGuard(trait, Map.copyOf(trait.getMapParams())));
            for (var trait : c.getTriggers()) traits.add(new TraitGuard(trait, Map.copyOf(trait.getMapParams())));
            for (var trait : c.getStaticAbilities()) traits.add(new TraitGuard(trait, Map.copyOf(trait.getMapParams())));
            for (var trait : c.getCurrentState().getReplacementEffects(false)) traits.add(new TraitGuard(trait, Map.copyOf(trait.getMapParams())));
            var attachments = new ArrayList<Card>(); for (Card a : c.getAllAttachedCards()) attachments.add(a);
            var keywords = new ArrayList<String>(); for (var keyword : c.getKeywords()) keywords.add(keyword.getOriginal());
            return new CardGuard(c, c.getZone(), c.getGameTimestamp(), c.getCurrentStateName(),
                    c.getOwner(), c.getController(), com.google.common.collect.ImmutableMultiset.copyOf(c.getCounters()),
                    c.getDamage(), Map.copyOf(c.getSVars()), c == source ? null : c.captureManaActivationState(),
                    c.getEntityAttachedTo(), List.copyOf(attachments), List.copyOf(traits), c.getType().toString(),
                    c.getNetPower(), c.getNetToughness(), List.copyOf(keywords));
        }
    }
    private record PlayerGuard(Player player, int life, int lands, com.google.common.collect.Multiset<?> counters, List<Mana> mana) { }

    private final Game game;
    private boolean enabled, restoring;
    private Capture capture;
    private long barrierVersion, restorationVersion;
    private Restoration lastRestoration;
    private String lastInvalidationReason = "disabled";

    ReversibleManaAction(Game game) { this.game = game; }
    public void enable() { game.getAction().withState(() -> enabled = true); }
    public long barrierVersion() { return game.getAction().readState(() -> barrierVersion); }
    public long restorationVersion() { return game.getAction().readState(() -> restorationVersion); }
    public Restoration lastRestoration() { return game.getAction().readState(() -> lastRestoration); }
    /** Engine diagnostics only; not part of the player capability or wire contract. */
    public String lastInvalidationReason() { return game.getAction().readState(() -> lastInvalidationReason); }
    public void invalidate(String reason) {
        game.getAction().withState(() -> { if (enabled) { capture = null; barrierVersion++; lastInvalidationReason = reason; } });
    }

    /** Call immediately before the normal priority activation runner, never a payment runner. */
    public Capture begin(Player actor, SpellAbility ability) {
        return game.getAction().readState(() -> {
            if (!enabled) return null;
            capture = null; barrierVersion++;
            Map<Byte, Integer> output = eligible(actor, ability);
            if (output == null) {
                lastInvalidationReason = "unsupported-route:params=" + (ability == null ? "null" : ability.getMapParams())
                        + ",paidFor=" + (actor != null && actor.getPaidForSA() != null)
                        + ",priority=" + (actor != null && game.getPhaseHandler().getPriorityPlayer() == actor)
                        + ",express=" + (ability == null || ability.getManaPart() == null ? "null" : ability.getManaPart().getExpressChoice());
                return null;
            }
            if (!quiescent()) { lastInvalidationReason = "nonquiescent:" + quiescenceDiagnostic; return null; }
            capture = new Capture(game, actor, ability, barrierVersion, output);
            lastInvalidationReason = "capturing";
            return capture;
        });
    }

    public void settle(Capture candidate) {
        game.getAction().withState(() -> {
            if (candidate == null || capture != candidate || candidate.settled) return;
            var c = candidate;
            var occurrence = game.getExecutions().occurrence(c.ability);
            if (occurrence == null || !"complete".equals(occurrence.step()) || !"resolved".equals(occurrence.outcome())
                    || !stable(c) || c.tapEvents != 1 || c.manaEvents != 1) { invalidate("unproven-completion"); return; }
            c.executionId = occurrence.id();
            c.resultingProduction = c.ability.getManaPart().captureReversibleProduction();
            c.resultingPool = c.actor.getManaPool().captureReversibleUnits();
            c.resultingCard = c.source.captureManaActivationState();
            c.resultingAbility = c.ability.captureManaActivationState();
            c.resultingHistory = game.getStack().captureManaActivationHistory();
            var turn = c.card.turn().copy(); turn.add(c.ability);
            var all = c.card.game().copy(); all.add(c.ability);
            var resolved = c.card.resolved().copy(); resolved.add(c.ability);
            var expectedCard = new Card.ManaActivationState(true, c.card.tappedThisTurn() + 1, turn, all, resolved);
            var expectedPool = new ArrayList<>(c.pool); expectedPool.addAll(c.resultingProduction);
            var produced = new HashMap<Byte, Integer>();
            boolean exact = !c.resultingProduction.isEmpty();
            for (Mana mana : c.resultingProduction) {
                // Mana retains event-time LKI, rather than the live Card reference.
                exact &= mana.getSourceCard().getId() == c.source.getId()
                        && mana.getSourceCard().getGameTimestamp() == c.source.getGameTimestamp()
                        && mana.getSourceCard().getGame() == game
                        && mana.getManaAbility() == c.ability.getManaPart() && mana.getPlayer() == c.actor;
                produced.merge(mana.getColor(), 1, Integer::sum);
            }
            var activated = c.resultingHistory.activated();
            exact &= c.resultingCard.equals(expectedCard) && c.output.equals(produced)
                    && sameUnits(expectedPool, c.resultingPool)
                    && activated.size() == c.history.activated().size() + 1
                    && activated.subList(0, c.history.activated().size()).equals(c.history.activated())
                    && c.actor.hasTappedLandForManaThisTurn() == (c.tappedLand || c.source.isLand());
            if (!exact) { invalidate("unexpected-footprint:card=" + c.resultingCard.equals(expectedCard)
                    + ",output=" + c.output.equals(produced) + ",pool=" + sameUnits(expectedPool,c.resultingPool)
                    + ",history=" + (activated.size() == c.history.activated().size() + 1)); return; }
            c.settled = true;
        });
    }

    public Offer current() {
        return game.getAction().readState(() -> {
            var c = capture;
            if (c == null || !c.settled) return null;
            if (!valid(c)) { invalidate("changed-footprint:" + validationDiagnostic); return null; }
            return new Offer(c.id, c.actionId, c.executionId, c.actor.getId(), c.source.getId(), c.barrier);
        });
    }
    public boolean validate(String offerId, String actionId, long barrier, Player actor) {
        return game.getAction().readState(() -> {
            var c = capture;
            if (!matches(c, offerId, actionId, barrier, actor)) return false;
            if (valid(c)) return true;
            invalidate("changed-footprint:" + validationDiagnostic); return false;
        });
    }
    public Result restore(String offerId, String actionId, long barrier, Player actor) {
        return game.getAction().readState(() -> {
            Capture c = capture;
            if (!matches(c, offerId, actionId, barrier, actor)) return Result.REJECTED;
            if (!valid(c)) { invalidate("changed-footprint:" + validationDiagnostic); return Result.REJECTED; }
            // All validation is complete before the first write. Owner methods
            // below are administrative and publish no intermediate game events.
            capture = null; barrierVersion++;
            restoring = true;
            try {
                c.source.restoreManaActivationState(c.card);
                c.ability.restoreManaActivationState(c.abilityState);
                c.ability.getManaPart().restoreReversibleProduction(c.production);
                c.actor.getManaPool().restoreReversibleUnits(c.pool);
                c.actor.setTappedLandForManaThisTurn(c.tappedLand);
                game.getStack().restoreManaActivationHistory(c.history);
                game.getStaticEffects().restoreReversiblePermissions(game, c.permissions);
                game.restoreManaLkiState(c.lki);
                lastRestoration = new Restoration(++restorationVersion, c.actionId, c.executionId, actor.getId());
                return Result.APPLIED;
            } finally { restoring = false; }
        });
    }

    private boolean matches(Capture c, String id, String action, long barrier, Player actor) {
        return enabled && c != null && c.settled && c.actor == actor && c.id.equals(id)
                && c.actionId.equals(action) && c.barrier == barrier && barrier == barrierVersion;
    }
    private String validationDiagnostic;
    private boolean failedValidation(String reason) { validationDiagnostic = reason; return false; }
    private boolean valid(Capture c) {
        if (!c.settled || !stable(c)) return false;
        if (!c.source.captureManaActivationState().equals(c.resultingCard)) return failedValidation("source-ledgers");
        if (!c.ability.captureManaActivationState().equals(c.resultingAbility)) return failedValidation("ability-ledgers");
        if (!c.actor.getManaPool().hasExactReversibleUnits(c.resultingPool)) return failedValidation("pool-units");
        if (!sameUnits(c.resultingProduction, c.ability.getManaPart().captureReversibleProduction())) return failedValidation("production");
        if (!game.getStack().captureManaActivationHistory().equals(c.resultingHistory)) return failedValidation("stack-history");
        if (c.actor.hasTappedLandForManaThisTurn() != (c.tappedLand || c.source.isLand())) return failedValidation("land-mana-flag");
        return capture == c && c.barrier == barrierVersion;
    }
    private boolean stable(Capture c) {
        if (!defaultController(c.actor)) return failedValidation("advanced-controller-controls");
        if (!(c.barrier == barrierVersion && !game.isGameOver() && !c.actor.hasLost()
                && game.getTimestamp() == c.timestamp && game.getPhaseHandler().getPriorityPlayer() == c.actor
                && game.getPhaseHandler().getPhase() == c.phase && game.getPhaseHandler().getTurn() == c.turn))
            return failedValidation("phase/actor/timestamp:barrier=" + (c.barrier == barrierVersion)
                    + ",over=" + game.isGameOver() + ",lost=" + c.actor.hasLost()
                    + ",timestamp=" + c.timestamp + "/" + game.getTimestamp()
                    + ",priority=" + (game.getPhaseHandler().getPriorityPlayer() == c.actor)
                    + ",phase=" + c.phase + "/" + game.getPhaseHandler().getPhase()
                    + ",turn=" + c.turn + "/" + game.getPhaseHandler().getTurn());
        if (!quiescent()) return failedValidation("nonquiescent:" + quiescenceDiagnostic);
        if (!c.cards.equals(guards(game,c.source))) return failedValidation("card-guards");
        if (!samePlayers(c.players,playerGuards(game,c.actor))) return failedValidation("player-guards");
        return c.permissions.sameFootprint(game.getStaticEffects().captureReversiblePermissions(game)) || failedValidation("permissions");
    }

    /** A stricter gate than funding prediction. Unknown script keys are unsupported. */
    private Map<Byte, Integer> eligible(Player actor, SpellAbility ability) {
        if (actor == null || ability == null || actor.getGame() != game || game.getPhaseHandler().getPriorityPlayer() != actor
                || !defaultController(actor)
                || actor.getPaidForSA() != null || game.getAge() != GameStage.Play || game.costPaymentStack.peek() != null
                || ability.getApi() != ApiType.Mana || !ability.isActivatedAbility() || !ability.isManaAbility()
                || ability.isCopied() || ability.isTrigger() || ability.getParent() != null || ability.getSubAbility() != null
                || ability.getKeyword() != null || ability.getGrantorStatic() != null
                || ability.usesTargeting() || !ability.getAdditionalAbilities().isEmpty() || !ability.getAdditionalAbilityLists().isEmpty()
                || !PARAMS.containsAll(ability.getMapParams().keySet()) || !"You".equals(ability.getParamOrDefault("Defined", "You"))) return null;
        var source = ability.getHostCard(); var mana = ability.getManaPart();
        if (source == null || source.getGame() != game || source.getController() != actor || source.isTapped()
                || source.isFaceDown() || source.isPhasedOut() || !source.isInZone(ZoneType.Battlefield)
                || source.getCurrentStateName() != CardStateName.Original || mana == null || mana.getSourceCard() != source
                || !mana.getManaRestrictions().isEmpty() || !mana.getExtraManaRestriction().isEmpty()
                || mana.getTriggersWhenSpent() || mana.addsCounters(ability) || mana.addKeywords(ability)
                || mana.isPersistentMana() || mana.isCombatMana() || mana.isCannotCounterPaidWith()
                || !mana.getExpressChoice().isEmpty() || ability.getPayCosts() == null || !ability.isUndoable()
                || !ability.reversibleManaStateWithin(MAX_HISTORY) || mana.getLastManaProduced().size() > MAX_MANA
                || (game.getStack().getUndoStackSize() > 0 && !game.getStack().canUndo(actor))) return null;
        var parts = ability.getPayCosts().getCostParts();
        if (parts.stream().filter(CostTap.class::isInstance).count() != 1 || parts.stream().anyMatch(part ->
                !(part instanceof CostTap) && !(part instanceof CostPartMana cost && cost.getMana().isZero()))) return null;
        String produced = mana.getOrigProduced(), amountText = ability.getParamOrDefault("Amount", "1");
        if (!amountText.matches("[1-9][0-9]?") || produced == null || !produced.matches("[WUBRGC1-9](?:[0-9]?|(?: [WUBRGC])*)")) return null;
        int amount = Integer.parseInt(amountText); var result = new HashMap<Byte, Integer>();
        for (String unit : produced.split(" ")) {
            if (unit.matches("[1-9][0-9]?")) result.merge((byte)forge.card.mana.ManaAtom.COLORLESS, Integer.parseInt(unit) * amount, Integer::sum);
            else result.merge(forge.card.mana.ManaAtom.fromName(unit), amount, Integer::sum);
        }
        return result.values().stream().mapToInt(Integer::intValue).sum() <= 64 ? Map.copyOf(result) : null;
    }

    private static boolean defaultController(Player actor) {
        var controller = actor.getController();
        // Bare core rules fixtures have no controller; hosted seats always do.
        return controller == null || controller.getFullControl().isEmpty();
    }

    private String quiescenceDiagnostic;
    private boolean notQuiescent(String reason) { quiescenceDiagnostic = reason; return false; }
    private boolean quiescent() {
        if (!game.getStack().isEmpty() || game.getStack().isFrozen() || game.getStack().isResolving()
                || game.getStack().hasSimultaneousStackEntries() || game.costPaymentStack.peek() != null
                || game.hasUnfinishedManaPreparation() || !game.sbaCheckedCommandList.isEmpty()
                || game.getStack().getAbilityActivatedThisTurn().size() > MAX_HISTORY
                || game.getStack().getUndoStackSize() > MAX_HISTORY) return notQuiescent("stack/preparation/history");
        if (!game.getTriggerHandler().reversibleManaIsQuiescent()) return notQuiescent("trigger-handler");
        if (!game.getReplacementHandler().reversibleManaIsQuiescent()) return notQuiescent("replacement-handler");
        for (var effect : game.getStaticEffects().getEffects()) {
            if (!StaticEffects.isReversibleCommandPermission(effect.getParams()) || !effect.getAffectedPlayers().isEmpty())
                return notQuiescent("static-effects:" + effect.getParams());
            for (Card affected : effect.getAffectedCards()) if (!affected.isInZone(ZoneType.Command)) return notQuiescent("non-command-permission");
        }
        int count = 0; var budget = new GuardBudget();
        for (Card card : game.getCardsInGame()) {
            if (++count > MAX_CARDS || !card.getStaticCommandList().isEmpty()
                    || !card.reversibleManaStateWithin(MAX_HISTORY) || !budget.map(card.getSVars())
                    || !budget.text(card.getType().toString()) || !budget.entries(card.getAllAttachedCards().size())
                    || !budget.entries(card.getMayPlay().size())) return notQuiescent("card-bound/deferred");
            for (var ability : card.getAllSpellAbilities()) if (!budget.trait(ability)) return notQuiescent("trait-bound");
            for (var ability : card.getTriggers()) if (!budget.trait(ability)) return notQuiescent("trait-bound");
            for (var ability : card.getStaticAbilities()) if (!budget.trait(ability)) return notQuiescent("trait-bound");
            for (var ability : card.getCurrentState().getReplacementEffects(false)) if (!budget.trait(ability)) return notQuiescent("trait-bound");
            for (var keyword : card.getKeywords()) if (!budget.text(keyword.getOriginal())) return notQuiescent("keyword-bound");
            for (var ability : card.getStaticAbilities()) if (ability.zonesCheck() && !StaticEffects.isReversibleCommandPermission(ability.getMapParams())) return notQuiescent("static:" + card.getZone().getZoneType() + ":" + ability.getMapParams());
            for (var ability : card.getHiddenStaticAbilities()) if (ability.zonesCheck()) return notQuiescent("hidden-static:" + card.getZone().getZoneType() + ":" + ability.getMapParams());
            for (var trigger : card.getTriggers()) if (trigger.zonesCheck(card.getZone())) return notQuiescent("trigger:" + trigger.getMode());
            for (var replacement : card.getCurrentState().getReplacementEffects(false)) if (replacement.zonesCheck(card.getZone())
                    && replacement.getMode() != forge.game.replacement.ReplacementType.Moved) return notQuiescent("replacement:" + replacement.getMode());
        }
        for (Player player : game.getPlayers()) if (player.getManaPool().totalMana() > MAX_MANA) return false;
        return true;
    }
    /** Limits are checked before allocating the record's detached maps, tables and text lists. */
    private static final class GuardBudget {
        private int entries, characters;
        boolean entries(int count) { entries += count; return count >= 0 && entries <= MAX_GUARD_ENTRIES; }
        boolean text(String text) {
            if (text == null || text.length() > MAX_TEXT) return false;
            characters += text.length(); return entries(1) && characters <= MAX_GUARD_TEXT;
        }
        boolean map(Map<String, String> map) {
            if (!entries(map.size())) return false;
            for (var entry : map.entrySet()) if (!text(entry.getKey()) || !text(entry.getValue())) return false;
            return true;
        }
        boolean trait(CardTraitBase trait) { return entries(1) && map(trait.getMapParams()); }
    }
    private static List<CardGuard> guards(Game game, Card source) {
        var result = new ArrayList<CardGuard>();
        for (Card card : game.getCardsInGame()) result.add(CardGuard.of(card, source));
        return List.copyOf(result);
    }
    private static List<PlayerGuard> playerGuards(Game game, Player actor) {
        var result = new ArrayList<PlayerGuard>();
        for (Player p : game.getPlayers()) result.add(new PlayerGuard(p, p.getLife(), p.getLandsPlayedThisTurn(),
                com.google.common.collect.ImmutableMultiset.copyOf(p.getCounters()), p == actor ? List.of() : p.getManaPool().captureReversibleUnits()));
        return List.copyOf(result);
    }
    private static boolean samePlayers(List<PlayerGuard> expected, List<PlayerGuard> actual) {
        if (expected.size() != actual.size()) return false;
        for (int i = 0; i < expected.size(); i++) {
            var a = expected.get(i); var b = actual.get(i);
            if (a.player != b.player || a.life != b.life || a.lands != b.lands || !a.counters.equals(b.counters) || !sameUnits(a.mana,b.mana)) return false;
        }
        return true;
    }
    private static boolean sameUnits(List<Mana> expected, List<Mana> actual) {
        var counts = new IdentityHashMap<Mana, Integer>();
        for (Mana mana : expected) counts.merge(mana, 1, Integer::sum);
        for (Mana mana : actual) { Integer n = counts.get(mana); if (n == null) return false; if (n == 1) counts.remove(mana); else counts.put(mana,n-1); }
        return counts.isEmpty();
    }

    /** Called before synchronous observers can receive an event. */
    void observeEvent(Event event) {
        if (restoring) throw new IllegalStateException("Administrative reversal emitted a game event");
        var c = capture;
        if (c == null || !(event instanceof GameEvent)) return;
        if (event instanceof GameEventPlayerPriority priority && priority.priority().getId() == c.actor.getId()
                && priority.phase() == c.phase) return;
        if (!c.settled && event instanceof GameEventCardTapped tap && tap.card() == c.source.getView() && tap.tapped()) { c.tapEvents++; return; }
        if (!c.settled && event instanceof GameEventManaPool mana && mana.player() == c.actor.getView() && mana.mode() == EventValueChangeType.Added) { c.manaEvents++; return; }
        if (!c.settled && event instanceof GameEventAddLog log && log.type() == GameLogEntryType.MANA) return;
        if (permissionRefreshDepth > 0 && event instanceof GameEventCardStatsChanged stats && !stats.transform()
                && stats.cards().stream().allMatch(c.permissions::containsRecipient)
                && c.cards.equals(guards(game, c.source))
                && c.permissions.sameFootprint(game.getStaticEffects().captureReversiblePermissions(game))) return;
        // Player.updateFlashbackForView publishes a computed view, not a Zone mutation.
        // Its identical event shape elsewhere is not sufficient authority to ignore it.
        if (c.settled && flashbackRefreshPlayer != null && event instanceof GameEventZone zone
                && zone.zoneType() == ZoneType.Flashback && zone.mode() == EventValueChangeType.Added
                && zone.card() == null && zone.sa() == null && zone.player() == flashbackRefreshPlayer.getView()
                && valid(c)) return;
        if (c.settled && lkiCopyZone != null && event instanceof GameEventZone zone
                && zone.zoneType() == lkiCopyZone.getZoneType() && zone.mode() == EventValueChangeType.ComplexUpdate
                && zone.card() == null && zone.sa() == null && zone.player() == null && valid(c)) return;
        String detail = event instanceof GameEventZone zone ? ":zone=" + zone.zoneType() + ",mode=" + zone.mode()
                + ",card=" + (zone.card() != null) + ",ability=" + (zone.sa() != null)
                + ",settled=" + c.settled + ",flashbackScope=" + (flashbackRefreshPlayer != null)
                + ",lkiScope=" + (lkiCopyZone != null) : event instanceof GameEventManaPool mana ? ":mode=" + mana.mode()
                + ",actor=" + (mana.player() == c.actor.getView()) + ",settled=" + c.settled : "";
        invalidate("rules-event:" + event.getClass().getSimpleName() + detail);
    }

    /** Central live-card setters also fence writes which emit no GameEvent. */
    public void observeCardMutation(Card card, String operation) {
        var c = capture;
        if (c == null || restoring || card.getZone() == null) return;
        boolean live = false;
        for (Card member : card.getZone().getCards()) if (member == card) { live = true; break; }
        if (!live) return;
        if (!c.settled && card == c.source && operation.equals("tap") && !card.isTapped()) return;
        invalidate("card-write:" + operation);
    }

    public void observeManaPoolMutation(Player owner, String operation) {
        var c = capture;
        if (c == null || restoring) return;
        if (!c.settled && owner == c.actor && operation.equals("produce")) return;
        invalidate("mana-write:" + operation);
    }

    public void observeLandManaFlag(Player player, boolean value) {
        var c = capture;
        if (c == null || restoring) return;
        if (!c.settled && player == c.actor && value && c.source.isLand()) return;
        invalidate("player-write:land-mana-flag");
    }

    public void observeAbilityLedgerMutation(Card card, SpellAbility ability) {
        var c = capture;
        if (c == null || restoring) return;
        if (!c.settled && card == c.source && ability == c.ability) return;
        observeCardMutation(card, "ability-ledger");
    }

    private int permissionRefreshDepth;
    private Player flashbackRefreshPlayer;
    private Zone lkiCopyZone;
    /** A freshly allocated detached LKI Zone publishes a synthetic ComplexUpdate. */
    public LkiCopy lkiZoneCopy(Zone zone) {
        var scope = new LkiCopy(lkiCopyZone);
        lkiCopyZone = zone;
        return scope;
    }
    public final class LkiCopy implements AutoCloseable {
        private final Zone previous;
        private boolean closed;
        private LkiCopy(Zone previous) { this.previous = previous; }
        @Override public void close() { if (!closed) { closed = true; lkiCopyZone = previous; } }
    }
    /** Scoped only by the computed Flashback view owner; never by Zone mutation methods. */
    public FlashbackRefresh flashbackViewRefresh(Player player) {
        var scope = new FlashbackRefresh(flashbackRefreshPlayer);
        flashbackRefreshPlayer = player;
        return scope;
    }
    public final class FlashbackRefresh implements AutoCloseable {
        private final Player previous;
        private boolean closed;
        private FlashbackRefresh(Player previous) { this.previous = previous; }
        @Override public void close() { if (!closed) { closed = true; flashbackRefreshPlayer = previous; } }
    }
    public PermissionRefresh permissionRefresh() { permissionRefreshDepth++; return new PermissionRefresh(); }
    public final class PermissionRefresh implements AutoCloseable {
        private boolean closed;
        private PermissionRefresh() { }
        @Override public void close() { if (!closed) { closed=true; permissionRefreshDepth--; } }
    }
    public void observePermissionMutation(Card card, forge.game.staticability.StaticAbility ability) {
        if (capture == null || restoring) return;
        if (permissionRefreshDepth > 0 && card.isInZone(ZoneType.Command)
                && StaticEffects.isReversibleCommandPermission(ability.getMapParams())) return;
        observeCardMutation(card, "play-permission");
    }
}
