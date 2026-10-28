package forge.api;

import forge.StaticData;
import forge.game.player.RegisteredPlayer;
import forge.item.IPaperCard;

import java.util.ArrayList;
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

    static void configure(List<RegisteredPlayer> players) {
        String name = players.get(0).getDeck().getName();
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
            case "AI gate tap-cost-funding" -> List.of("Selesnya Evangel", "Birds of Paradise", "Grizzly Bears");
            case "Execution journey Loch Mare" -> List.of("Loch Mare", "Island", "Island", "Island");
            default -> null;
        };
        if (extras == null) return;
        if (name.equals("Execution journey Loch Mare")) players.get(0).setEnableETBCountersEffect(true);
        // The deck is a valid Commander deck, not a truncated test library.
        // Setup occurs before constructing a table; no live game edits or synthetic replies.
        List<IPaperCard> cards = new ArrayList<>();
        for (String card : extras) {
            var printing = StaticData.instance().getCommonCards().getCard(card);
            if (printing == null) throw new IllegalArgumentException("Missing release fixture card: " + card);
            cards.add(printing);
        }
        players.get(0).addExtraCardsOnBattlefield(cards);
    }
}
