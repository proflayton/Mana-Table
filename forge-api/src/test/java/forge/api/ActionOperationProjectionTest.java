package forge.api;

import com.google.gson.Gson;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.ability.ApiType;
import forge.game.ability.AbilityUtils;
import forge.game.card.Card;
import forge.game.card.CounterEnumType;
import forge.game.cost.CostSacrifice;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

/** Real script graphs and rules target queries, without playing a scripted AI game. */
public class ActionOperationProjectionTest {
    @Test(timeOut = 90000)
    public void chainedEffectsShareTheActualTargetAndKeepTheirRecipientControllerBinding() throws Exception {
        try (var fixture = new Fixture()) {
            fixture.add("Grizzly Bears", ZoneType.Battlefield, 0);
            fixture.add("Birds of Paradise", ZoneType.Battlefield, 1);
            var blood = fixture.ability("Sign in Blood", ApiType.Draw);
            var graph = fixture.graph(blood);
            assertEquals(graph.get("knowledge"), "Known");
            var nodes = nodes(graph);
            assertEquals(nodes.size(), 2);
            assertEquals(nodes.get(0).get("effect"), "Draw");
            assertEquals(nodes.get(1).get("effect"), "LifeLoss");
            for (var node : nodes) {
                assertEquals(node.get("amount"), 2);
                assertEquals(recipients(node).get("binding"), "Targets");
                assertEquals(recipients(node).get("targetOperationId"), 0);
            }
            assertEquals(candidates(nodes.get(0)).size(), 2, "Actual player target candidates are retained as a finite domain");
            assertTrue(candidates(nodes.get(0)).stream().allMatch(candidate -> candidate.get("kind").equals("Player")));

            var beast = fixture.graph(fixture.ability("Beast Within", ApiType.Destroy));
            var beastNodes = nodes(beast);
            assertEquals(candidates(beastNodes.get(0)).size(), 2);
            assertEquals(recipients(beastNodes.get(1)).get("binding"), "TargetControllers");
            assertEquals(recipients(beastNodes.get(1)).get("targetOperationId"), 0);
            assertEquals(beastNodes.get(1).get("effect"), "Token");
            assertEquals(beast.get("knowledge"), "Unknown", "Token characteristics are not silently replaced with an invented token value");

            var continuation = DecisionSemantics.describeAbility(blood.getSubAbility(), fixture.actor(), fixture.table.identities::id);
            assertEquals(continuation.get("operationId"), 1);
            assertEquals(nodes(castMap(continuation.get("operationGraph"))).size(), 2,
                    "A continuation identifies its operation within the whole causal chain");
            var priorLoss = blood.getSubAbility();
            var anotherTarget = AbilityFactory.getAbility("DB$ Draw | NumCards$ 1 | ValidTgts$ Player", blood.getHostCard());
            anotherTarget.setSubAbility(priorLoss);
            blood.setSubAbility((forge.game.spellability.AbilitySub)anotherTarget);
            blood.setActivatingPlayer(fixture.actor());
            assertEquals(recipients(nodes(fixture.graph(blood)).get(2)).get("knowledge"), "Unknown",
                    "A Defined Targeted union across multiple groups is not fabricated as the nearest target group");
        }
    }

