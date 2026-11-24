package forge.api;

import com.google.gson.GsonBuilder;
import forge.StaticData;
import forge.card.CardRarity;
import forge.card.CardRules;
import forge.deck.Deck;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.item.PaperCard;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;

import static org.testng.Assert.*;

public class AttachmentCapabilitiesTest {
    private static final String MODIFIER = "Mode$ Continuous | AffectedDefined$ Enchanted | Affected$ Creature";

    @Test(timeOut = 90000)
    public void actualRancorRouteRetainsLegalFriendlyAndOpponentBindingsAndPortableModifier() throws Exception {
        try (var fixture = new Fixture()) {
            var aura = fixture.add("Rancor", ZoneType.Hand, 0);
            var friendly = fixture.add("Grizzly Bears", ZoneType.Battlefield, 0);
            var opponent = fixture.add("Hill Giant", ZoneType.Battlefield, 1);
            var ability = fixture.attach(aura);
            var graph = fixture.graph(ability);
            var node = nodes(graph).get(0);
            // This assertion is also run with the actual pre-change projection
            // classes first on an isolated baseline classpath.
            assertNotNull(node.get("attachment"), "The actual Rancor Attach root needs its structurally proven modifier");
            assertEquals(body(node).get("knowledge"), "Known");
            assertEquals(body(node).get("powerDelta"), 2);
            assertEquals(body(node).get("toughnessDelta"), 0);
            assertEquals(body(node).get("grantedKeywords"), List.of("trample"));
            assertEquals(node.get("effect"), "Unknown", "No new enum is emitted to old ordinary/sync readers");
            assertEquals(node.get("knowledge"), "Unknown"); assertEquals(graph.get("knowledge"), "Unknown");
            assertEquals(map(node.get("sources")).get("binding"), "Source");
            assertFalse(node.containsKey("attachmentSourceCardId"), "A current published zone source needs no decision-portrait sidecar");
            var target = map(node.get("targets"));
            assertEquals(target.get("knowledge"), "Known"); assertEquals(target.get("min"), 1); assertEquals(target.get("max"), 1);
            assertEquals(map(node.get("recipients")).get("binding"), "Targets");
            assertEquals(map(node.get("recipients")).get("targetOperationId"), node.get("id"));
            var candidates = maps(target.get("candidates"));
            assertTrue(candidates.stream().anyMatch(value -> Objects.equals(value.get("ownerId"), friendly.getOwner().getId())));
            assertTrue(candidates.stream().anyMatch(value -> Objects.equals(value.get("ownerId"), opponent.getOwner().getId())));
            assertTrue(candidates.stream().allMatch(value -> value.get("kind").equals("Card") && value.get("ownerId") != null && value.get("controllerId") != null));
            assertEquals(AttachmentCapabilities.export(aura.getPaperCard().getRules().getMainPart()),
                    AttachmentCapabilities.exportCurrent(ability, fixture.actor()), "Offline and current live source use the same structural evaluator/fingerprint");
            assertTrue(fixture.table.game.getStack().isEmpty(), "Reading the modifier cannot cast or attach anything");
            assertTrue(friendly.getAttachedCards().isEmpty()); assertTrue(opponent.getAttachedCards().isEmpty());
        }
    }

