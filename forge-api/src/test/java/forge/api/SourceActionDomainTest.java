package forge.api;

import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Complete timing domains and payment certainty are separate engine facts. */
public class SourceActionDomainTest {
    @Test public void stableRouteIdentityKeepsTheExistingCostShapeAndEquivalentRouteHash() {
        var card = new forge.game.card.Card(42, null); card.setGameTimestamp(123);
        var ability = new forge.game.spellability.SpellAbility.EmptySa(card, null);
        ability.putParam("Mode", "Test"); ability.setPayCosts(new forge.game.cost.Cost("T", true));
        String expected = "fdceb9ed-8a6a-3ab7-8581-000d5b80ce4e";
        assertEquals(forge.game.spellability.SpellAbilityRouteIdentity.stableId(ability), expected,
                "The pre-existing canonical route shape retains its exact name-based UUID");
        var tap = ActionSemantics.cost(ability.getPayCosts().getCostParts().get(0));
        assertEquals(new java.util.ArrayList<>(tap.keySet()), List.of("kind", "amount", "fromSource", "counterType", "manaSymbols"));
        assertEquals(tap.toString(), "{kind=Tap, amount=1, fromSource=true, counterType=, manaSymbols=[]}");
        ability.putParam("SpellDescription", "Different displayed words"); ability.putParam("AILogic", "DifferentPolicy");
        ability.setPayCosts(ability.getPayCosts().copy());
        assertEquals(forge.game.spellability.SpellAbilityRouteIdentity.stableId(ability), expected,
                "Equivalent copied costs and excluded display/policy prose do not create a different route");
        ability.setPayCosts(new forge.game.cost.Cost("Q", true));
        assertNotEquals(forge.game.spellability.SpellAbilityRouteIdentity.stableId(ability), expected,
                "A different actual payment shape remains a different route");
        ability.setPayCosts(new forge.game.cost.Cost("T", true)); card.setGameTimestamp(124);
        assertNotEquals(forge.game.spellability.SpellAbilityRouteIdentity.stableId(ability), expected,
                "A new physical source occurrence cannot revive the previous route identity");
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anOrdinaryManifestedCreatureCanBeReadQueriedAndActuallyTurnedFaceUpWithoutPublicDisclosure() throws Exception {
        try (var scenario = new RulesScenario("ordinary-manifested-creature", Map.of(0, List.of("Island", "Island", "Mogg Fanatic")), "Merfolk Looter", "Rhys the Redeemed")) {
            scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            var actor = scenario.table.game.getRegisteredPlayers().get(0);
            HeadlessPlatform.query(scenario.table, () -> {
                // Controlled real engine operation: this is the same manifest
                // implementation used by Reality Shift's resolving effect.
                var cause = forge.game.ability.AbilityFactory.getAbility("DB$ Manifest | DefinedPlayer$ You", actor.getCommanders().get(0));
                cause.setActivatingPlayer(actor);
                assertTrue(actor.getCardsIn(ZoneType.Library).get(0).manifest(actor, cause, forge.game.ability.AbilityKey.newMap()).isManifested());
                scenario.table.publish(); return null;
            });
            var json = new com.google.gson.Gson();
            var owned = json.toJsonTree(scenario.session(0).decisionObservation()).getAsJsonObject();
            assertEquals(text(owned.getAsJsonObject("domain"), "coverage"), "Complete",
                    "An original ordinary owned read must include its entitled face-down turn-up route");
            var legacy = scenario.state(0);
            assertTrue(legacy.getAsJsonArray("actionSources").isEmpty(), "Unopted readers never receive the new Battlefield source zone");
            var state = json.toJsonTree(scenario.session(0).state(true)).getAsJsonObject();
            assertEquals(state.get("revision"), legacy.get("revision")); assertEquals(state.get("boardRevision"), legacy.get("boardRevision"));
            var row = state.getAsJsonArray("actionSources").get(0).getAsJsonObject();
            var physical = row.getAsJsonObject("card"); var known = row.getAsJsonObject("knownFace");
            assertEquals(text(known, "name"), "Merfolk Looter"); assertFalse(known.get("faceDown").getAsBoolean());
            for (String identity : List.of("key", "visualId", "combatId")) assertEquals(text(known, identity), "");
            assertTrue(physical.get("faceDown").getAsBoolean()); assertEquals(text(physical, "name"), "Face-down card");
            assertEquals(physical.get("power").getAsInt(), 2); assertEquals(physical.get("toughness").getAsInt(), 2);
            var published = zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(card -> card.get("faceDown").getAsBoolean()).findFirst().orElseThrow();
            assertEquals(physical, published, "A private reading descriptor cannot rewrite any physical battlefield fact");
            var ignored = state.getAsJsonObject("prompt").getAsJsonArray("priorityAbilities").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(ability -> text(ability, "sourceName").equals("Mogg Fanatic")).findFirst().orElseThrow();
            var receipt = json.toJsonTree(scenario.session(0).action(request(new RulesScenario.Input(0, state, state.getAsJsonObject("prompt")),
                    Map.of("action", "setAbilityAutoPass", "abilityId", text(ignored, "id"), "ignored", true)))).getAsJsonObject();
            assertTrue(receipt.getAsJsonArray("actionSources").isEmpty(), "A default command receipt must remain readable by an unopted old client");
            assertFalse(json.toJsonTree(scenario.session(0).state(true)).getAsJsonObject().getAsJsonArray("actionSources").isEmpty(),
                    "Filtering the command receipt cannot revoke or mutate the current owner's separate opted-in reading view");
            String concealed = json.toJson(SpectatorObservation.project(scenario.table, 1, null));
            assertFalse(concealed.contains("Merfolk Looter")); assertTrue(concealed.contains("Face-down card"));
            var before = new RulesScenario.Input(0, state, state.getAsJsonObject("prompt"));
            assertThrows(IllegalArgumentException.class, () -> scenario.session(1).sourceActions(request(before, Map.of("key", text(physical, "key")))));
            var catalog = json.toJsonTree(scenario.session(0).sourceActions(request(before, Map.of("key", text(physical, "key"))))).getAsJsonObject();
            assertEquals(text(catalog, "sourceCardId"), text(physical, "combatId"));
            assertFalse(catalog.getAsJsonArray("actions").isEmpty());
            var command = Map.of("action", "useSourceAction", "key", text(physical, "key"), "actionId",
                    text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"));
            assertTrue(scenario.answer(before, command));
            for (int resource = 0; resource < 2; resource++) {
                var payment = scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
                var island = zone(viewer(payment.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Island") && !card.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                var mana = json.toJsonTree(scenario.session(0).sourceActions(request(payment, Map.of("key", text(island, "key"))))).getAsJsonObject();
                assertTrue(scenario.answer(payment, Map.of("action", "useSourceAction", "key", text(island, "key"), "actionId",
                        text(mana.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
                final int expected = resource + 1;
                long deadline = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(5);
                while (System.nanoTime() < deadline && actor.getCardsIn(ZoneType.Battlefield).stream().filter(forge.game.card.Card::isTapped).count() < expected)
                    Thread.sleep(5);
                assertEquals(actor.getCardsIn(ZoneType.Battlefield).stream().filter(forge.game.card.Card::isTapped).count(), expected);
            }
            boolean resumed = false;
            for (int notices = 0; notices <= 4; notices++) {
                var next = scenario.until(current -> text(current.prompt(), "kind").equals("reveal")
                        || current.seat() == 0 && text(current.prompt(), "inputType").equals("InputPassPriority")
                            && actor.getCardsIn(ZoneType.Battlefield).stream().noneMatch(forge.game.card.Card::isFaceDown));
                if (!text(next.prompt(), "kind").equals("reveal")) { resumed = true; break; }
                assertEquals(next.prompt().get("min").getAsInt(), 0); assertEquals(next.prompt().get("max").getAsInt(), 0);
                assertTrue(scenario.answer(next, Map.of("choices", List.of())), "A real transformed-card reveal requires its ordinary acknowledgment");
            }
            assertTrue(resumed, "All real reveal acknowledgments return the caster's current priority");
            assertTrue(json.toJsonTree(scenario.session(0).state(true)).getAsJsonObject().getAsJsonArray("actionSources").isEmpty());
            assertThrows(IllegalArgumentException.class, () -> scenario.session(0).action(request(before, command)));
            assertTrue(json.toJson(SpectatorObservation.project(scenario.table, 1, null)).contains("Merfolk Looter"));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aManifestedLandCanBeReadByItsOwnerWithoutInventingItsPrintedManaOrTurnFaceUpAction() throws Exception {
        try (var scenario = new RulesScenario("known-manifested-land", Map.of(), "Forest", "Rhys the Redeemed")) {
            scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            var actor = scenario.table.game.getRegisteredPlayers().get(0);
            HeadlessPlatform.query(scenario.table, () -> {
                var cause = forge.game.ability.AbilityFactory.getAbility("DB$ Manifest | DefinedPlayer$ You", actor.getCommanders().get(0));
                cause.setActivatingPlayer(actor);
                var manifested = actor.getCardsIn(ZoneType.Library).get(0).manifest(actor, cause, forge.game.ability.AbilityKey.newMap());
                assertTrue(manifested.isManifested()); assertTrue(manifested.isFaceDown());
                scenario.table.publish();
                return null;
            });
            var state = new com.google.gson.Gson().toJsonTree(scenario.session(0).state(true)).getAsJsonObject();
            var row = state.getAsJsonArray("actionSources").get(0).getAsJsonObject();
            assertEquals(text(row.getAsJsonObject("knownFace"), "name"), "Forest");
            var physical = row.getAsJsonObject("card");
            assertEquals(text(physical, "name"), "Face-down card"); assertEquals(text(physical, "type"), "Creature");
            assertEquals(physical.get("power").getAsInt(), 2); assertEquals(physical.get("toughness").getAsInt(), 2);
            var catalog = new com.google.gson.Gson().toJsonTree(scenario.session(0).sourceActions(request(
                    new RulesScenario.Input(0, state, state.getAsJsonObject("prompt")), Map.of("key", text(physical, "key"))))).getAsJsonObject();
            assertTrue(catalog.getAsJsonArray("actions").isEmpty(), "A known printed land is still only its actual face-down creature");
            var own = new com.google.gson.Gson().toJsonTree(scenario.session(0).decisionObservation(true)).getAsJsonObject();
            assertEquals(text(own.getAsJsonObject("domain"), "coverage"), "Complete");
            assertFalse(own.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream()
                    .map(value -> value.getAsJsonObject()).anyMatch(source -> text(source, "sourceCardKey").equals(text(physical, "key"))));
        }
    }


    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void discoversCommandHandAndManaWithoutTheBoundedHighlightScanOrInventingAffordability() throws Exception {
        try (var scenario = new RulesScenario("source-action-domain", Map.of(0,
                List.of("Island", "Loch Mare")), "Miscast", "Rhys the Redeemed", true)) {
            scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            var actor = scenario.table.game.getPlayers().get(0);
            var input = ((PlayerControllerHuman) actor.getController()).getInputQueue().getInput();
            var domain = SourceActions.discover(input, actor, card -> true);
            assertTrue(domain.complete());
            assertTrue(domain.sources().stream().anyMatch(card -> card.isInZone(ZoneType.Command)),
                    "Command-zone abilities are in the full domain despite absent mana");
            assertTrue(domain.sources().stream().anyMatch(card -> card.isInZone(ZoneType.Hand)));
            var miscast = domain.sources().stream().filter(card -> card.getName().equals("Miscast")).findFirst().orElseThrow();
            var counter = SourceActions.describe(input, miscast, "miscast", 1, card -> true).actions().stream()
                    .filter(action -> action.ability().isSpell()).findFirst().orElseThrow();
            assertEquals(ActionSemantics.describe(counter).get("effect"), "CounterSpell");
            assertEquals(ActionSemantics.describe(counter).get("targetsAvailable"), false,
                    "A targetless counterspell remains discoverable without being mistaken for a usable route");
            var island = domain.sources().stream().filter(card -> card.getName().equals("Island")).findFirst().orElseThrow();
            var mana = SourceActions.describe(input, island, "island", 1, card -> true).actions().stream()
                    .filter(action -> action.ability().isManaAbility()).findFirst().orElseThrow();
            var manaFacts = ActionSemantics.describe(mana);
            assertEquals(manaFacts.get("manaColors"), List.of("U"));
            assertEquals(manaFacts.get("manaAmount"), 1);
            assertEquals(manaFacts.get("affordability"), "Payable");

            var mare = domain.sources().stream().filter(card -> card.getName().equals("Loch Mare")).findFirst().orElseThrow();
            var catalog = SourceActions.describe(input, mare, "mare", 1, card -> true);
            var draw = catalog.actions().stream().filter(action -> action.ability().getApi() == forge.game.ability.ApiType.Draw)
                    .findFirst().orElseThrow();
            var facts = ActionSemantics.describe(draw);
            assertEquals(facts.get("effect"), "Draw");
            assertEquals(facts.get("effectAmount"), 1);
            assertEquals(DecisionSemantics.describeAbility(draw.ability()).get("intent"), "Benefit");
            var modeFacts = DecisionSemantics.options(List.of("Draw ten cards", draw.ability()), view -> null);
            assertEquals(modeFacts.size(), 1, "Display prose is never treated as an engine effect");
            assertEquals(modeFacts.get(0).get("index"), 1, "Mode metadata preserves the original choice index");
            assertEquals(modeFacts.get(0).get("effect"), "Draw");
            assertEquals(facts.get("affordability"), "Unknown", "Insufficient planner knowledge does not erase a timing-legal route");
            @SuppressWarnings("unchecked") var costs = (List<Map<String, Object>>) facts.get("costs");
            assertTrue(costs.stream().anyMatch(cost -> cost.get("kind").equals("RemoveCounters") && cost.get("amount").equals(1)
                    && cost.get("fromSource").equals(true)));
            assertTrue(costs.stream().anyMatch(cost -> cost.get("kind").equals("Mana") && cost.get("amount").equals(1)
                    && cost.get("manaSymbols").equals(List.of("{U}"))));
            var reread = SourceActions.describe(input, mare, "mare", 2, card -> true).actions().stream()
                    .filter(action -> action.ability().getApi() == forge.game.ability.ApiType.Draw).findFirst().orElseThrow();
            assertNotEquals(draw.id(), reread.id(), "Opaque authority belongs to the current catalog");
            assertEquals(ActionSemantics.describe(reread).get("stableId"), facts.get("stableId"),
                    "Equivalent route identity survives a fresh offer without becoming submission authority");
            assertFalse(SourceActions.discover(input, actor, card -> card != island).complete(),
                    "A disclosure omission must never authorize complete-domain passing");
            assertEquals(mare.getCounters().size(), 3,
                    "Reading rules metadata never pays a counter cost");
            assertFalse(island.isTapped(), "Discovery never taps a mana source");
            var publicState = SpectatorObservation.project(scenario.table, 1, null);
            String publicJson = new com.google.gson.Gson().toJson(publicState);
            assertFalse(publicJson.contains("Miscast"), "A spectator never receives any bot/human hand's known face");
            assertTrue(publicJson.contains("Loch Mare"), "The same public audience still receives battlefield faces");
            assertNull(publicState.get("prompt"));
            assertEquals(publicState.get("viewerId"), -1);
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void entitledFaceDownSourceRoutesRemainPrivateToTheirOwner() throws Exception {
        try (var scenario = new RulesScenario("source-action-domain-facedown", Map.of(0,
                List.of("Island", "Island", "Island")), "Willbender", "Rhys the Redeemed")) {
            var main = scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            var actor = scenario.table.game.getPlayers().get(0);
            var input = ((PlayerControllerHuman) actor.getController()).getInputQueue().getInput();
            var source = actor.getCardsIn(ZoneType.Hand).get(0);
            var actual = input.getSourceActions(source);
            int faceDownIndex = -1;
            for (int index = 0; index < actual.size(); index++) if (actual.get(index).isCastFaceDown()) { faceDownIndex = index; break; }
            assertTrue(faceDownIndex >= 0, "The actual card offers its morph casting route");
            var hand = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var sourceRequest = request(main, Map.of("key", text(hand, "key")));
            var offers = new com.google.gson.Gson().toJsonTree(scenario.table.seat(0).sourceActions(sourceRequest)).getAsJsonObject();
            var offer = offers.getAsJsonArray("actions").get(faceDownIndex).getAsJsonObject();
            assertTrue(scenario.answer(main, Map.of("action", "useSourceAction", "key", text(hand, "key"), "actionId", text(offer, "id"))));
            for (int resource = 0; resource < 3; resource++) {
                var payment = scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
                var island = zone(viewer(payment.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Island") && !card.get("tapped").getAsBoolean())
                        .findFirst().orElseThrow();
                var manaOffers = new com.google.gson.Gson().toJsonTree(scenario.table.seat(0).sourceActions(request(payment,
                        Map.of("key", text(island, "key"))))).getAsJsonObject();
                assertTrue(scenario.answer(payment, Map.of("action", "useSourceAction", "key", text(island, "key"),
                        "actionId", text(manaOffers.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
                // An input may republish while the queued mana ability is still
                // running. Wait for its authoritative tap/payment publication
                // before choosing the next source from a snapshot.
                boolean published = false;
                for (int wait = 0; wait < 400; wait++) {
                    var current = scenario.state(0);
                    long tapped = zone(viewer(current), "Battlefield").getAsJsonArray("cards").asList().stream()
                            .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Island") && card.get("tapped").getAsBoolean()).count();
                    var prompt = current.has("prompt") && current.get("prompt").isJsonObject() ? current.getAsJsonObject("prompt") : null;
                    if (tapped >= resource + 1 && prompt != null && (resource < 2
                            ? text(prompt, "inputType").startsWith("InputPayMana") : text(prompt, "inputType").equals("InputPassPriority"))) {
                        published = true; break;
                    }
                    Thread.sleep(5);
                }
                assertTrue(published, "The exact mana action publishes its tap and remaining payment before another source is chosen");
            }
            scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && next.state().getAsJsonArray("stack").isEmpty() && actor.getCardsIn(ZoneType.Battlefield).stream().anyMatch(forge.game.card.Card::isFaceDown));
            var faceDown = actor.getCardsIn(ZoneType.Battlefield).stream().filter(forge.game.card.Card::isFaceDown).findFirst().orElseThrow();
            input = ((PlayerControllerHuman) actor.getController()).getInputQueue().getInput();
            var allowed = SourceActions.discover(input, actor, card -> card.getView().canBeShownTo(actor.getView())
                    && card.getView().canFaceDownBeShownTo(actor.getView()));
            assertTrue(allowed.complete());
            assertTrue(allowed.sources().contains(faceDown), "An entitled actor can discover its actual turn-face-up route");
            assertFalse(SourceActions.discover(input, actor, card -> !card.isFaceDown() && card.getView().canBeShownTo(actor.getView())).complete(),
                    "The public audience cannot promote an undisclosed route to complete coverage");
            String publicJson = new com.google.gson.Gson().toJson(SpectatorObservation.project(scenario.table, 1, null));
            assertFalse(publicJson.contains("Willbender"), "Hidden hands and the morph's original face/name/art never enter public projection");
            assertTrue(publicJson.contains("Face-down card"));
        }
    }
}
