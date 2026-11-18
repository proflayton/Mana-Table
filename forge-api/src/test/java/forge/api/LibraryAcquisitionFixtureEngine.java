package forge.api;

import forge.StaticData;
import forge.card.CardRarity;
import forge.card.CardRules;
import forge.deck.DeckSection;
import forge.game.player.RegisteredPlayer;
import forge.item.PaperCard;

import java.util.Arrays;
import java.util.List;

/** Private test-classpath launcher only. Setup is synthetic; all subsequent
 * observations, option authority, IPC and zone-change resolution are production.
 * No production command, deck name or process flag enables this fixture. */
public final class LibraryAcquisitionFixtureEngine {
    enum Mode { DIRECT_BATTLEFIELD, DIRECT_HAND, BUNDLE, RENAMED_BUNDLE }
    private LibraryAcquisitionFixtureEngine() { }

    public static void main(String[] args) throws Exception { run(args, Mode.BUNDLE); }
    public static final class DirectBattlefield { public static void main(String[] args) throws Exception { run(args, Mode.DIRECT_BATTLEFIELD); } }
    public static final class DirectHand { public static void main(String[] args) throws Exception { run(args, Mode.DIRECT_HAND); } }
    public static final class Bundle { public static void main(String[] args) throws Exception { run(args, Mode.BUNDLE); } }
    public static final class RenamedBundle { public static void main(String[] args) throws Exception { run(args, Mode.RENAMED_BUNDLE); } }

    private static void run(String[] args, Mode mode) throws Exception {
        DesktopEngine.run(args, (players, resources, profile) -> {
            configure(players, mode);
            return new MatchTable("Commander", players, resources, profile, true);
        });
    }

    static void configure(List<RegisteredPlayer> players, Mode mode) {
        if (players.size() != 4 || players.stream().anyMatch(player -> !(player.getPlayer() instanceof AutomatedLobbyPlayer)))
            throw new IllegalArgumentException("The private library-acquisition fixture requires four automated players");
        var paper = paper(mode);
        StaticData.instance().getCommonCards().addCard(paper);
        for (var player : players) {
            player.getDeck().getMain().clear();
            player.getDeck().getMain().add(StaticData.instance().getCommonCards().getCard("Plains"), 99);
        }
        players.get(0).addExtraCardsOnBattlefield(List.of(paper));
        var powerPlay = StaticData.instance().getVariantCards().getCard("Power Play");
        if (powerPlay == null) throw new IllegalArgumentException("Missing private fixture starting-player card");
        players.get(0).getDeck().getOrCreate(DeckSection.Conspiracy).add(powerPlay);
        players.get(0).assignConspiracies();
    }

    static PaperCard paper(Mode mode) {
        String name = mode == Mode.RENAMED_BUNDLE ? "Unrelated lantern fixture" : "Library acquisition fixture " + mode;
        String root = switch (mode) {
            case DIRECT_BATTLEFIELD -> "AB$ ChangeZone | Cost$ 0 | Origin$ Library | Destination$ Battlefield | ChangeType$ Plains,Island,Swamp,Mountain | Tapped$ True | ChangeNum$ 1 | SpellDescription$ Unrelated text.";
            case DIRECT_HAND -> "AB$ ChangeZone | Cost$ 0 | Origin$ Library | Destination$ Hand | ChangeType$ Land.Basic | ChangeNum$ 1 | SpellDescription$ Unrelated text.";
            case BUNDLE, RENAMED_BUNDLE -> "AB$ ChangeZone | Cost$ 0 | Origin$ Library | Destination$ Library | ChangeType$ Land.Basic | ChangeTypeDesc$ basic land | ChangeNum$ 2 | RememberChanged$ True | Reveal$ True | Shuffle$ False | StackDescription$ SpellDescription | SubAbility$ DBField | SpellDescription$ Unrelated text.";
        };
        String tail = mode == Mode.BUNDLE || mode == Mode.RENAMED_BUNDLE ? "\nSVar:DBField:DB$ ChangeZone | Origin$ Library | Destination$ Battlefield | ChangeType$ Land.IsRemembered | ChangeNum$ 1 | Mandatory$ True | NoLooking$ True | SelectPrompt$ Arbitrary first prompt | Tapped$ True | Shuffle$ False | SubAbility$ DBHand | StackDescription$ None"
                + "\nSVar:DBHand:DB$ ChangeZone | Origin$ Library | Destination$ Hand | ChangeType$ Land.IsRemembered | Mandatory$ True | NoLooking$ True | SelectPrompt$ Arbitrary second prompt | StackDescription$ None | SubAbility$ DBCleanup"
                + "\nSVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True" : "";
        return new PaperCard(new CardRules.Reader().readCard(Arrays.asList(("Name:" + name
                + "\nManaCost:0\nTypes:Artifact\nA:" + root + tail + "\nOracle:Private synthetic acquisition fixture.").split("\n"))), "TST", CardRarity.Common);
    }
}
