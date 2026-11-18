package forge.api;

import forge.card.ICardFace;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.util.FileSection;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.*;

/** A deliberately narrow, structural description of an Aura's direct continuous
 * modifiers. It neither predicts other card abilities nor grants action authority. */
public final class AttachmentCapabilities {
    public static final String EXTRACTOR_VERSION = "forge-attachment/1";
    private static final int LIMIT = 1000, MAX_BLOCKS = 32;
    private static final Set<String> PARAMS = Set.of("Mode", "AffectedDefined", "Affected", "AddPower", "AddToughness", "AddKeyword");
    private static final Set<String> KEYWORDS = Set.of("flying", "trample", "reach", "vigilance", "lifelink", "deathtouch",
            "first strike", "double strike", "indestructible");
    private record Definition(String type, boolean aura, List<String> keywords, List<Map<String, String>> statics) { }
    private record Capability(Definition definition, String knowledge, Integer power, Integer toughness,
                              List<String> keywords, List<String> unsupported) { }

    /** Public catalog input only. The same evaluator consumes live current-face
     * parameter copies after the live caller's disclosure and state checks. */
    public static Map<String, Object> export(ICardFace face) {
        if (face == null) return envelope(unavailable("missing_face"));
        var statics = new ArrayList<Map<String, String>>();
        try {
            for (String value : face.getStaticAbilities())
                statics.add(parameters(FileSection.parseToMap(value, FileSection.DOLLAR_SIGN_KV_SEPARATOR)));
        } catch (RuntimeException malformed) { return envelope(unavailable("malformed_static_definition")); }
        var keywords = new ArrayList<String>(); face.getKeywords().forEach(keywords::add);
        return envelope(evaluate(new Definition(face.getType().toString(), face.getType().isAura() && face.getType().isEnchantment()
                && !face.getType().isCreature() && !face.getType().isArtifact(),
                List.copyOf(keywords), List.copyOf(statics))));
    }

    static Map<String, Object> describe(SpellAbility ability, Player viewer) {
        if (ability == null || ability.getApi() != ApiType.Attach) return null;
        return modifier(current(ability, viewer));
    }

    static Map<String, Object> exportCurrent(SpellAbility ability, Player viewer) {
        return envelope(current(ability, viewer));
    }

    private static Capability current(SpellAbility ability, Player viewer) {
        Card card = ability == null ? null : ability.getHostCard();
        if (ability == null || ability.getApi() != ApiType.Attach || card == null || viewer == null
                || card.isFaceDown() || !card.getView().canBeShownTo(viewer.getView())) return unavailable("undisclosed_source");
        if (ability.getCardState() == null || ability.getCardStateName() != card.getCurrentStateName()
                || !card.getChangedTextColorWords().isEmpty() || !card.getChangedTextTypeWords().isEmpty()
                || !ability.getChangedTextColors().isEmpty() || !ability.getChangedTextTypes().isEmpty())
            return unavailable("changed_source_face");
        var statics = new ArrayList<Map<String, String>>();
        for (var value : card.getStaticAbilities()) {
            if (value.isSuppressed() || !value.isIntrinsic() || value.getCardStateName() != card.getCurrentStateName()
                    || !value.getChangedTextColors().isEmpty() || !value.getChangedTextTypes().isEmpty())
                return unavailable("changed_static_definition");
            statics.add(parameters(value.getMapParams()));
        }
        var keywords = card.getCurrentState().getIntrinsicKeywords().stream().map(value -> value.getOriginal()).toList();
        return evaluate(new Definition(card.getType().toString(), card.getType().isAura() && card.getType().isEnchantment()
                && !card.getType().isCreature() && !card.getType().isArtifact(), keywords, List.copyOf(statics)));
    }

    private static Map<String, String> parameters(Map<String, String> source) {
        var copy = new TreeMap<String, String>();
        // Description is presentation only. Every other parameter, including an
        // unrecognized AI-prefixed key, remains visible to the structural gate.
        source.forEach((key, value) -> { if (!key.equals("Description")) copy.put(key, value); });
        return Collections.unmodifiableMap(copy);
    }

