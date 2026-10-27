package forge.api;

import forge.card.mana.ManaAtom;
import forge.game.GameEntity;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;

import java.util.*;

/**
 * Independent public audience. This never receives, copies or redacts a bot's
 * private snapshot: hidden zones are represented by counts from the outset.
 * Called only at the table's stable published-input boundary.
 */
final class SpectatorObservation {
    private static final PlayerView AUDIENCE = new PlayerView(-1, null);
    private static final List<ZoneType> ZONES = List.of(ZoneType.Battlefield, ZoneType.Hand,
            ZoneType.Library, ZoneType.Graveyard, ZoneType.Exile, ZoneType.Command);

    static Map<String, Object> project(MatchTable table, long revision, String error) {
        var game = table.game;
        var view = game.getView();
        var players = new ArrayList<Object>();
        for (Player player : game.getRegisteredPlayers()) {
            PlayerView publicPlayer = player.getView();
            var zones = new ArrayList<Object>();
            for (ZoneType zone : ZONES) {
                var cards = new ArrayList<Object>();
                // Neither a private reveal nor an owner's knowledge grants this
                // audience access to hands, libraries or their occurrence IDs.
                if (zone != ZoneType.Hand && zone != ZoneType.Library && publicPlayer.getCards(zone) != null)
                    for (CardView card : publicPlayer.getCards(zone))
                        if (card.getZone() == zone && card.canBeShownTo(AUDIENCE)) cards.add(card(table, card));
                zones.add(map("name", zone.name(), "count", publicPlayer.getZoneSize(zone), "cards", List.copyOf(cards), "topCard", null));
            }
            var mana = new LinkedHashMap<String, Integer>();
            String[] colors = {"W", "U", "B", "R", "G", "C"};
            for (int color = 0; color < ManaAtom.MANATYPES.length; color++)
                mana.put(colors[color], publicPlayer.getMana(ManaAtom.MANATYPES[color]));
            var counters = new LinkedHashMap<String, Integer>();
            if (publicPlayer.getCounters() != null)
                for (var count : publicPlayer.getCounters().entrySet()) counters.put(count.getElement().getName(), count.getCount());
            var commanderDamage = new ArrayList<Object>();
            if (table.format.equals("Commander")) for (Player owner : game.getRegisteredPlayers()) {
                var commanders = owner.getView().getCommanders();
                if (commanders == null) continue;
                for (CardView commander : commanders) commanderDamage.add(map("name", commander.isFaceDown() ? "Face-down commander" : commander.getCurrentState().getName(),
                        "ownerId", owner.getId(), "owner", owner.getName(), "damage", publicPlayer.getCommanderDamage(commander)));
            }
            players.add(map("id", player.getId(), "name", player.getName(), "human", false, "seat", players.size() + 1,
                    "eliminated", publicPlayer.getHasLost(), "life", publicPlayer.getLife(), "priority", publicPlayer.getHasPriority(),
                    "maxHandSize", publicPlayer.hasUnlimitedHandSize() ? null : publicPlayer.getMaxHandSize(),
                    "landPlaysUsed", publicPlayer.getNumLandThisTurn(), "landPlayLimit", publicPlayer.hasUnlimitedLandPlay() ? null : publicPlayer.getMaxLandPlay(),
                    "mana", Collections.unmodifiableMap(mana), "counters", Collections.unmodifiableMap(counters),
                    "states", PublicPlayerStates.of(player), "zones", List.copyOf(zones), "commanderDamage", List.copyOf(commanderDamage)));
        }
        var stack = new ArrayList<Object>();
        for (var item : game.getStack()) {
            var ability = item.getSpellAbility();
            CardView source = ability.getHostCard().getView();
            boolean disclosed = !source.isFaceDown() && publicCard(source);
            var targets = new ArrayList<Object>();
            if (disclosed) for (var part : ability.getAllTargetChoices()) {
                for (Player target : part.getTargetPlayers()) targets.add(map("kind", "player", "id", String.valueOf(target.getId()), "playerId", target.getId(), "name", target.getName()));
                for (Card target : part.getTargetCards()) {
                    CardView face = target.getView();
                    if (publicCard(face)) targets.add(map("kind", "card", "id", id(table, face),
                            "name", face.isFaceDown() ? "Face-down card" : face.getCurrentState().getName()));
                }
            }
            stack.add(map("id", item.getId(), "name", disclosed ? source.getCurrentState().getName() : "Face-down spell",
                    // Dynamic stack descriptions can embed private target names.
                    // Public rules wording and independently authorized target
                    // references are projected separately for this audience.
                    "text", disclosed ? CardRulesText.of(source) : "", "card", publicCard(source) ? card(table, source) : null,
                    "ability", !ability.isSpell(), "kind", ability.isTrigger() ? "triggered" : ability.isSpell() ? "spell" : ability.isActivatedAbility() ? "activated" : "ability",
                    "controllerId", ability.getActivatingPlayer() == null ? null : ability.getActivatingPlayer().getId(),
                    "targets", List.copyOf(targets), "effect", null));
        }
        String result = null;
        if (error == null && game.isGameOver() && game.getOutcome() != null) {
            var outcome = game.getOutcome();
            result = outcome.isDraw() ? "Draw" : outcome.getWinningLobbyPlayer() == null ? "Game over" : outcome.getWinningLobbyPlayer().getName() + " wins";
        }
        return Collections.unmodifiableMap(map("id", table.id, "revision", revision, "boardRevision", revision,
                "format", table.format, "status", error != null ? "error" : revision == 0 ? "starting" : game.isGameOver() ? "finished" : "playing",
                "error", error, "result", result, "viewerId", -1, "spectator", true, "playerCount", players.size(),
                "turn", view.getTurn(), "phase", view.getPhase() == null ? "Pregame" : view.getPhase().nameForUi,
                "phaseKey", view.getPhase() == null ? "PREGAME" : view.getPhase().name(),
                "activePlayerId", view.getPlayerTurn() == null ? null : view.getPlayerTurn().getId(),
                "priorityPlayerId", game.getRegisteredPlayers().stream().filter(player -> !player.hasLost() && player.getView().getHasPriority()).map(Player::getId).findFirst().orElse(null),
                "decisionOwnerId", null, "actionClass", game.isGameOver() ? "finished" : "observing",
                "players", List.copyOf(players), "stack", List.copyOf(stack), "combat", combat(table), "prompt", null,
                "states", PublicStates.of(forge.game.PublicTableStates.snapshot(game)), "activity", List.of(), "notices", List.of(),
                "playAccesses", List.of(), "ignoredPriorityAbilities", List.of()));
    }

