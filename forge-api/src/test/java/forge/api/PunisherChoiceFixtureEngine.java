package forge.api;

import forge.StaticData;
import forge.card.CardRarity;
import forge.card.CardRules;
import forge.deck.DeckSection;
import forge.item.PaperCard;

import java.util.ArrayList;
import java.util.List;

/** Private pregame setup only. The actual card script's choice and following
 * rules execute through unchanged production controllers, IPC and resolution.
 * The activation that supplies four counters is explicit synthetic setup, not
 * a claim to have played four natural end steps or evaluated their choices. */
public final class PunisherChoiceFixtureEngine {
    private PunisherChoiceFixtureEngine() { }
    public static final class LowZero { public static void main(String[] args) throws Exception { run(args, 12, false); } }
    public static final class LowSeven { public static void main(String[] args) throws Exception { run(args, 12, true); } }
    public static final class HighZero { public static void main(String[] args) throws Exception { run(args, 40, false); } }
    public static final class HighSeven { public static void main(String[] args) throws Exception { run(args, 40, true); } }

    private static void run(String[] args, int life, boolean seven) throws Exception {
        DesktopEngine.run(args, (players, resources, profile) -> {
            if (players.size() != 4 || players.stream().anyMatch(player -> !(player.getPlayer() instanceof AutomatedLobbyPlayer)))
                throw new IllegalArgumentException("The private punisher fixture requires four automated players");
            var script = new ArrayList<String>(List.of("Name:Unrelated choice fixture", "ManaCost:0", "Types:Artifact",
                    "A:AB$ PutCounter | Cost$ 0 | Defined$ Self | CounterType$ INFLUENCE | CounterNum$ 4 | SubAbility$ DBChoice | SpellDescription$ Private setup."));
            // Copy mechanics from the actual tracked card, including its X/Y
            // definitions; neither native policy nor recognition uses its name.
            var original = StaticData.instance().getCommonCards().getCard("Palant\u00edr of Orthanc");
            if (original == null) throw new IllegalArgumentException("Missing actual card mechanics in the fixture catalog");
            for (var variable : original.getRules().getMainPart().getVariables())
                script.add("SVar:" + variable.getKey() + ":" + variable.getValue());
            script.add("Oracle:Private synthetic setup for the current choice.");
            var paper = new PaperCard(new CardRules.Reader().readCard(script), "TST", CardRarity.Common);
            StaticData.instance().getCommonCards().addCard(paper);
            for (var player : players) {
                player.getDeck().getMain().clear();
                player.getDeck().getMain().add(StaticData.instance().getCommonCards().getCard("Plains"), 99);
            }
            if (seven) {
                players.get(0).getDeck().getMain().clear();
                players.get(0).getDeck().getMain().add(StaticData.instance().getCommonCards().getCard("Chromatic Orrery"), 99);
            }
            players.get(1).setStartingLife(life);
            players.get(0).addExtraCardsOnBattlefield(List.of(paper));
            var powerPlay = StaticData.instance().getVariantCards().getCard("Power Play");
            if (powerPlay == null) throw new IllegalArgumentException("Missing private starting-player fixture");
            players.get(0).getDeck().getOrCreate(DeckSection.Conspiracy).add(powerPlay);
            players.get(0).assignConspiracies();
            return new MatchTable("Commander", players, resources, profile, true);
        });
    }
}