    @Test(timeOut = 90000)
    public void modalConditionsAndCounterOperationsRemainStructuredInsteadOfInventingFixedAdditions() throws Exception {
        try (var fixture = new Fixture()) {
            var charm = fixture.graph(fixture.ability("Cryptic Command", ApiType.Charm));
            var root = nodes(charm).get(0);
            assertEquals(root.get("effect"), "Choose");
            assertEquals(root.get("minChoices"), 2); assertEquals(root.get("maxChoices"), 2);
            assertEquals(root.get("mayRepeatChoices"), false);
            assertEquals(list(root.get("links")).stream().filter(link -> link.get("kind").equals("Choice")).count(), 4L);
            assertEquals(fixture.graph(fixture.ability("Mystic Confluence", ApiType.Charm)).get("knowledge"), "Unknown",
                    "Repeated targeted modes need independent target slots, not one silently shared target assignment");

            var vorel = fixture.graph(fixture.ability("Vorel of the Hull Clade", ApiType.MultiplyCounter));
            assertEquals(nodes(vorel).get(0).get("effect"), "MultiplyCounters");
            assertEquals(nodes(vorel).get(0).get("amount"), 2, "Doubling is a multiplier, not an invented one-counter addition");
            var augury = fixture.graph(fixture.ability("Experimental Augury", ApiType.Dig));
            assertTrue(nodes(augury).stream().anyMatch(node -> node.get("effect").equals("Proliferate")));
            assertEquals(augury.get("knowledge"), "Unknown", "Optional counter recipients and unmodeled dig operations remain explicit gaps");
            var amass = fixture.graph(fixture.ability("Relentless Advance", ApiType.Amass));
            assertEquals(nodes(amass).get(0).get("effect"), "Amass");
            assertEquals(nodes(amass).get(0).get("amount"), 3);
            assertEquals(amass.get("knowledge"), "Unknown", "An army and three counters are not three invented token creations");

            var source = fixture.add("Grizzly Bears", ZoneType.Battlefield, 0);
            var conditional = AbilityFactory.getAbility("DB$ PutCounter | Defined$ Self | CounterType$ P1P1 | CounterNum$ X | ConditionCheckSVar$ X | ConditionSVarCompare$ GE1", source);
            conditional.setActivatingPlayer(fixture.actor());
            var conditionNode = nodes(fixture.graph(conditional)).get(0);
            assertEquals(conditionNode.get("conditional"), true);
            assertNull(conditionNode.get("amount"));
            assertEquals(conditionNode.get("amountKnowledge"), "Unknown");
            assertEquals(conditionNode.get("knowledge"), "Unknown");

            var lens = fixture.add("Infiltration Lens", ZoneType.Battlefield, 0);
            var optionalDraw = AbilityFactory.getAbility(lens.getSVar("TrigDraw"), lens);
            optionalDraw.setActivatingPlayer(fixture.actor());
            var optionalNode = nodes(fixture.graph(optionalDraw)).get(0);
            assertEquals(optionalNode.get("amount"), 2);
            assertEquals(optionalNode.get("conditional"), true);
            assertEquals(optionalNode.get("knowledge"), "Unknown", "OptionalDecider is a real rules choice, not a guaranteed draw");

            var djinn = fixture.add("Indentured Djinn", ZoneType.Battlefield, 0);
            var uptoDraw = AbilityFactory.getAbility(djinn.getSVar("TrigDraw"), djinn);
            uptoDraw.setActivatingPlayer(fixture.actor());
            var uptoNode = nodes(fixture.graph(uptoDraw)).get(0);
            assertEquals(uptoNode.get("amount"), 3);
            assertEquals(uptoNode.get("conditional"), true);
            assertEquals(uptoNode.get("knowledge"), "Unknown", "The engine's Upto parameter preserves a choice of quantity");

            var welcome = nodes(fixture.graph(fixture.ability("Explosive Welcome", ApiType.DealDamage)));
            assertEquals(welcome.get(0).get("knowledge"), "Unknown", "Staged damage is not a resolved simple damage operation");
            assertEquals(castMap(welcome.get(1).get("targets")).get("knowledge"), "Unknown",
                    "Another target is a cross-operation constraint, not two independent candidate sets");
            var patch = nodes(fixture.graph(fixture.ability("Patch Up", ApiType.ChangeZone))).get(0);
            assertEquals(castMap(patch.get("targets")).get("knowledge"), "Unknown",
                    "A total mana-value budget is not captured by individual legal target candidates");
            var beam = nodes(fixture.graph(fixture.ability("Cleansing Beam", ApiType.DealDamage))).get(0);
            assertEquals(recipients(beam).get("knowledge"), "Unknown",
                    "Radiance also affects untargeted recipients and cannot masquerade as only its selected target");
            assertEquals(beam.get("knowledge"), "Unknown");
        }
    }

