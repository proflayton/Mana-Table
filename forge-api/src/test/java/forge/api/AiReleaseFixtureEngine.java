package forge.api;

import forge.StaticData;
import forge.deck.DeckSection;
import forge.game.player.RegisteredPlayer;
import forge.item.IPaperCard;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Test-classpath-only pregame fixtures. All observations, controllers, rules and IPC are production code. */
public final class AiReleaseFixtureEngine {
    private AiReleaseFixtureEngine() { }

    public static void main(String[] args) throws Exception {
        DesktopEngine.run(args, (players, resources, profile) -> {
            configure(players);
            return new MatchTable("Commander", players, resources, profile, true);
        });
    }

    /** Explicit private launcher; no command, deck name or process-global flag
     * can enable this setup in the production entry point. */
    public static final class PolledFanout {
        private PolledFanout() { }
        public static void main(String[] args) throws Exception {
            DesktopEngine.run(args, (players, resources, profile) -> {
                configure(players);
                return new MatchTable("Commander", players, resources, profile, true);
            }, AiReleaseFixtureEngine::configurePolledFanout);
        }
    }

    /** Ordinary connected owners exercise the actual consuming-mana payment
     * route. This bootstrap exists only in the private fixture launcher JAR. */
    public static final class PolledManaOutput {
        private PolledManaOutput() { }
        public static void main(String[] args) throws Exception {
            DesktopEngine.run(args, (players, resources, profile) -> {
                configure(players);
                return new MatchTable("Commander", players, resources, profile, true);
            }, AiReleaseFixtureEngine::configurePolledManaOutput);
        }
    }

    static void configurePolledManaOutput(String format, List<RegisteredPlayer> players) {
        if (!format.equals("Commander") || players.size() != 4
                || !(players.get(0).getPlayer() instanceof forge.player.LobbyPlayerHuman) || !(players.get(1).getPlayer() instanceof forge.player.LobbyPlayerHuman)
                || players.get(0).getPlayer() instanceof AutomatedLobbyPlayer || players.get(1).getPlayer() instanceof AutomatedLobbyPlayer
                || !(players.get(2).getPlayer() instanceof AutomatedLobbyPlayer) || !(players.get(3).getPlayer() instanceof AutomatedLobbyPlayer)
                || !players.get(0).getDeck().getName().equals("AI gate consuming-output-payment"))
            throw new IllegalArgumentException("The private mana-output fixture requires its declared host deck, two ordinary humans and two automated seats");
        var hostMain = players.get(0).getDeck().getMain();
        hostMain.clear(); hostMain.add(StaticData.instance().getCommonCards().getCard("Foundry Inspector"), 99);
        var powerPlay = StaticData.instance().getVariantCards().getCard("Power Play");
        if (powerPlay == null) throw new IllegalArgumentException("Missing mana-output fixture card: Power Play");
        players.get(0).getDeck().getOrCreate(DeckSection.Conspiracy).add(powerPlay);
        players.get(0).assignConspiracies();
        addExtras(players.get(0), List.of("Wastes", "Crystal Vein"));
    }

    static void configurePolledFanout(String format, List<RegisteredPlayer> players) {
        if (!format.equals("Commander") || players.size() != 4
                || !(players.get(0).getPlayer() instanceof forge.player.LobbyPlayerHuman) || !(players.get(1).getPlayer() instanceof forge.player.LobbyPlayerHuman)
                || players.get(0).getPlayer() instanceof AutomatedLobbyPlayer || players.get(1).getPlayer() instanceof AutomatedLobbyPlayer
                || !(players.get(2).getPlayer() instanceof AutomatedLobbyPlayer) || !(players.get(3).getPlayer() instanceof AutomatedLobbyPlayer))
            throw new IllegalArgumentException("The private fanout fixture requires two ordinary humans and two host-owned automated seats");
        // Deliberately controlled registration, not a legal host-deck or natural
        // campaign qualification. The ordinary library/hand/cast path supplies
        // the actual spell and enough real cards for its 55 draws.
        var hostMain = players.get(0).getDeck().getMain();
        hostMain.clear(); hostMain.add(StaticData.instance().getCommonCards().getCard("Shamanic Revelation"), 99);
        // Use the real pregame starting-player rule. This keeps the measured
        // trigger workload on turn one without overriding an AI's choices.
        var powerPlay = StaticData.instance().getVariantCards().getCard("Power Play");
        if (powerPlay == null) throw new IllegalArgumentException("Missing fanout fixture card: Power Play");
        players.get(0).getDeck().getOrCreate(DeckSection.Conspiracy).add(powerPlay);
        players.get(0).assignConspiracies();
        var extras = new ArrayList<>(List.of("Toothy, Imaginary Friend", "Reliquary Tower"));
        extras.addAll(Collections.nCopies(54, "Memnite")); extras.addAll(Collections.nCopies(5, "Forest"));
        addExtras(players.get(0), extras); addExtras(players.get(1), List.of("Mind's Eye"));
    }