    private static boolean publicCard(CardView card) {
        return card != null && card.getZone() != null
                && EnumSet.of(ZoneType.Battlefield, ZoneType.Graveyard, ZoneType.Exile, ZoneType.Command, ZoneType.Stack).contains(card.getZone())
                && card.canBeShownTo(AUDIENCE);
    }
    private static String id(MatchTable table, CardView card) {
        return card.isFaceDown() ? table.identities.concealed(card) : table.identities.id(card);
    }
    private static Map<String, Object> card(MatchTable table, CardView card) {
        var face = card.getCurrentState();
        boolean hidden = card.isFaceDown();
        boolean publicStats = !hidden || card.getZone() == ZoneType.Battlefield;
        var counters = new LinkedHashMap<String, Integer>();
        if (card.getCounters() != null) for (var count : card.getCounters().entrySet()) counters.put(count.getElement().getName(), count.getCount());
        String kind = hidden ? "card" : card.isEmblem() ? "emblem" : card.isImmutable() ? "effect" : card.isToken() ? "token" : "card";
        var result = map("key", "", "visualId", id(table, card), "combatId", id(table, card),
                "name", hidden ? "Face-down card" : face.getName(), "type", publicStats ? face.getType().toString() : "",
                "gamePieceType", kind, "manaCost", hidden ? "" : face.getManaCost().toString(), "text", hidden ? "" : CardRulesText.of(card),
                "power", publicStats ? face.getPower() : null, "toughness", publicStats ? face.getToughness() : null,
                "tapped", card.isTapped(), "phasedOut", card.isPhasedOut(), "sick", card.isSick(), "damage", card.getDamage(),
                "attacking", card.isAttacking(), "blocking", card.isBlocking(), "faceDown", hidden, "selectable", false, "highlighted", false,
                "counters", Collections.unmodifiableMap(counters), "states", publicStats ? PublicStates.of(card.getPublicStates(AUDIENCE)) : List.of(),
                "combatKeywords", publicStats ? face.getPublicStates().stream().map(forge.game.PublicState::key).distinct().toList() : List.of(),
                "attachedTo", attachment(table, card.getAttachedTo()), "attachments", card.getAllAttachedCards().stream().filter(SpectatorObservation::publicCard).map(attached -> attachment(table, attached)).toList());
        MatchSession.addCardFaces(result, card, !hidden);
        MatchSession.addCardPrinting(result, card, !hidden);
        return Collections.unmodifiableMap(result);
    }
    private static Map<String, Object> attachment(MatchTable table, CardView card) {
        return !publicCard(card) ? null : map("id", id(table, card), "name", card.isFaceDown() ? "Face-down permanent" : card.getCurrentState().getName(),
                "type", card.isFaceDown() ? "" : card.getCurrentState().getType().toString());
    }
    private static Map<String, Object> combat(MatchTable table) {
        var combat = table.game.getCombat();
        if (combat == null) return null;
        var attackers = new ArrayList<Object>();
        for (Card attacker : combat.getAttackers()) {
            var blockers = combat.getBlockers(attacker).stream().map(card -> id(table, card.getView())).toList();
            Player defending = combat.getDefenderPlayerByAttacker(attacker);
            var defender = combat.getDefenderByAttacker(attacker);
            attackers.add(map("cardId", id(table, attacker.getView()), "hasDefender", CombatDefenders.isCurrent(defender),
                    "defender", defender(table, defender),
                    "defendingPlayerId", defending == null ? null : defending.getId(), "blockerIds", blockers,
                    "blocked", combat.isBlocked(attacker), "eligibleBlockerIds", List.of()));
        }
        return map("attackingPlayerId", combat.getAttackingPlayer().getId(), "attackers", List.copyOf(attackers),
                "defenders", combat.getDefenders().stream().filter(CombatDefenders::isCurrent).map(entity -> defender(table, entity)).toList(),
                "selectedDefender", null, "attackerCandidates", List.of(), "attackOptions", List.of(), "blockerCandidates", List.of(), "blockProblem", null);
    }
    private static Map<String, Object> defender(MatchTable table, GameEntity entity) {
        if (!CombatDefenders.isCurrent(entity)) return null;
        if (entity instanceof Player player) return map("kind", "player", "id", player.getId(), "name", player.getName(), "playerId", player.getId());
        if (entity instanceof Card card) return map("kind", "card", "id", id(table, card.getView()),
                "name", card.isFaceDown() ? "Face-down permanent" : card.getName(), "playerId",
                card.isBattle() && card.getProtectingPlayer() != null ? card.getProtectingPlayer().getId() : card.getController().getId());
        return null;
    }
    private static LinkedHashMap<String, Object> map(Object... values) {
        var result = new LinkedHashMap<String, Object>();
        for (int index = 0; index < values.length; index += 2) result.put((String) values[index], values[index + 1]);
        return result;
    }
    private SpectatorObservation() { }
}