    @Test(timeOut = 90000)
    public void nominalManaBundlesAndCoupledCostsDoNotClaimUnprovedPayability() throws Exception {
        try (var fixture = new Fixture()) {
            var bird = fixture.ability("Birds of Paradise", ApiType.Mana);
            var alternatives = list(ManaProductionProjection.describe(bird).get("alternatives"));
            assertEquals(alternatives.size(), 5, "One mana of a chosen color is five distinct alternatives");
            assertTrue(alternatives.stream().allMatch(value -> castMap(value.get("colors")).size() == 1));
            var signet = ManaProductionProjection.describe(fixture.ability("Simic Signet", ApiType.Mana));
            assertEquals(signet.get("knowledge"), "Known");
            assertEquals(list(signet.get("alternatives")).get(0).get("colors"), Map.of("G", 1, "U", 1),
                    "Simultaneously produced colors remain one bundle on one route");
            assertEquals(ManaProductionProjection.describe(fixture.ability("Jegantha, the Wellspring", ApiType.Mana)).get("knowledge"), "Unknown",
                    "Nominal color production cannot certify restricted generic spending");

            fixture.add("Overgrown Tomb", ZoneType.Battlefield, 0);
            var jarad = fixture.ability("Jarad, Golgari Lich Lord", ApiType.ChangeZone);
            assertTrue(jarad.getPayCosts().canPay(jarad, fixture.actor(), false), "Forge's individual cost-part probes each find the same dual land");
            assertEquals(ActionSemantics.describeAbility(jarad).get("affordability"), "Unknown",
                    "A Swamp and a Forest require two distinct sacrifices; separate successful probes are not a joint payment proof");
            var move = nodes(fixture.graph(jarad)).get(0);
            assertEquals(move.get("effect"), "MoveCard");
            assertEquals(move.get("originZone"), "Graveyard");
            assertEquals(move.get("destinationZone"), "Hand");
            assertEquals(ActionSemantics.describeAbility(jarad).get("manaCostKnowledge"), "Unknown",
                    "Base costs are not represented as an adjusted current payment quote");
            var top = AbilityFactory.getAbility("DB$ ChangeZone | Defined$ TopOfLibrary | Origin$ Library | Destination$ Exile", jarad.getHostCard());
            top.setActivatingPlayer(fixture.actor());
            assertNotEquals(ActionSemantics.describeAbility(top).get("effect"), "Search", "Moving a defined library card does not search a library");
            var ambiguous = AbilityFactory.getAbility("DB$ ChangeZone | Origin$ Graveyard,Library | Destination$ Hand | ChangeType$ Card", jarad.getHostCard());
            ambiguous.setActivatingPlayer(fixture.actor());
            var mixed = fixture.graph(ambiguous);
            assertEquals(nodes(mixed).get(0).get("originZone"), "");
            assertEquals(mixed.get("knowledge"), "Unknown", "Mixed origins never masquerade as one canonical source zone");
        }
    }

