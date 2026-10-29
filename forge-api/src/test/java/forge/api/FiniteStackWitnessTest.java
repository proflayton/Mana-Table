package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import static org.testng.Assert.*;

public class FiniteStackWitnessTest {
    @Test public void actualCommittedTargetIdentityAndAllocationAreCopiedBeforeAnyRetarget() throws Exception {
        var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
        try (var table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Actor"),
                MatchTable.human(new Deck(), "Constructed", "Opponent")), resources, Files.createTempDirectory("finite-witness"))) {
            HeadlessPlatform.activate(table);
            var owner = table.game.getPlayers().get(0); var opponent = table.game.getPlayers().get(1);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var ability = new SpellAbility.EmptySa(source, owner);
            ability.getTargets().add(opponent); ability.getTargets().addDividedAllocation(opponent, 5);
            var before = FiniteStackWitness.capture(ability, "same-execution", owner, card -> "source");
            ability.getTargets().clear(); ability.getTargets().add(owner); ability.getTargets().addDividedAllocation(owner, 5);
            var after = FiniteStackWitness.capture(ability, "same-execution", owner, card -> "source");
            assertNotEquals(after, before, "Retargeting an existing occurrence changes its consent witness without a new execution");
            ability.getTargets().clear(); ability.getTargets().add(opponent); ability.getTargets().addDividedAllocation(opponent, 4);
            assertNotEquals(FiniteStackWitness.capture(ability, "same-execution", owner, card -> "source"), before);
            ability.getTargets().addDividedAllocation(opponent, 5); source.setTapped(true);
            assertEquals(FiniteStackWitness.capture(ability, "same-execution", owner, card -> "source"), before,
                    "Ordinary source resource changes do not manufacture a new queue identity");
            assertNull(FiniteStackWitness.capture(ability, "same-execution", owner, card -> null));
        }
    }
}