    @Test(timeOut = 90000)
    public void renamedDefinitionsHintsAndDescriptionsCannotSupplyModifierAuthority() throws Exception {
        var first = paper("Fixture positive first", MODIFIER + " | AddPower$ 2 | AddKeyword$ Trample | Description$ Harm your opponent.", "Curse");
        var renamed = paper("Completely unrelated label", MODIFIER + " | AddPower$ 2 | AddKeyword$ Trample | Description$ Unrelated prose.", "Pump");
        var original = AttachmentCapabilities.export(first.getRules().getMainPart());
        assertEquals(original, AttachmentCapabilities.export(renamed.getRules().getMainPart()));
        try (var fixture = new Fixture()) {
            var card = fixture.add(renamed, ZoneType.Hand, 0);
            assertEquals(bodyFromEnvelope(AttachmentCapabilities.exportCurrent(fixture.attach(card), fixture.actor())).get("knowledge"), "Known");
            assertEquals(original, AttachmentCapabilities.exportCurrent(fixture.attach(card), fixture.actor()));
        }
        var harmful = AttachmentCapabilities.export(paper("Fixture negative", MODIFIER + " | AddPower$ -2 | AddToughness$ -2", "Pump").getRules().getMainPart());
        assertEquals(bodyFromEnvelope(harmful).get("powerDelta"), -2); assertEquals(bodyFromEnvelope(harmful).get("toughnessDelta"), -2);
        assertNotEquals(map(original.get("source")).get("definitionFingerprint"), map(harmful.get("source")).get("definitionFingerprint"));
    }

    @Test(timeOut = 90000)
    public void unsupportedMixedConditionalDynamicAndOutOfBoundsInputsRemainWhollyUnknown() {
        var definitions = List.of(
                MODIFIER + " | AddPower$ X | AddKeyword$ Trample",
                MODIFIER + " | AddPower$ 2 | Condition$ Threshold",
                MODIFIER + " | AddPower$ 2 | AIUnrecognizedRestriction$ True",
                MODIFIER + " | AddPower$ 2 | AddKeyword$ Hexproof:Blue",
                MODIFIER + " | AddPower$ 1001",
                MODIFIER + " | AddPower$ 800\nS:" + MODIFIER + " | AddPower$ 800",
                MODIFIER + " | AddPower$ 2\nS:" + MODIFIER + " | AddType$ Dragon",
                MODIFIER + " | AddPower$ 0 | AddToughness$ 0");
        for (int index = 0; index < definitions.size(); index++) {
            var capability = AttachmentCapabilities.export(paper("Unsupported fixture " + index, definitions.get(index), "Pump").getRules().getMainPart());
            unknown(bodyFromEnvelope(capability));
            assertEquals(map(capability.get("coverage")).get("status"), "partial");
            assertFalse(((List<?>) map(capability.get("coverage")).get("unsupportedFeatures")).isEmpty());
        }
    }

    @Test(timeOut = 90000)
    public void concealedChangedAndNonAttachSourcesCannotReuseAProvenDefinition() throws Exception {
        try (var fixture = new Fixture()) {
            var aura = fixture.add("Rancor", ZoneType.Hand, 0); var ability = fixture.attach(aura);
            assertEquals(bodyFromEnvelope(AttachmentCapabilities.exportCurrent(ability, fixture.actor())).get("knowledge"), "Known");
            unknown(bodyFromEnvelope(AttachmentCapabilities.exportCurrent(ability, fixture.opponent())));
            aura.addChangedTextTypeWord("Creature", "Goblin", fixture.table.game.getNextTimestamp(), 999);
            unknown(bodyFromEnvelope(AttachmentCapabilities.exportCurrent(ability, fixture.actor())));
            aura.removeChangedTextTypeWord(fixture.table.game.getTimestamp(), 999);
            var hidden = fixture.add("Rancor", ZoneType.Hand, 0); var hiddenAbility = fixture.attach(hidden);
            hidden.turnFaceDown(true);
            unknown(bodyFromEnvelope(AttachmentCapabilities.exportCurrent(hiddenAbility, fixture.actor())));
            var spell = fixture.add("Giant Growth", ZoneType.Hand, 0).getSpellAbilities().get(0);
            spell.setActivatingPlayer(fixture.actor()); assertNull(AttachmentCapabilities.describe(spell, fixture.actor()));
        }
    }