    @Test(timeOut = 90000)
    public void finiteManaOutputWitnessesPreserveEachAndIndependentPartsWithoutChangingTheAbility() throws Exception {
        try (var fixture = new Fixture()) {
            var temple = fixture.ability("Temple of Epiphany", ApiType.Mana);
            assertEquals(ManaProductionProjection.outputChoices(temple, Map.of("R", 1)), Map.of(temple.getManaPart(), "R"));
            assertNull(ManaProductionProjection.outputChoices(temple, Map.of("G", 1)));
            assertEquals(temple.getManaPart().getExpressChoice(), "", "An output query cannot commit a color");
            var signet = fixture.ability("Simic Signet", ApiType.Mana);
            assertEquals(ManaProductionProjection.outputChoices(signet, Map.of("U", 1, "G", 1)), Map.of(),
                    "A simultaneous fixed bundle is not converted into a color choice");
            assertNull(ManaProductionProjection.outputChoices(signet, Map.of("U", 1)));
            var birds = fixture.ability("Birds of Paradise", ApiType.Mana);
            assertEquals(ManaProductionProjection.outputChoices(birds, Map.of("G", 1)), Map.of(birds.getManaPart(), "G"));
            var host = fixture.add("Gilded Lotus", ZoneType.Battlefield, 0);
            var root = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ Combo U R | Amount$ 2 | Each$ 3", host);
            var sub = AbilityFactory.getAbility("DB$ Mana | Produced$ Combo W G | Amount$ 1", host);
            root.setSubAbility((forge.game.spellability.AbilitySub)sub); root.setActivatingPlayer(fixture.actor());
            var desired = Map.of("U", 3, "R", 3, "G", 1);
            var production = ManaProductionProjection.describe(root);
            var choices = ManaProductionProjection.outputChoices(root, desired);
            assertNotNull(choices);
            assertEquals(choices.get(root.getManaPart()), "R U", "Each multiplies two decisions into six mana, rather than expanding six decision tokens");
            assertEquals(choices.get(sub.getManaPart()), "G", "A nested part keeps its own independent choice");
            assertEquals(root.getManaPart().getExpressChoice(), ""); assertEquals(sub.getManaPart().getExpressChoice(), "");
            assertEquals(ManaProductionProjection.describe(root), production);
            var parsed = ManaOutputChoice.parse(new Gson().toJsonTree(Map.of("colors", desired)));
            assertEquals(parsed.choicesFor(root), choices);
            for (var malformed : List.of(Map.of("colors", Map.of("R", -1)), Map.of("colors", Map.of("r", 1)),
                    Map.of("colors", Map.of("R", 1.5)), Map.of("colors", Map.of("R", "1")),
                    Map.of("colors", Map.of("R", 1024, "U", 1)), Map.of("colors", Map.of()),
                    Map.of("colors", Map.of("R", 1), "action", "cast")))
                assertThrows(IllegalArgumentException.class, () -> ManaOutputChoice.parse(new Gson().toJsonTree(malformed)));
            var restricted = fixture.ability("Jegantha, the Wellspring", ApiType.Mana);
            assertNull(ManaProductionProjection.outputChoices(restricted, Map.of("W", 1, "U", 1, "B", 1, "R", 1, "G", 1)),
                    "An unsupported production route cannot accept an exact output intent");

            // This actual under-construction script is a focused rules edge,
            // not a claim that the varied Commander campaign plays this card.
            var skyscraper = fixture.add("Under-Construction Skyscraper", ZoneType.Battlefield, 0);
            var abzan = AbilityFactory.getAbility(skyscraper.getSVar("ABManaAbzan"), skyscraper);
            abzan.setActivatingPlayer(fixture.actor());
            assertEquals(abzan.getManaPart().getComboColors(abzan), "W B G C");
            var colorless = ManaProductionProjection.outputChoices(abzan, Map.of("C", 1));
            assertEquals(colorless, Map.of(abzan.getManaPart(), "C"));
            assertNull(ManaProductionProjection.outputChoices(abzan, Map.of("U", 1)));
            colorless.forEach(forge.game.spellability.AbilityManaPart::setExpressChoice);
            ApiType.Mana.getSpellEffect().resolve(abzan);
            assertEquals(fixture.actor().getManaPool().getAmountOfColor((byte)forge.card.mana.ManaAtom.COLORLESS), 1,
                    "An explicit C alternative remains colorless even when it is last in the current palette");
            assertEquals(fixture.actor().getManaPool().totalMana(), 1);
            assertEquals(abzan.getManaPart().getExpressChoice(), "", "The ordinary effect clears its one-use selection");
        }
    }

