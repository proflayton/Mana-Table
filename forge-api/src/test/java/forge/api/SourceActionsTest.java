package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.nio.file.Files;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The production source-action capability observes first, then invokes exactly one engine ability. */
public class SourceActionsTest {
    private static final Gson JSON = new Gson();

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void linkedPreparedSpellsCastFromTheirCreatureAndRevokeConsumedOrReplacedCopies() throws Exception {
        String creature = "Emeritus of Ideation", spell = "Ancestral Recall";
        try (var game = new RulesScenario("source-actions-prepared", Map.of(0,
                List.of(creature, "Island", "Island", "Plains", "Plains", "Plains")), "Cloudshift", "Rhys the Redeemed")) {
            var input = main(game);
            // Registered fixture permanents bypass entering replacements. A real
            // blink exercises the creature's actual enters-prepared rules.
            input = blinkPreparedSource(game, input, creature);
            var source = card(input.state(), "Battlefield", creature);
            assertTrue(prepared(source), "The engine publishes the current non-keyword Prepared designation");
            assertTrue(source.getAsJsonArray("states").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(state -> text(state, "key").equals("prepared") && text(state, "detail").equals(spell + " {U}")));
            assertTrue(source.get("selectable").getAsBoolean(), "A creature whose only action is its linked spell remains discoverable on the battlefield");
            var before = game.state(0);
            var actions = query(game, input, source);
            assertEquals(query(game, input, source), actions, "Repeated reading preserves the exact current action handle");
            assertEquals(game.state(0), before, "Reading a prepared spell does not cast, unprepare, or advance priority");
            var cast = castAction(actions);
            assertEquals(actions.getAsJsonArray("actions").size(), 1);
            assertEquals(text(cast, "zone"), "Exile");
            var preview = cast.getAsJsonObject("card");
            assertEquals(text(preview, "name"), spell);
            assertEquals(text(preview, "manaCost"), "{U}");
            assertEquals(text(preview, "artName"), spell, "The spell preview must not request the creature's full card artwork");
            assertEquals(text(preview, "artSet"), ""); assertEquals(text(preview, "collectorNumber"), "");
            assertEquals(text(preview, "key"), ""); assertFalse(preview.get("selectable").getAsBoolean());
            assertFalse(preview.get("highlighted").getAsBoolean());
            assertEquals(text(actions, "sourceCardId"), text(source, "visualId"));
            assertNotEquals(text(preview, "visualId"), text(source, "visualId"));
            Files.writeString(game.evidence.resolve("prepared-source-actions.json"), JSON.toJson(actions));

            assertTrue(game.answer(input, invoke(actions, cast)));
            finishTargetedCast(game, spell, null);
            input = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                    && !prepared(card(next.state(), "Battlefield", creature)));
            assertEquals(count(viewer(input.state()), "Exile"), 0, "Casting consumes the prepared copy instead of leaving a stale exile entry");
            assertEquals(count(viewer(input.state()), "Hand"), count(viewer(before), "Hand") + 3, "The actual prepared Ancestral Recall resolves its draw-three effect");
            assertTrue(query(game, input, card(input.state(), "Battlefield", creature)).getAsJsonArray("actions").isEmpty());

            input = blinkPreparedSource(game, input, creature);
            var renewed = query(game, input, card(input.state(), "Battlefield", creature));
            var renewedCast = castAction(renewed);
            assertNotEquals(text(renewedCast.getAsJsonObject("card"), "visualId"), text(preview, "visualId"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(
                    new Input(0, game.state(0), game.state(0).getAsJsonObject("prompt")), invoke(renewed, cast))));

            input = blinkPreparedSource(game, input, creature);
            var replaced = query(game, input, card(input.state(), "Battlefield", creature));
            assertEquals(count(viewer(input.state()), "Exile"), 1, "Leaving play removes the old prepared copy before re-entering creates its replacement");
            assertNotEquals(text(castAction(replaced).getAsJsonObject("card"), "visualId"),
                    text(renewedCast.getAsJsonObject("card"), "visualId"));
            var current = input;
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(current, invoke(replaced, renewedCast))));
            assertTrue(prepared(card(input.state(), "Battlefield", creature)));
            for (int seat = 1; seat < 4; seat++) {
                var other = game.state(seat);
                assertTrue(zone(player(other, input.state().get("viewerId").getAsInt()), "Hand").getAsJsonArray("cards").isEmpty(),
                        "A linked public spell never broadens another seat's hand visibility");
            }
        }
    }

    private static boolean prepared(JsonObject card) {
        return card != null && card.getAsJsonArray("states").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(state -> text(state, "key").equals("prepared") && text(state, "kind").equals("status"));
    }

    private static Input blinkPreparedSource(RulesScenario game, Input input, String creature) throws Exception {
        var blink = query(game, input, card(input.state(), "Hand", "Cloudshift"));
        assertTrue(game.answer(input, invoke(blink, castAction(blink))));
        finishTargetedCast(game, "Cloudshift", creature);
        return game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                && prepared(card(next.state(), "Battlefield", creature)));
    }

    private static void finishTargetedCast(RulesScenario game, String spell, String targetCard) throws Exception {
        // Targeting/payment may publish intermediate prompts (including another
        // payment prompt). Their count is not the spell's completion condition.
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(45);
        while (System.nanoTime() < deadline) {
            var input = game.until(next -> next.seat() == 0 && (text(next.prompt(), "inputType").equals("InputSelectTargets")
                    || text(next.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(next) && next.state().getAsJsonArray("stack").asList().stream().anyMatch(item -> text(item.getAsJsonObject(), "name").equals(spell))));
            if (priority(input)) return;
            if (text(input.prompt(), "inputType").equals("InputSelectTargets")) {
                assertTrue(game.answer(input, targetCard == null
                        ? Map.of("action", "player", "playerId", input.state().get("viewerId").getAsInt())
                        : Map.of("action", "card", "key", text(card(input.state(), "Battlefield", targetCard), "key"))));
            } else {
                assertTrue(input.prompt().get("okEnabled").getAsBoolean(), "The fixture supplies the real spell's mana cost");
                assertTrue(game.answer(input, Map.of("action", "ok")));
            }
        }
        fail("The actual targeted spell did not reach the stack: " + spell);
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void readingRealManaActionsDoesNotAnswerPriorityAndAnExplicitActionUsesItsExactSource() throws Exception {
        try (var game = new RulesScenario("source-actions-mana", Map.of(0, List.of("Concordant Crossroads", "Llanowar Elves", "Llanowar Elves")))) {
            var input = main(game);
            HeadlessPlatform.query(() -> null);
            var before = game.state(0);
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input,
                    Map.of("action", "ok", "expectedTask", "openingHand"))));
            assertEquals(game.state(0), before, "A typed opening-hand answer cannot become a priority pass");
            assertEquals(text(input.prompt(), "priorityAlternative"), "passTurn");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input,
                    Map.of("action", "priorityUndo"))));
            assertEquals(game.state(0), before, "An unavailable undo must not fall through to passing the turn");
            var elves = zone(viewer(before), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Llanowar Elves")).toList();
            var first = query(game, input, elves.get(0));
            assertEquals(query(game, input, elves.get(0)), first, "Repeated reads retain the same current opaque action");
            assertEquals(game.state(0), before, "Reading actions changes neither snapshot nor prompt nor mana nor source");
            assertEquals(text(first, "sourceCardId"), text(elves.get(0), "visualId"));
            var action = first.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(first.getAsJsonArray("actions").size(), 1);
            assertEquals(text(action, "kind"), "activate");
            assertTrue(text(action, "text").contains("Add {G}"), "The engine's full ability description supplies its effect");
            assertTrue(text(action, "text").contains("{T}"), "The engine supplies the actual tap cost");
            var second = query(game, input, elves.get(1));
            assertNotEquals(text(second.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"), text(action, "id"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input,
                    Map.of("action", "useSourceAction", "key", text(elves.get(1), "key"), "actionId", text(action, "id")))));
            assertEquals(game.state(0), before);
            assertTrue(game.answer(input, invoke(first, action)));
            var after = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            var current = zone(viewer(after.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).toList();
            assertTrue(current.stream().filter(card -> text(card, "visualId").equals(text(first, "sourceCardId")))
                    .findFirst().orElseThrow().get("tapped").getAsBoolean());
            assertFalse(current.stream().filter(card -> text(card, "visualId").equals(text(second, "sourceCardId")))
                    .findFirst().orElseThrow().get("tapped").getAsBoolean());
            assertTrue(after.state().getAsJsonArray("stack").isEmpty(), "A real mana ability does not create a fabricated ability chooser or stack item");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input, invoke(first, action))));
            assertEquals(text(after.prompt(), "priorityAlternative"), "undo");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(after,
                    Map.of("action", "priorityPassTurn"))));
            assertTrue(game.answer(after, Map.of("action", "priorityUndo")));
            var undone = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 0
                    && !text(next.prompt(), "id").equals(text(after.prompt(), "id")));
            assertFalse(card(undone.state(), "Battlefield", "Llanowar Elves").get("tapped").getAsBoolean(),
                    "Explicit undo reverses the mana action through the engine");
            assertEquals(text(undone.prompt(), "priorityAlternative"), "passTurn");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(after,
                    Map.of("action", "priorityUndo"))));
            assertTrue(game.answer(undone, Map.of("action", "priorityPassTurn", "expectedTask", "priority")));
            var yielded = game.until(next -> next.seat() != 0 && priority(next));
            assertNotEquals(yielded.seat(), 0, "Explicit pass-turn advances without substituting an undo");
        }
    }

    @Test(timeOut = 90000)
    public void anExactNonManaActionKeepsItsLaterRequiredChoiceInsteadOfChoosingTheOtherAbility() throws Exception {
        try (var game = new RulesScenario("source-actions-placement", Map.of(0, List.of("Gaea's Touch")))) {
            var input = main(game);
            var source = card(input.state(), "Battlefield", "Gaea's Touch");
            var actions = query(game, input, source);
            assertEquals(actions.getAsJsonArray("actions").size(), 2, "Both real abilities are available before selecting the source");
            var placement = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "text").contains("Forest")).findFirst().orElseThrow();
            assertTrue(game.answer(input, invoke(actions, placement)));
            var search = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectEntitiesFromList"));
            assertNotNull(card(search.state(), "Battlefield", "Gaea's Touch"), "The exact action never substitutes the sacrifice ability");
            assertEquals(viewer(search.state()).getAsJsonObject("mana").get("G").getAsInt(), 0);
            assertEquals(text(search.prompt().getAsJsonObject("sourceCard"), "name"), "Gaea's Touch");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).sourceActions(request(search,
                    Map.of("key", text(card(search.state(), "Battlefield", "Gaea's Touch"), "key")))));
            assertEquals(text(game.state(0).getAsJsonObject("prompt"), "id"), text(search.prompt(), "id"));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void paymentUsesThePaymentInputsActualManaActionsWithoutASecondAbilityChooser() throws Exception {
        try (var game = new RulesScenario("source-actions-payment", Map.of(0, List.of("Forest")))) {
            var input = main(game);
            var commander = zone(viewer(input.state()), "Command").getAsJsonArray("cards").get(0).getAsJsonObject();
            var casts = query(game, input, commander);
            var cast = casts.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(text(cast, "kind"), "cast");
            assertTrue(game.answer(input, invoke(casts, cast)));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var forest = card(payment.state(), "Battlefield", "Forest");
            var before = game.state(0);
            var mana = query(game, payment, forest);
            assertEquals(game.state(0), before);
            var action = mana.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(text(action, "kind"), "activate");
            assertTrue(text(action, "text").contains("Add {G}"));
            assertTrue(game.answer(payment, invoke(mana, action)));
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            while (game.state(0).getAsJsonArray("stack").isEmpty() && System.nanoTime() < deadline) Thread.sleep(5);
            var paid = game.until(next -> next.seat() == 0 && priority(next) && !next.state().getAsJsonArray("stack").isEmpty());
            assertTrue(card(paid.state(), "Battlefield", "Forest").get("tapped").getAsBoolean());
            assertEquals(text(paid.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "name"), "Rhys the Redeemed");
        }
    }

    @Test(timeOut = 90000)
    public void bothRealPartnerCommandersExposeIndependentCastsAndToothyCanRecastWithItsOwnTax() throws Exception {
        String toothy = "Toothy, Imaginary Friend", pir = "Pir, Imaginative Rascal";
        try (var game = new RulesScenario("source-actions-toothy-partners",
                Map.of(0, List.of("Island", "Island", "Island", "Island", "Forest", "Forest", "Forest", "Forest", "Seal of Removal")),
                "Island", List.of(toothy, pir))) {
            var input = main(game);
            assertEquals(zone(viewer(input.state()), "Command").getAsJsonArray("cards").size(), 2);
            assertEquals(zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Island") && !c.get("tapped").getAsBoolean()).count(), 4L);
            var toothyActions = query(game, input, card(input.state(), "Command", toothy));
            var pirActions = query(game, input, card(input.state(), "Command", pir));
            Files.writeString(game.evidence.resolve("before-first-cast.json"), JSON.toJson(input.state()));
            Files.writeString(game.evidence.resolve("partner-source-actions.json"), JSON.toJson(Map.of("toothy", toothyActions, "pir", pirActions)));
            var toothyCast = castAction(toothyActions);
            var pirCast = castAction(pirActions);
            assertNotEquals(text(toothyActions, "sourceCardKey"), text(pirActions, "sourceCardKey"));
            assertNotEquals(text(toothyCast, "id"), text(pirCast, "id"));
            var wrongPartner = invoke(pirActions, toothyCast);
            var unchanged = game.state(0);
            var initialInput = input;
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(initialInput, wrongPartner)));
            assertEquals(game.state(0), unchanged, "One partner's opaque action cannot cast the other commander");

            castAndPay(game, input, toothyActions, toothy, "first-toothy");
            input = finishPartnerSearch(game);
            assertEquals(card(input.state(), "Battlefield", toothy).get("commanderTax").getAsInt(), 2);
            assertEquals(card(input.state(), "Command", pir).get("commanderTax").getAsInt(), 0);
            pirActions = query(game, input, card(input.state(), "Command", pir));
            castAndPay(game, input, pirActions, pir, "first-pir");
            input = finishPartnerSearch(game);
            assertEquals(card(input.state(), "Battlefield", pir).get("commanderTax").getAsInt(), 2);
            assertEquals(card(input.state(), "Battlefield", toothy).get("commanderTax").getAsInt(), 2);

            var seal = card(input.state(), "Battlefield", "Seal of Removal");
            var removal = query(game, input, seal);
            assertTrue(game.answer(input, invoke(removal, removal.getAsJsonArray("actions").get(0).getAsJsonObject())));
            var target = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectTargets"));
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(card(target.state(), "Battlefield", toothy), "key"))));
            var replacement = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm")
                    && text(next.prompt(), "message").toLowerCase(java.util.Locale.ROOT).contains("command"));
            assertTrue(game.answer(replacement, Map.of("action", "ok")));
            input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                    && next.state().getAsJsonArray("stack").isEmpty() && card(next.state(), "Command", toothy) != null);
            assertEquals(card(input.state(), "Command", toothy).get("commanderTax").getAsInt(), 2);
            var land = query(game, input, card(input.state(), "Hand", "Island"));
            assertTrue(game.answer(input, invoke(land, land.getAsJsonArray("actions").get(0).getAsJsonObject())));
            input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                    && next.state().getAsJsonArray("stack").isEmpty());
            int previousTurn = input.state().get("turn").getAsInt();
            assertTrue(game.answer(input, Map.of("action", "ok")));
            boolean nextTurn = false;
            for (int step = 0; step < 8; step++) {
                input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                        && next.state().get("activePlayerId").getAsInt() == next.state().get("viewerId").getAsInt()
                        && next.state().get("turn").getAsInt() > previousTurn && next.state().getAsJsonArray("stack").isEmpty()
                        || text(next.state(), "phaseKey").equals("CLEANUP") && text(next.prompt(), "inputType").equals("InputSelectCardsFromList"));
                if (priority(input)) { nextTurn = true; break; }
                var discard = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> value.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(discard, "key"))));
            }
            assertTrue(nextTurn, "The four-seat fixture reaches the caster's next main phase after explicit cleanup discards");
            toothyActions = query(game, input, card(input.state(), "Command", toothy));
            Files.writeString(game.evidence.resolve("before-taxed-recast.json"), JSON.toJson(input.state()));
            Files.writeString(game.evidence.resolve("taxed-toothy-source-actions.json"), JSON.toJson(toothyActions));
            castAndPay(game, input, toothyActions, toothy, "taxed-toothy");
            input = finishPartnerSearch(game);
            assertEquals(card(input.state(), "Battlefield", toothy).get("commanderTax").getAsInt(), 4);
            assertEquals(card(input.state(), "Battlefield", pir).get("commanderTax").getAsInt(), 2,
                    "Toothy's recast increments only Toothy's own cast count, including with its partner on the battlefield");
            Files.writeString(game.evidence.resolve("both-partners-after-taxed-cast.json"), JSON.toJson(input.state()));
        }
    }

    private static JsonObject castAction(JsonObject actions) {
        return actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(action -> text(action, "kind").equals("cast")).findFirst()
                .orElseThrow(() -> new AssertionError("A real current cast action is required: " + actions));
    }

    private static void castAndPay(RulesScenario game, Input input, JsonObject actions, String name, String evidence) throws Exception {
        assertTrue(game.answer(input, invoke(actions, castAction(actions))));
        var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
        Files.writeString(game.evidence.resolve(evidence + "-payment.json"), JSON.toJson(payment.state()));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "The supplied untapped mana sources make Auto payment available");
        assertTrue(game.answer(payment, Map.of("action", "ok")));
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (game.state(0).getAsJsonArray("stack").isEmpty() && System.nanoTime() < deadline) Thread.sleep(5);
        var pending = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").asList().stream()
                .map(item -> item.getAsJsonObject()).anyMatch(item -> text(item, "name").equals(name)));
        Files.writeString(game.evidence.resolve(evidence + "-on-stack.json"), JSON.toJson(pending.state()));
        assertTrue(viewer(pending.state()).getAsJsonObject("mana").entrySet().stream().allMatch(entry -> entry.getValue().getAsInt() == 0),
                "The real payment spends all mana produced for this commander");
    }

    private static Input finishPartnerSearch(RulesScenario game) throws Exception {
        var target = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectTargets"));
        assertTrue(game.answer(target, Map.of("action", "player", "playerId", target.state().get("viewerId").getAsInt())));
        var search = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm"));
        assertTrue(text(search.prompt(), "message").toLowerCase(java.util.Locale.ROOT).contains("library"));
        assertTrue(game.answer(search, Map.of("action", "cancel")));
        return game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                && next.state().getAsJsonArray("stack").isEmpty());
    }

    @Test(timeOut = 90000)
    public void aReadOnlySeatConnectionCanDescribeAndPlayALandButCannotQueryAnotherSeatsPrivateHandles() throws Exception {
        try (var game = new RulesScenario("source-actions-seat-routing", Map.of())) {
            var input = main(game);
            var land = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            try (var connection = SeatConnection.local(game.table.seat(0))) {
                var result = JSON.toJsonTree(connection.sourceActions(request(input, Map.of("key", text(land, "key"))))).getAsJsonObject();
                var action = result.getAsJsonArray("actions").get(0).getAsJsonObject();
                assertEquals(text(action, "kind"), "play");
                assertEquals(text(result, "sourceCardId"), text(land, "visualId"));
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(1).sourceActions(request(input, Map.of("key", text(land, "key")))));
                assertTrue(zone(player(game.state(1), input.state().get("viewerId").getAsInt()), "Hand").getAsJsonArray("cards").isEmpty());
                connection.action(request(input, invoke(result, action)));
                var played = game.until(next -> next.seat() == 0 && priority(next) && count(viewer(next.state()), "Battlefield") == 1);
                assertEquals(viewer(played.state()).get("landPlaysUsed").getAsInt(), 1);
            }
        }
    }

    @Test(timeOut = 90000)
    public void aLateRulesMutationRevokesAnAcceptedQueuedActionAndRestoresTheUnansweredInput() throws Exception {
        try (var game = new RulesScenario("source-actions-late-revocation", Map.of(0, List.of("Sol Ring"), 1, List.of("Spellbook")))) {
            var input = main(game);
            var source = card(input.state(), "Battlefield", "Sol Ring");
            var actions = query(game, input, source);
            var action = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
            var entered = new CountDownLatch(1);
            var release = new CountDownLatch(1);
            try {
                HeadlessPlatform.later(() -> { entered.countDown(); await(release); });
                assertTrue(entered.await(5, TimeUnit.SECONDS));
                assertTrue(game.answer(input, invoke(actions, action)));
                var concede = new JsonObject(); concede.addProperty("sessionId", text(input.state(), "id"));
                game.table.seat(1).concede(concede);
                release.countDown();
                HeadlessPlatform.query(() -> null);
                var restored = game.state(0);
                assertEquals(text(restored.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
                assertFalse(card(restored, "Battlefield", "Sol Ring").get("tapped").getAsBoolean());
                assertEquals(viewer(restored).getAsJsonObject("mana").get("C").getAsInt(), 0);
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input, invoke(actions, action))));
                assertTrue(query(game, new Input(0, restored, restored.getAsJsonObject("prompt")), source).getAsJsonArray("actions").size() > 0);
            } finally { release.countDown(); }
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElse(null);
    }
    private static JsonObject query(RulesScenario game, Input input, JsonObject source) {
        return JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
    }
    private static Map<String, ?> invoke(JsonObject actions, JsonObject action) {
        return Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"));
    }
    private static JsonObject request(Input input, Map<String, ?> body) {
        var request = JSON.toJsonTree(body).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id")); request.addProperty("promptId", text(input.prompt(), "id"));
        return request;
    }
    private static void await(CountDownLatch latch) {
        try { if (!latch.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input queue was not released"); }
        catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
    }
}
