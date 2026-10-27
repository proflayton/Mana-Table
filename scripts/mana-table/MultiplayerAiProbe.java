package forge.api;

import com.google.gson.GsonBuilder;
import forge.ai.*;
import forge.deck.Deck;
import forge.game.*;
import forge.game.ability.AbilityFactory;
import forge.game.card.CardFactory;
import forge.game.phase.PhaseType;
import forge.game.player.*;
import forge.util.MyRandom;
import java.nio.file.*;
import java.security.*;
import java.util.*;

/** Offline characterization of Forge selection routines, never a live table or a full-match benchmark. */
public final class MultiplayerAiProbe {
    public static void main(String[] args) {
        try { run(args); System.exit(0); }
        catch (Exception failure) { failure.printStackTrace(); System.exit(1); }
    }

    private static void run(String[] args) throws Exception {
        if (args.length != 2) throw new IllegalArgumentException("Expected: <forge-res directory> <disposable output/profile directory>");
        Path resources = Path.of(args[0]), profile = Path.of(args[1]);
        Files.createDirectories(profile);
        EngineResources.load(resources);
        HeadlessPlatform.initialize(resources, profile);
        var results = new ArrayList<Object>();
        for (int[] life : List.of(new int[]{40,40,40,40}, new int[]{30,40,40,40},
                new int[]{12,40,40,40}, new int[]{40,12,40,40})) {
            MyRandom.setRandom(new Random(261026));
            AiCache.clear();
            var registered = new ArrayList<RegisteredPlayer>();
            for (int n = 0; n < 4; n++) {
                var seat = new RegisteredPlayer(new Deck("Audit seat " + n));
                var lobby = new LobbyPlayerAi("Seat " + n, Set.of());
                lobby.setAiProfile("Default");
                seat.setPlayer(lobby); seat.setStartingLife(40); registered.add(seat);
            }
            var game = new Match(new GameRules(GameType.Constructed), registered, "Offline AI audit").createGame();
            var players = game.getPlayers();
            for (int n = 0; n < 4; n++) { players.get(n).setStartingLife(40); players.get(n).setLife(life[n], null); }
            var decisions = new ArrayList<Object>();
            for (Player ai : players) {
                game.getPhaseHandler().devModeSet(PhaseType.MAIN1, ai, false, 8);
                var combat = new TreeMap<Integer,Integer>();
                for (int n = 0; n < 256; n++)
                    combat.merge(AiAttackController.choosePreferredDefenderPlayer(ai, true).getId(), 1, Integer::sum);
                var source = CardFactory.getCard(forge.StaticData.instance().getCommonCards().getCard("Lightning Bolt"), ai, game);
                var pings = new TreeMap<Integer,Integer>();
                for (int n = 0; n < 16; n++) {
                    var effect = AbilityFactory.getAbility("AB$ DealDamage | Cost$ 0 | ValidTgts$ Opponent | NumDmg$ 1 | SpellDescription$ Deal 1 damage to target opponent.", source);
                    effect.setActivatingPlayer(ai);
                    boolean chosen = SpellApiToAi.Converter.get(effect).doTrigger(ai, effect, true);
                    var targets = new ArrayList<Player>(); effect.getTargets().getTargetPlayers().forEach(targets::add);
                    if (!chosen || targets.size() != 1)
                        throw new IllegalStateException("Expected exactly one mandatory ping target");
                    pings.merge(targets.get(0).getId(), 1, Integer::sum);
                }
                decisions.add(Map.of("actor", ai.getId(), "opponentsInOrder", ai.getOpponents().stream().map(Player::getId).toList(),
                        "weakest", ai.getWeakestOpponent().getId(), "strongest", ai.getStrongestOpponent().getId(),
                        "combatPreferenceSamples", combat, "mandatoryOneDamageTargetSamples", pings));
            }
            results.add(Map.of("life", life, "decisions", decisions));
        }
        Path engine = Path.of(EngineResources.class.getProtectionDomain().getCodeSource().getLocation().toURI());
        var digest = MessageDigest.getInstance("SHA-256");
        if (Files.isRegularFile(engine)) try (var input = new DigestInputStream(Files.newInputStream(engine), digest)) {
            input.transferTo(java.io.OutputStream.nullOutputStream());
        }
        var evidence = Map.of("fixture", "Four AI seats; empty boards/hands/libraries; Constructed rules with starting life 40; main phase, turn 8",
                "limitations", "Repeated unchanged-state selector probes, not executed attacks, resolved damage, Commander matches or human experience validation",
                "engineLocation", engine.toString(), "engineSha256", Files.isRegularFile(engine) ? HexFormat.of().formatHex(digest.digest()) : "unpackaged classes",
                "javaVersion", System.getProperty("java.version"), "seed", 261026, "cases", results);
        Files.writeString(profile.resolve("targeting-probe.json"), new GsonBuilder().setPrettyPrinting().create().toJson(evidence));
        System.out.println("Wrote four offline selection cases to " + profile.resolve("targeting-probe.json"));
    }
}