    @Test(timeOut = 90000)
    public void currentSourceCountersGiveBoundedNominalManaWithoutPayingOrFollowingCoupledEffects() throws Exception {
        try (var fixture = new Fixture()) {
            var chalice = fixture.add("Everflowing Chalice", ZoneType.Battlefield, 0);
            var ability = chalice.getSpellAbilities().stream().filter(part -> part.getApi() == ApiType.Mana).findFirst().orElseThrow();
            ability.setActivatingPlayer(fixture.actor());
            var charge = CounterEnumType.CHARGE;
            var pool = fixture.actor().getManaPool().totalMana();
            var zero = ManaProductionProjection.describe(ability);
            assertEquals(AbilityUtils.calculateAmount(chalice, ability.getParam("Amount"), ability), 0);
            assertEquals(zero.get("knowledge"), "Known");
            assertEquals(list(zero.get("alternatives")), List.of(Map.of("colors", Map.of())),
                    "An actual uncharged Chalice has exact zero nominal output, rather than unknown capacity");

            chalice.setCounters(charge, 3);
            var three = ManaProductionProjection.describe(ability);
            assertEquals(AbilityUtils.calculateAmount(chalice, ability.getParam("Amount"), ability), 3,
                    "The actual rules amount matches the projected current source count");
            assertEquals(three.get("knowledge"), "Known");
            assertEquals(list(three.get("alternatives")), List.of(Map.of("colors", Map.of("C", 3))));
            assertEquals(ManaProductionProjection.describe(ability), three, "Repeated projection is stable");
            assertEquals(chalice.getCounters(charge), 3, "Reading output cannot spend counters");
            assertFalse(chalice.isTapped(), "Reading output cannot tap the source");
            assertEquals(fixture.actor().getManaPool().totalMana(), pool, "Reading output cannot produce mana");

            var removing = AbilityFactory.getAbility("AB$ Mana | Cost$ T SubCounter<1/CHARGE> | Produced$ C | Amount$ X", chalice);
            removing.setActivatingPlayer(fixture.actor());
            assertEquals(ManaProductionProjection.describe(removing).get("knowledge"), "Unknown",
                    "Counter-removal costs change the quantity before resolution and cannot reuse the current count");
            var formula = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ C | Amount$ Count$CardCounters.CHARGE/Plus.1", chalice);
            formula.setActivatingPlayer(fixture.actor());
            assertEquals(ManaProductionProjection.describe(formula).get("knowledge"), "Unknown",
                    "The bounded source-counter case does not become a recursive expression evaluator");
            chalice.setCounters(charge, 1025);
            assertEquals(ManaProductionProjection.describe(ability).get("knowledge"), "Unknown");
            chalice.setCounters(charge, 3);

            var reflection = fixture.add("Mana Reflection", ZoneType.Battlefield, 0);
            assertEquals(ManaProductionProjection.describe(ability).get("knowledge"), "Unknown",
                    "An active mana replacement stays outside this current-count fact");
            reflection.getOwner().getZone(ZoneType.Battlefield).remove(reflection);
            assertEquals(ManaProductionProjection.describe(ability), three, "Only current active occurrences affect the conservative boundary");
            fixture.add("Leyline of Abundance", ZoneType.Battlefield, 0);
            assertEquals(ManaProductionProjection.describe(ability).get("knowledge"), "Unknown",
                    "Tap-for-mana trigger coupling is not silently simulated");
        }
    }