    private static Capability evaluate(Definition definition) {
        var reasons = new TreeSet<String>(); var granted = new TreeSet<String>();
        if (!definition.aura() || !definition.keywords().contains("Enchant:Creature"))
            reasons.add("unsupported_aura_enchant_domain");
        if (definition.statics().isEmpty()) reasons.add("missing_continuous_modifier");
        if (definition.statics().size() > MAX_BLOCKS)
            return new Capability(definition, "Unknown", null, null, List.of(), List.of("modifier_block_limit"));
        int power = 0, toughness = 0;
        for (var block : definition.statics()) {
            if (!block.getOrDefault("Mode", "").equals("Continuous")
                    || !block.getOrDefault("AffectedDefined", "").equals("Enchanted")
                    || !block.getOrDefault("Affected", "").equals("Creature")
                    || !PARAMS.containsAll(block.keySet())) {
                reasons.add("unsupported_continuous_definition"); continue;
            }
            Integer p = literal(block.getOrDefault("AddPower", "0")), t = literal(block.getOrDefault("AddToughness", "0"));
            if (p == null || t == null) reasons.add("nonliteral_or_out_of_bounds_pt");
            else {
                long nextPower = (long) power + p, nextToughness = (long) toughness + t;
                if (Math.abs(nextPower) > LIMIT || Math.abs(nextToughness) > LIMIT) reasons.add("modifier_aggregate_limit");
                else { power = (int) nextPower; toughness = (int) nextToughness; }
            }
            if (block.containsKey("AddKeyword")) {
                for (String keyword : block.get("AddKeyword").split("\\s*&\\s*", -1)) {
                    String canonical = keyword.trim().toLowerCase(Locale.ROOT);
                    if (!KEYWORDS.contains(canonical)) reasons.add("unsupported_granted_keyword");
                    else granted.add(canonical);
                }
            }
        }
        if (power == 0 && toughness == 0 && granted.isEmpty()) reasons.add("empty_modifier");
        boolean known = reasons.isEmpty();
        return new Capability(definition, known ? "Known" : "Unknown", known ? power : null, known ? toughness : null,
                known ? List.copyOf(granted) : List.of(), List.copyOf(reasons));
    }

    private static Integer literal(String value) {
        if (value == null || value.length() > 6 || !value.matches("[+-]?[0-9]+")) return null;
        try { int result = Integer.parseInt(value); return result >= -LIMIT && result <= LIMIT ? result : null; }
        catch (NumberFormatException invalid) { return null; }
    }

    private static Capability unavailable(String reason) {
        return new Capability(new Definition("", false, List.of(), List.of()), "Unknown", null, null, List.of(), List.of(reason));
    }

    private static Map<String, Object> modifier(Capability capability) {
        var result = new LinkedHashMap<String, Object>();
        result.put("knowledge", capability.knowledge()); result.put("powerDelta", capability.power());
        result.put("toughnessDelta", capability.toughness()); result.put("grantedKeywords", capability.keywords());
        return Collections.unmodifiableMap(result);
    }

    private static Map<String, Object> envelope(Capability capability) {
        return Map.of("schemaVersion", "1.0", "kind", "attachment_capability", "scope", "direct_continuous_modifiers",
                "source", Map.of("provider", "forge", "definitionFingerprint", fingerprint(capability.definition()), "extractorVersion", EXTRACTOR_VERSION),
                "coverage", Map.of("status", capability.knowledge().equals("Known") ? "complete_for_declared_features" : "partial",
                        "declaredFeatures", List.of("signed_pt", "keyword_grants"), "unsupportedFeatures", capability.unsupported()),
                "attachment", modifier(capability));
    }

    private static String fingerprint(Definition definition) {
        try {
            var digest = MessageDigest.getInstance("SHA-256");
            hash(digest, EXTRACTOR_VERSION); hash(digest, definition.type()); hash(digest, definition.aura() ? "aura" : "other");
            var keywords = new TreeSet<>(definition.keywords()); hash(digest, String.valueOf(keywords.size()));
            keywords.forEach(value -> hash(digest, value));
            hash(digest, String.valueOf(definition.statics().size()));
            for (var block : definition.statics()) {
                hash(digest, String.valueOf(block.size()));
                block.forEach((key, value) -> { hash(digest, key); hash(digest, value); });
            }
            return HexFormat.of().formatHex(digest.digest());
        } catch (NoSuchAlgorithmException impossible) { throw new IllegalStateException("SHA-256 is unavailable", impossible); }
    }
    private static void hash(MessageDigest digest, String value) {
        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        digest.update(ByteBuffer.allocate(4).putInt(bytes.length).array()); digest.update(bytes);
    }

    private AttachmentCapabilities() { }
}