    static void configure(List<RegisteredPlayer> players) {
        String name = players.get(0).getDeck().getName();
        if (name.equals("AI terminal-risk current routes")) {
            // Declared test-only opening library: seven plus the first natural
            // draw reveals every witness. Stop after the nondrawing safe spell;
            // this is not a shipping list or a natural-game qualification.
            var main = players.get(0).getDeck().getMain();
            main.clear();
            for (String card : List.of("Final Fortune", "Glorious End", "Sol Ring",
                    "Mountain", "Mountain", "Mountain", "Mountain", "Mountain")) {
                var printing = StaticData.instance().getCommonCards().getCard(card);
                if (printing == null) throw new IllegalArgumentException("Missing terminal-risk fixture card: " + card);
                main.add(printing);
            }
            addExtras(players.get(0), Collections.nCopies(4, "Mountain"));
            return;
        }
        if (name.equals("Resource qualification Traxos")) {
            // Explicit controlled runtime, not a shipping Commander list or
            // campaign deck qualification: opening seven plus the actual first
            // draw reaches both witnesses before any empty-library draw.
            var main = players.get(0).getDeck().getMain();
            main.clear();
            for (String card : List.of("Mind Stone", "Spatial Contortion",
                    "Wastes", "Wastes", "Wastes", "Wastes", "Wastes", "Wastes"))
                main.add(StaticData.instance().getCommonCards().getCard(card));
            addExtras(players.get(0), Collections.nCopies(8, "Wastes"));
            return;
        }
        if (name.equals("AI cooperative ally-benefit") || name.equals("AI cooperative enemy-removal")) {
            if (players.size() != 4) throw new IllegalArgumentException("The cooperative tactical fixture requires four registered players");
            if (name.equals("AI cooperative ally-benefit")) {
                addExtras(players.get(0), List.of("Dragon Blood", "Wastes", "Wastes"));
                addExtras(players.get(1), List.of("Grizzly Bears"));
                addExtras(players.get(2), List.of("Colossal Dreadmaw"));
            } else {
                addExtras(players.get(0), List.of("Aeolipile", "Wastes"));
                addExtras(players.get(1), List.of("Ancient Brontodon"));
                addExtras(players.get(2), List.of("Priest of Titania"));
            }
            addExtras(players.get(3), List.of("Colossal Dreadmaw"));
            return;
        }
        List<String> extras = switch (name) {
            case "AI gate x-funded" -> List.of("Wastes", "Wastes", "Wastes", "Wastes", "Wastes");
            case "AI gate x-unfunded" -> List.of();
            case "AI gate consuming-funded" -> List.of("Crystal Vein", "Wastes");
            case "AI gate consuming-shortfall" -> List.of("Crystal Vein");
            case "AI gate optional-decline" -> List.of("Mentor of the Meek");
            case "AI gate optional-pay" -> List.of("Mentor of the Meek", "Forest");
            case "AI gate flexible-output" -> List.of("Temple of Epiphany", "Island", "Island", "Island");
            case "AI gate mana-reduction" -> List.of("Mystic Monastery", "Mystic Monastery", "Mystic Monastery", "Mystic Monastery", "Mystic Monastery", "Mystic Monastery",
                    "Lightning Angel", "Lightning Angel", "Lightning Angel", "Lightning Angel", "Lightning Angel", "Lightning Angel", "Lightning Angel");
            case "AI gate partner-draw-counters" -> List.of("Island", "Island", "Island", "Island", "Island", "Forest", "Forest", "Forest");
            // Variant setup creatures begin sick. A real passive haste grant
            // makes both tap abilities legal before the first land play.
            case "AI gate tap-cost-funding" -> List.of("Selesnya Evangel", "Birds of Paradise", "Grizzly Bears", "Concordant Crossroads");
            case "Execution journey Loch Mare" -> List.of("Loch Mare", "Pir, Imaginative Rascal", "Chasm Skulker", "Toothy, Imaginary Friend",
                    "Island", "Island", "Island");
            case "AI cadence fifty forced draws" -> {
                // One real Pestilence activation kills fifty nontoken creatures.
                // Haruspex survives its one damage and supplies fifty forced draws.
                var cards = new ArrayList<>(List.of("Grim Haruspex", "Pestilence", "Swamp"));
                cards.addAll(Collections.nCopies(50, "Memnite"));
                yield cards;
            }
            default -> null;
        };
        if (extras == null) return;
        if (name.equals("Execution journey Loch Mare")) players.get(0).setEnableETBCountersEffect(true);
        // The deck is a valid Commander deck, not a truncated test library.
        // Setup occurs before constructing a table; no live game edits or synthetic replies.
        addExtras(players.get(0), extras);
    }

    private static void addExtras(RegisteredPlayer player, List<String> extras) {
        List<IPaperCard> cards = new ArrayList<>();
        for (String card : extras) {
            var printing = StaticData.instance().getCommonCards().getCard(card);
            if (printing == null) throw new IllegalArgumentException("Missing release fixture card: " + card);
            cards.add(printing);
        }
        player.addExtraCardsOnBattlefield(cards);
    }
}