    @Test(timeOut = 90000)
    public void publicCommanderColorIdentityProducesFiniteAlternativesWithoutInventingColorlessOrFollowingCoupledEffects() throws Exception {
        try (var fixture = new Fixture()) {
            var tower = fixture.add("Command Tower", ZoneType.Battlefield, 0);
            var ability = tower.getSpellAbilities().stream().filter(part -> part.getApi() == ApiType.Mana).findFirst().orElseThrow();
            ability.setActivatingPlayer(fixture.actor());
            assertEquals(ability.getManaPart().getComboColors(ability), "");
            assertEquals(list(ManaProductionProjection.describe(ability).get("alternatives")), List.of(Map.of("colors", Map.of())),
                    "Without a commander the real operator produces no mana");

            var toothy = fixture.add("Toothy, Imaginary Friend", ZoneType.Command, 0);
            fixture.actor().addCommander(toothy);
            assertEquals(ability.getManaPart().getComboColors(ability), "U");
            assertEquals(list(ManaProductionProjection.describe(ability).get("alternatives")), List.of(Map.of("colors", Map.of("U", 1))));
            var pir = fixture.add("Pir, Imaginative Rascal", ZoneType.Command, 0);
            fixture.actor().addCommander(pir);
            assertEquals(ability.getManaPart().getComboColors(ability), "U G", "The real engine unions both registered partner identities");
            var partnerColors = List.of(Map.of("colors", Map.of("G", 1)), Map.of("colors", Map.of("U", 1)));
            var produced = ManaProductionProjection.describe(ability);
            assertEquals(produced.get("knowledge"), "Known");
            assertTrue(ManaProductionProjection.simpleTapSourceIsStable(ability));
            assertEquals(list(produced.get("alternatives")), partnerColors,
                    "One mana of a chosen commander color is not one mana of every color simultaneously");
            for (String name : List.of("Arcane Signet", "Commander's Sphere")) {
                var source = fixture.add(name, ZoneType.Battlefield, 0);
                var route = source.getSpellAbilities().stream().filter(part -> part.getApi() == ApiType.Mana).findFirst().orElseThrow();
                route.setActivatingPlayer(fixture.actor());
                assertEquals(ManaProductionProjection.describe(route), produced, "The structural operator applies to independent real card definitions");
            }
            var multi = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ Combo ColorIdentity | Amount$ 2", tower);
            multi.setActivatingPlayer(fixture.actor());
            assertEquals(list(ManaProductionProjection.describe(multi).get("alternatives")), List.of(
                    Map.of("colors", Map.of("G", 2)), Map.of("colors", Map.of("G", 1, "U", 1)), Map.of("colors", Map.of("U", 2))));
            var oversized = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ Combo ColorIdentity | Amount$ 32", tower);
            oversized.setActivatingPlayer(fixture.actor());
            assertEquals(ManaProductionProjection.describe(oversized).get("knowledge"), "Unknown", "Finite expansion keeps its existing cap");
            var restricted = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ Combo ColorIdentity | RestrictValid$ Creature", tower);
            restricted.setActivatingPlayer(fixture.actor());
            assertEquals(ManaProductionProjection.describe(restricted).get("knowledge"), "Unknown");
            assertFalse(ManaProductionProjection.simpleTapSourceIsStable(restricted), "The shared source guard itself rejects restricted spending");
            var path = fixture.add("Path of Ancestry", ZoneType.Battlefield, 0);
            var withTrigger = path.getSpellAbilities().stream().filter(part -> part.getApi() == ApiType.Mana).findFirst().orElseThrow();
            withTrigger.setActivatingPlayer(fixture.actor());
            assertEquals(ManaProductionProjection.describe(withTrigger).get("knowledge"), "Unknown", "A spending-trigger dependency is not expanded");
            var palace = fixture.add("Opal Palace", ZoneType.Battlefield, 0);
            var withCounters = palace.getSpellAbilities().stream().filter(part -> part.getApi() == ApiType.Mana
                    && part.getManaPart().getOrigProduced().equals("Combo ColorIdentity")).findFirst().orElseThrow();
            withCounters.setActivatingPlayer(fixture.actor());
            assertEquals(ManaProductionProjection.describe(withCounters).get("knowledge"), "Unknown", "A paid filter with additional counter effects stays unresolved");
            var reflection = fixture.add("Mana Reflection", ZoneType.Battlefield, 0);
            assertEquals(ManaProductionProjection.describe(ability).get("knowledge"), "Unknown", "Ambient mana replacement is not simulated");
            reflection.getOwner().getZone(ZoneType.Battlefield).remove(reflection);
            assertEquals(ManaProductionProjection.describe(ability), produced);
            assertFalse(tower.isTapped());
            assertEquals(ability.getManaPart().getExpressChoice(), "", "Projection cannot make a color choice on behalf of the controller");
            assertEquals(fixture.actor().getManaPool().totalMana(), 0, "Projection never produces mana");

            fixture.actor().removeCommander(toothy); fixture.actor().removeCommander(pir);
            var traxos = fixture.add("Traxos, Scourge of Kroog", ZoneType.Command, 0);
            fixture.actor().addCommander(traxos);
            assertEquals(ability.getManaPart().getComboColors(ability), "");
            assertEquals(list(ManaProductionProjection.describe(ability).get("alternatives")), List.of(Map.of("colors", Map.of())),
                    "Colorless commander identity never becomes invented colorless mana from a colors-only operator");
        }
    }