    @Test(timeOut = 90000)
    public void exportsActualSourceDefinitionsForPortableConformance() throws Exception {
        try (var fixture = new Fixture()) {
            var directory = Path.of("target", "attachment-capabilities"); Files.createDirectories(directory);
            var json = new GsonBuilder().serializeNulls().setPrettyPrinting().disableHtmlEscaping().create();
            var selected = new LinkedHashMap<String, String>(); selected.put("rancor", "Rancor"); selected.put("dead-weight", "Dead Weight");
            selected.put("mixed-unsupported", "Ethereal Armor"); selected.put("conditional-unsupported", "Zealot's Conviction");
            for (var entry : selected.entrySet()) {
                var printing = StaticData.instance().getCommonCards().getCard(entry.getValue()); assertNotNull(printing);
                var capability = AttachmentCapabilities.export(printing.getRules().getMainPart());
                assertEquals(map(capability.get("source")).get("definitionFingerprint").toString().length(), 64);
                assertEquals(bodyFromEnvelope(capability).get("knowledge"), entry.getKey().endsWith("unsupported") ? "Unknown" : "Known");
                Files.writeString(directory.resolve(entry.getKey() + ".json"), json.toJson(capability) + "\n");
                String source = entry.getValue().equals("Zealot's Conviction") ? "z/zealots_conviction.txt"
                        : entry.getValue().equals("Ethereal Armor") ? "e/ethereal_armor.txt"
                        : entry.getValue().equals("Dead Weight") ? "d/dead_weight.txt" : "r/rancor.txt";
                Files.writeString(directory.resolve(entry.getKey() + ".source.txt"), Files.readString(Path.of("../forge-gui/res/cardsfolder").resolve(source)));
            }
        }
    }

    static PaperCard paper(String name, String modifier, String hint) {
        return new PaperCard(new CardRules.Reader().readCard(Arrays.asList(("Name:" + name + "\nManaCost:G\nTypes:Enchantment Aura\nK:Enchant:Creature\nSVar:AttachAILogic:"
                + hint + "\nS:" + modifier + "\nOracle:Fixture wording.").split("\n"))), "TST", CardRarity.Common);
    }
    private static void unknown(Map<String, Object> value) {
        assertEquals(value.get("knowledge"), "Unknown"); assertNull(value.get("powerDelta")); assertNull(value.get("toughnessDelta"));
        assertEquals(value.get("grantedKeywords"), List.of());
    }
    @SuppressWarnings("unchecked") static Map<String, Object> map(Object value) { return (Map<String, Object>) value; }
    @SuppressWarnings("unchecked") static List<Map<String, Object>> maps(Object value) { return (List<Map<String, Object>>) value; }
    private static List<Map<String, Object>> nodes(Map<String, Object> graph) { return maps(graph.get("operations")); }
    private static Map<String, Object> body(Map<String, Object> node) { return map(node.get("attachment")); }
    private static Map<String, Object> bodyFromEnvelope(Map<String, Object> envelope) { return map(envelope.get("attachment")); }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Actor"),
                    MatchTable.human(new Deck(), "Constructed", "Opponent")), resources, Files.createTempDirectory("attachment-projection"));
            HeadlessPlatform.activate(table);
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Player opponent() { return table.game.getRegisteredPlayers().get(1); }
        Card add(String name, ZoneType zone, int seat) { return add(StaticData.instance().getCommonCards().getCard(name), zone, seat); }
        Card add(PaperCard paper, ZoneType zone, int seat) {
            var card = Card.fromPaperCard(paper, table.game.getRegisteredPlayers().get(seat)); card.getOwner().getZone(zone).add(card); return card;
        }
        SpellAbility attach(Card card) {
            var ability = card.getSpellAbilities().stream().filter(value -> value.getApi() == ApiType.Attach).findFirst().orElseThrow();
            ability.setActivatingPlayer(actor()); return ability;
        }
        Map<String, Object> graph(SpellAbility ability) { return table.game.getAction().readState(() -> ActionOperationProjection.describe(ability, actor(), table.identities::id)); }
        public void close() { table.close(); }
    }
}