    @Test(timeOut = 90000)
    public void unrelatedConcealedOriginalsDoNotErasePublicCommanderProductionButCurrentGrantedModifiersDo() throws Exception {
        try (var fixture = new Fixture()) {
            var tower = fixture.add("Command Tower", ZoneType.Battlefield, 0);
            var ability = tower.getManaAbilities().get(0);
            ability.setActivatingPlayer(fixture.actor());
            var toothy = fixture.add("Toothy, Imaginary Friend", ZoneType.Command, 0);
            var pir = fixture.add("Pir, Imaginative Rascal", ZoneType.Command, 0);
            fixture.actor().addCommander(toothy); fixture.actor().addCommander(pir);
            toothy = fixture.table.game.getAction().moveToPlay(toothy, fixture.actor(), null, forge.game.ability.AbilityKey.newMap());
            assertTrue(toothy.isInZone(ZoneType.Battlefield));
            var baseline = ManaProductionProjection.describe(ability);
            assertEquals(baseline.get("knowledge"), "Known");
            assertEquals(list(baseline.get("alternatives")), List.of(Map.of("colors", Map.of("G", 1)), Map.of("colors", Map.of("U", 1))),
                    "Registered commander identity survives an actual move out of the command zone");

            var concealedModifier = fixture.add("Mana Reflection", ZoneType.Battlefield, 1);
            assertTrue(concealedModifier.turnFaceDown(true));
            assertTrue(concealedModifier.getCurrentState().getReplacementEffects(false).isEmpty(),
                    "The real face-down state has none of its concealed original's replacement effects");
            assertEquals(ManaProductionProjection.describe(ability), baseline,
                    "An unrelated face-down permanent cannot erase public nominal output");
            var concealedProducer = fixture.add("Gilded Lotus", ZoneType.Battlefield, 1);
            assertTrue(concealedProducer.turnFaceDown(true));
            assertEquals(new Gson().toJson(ManaProductionProjection.describe(ability)), new Gson().toJson(baseline),
                    "Different concealed originals yield identical metadata, with no identity-dependent side channel");

            var publicModifier = fixture.add("Mana Reflection", ZoneType.Hand, 0);
            var granted = publicModifier.getReplacementEffects().get(0).copy(concealedModifier, false);
            concealedModifier.addReplacementEffect(granted);
            assertEquals(ManaProductionProjection.describe(ability).get("knowledge"), "Unknown",
                    "A currently granted mana replacement remains unresolved even on a face-down permanent");
            assertFalse(tower.isTapped());
            assertEquals(ability.getManaPart().getExpressChoice(), "");
            assertEquals(fixture.actor().getManaPool().totalMana(), 0);
        }
    }

    @Test(timeOut = 90000)
    public void undisclosedTargetOccurrencesNeverEnterTheSemanticGraph() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Grizzly Bears", ZoneType.Battlefield, 0);
            var hidden = fixture.add("Black Lotus", ZoneType.Library, 1);
            var target = AbilityFactory.getAbility("DB$ Destroy | ValidTgts$ Card | TgtZone$ Library", source);
            target.setActivatingPlayer(fixture.actor());
            var graph = fixture.graph(target);
            assertEquals(castMap(nodes(graph).get(0).get("targets")).get("knowledge"), "Unknown");
            assertTrue(candidates(nodes(graph).get(0)).isEmpty());
            String json = new Gson().toJson(graph);
            assertFalse(json.contains(hidden.getName()));
            assertFalse(json.contains(fixture.table.identities.id(hidden.getView())), "Even an opaque occurrence identity would disclose hidden target membership");
        }
    }

    @Test(timeOut = 90000)
    public void finiteConsumingManaRetainsItsExactSourceCostsWithoutBecomingReusableOrPayable() throws Exception {
        try (var fixture = new Fixture()) {
            var vein = fixture.add("Crystal Vein", ZoneType.Battlefield, 0);
            var sacrifice = vein.getManaAbilities().stream().filter(ability -> ability.getPayCosts().getCostParts().stream()
                    .anyMatch(CostSacrifice.class::isInstance)).findFirst().orElseThrow();
            sacrifice.setActivatingPlayer(fixture.actor());
            var facts = ActionSemantics.describeAbility(sacrifice);
            assertEquals(list(castMap(facts.get("production")).get("alternatives")), List.of(Map.of("colors", Map.of("C", 2))));
            var costs = list(facts.get("costs"));
            assertTrue(costs.stream().anyMatch(cost -> cost.get("kind").equals("Tap") && cost.get("fromSource").equals(true)));
            assertTrue(costs.stream().anyMatch(cost -> cost.get("kind").equals("Sacrifice")
                    && cost.get("fromSource").equals(true) && cost.get("amount").equals(1)));
            assertEquals(facts.get("affordability"), "Unknown", "A finite nominal output is not authoritative payment certification");
            var tapOnly = vein.getManaAbilities().stream().filter(ability -> ability != sacrifice).findFirst().orElseThrow();
            tapOnly.setActivatingPlayer(fixture.actor());
            assertSame(tapOnly.getHostCard(), sacrifice.getHostCard(), "The two output routes consume the same physical land");
            assertEquals(list(ManaProductionProjection.describe(tapOnly).get("alternatives")), List.of(Map.of("colors", Map.of("C", 1))));
            var pet = fixture.add("Blood Pet", ZoneType.Battlefield, 0); pet.setSickness(true);
            var petMana = pet.getManaAbilities().get(0); petMana.setActivatingPlayer(fixture.actor());
            assertTrue(petMana.getPayCosts().canPay(petMana, fixture.actor(), false), "Summoning sickness does not prohibit a nontap source sacrifice");
            var petFacts = ActionSemantics.describeAbility(petMana);
            assertEquals(list(castMap(petFacts.get("production")).get("alternatives")), List.of(Map.of("colors", Map.of("B", 1))));
            assertTrue(list(petFacts.get("costs")).stream().anyMatch(cost -> cost.get("kind").equals("Sacrifice")
                    && cost.get("fromSource").equals(true) && cost.get("amount").equals(1)));
            assertTrue(list(petFacts.get("costs")).stream().noneMatch(cost -> cost.get("kind").equals("Tap")));
            var altar = fixture.add("Ashnod's Altar", ZoneType.Battlefield, 0);
            var altarMana = altar.getManaAbilities().get(0); altarMana.setActivatingPlayer(fixture.actor());
            var altarFacts = ActionSemantics.describeAbility(altarMana);
            assertTrue(list(altarFacts.get("costs")).stream().anyMatch(cost -> cost.get("kind").equals("Sacrifice")
                    && cost.get("fromSource").equals(false)), "Choosing another creature is a separate coupling, not a one-use self sacrifice");
            assertEquals(pet.getZone().getZoneType(), ZoneType.Battlefield);
            assertEquals(vein.getZone().getZoneType(), ZoneType.Battlefield);
            assertFalse(vein.isTapped()); assertEquals(fixture.actor().getManaPool().totalMana(), 0);
        }
    }

    @SuppressWarnings("unchecked") private static Map<String, Object> castMap(Object value) { return (Map<String, Object>) value; }
    @SuppressWarnings("unchecked") private static List<Map<String, Object>> list(Object value) { return (List<Map<String, Object>>) value; }
    private static List<Map<String, Object>> nodes(Map<String, Object> graph) { return list(graph.get("operations")); }
    private static Map<String, Object> recipients(Map<String, Object> node) { return castMap(node.get("recipients")); }
    private static List<Map<String, Object>> candidates(Map<String, Object> node) { return list(castMap(node.get("targets")).get("candidates")); }

    private static final class Fixture implements AutoCloseable {
        private final MatchTable table;
        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Actor"),
                    MatchTable.human(new Deck(), "Constructed", "Opponent")), resources, Files.createTempDirectory("operation-projection"));
            HeadlessPlatform.activate(table);
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Card add(String name, ZoneType zone, int seat) {
            Card card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), table.game.getRegisteredPlayers().get(seat));
            card.getOwner().getZone(zone).add(card);
            return card;
        }
        SpellAbility ability(String name, ApiType api) {
            Card card = add(name, ZoneType.Hand, 0);
            var ability = card.getSpellAbilities().stream().filter(part -> part.getApi() == api).findFirst().orElseThrow();
            ability.setActivatingPlayer(actor()); return ability;
        }
        Map<String, Object> graph(SpellAbility ability) {
            return table.game.getAction().readState(() -> ActionOperationProjection.describe(ability, actor(), table.identities::id));
        }
        @Override public void close() { table.close(); }
    }
}
