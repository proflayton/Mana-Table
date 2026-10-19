package forge.api;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.regex.Pattern;

/** Explainable, read-only analysis. Strategic estimates are never deck validation or measured win rates. */
public final class DeckAnalysis {
    private DeckAnalysis() { }
    public static final List<String> STRATEGIES = List.of("unspecified", "aggro", "midrange", "control", "combo", "landfall", "artifact-recursion");
    public static final List<String> PACES = List.of("relaxed", "balanced", "fast");
    public static final List<String> FORMATS = List.of("Constructed", "Commander", "Limited", "Standard", "Pioneer", "Modern", "Legacy", "Vintage", "Pauper");
    public record Profile(String rulesFormat, String strategy, String plan, String backup, String pace, int targetTurn,
                          List<String> protectedCards, List<String> pool) {
        public Profile {
            rulesFormat = checked(rulesFormat, 40);
            if (!rulesFormat.isEmpty() && !FORMATS.contains(rulesFormat)) throw new IllegalArgumentException("Unknown analysis format");
            if (strategy == null || !STRATEGIES.contains(strategy)) throw new IllegalArgumentException("Choose a supported strategy");
            if (pace == null || !PACES.contains(pace)) throw new IllegalArgumentException("Choose a supported pace");
            if (targetTurn < 0 || targetTurn > 30) throw new IllegalArgumentException("Target turn must be 0..30");
            plan = checked(plan, 2000); backup = checked(backup, 1000);
            protectedCards = names(protectedCards); pool = names(pool);
        }
        public static Profile defaults() { return new Profile("", "unspecified", "", "", "balanced", 0, List.of(), List.of()); }
    }
    public record FormatProfile(String name, int minimumMain, boolean exactSize, int copyLimit, int sideboardLimit,
                                boolean commanderIdentity, boolean poolRequired) { }
    public record Function(String role, String event, String provides, List<String> requires, String confidence) { }
    public record CardReview(String cardId, String name, int quantity, String section, int manaValue,
                             List<Function> functions, List<String> engineIds, boolean protectedCard) { }
    public record Engine(String id, String name, List<String> cards, List<String> payoffs, List<String> missing, List<String> steps,
                         List<String> outputs, List<String> requirements, String confidence) { }
    public record Finding(String id, String dimension, String severity, String title, String evidence,
                          List<String> cards, String confidence) { }
    public record Metrics(int mainCards, int lands, int creatures, int expensiveSpells, int earlyRamp,
                          int unconditionalDraw, int conditionalDraw, int interaction, int[] curve,
                          int cardsSeen, int landGoal, Double landProbability, String probabilityAssumptions) { }
    public record Recommendation(String id, String findingId, String cutCardId, String cutName,
                                 CardCatalog.CardInfo add, String benefit, String tradeoff,
                                 Map<String, Integer> roleDelta, int manaValueDelta, String eligibility, String confidence) { }
    public record Report(String deckId, long revision, String fingerprint, String versionId, Profile profile, FormatProfile format,
                         Metrics metrics, List<CardReview> cards, List<Engine> engines, List<Finding> findings,
                         List<Recommendation> recommendations, List<String> limitations) { }
    /** Catalog legality is supplied by the adapter; tests and offline callers may have no rules dataset. */
    public interface Eligibility { String check(CardCatalog.CardInfo card, String format); }
    private static final Pattern DRAW = Pattern.compile("\\bdraws? (?:(?:a|an|one|two|three|four|five|six|seven|that many|x|[0-9]+)(?: additional)? cards?\\b|cards\\b)");
    private static final Pattern EARTHBEND_ETB = Pattern.compile("when (?:this creature|[a-z ,'-]+) enters, earthbend [0-9]+\\.");
    private static final String TOPH = "Toph, the First Metalbender", ASHAYA = "Ashaya, Soul of the Wild";

    public static FormatProfile format(String name, int commanders) {
        return switch (name) {
            case "Commander" -> new FormatProfile(name, 100 - commanders, true, 1, 0, true, false);
            case "Limited" -> new FormatProfile(name, 40, false, 0, 0, false, true);
            case "Vintage" -> new FormatProfile(name, 60, false, 4, 15, false, false);
            default -> new FormatProfile(name, 60, false, 4, 15, false, false);
        };
    }

    public static List<Function> functions(CardCatalog.CardInfo card) {
        var result = new ArrayList<Function>();
        String text = card.oracleText().toLowerCase(Locale.ROOT), type = card.type();
        if (type.contains("Land")) result.add(fn("Lands", "play", "A land drop; colored and tapped requirements need separate consideration."));
        if (!type.contains("Land") && (text.contains("add {") || text.contains("add one mana")
                || text.contains("search your library") && text.contains("land") && text.contains("onto the battlefield")))
            result.add(new Function("Ramp", text.contains("{t}") ? "activate" : "resolve", "Mana production or additional lands",
                    text.contains("{t}") ? List.of("Untapped permanent; creature tap abilities also need haste or a turn in play.") : List.of(), "text-estimate"));
        if (text.contains("additional land")) result.add(new Function("Ramp", "play", "An additional land play", List.of("A land available to play; this effect does not supply the card."), "text-estimate"));
        if (ownDrawEffect(text)) {
            String event = text.contains("whenever you cast") ? "cast" : text.contains("opponent casts") || text.contains("opponent cast") ? "opponent-cast" : text.contains("enters") || text.contains("enter the battlefield") ? "enter"
                    : text.contains("dies") || text.contains("graveyard from the battlefield") ? "die" : text.contains("combat damage") ? "combat"
                    : text.contains("at the beginning") ? "scheduled" : text.contains(":") ? "activate" : text.contains("whenever") || text.startsWith("when ") ? "trigger" : "resolve";
            var requires = new ArrayList<String>();
            if (!event.equals("resolve")) requires.add("Needs its " + event + " event; entering without casting does not trigger a cast ability.");
            if (text.contains("you may pay")) requires.add("Additional mana payment");
            if (text.contains("unless") && text.contains("pay")) requires.add("Another player may prevent the draw by paying.");
            if (text.contains("discard")) requires.add("Discard cost or selection; raw draws are not necessarily net card advantage.");
            if (text.contains("draw cards equal") || text.contains("draw x cards")) requires.add("Card count depends on the specified variable or board condition.");
            result.add(new Function("Draw", event, "Card access", List.copyOf(requires), "text-estimate"));
        }
        if (DeckInsights.roles(card).contains("Interaction")) result.add(new Function("Interaction", type.contains("Instant") ? "instant" : "resolve",
                "Answers or disrupts a relevant target", List.of("A legal, relevant target; removal coverage is not interchangeable."), "text-estimate"));
        if (text.contains("search your library") && !text.contains("basic land")) result.add(fn("Tutor", "search", "Access to a subset of cards, subject to the search restriction."));
        int sacrifice = text.indexOf("sacrifice a creature:");
        if (sacrifice < 0) sacrifice = text.indexOf("sacrifice a land:");
        if (sacrifice >= 0) {
            int start = Math.max(text.lastIndexOf('.', sacrifice), text.lastIndexOf('\n', sacrifice)) + 1;
            String cost = text.substring(start, text.indexOf(':', sacrifice)).strip();
            boolean free = cost.equals("sacrifice a creature") || cost.equals("sacrifice a land");
            result.add(new Function("Sacrifice", "activate", free ? "Sacrifice outlet with no mana or tap cost." : "Sacrifice outlet with additional activation costs.",
                    free ? List.of("Any printed timing or activation restrictions still apply.") : List.of("Pay the complete printed activation cost: " + cost, "Any printed timing or activation restrictions still apply."), "text-estimate"));
        }
        if (text.contains("earthbend")) result.add(new Function("Enabler", "earthbend", "Animates a land and arms one return after death or exile",
                List.of("A land on the battlefield", "Returning creates a new object; repeat loops must renew earthbend."), "text-estimate"));
        if (text.contains("landfall") || text.contains("whenever a land")) result.add(fn("Payoff", "land-enter", "Rewards lands entering; becoming a land in place is not an entry."));
        if (text.contains("+1/+1 counter")) result.add(fn("Counters", "counter", "Places or rewards +1/+1 counters."));
        if (text.contains("indestructible") || text.contains("hexproof") || text.contains("shroud")) result.add(fn("Protection", "effect", "Protection subject to its duration and scope."));
        // An adventure/transform face is an option, not a second card or guaranteed early resource.
        if (card.otherFace() != null) result.add(new Function("Alternate face", "choice", card.otherFace().name(), List.of("Check the casting, transform, or adventure requirements separately."), "text-estimate"));
        return List.copyOf(result);
    }
    private static Function fn(String role, String event, String provides) { return new Function(role, event, provides, List.of(), "text-estimate"); }
    private static boolean ownDrawEffect(String text) {
        var match = DRAW.matcher(text);
        while (match.find()) {
            int boundary = Math.max(text.lastIndexOf('.', match.start()), Math.max(text.lastIndexOf('\n', match.start()), text.lastIndexOf(':', match.start())));
            String before = text.substring(boundary + 1, match.start()).strip();
            // A draw mentioned only as a trigger condition is not a draw provided by the card.
            if ((before.startsWith("whenever ") || before.startsWith("when ") || before.startsWith("if ")) && !before.contains(",")) continue;
            if (before.matches("(?s).*(?:opponent|opponents|they|that player|its controller|enchanted player)\\s*$") && !before.contains("you and")) continue;
            return true;
        }
        return false;
    }

    public static Report analyze(CardCatalog catalog, DeckEditor.Snapshot deck, String deckFormat, String deckId,
                                 String fingerprint, Profile profile, Eligibility eligibility) {
        String rulesFormat = profile.rulesFormat().isEmpty() ? deckFormat : profile.rulesFormat();
        // Analysis cannot silently reinterpret a Commander deck as a 60-card constructed deck.
        if ((List.of("Commander", "Limited").contains(deckFormat) || List.of("Commander", "Limited").contains(rulesFormat)) && !rulesFormat.equals(deckFormat))
            throw new IllegalArgumentException("Analysis format must match this deck's Commander or Limited structure");
        int commanders = count(deck, "Commander");
        var format = format(rulesFormat, commanders);
        var active = deck.entries().stream().filter(e -> e.section().equals("Main") || format.commanderIdentity() && e.section().equals("Commander")).toList();
        var byName = new LinkedHashMap<String, CardCatalog.CardInfo>();
        active.forEach(e -> byName.put(e.card().name(), e.card()));
        var engines = engines(byName);
        Set<String> protectedNames = new TreeSet<>(String.CASE_INSENSITIVE_ORDER);
        protectedNames.addAll(profile.protectedCards());
        engines.stream().filter(e -> e.missing().isEmpty()).forEach(e -> protectedNames.addAll(e.cards()));
        var reviews = deck.entries().stream().map(e -> new CardReview(e.card().id(), e.card().name(), e.quantity(), e.section(), e.card().manaValue(),
                functions(e.card()), engines.stream().filter(engine -> engine.cards().contains(e.card().name()) || engine.payoffs().contains(e.card().name())).map(Engine::id).toList(),
                protectedNames.contains(e.card().name()))).toList();
        var main = reviews.stream().filter(c -> c.section().equals("Main")).toList();
        int size = count(deck, "Main"), lands = sum(main, c -> c.functions().stream().anyMatch(f -> f.role().equals("Lands")));
        int creatures = active.stream().filter(e -> e.section().equals("Main") && e.card().type().contains("Creature")).mapToInt(DeckEditor.Entry::quantity).sum();
        int expensive = sum(main, c -> !has(c, "Lands") && c.manaValue() >= 5);
        int earlyRamp = sum(main, c -> has(c, "Ramp") && c.manaValue() <= 2);
        int unconditional = sum(main, c -> c.functions().stream().anyMatch(f -> f.role().equals("Draw") && f.event().equals("resolve") && f.requires().isEmpty()));
        int conditional = sum(main, c -> has(c, "Draw")) - unconditional;
        int interaction = sum(main, c -> has(c, "Interaction"));
        int[] curve = new int[8];
        main.stream().filter(c -> !has(c, "Lands")).forEach(c -> curve[Math.min(7, c.manaValue())] += c.quantity());
        int target = profile.targetTurn() > 0 ? profile.targetTurn() : format.commanderIdentity()
                ? active.stream().filter(e -> e.section().equals("Commander")).mapToInt(e -> e.card().manaValue()).max().orElse(0) : 0;
        int seen = target == 0 ? 0 : Math.min(size, 7 + Math.max(0, target - (format.commanderIdentity() ? 0 : 1)));
        Double probability = target == 0 || size < format.minimumMain() ? null : atLeast(size, lands, seen, target);
        var metrics = new Metrics(size, lands, creatures, expensive, earlyRamp, unconditional, conditional, interaction, curve, seen, target,
                probability, (format.commanderIdentity() ? "Seven-card keep, one draw on every turn including turn one in multiplayer Commander" : "Seven-card keep, on the play, one draw on turns 2 onward")
                        + "; no mulligans, ramp, selection, color, or tapped-land modeling. This is a land-count probability, not castability or win probability.");
        var findings = new ArrayList<Finding>();
        if (profile.plan().isBlank() && profile.strategy().equals("unspecified")) findings.add(finding("choose-plan", "Plan", "info", "Describe what this deck is trying to do", "Choose a strategy and a primary plan to give strategic recommendations context.", List.of()));
        if (size < format.minimumMain()) findings.add(finding("incomplete", "Structure", "info", "This list is still being built", "Main deck has " + size + " cards; this profile needs " + format.minimumMain() + ". Counts are shown without diagnosing shortages in an unfinished list.", List.of()));
        if (probability != null && probability < .65) findings.add(finding("land-consistency", "Mana", "review", "Check access to early land drops",
                lands + " lands give approximately " + Math.round(probability * 100) + "% to see " + target + " lands in " + seen + " cards under the displayed assumptions. Review ramp and colored sources before changing the land count.", List.of()));
        if (size >= format.minimumMain() && profile.pace().equals("fast") && expensive > size / 6) findings.add(finding("expensive-support", "Pace", "review", "Review the cost of setup and support",
                expensive + " nonland cards cost five or more mana. Your chosen pace is fast; inspect which contribute immediately or have a real alternate casting route.", main.stream().filter(c -> !has(c, "Lands") && c.manaValue() >= 5 && !c.protectedCard()).map(CardReview::name).toList()));
        if (conditional > 0 && unconditional == 0 && size >= format.minimumMain()) findings.add(finding("conditional-draw", "Resources", "review", "All detected card access needs setup",
                conditional + " draw cards require an event, activation, or discard. Check how the deck finds pieces before its engine is working; these are text estimates.", main.stream().filter(c -> has(c, "Draw")).map(CardReview::name).toList()));
        if (format.poolRequired() && profile.pool().isEmpty()) findings.add(finding("missing-pool", "Pool", "info", "Add your draft or sealed pool", "Limited recommendations must come from your available pool. No catalog additions are recommended without it.", List.of()));
        if (eligibility != null) for (var entry : deck.entries()) {
            if (!List.of("Main", "Sideboard", "Commander").contains(entry.section())) continue;
            String status = eligibility.check(entry.card(), rulesFormat);
            if (status.equals("ineligible")) findings.add(finding("format:" + entry.card().id(), "Format", "review", "Check " + entry.card().name() + " in " + rulesFormat,
                    "This card is excluded by the bundled format definition. These definitions are an offline snapshot, not a live tournament ruling.", List.of(entry.card().name())));
            if (status.equals("restricted") && deck.entries().stream().filter(e -> e.card().name().equals(entry.card().name()) && List.of("Main", "Sideboard").contains(e.section())).mapToInt(DeckEditor.Entry::quantity).sum() > 1)
                findings.add(finding("restricted:" + entry.card().name(), "Format", "review", "Restricted card has multiple copies", entry.card().name() + " is restricted to one copy by the bundled rules.", List.of(entry.card().name())));
        }
        if (byName.containsKey(TOPH)) {
            findings.add(finding("toph-zones", "Rules", "info", "Artifact lands exist on the battlefield", "Toph does not make artifact cards in your library or graveyard into lands. Crop Rotation and land-play permissions cannot use that conversion outside the battlefield.", List.of(TOPH)));
            if (byName.containsKey("Lotus Petal") || byName.containsKey("Pyrite Spellbomb")) findings.add(finding("earthbend-renewal", "Rules", "info", "A returning artifact needs another earthbend", "Earthbend's delayed return belongs to the old object. Returning Lotus Petal or a Spellbomb does not automatically rearm a sacrifice loop.", List.of(TOPH)));
            if (byName.containsKey("Cloudstone Curio")) findings.add(finding("curio-artifacts", "Rules", "info", "Artifact land entries do not trigger Curio", "Being a land does not remove the artifact type. A nonartifact land can trigger Curio and return an artifact land that shares the land permanent type.", List.of(TOPH, "Cloudstone Curio")));
            if (byName.containsKey("Panharmonicon")) findings.add(finding("panharmonicon-landfall", "Rules", "info", "Panharmonicon can double artifact landfall", "With Toph, nontoken artifacts enter as lands. Panharmonicon doubles triggered abilities caused by those artifact entries, including landfall; it does not double Toph's end-step trigger.", List.of(TOPH, "Panharmonicon")));
        }
        if (byName.containsKey("Cloudstone Curio") && byName.containsKey("Mycosynth Lattice")) findings.add(finding("curio-lattice", "Dependencies", "review", "Lattice switches off Curio", "Cloudstone Curio triggers only for nonartifact entries. Mycosynth Lattice makes every entering permanent an artifact.", List.of("Cloudstone Curio", "Mycosynth Lattice")));
        if (byName.containsKey("Scourglass") && byName.containsKey("Mycosynth Lattice")) findings.add(finding("scourglass-lattice", "Dependencies", "review", "Lattice leaves Scourglass nothing to destroy", "Scourglass preserves artifacts and lands; Lattice makes all permanents artifacts.", List.of("Scourglass", "Mycosynth Lattice")));
        if (byName.containsKey("Lifecrafter's Bestiary") && (profile.strategy().equals("artifact-recursion") || byName.containsKey(TOPH))) findings.add(finding("bestiary-casts", "Resources", "review", "Bestiary does not draw from returns", "Its draw ability requires casting a creature and paying green mana. Sacrificing and returning a creature bypasses that event.", List.of("Lifecrafter's Bestiary")));
        var recommendations = recommend(catalog, deck, format, profile, reviews, findings, eligibility);
        return new Report(deckId, deck.revision(), fingerprint, fingerprint, profile, format, metrics, reviews, engines, List.copyOf(findings), recommendations,
                List.of("Functions and strategic findings are estimates; no overall power score or automatic bracket assignment.",
                        "Engine coverage is a small curated registry with explicit prerequisites, not exhaustive combo discovery or a simulator proof.",
                        "Card prices, matchup statistics, and playtest outcomes are not inferred. Format eligibility uses bundled rules when available and reports unknown otherwise.",
                        "Limited pool entries are names only; available quantities are not modeled. Card-function estimates are separate from your editable building categories."));
    }

    private record LoopOutlet(String name, String output) { }
    static List<Engine> engines(Map<String, CardCatalog.CardInfo> cards) {
        var result = new ArrayList<Engine>();
        var earthbenders = cards.values().stream().filter(c -> c.type().contains("Creature")
                && EARTHBEND_ETB.matcher(c.oracleText().toLowerCase(Locale.ROOT)).find()).toList();
        boolean ashaya = cards.containsKey(ASHAYA), metal = cards.containsKey(TOPH) && cards.containsKey("Mycosynth Lattice");
        for (var bender : earthbenders) for (var outlet : List.of(new LoopOutlet("Phyrexian Altar", "Arbitrarily much mana of any color"),
                new LoopOutlet("Ashnod's Altar", "Arbitrarily much colorless mana"), new LoopOutlet("Zuran Orb", "Arbitrarily much life"),
                new LoopOutlet("Goblin Bombardment", "Arbitrarily much damage to legal targets"))) {
            if (!cards.containsKey(outlet.name()) && !ashaya && !metal) continue;
            var needed = new ArrayList<String>();
            if (ashaya || !metal) needed.add(ASHAYA); else needed.addAll(List.of(TOPH, "Mycosynth Lattice"));
            needed.add(bender.name()); needed.add(outlet.name());
            var payoffs = new ArrayList<String>();
            var outputs = new ArrayList<>(List.of(outlet.output(), "Repeated creature entries, deaths, and counter placement"));
            if (cards.containsKey("Impact Tremors")) { payoffs.add("Impact Tremors"); outputs.add("Damage to each opponent through Impact Tremors"); }
            if (cards.containsKey("All Will Be One")) { payoffs.add("All Will Be One"); outputs.add("Damage through repeated counter placement"); }
            result.add(new Engine("earthbend:" + bender.name() + ":" + outlet.name(), "Renew earthbend with " + bender.name(), List.copyOf(needed), List.copyOf(payoffs),
                    needed.stream().filter(n -> !cards.containsKey(n)).toList(), List.of("The entering earthbender is a land before its enters trigger is put on the stack.",
                    "Target itself with earthbend; let that ability resolve.", "Sacrifice it to the outlet; resolve earthbend's return.",
                    "The new object's enters ability earthbends itself again, restoring the loop."), List.copyOf(outputs),
                    List.of("All required permanents on the battlefield", "Counter placement allowed; the earthbender survives until sacrifice", "Targets and sacrifice permissions remain legal", "Opponents can respond to triggered abilities"), "curated-rules-line"));
        }
        if (cards.containsKey(ASHAYA) && cards.containsKey("Bumi, Unleashed")) {
            var needed = List.of(ASHAYA, "Bumi, Unleashed", "Silver Shroud Costume");
            result.add(new Engine("bumi-combat", "Repeat combat with Bumi", needed, List.of(), needed.stream().filter(n -> !cards.containsKey(n)).toList(),
                    List.of("Ashaya makes Bumi a land; Bumi can earthbend itself on entry to gain haste.", "Attach Costume to make Bumi unblockable.",
                            "Connecting untaps all lands, including Bumi, and creates another combat in which land creatures may attack."),
                    List.of("Repeated combat damage"), List.of("Bumi can attack and deal combat damage", "Costume attached; legal opponents to attack"), "curated-rules-line"));
        }
        result.sort(Comparator.comparingInt((Engine engine) -> engine.missing().size()).thenComparing(Engine::id));
        return List.copyOf(result);
    }

    private record Swap(String cut, String add, String finding, String benefit, String tradeoff) { }
    private static final List<Swap> TOPH_SWAPS = List.of(
            new Swap("Ajani Resolute", "Goblin Bombardment", "engine-outlet", "Adds a free sacrifice outlet that also converts recurring creatures into damage.", "Removes lifegain and planeswalker payoffs; Bombardment needs creatures."),
            new Swap("Chandra, the Firebrand", "Enlightened Tutor", "engine-access", "Finds an artifact or enchantment engine piece for one mana.", "Puts the card on top, costs a card, and removes spell copying."),
            new Swap("Hydra's Growth", "Sylvan Library", "early-access", "Provides early selection and optional additional cards.", "Removes single-creature counter growth; extra cards may cost life."),
            new Swap("Lifecrafter's Bestiary", "Ichor Wellspring", "bestiary-casts", "Draws on artifact entry and death, including earthbend returns.", "Needs a sacrifice outlet to realize its recurring value; removes upkeep scry."),
            new Swap("Cracked Earth Technique", "Swords to Plowshares", "efficient-interaction", "Adds a one-mana instant answer while repeatable earthbenders supply animation.", "Removes two immediate earthbends and gives the target's controller life."),
            new Swap("Patriar's Seal", "Arcane Signet", "early-ramp", "Accelerates colored mana one turn earlier.", "Removes legendary-creature untapping."),
            new Swap("Conjurer's Closet", "Tiller Engine", "return-untap", "Adds inexpensive redundancy for untapping lands returned by earthbend.", "Removes a repeatable blink; Tiller needs lands to enter tapped."),
            new Swap("Krang, Utrom Warlord", "Finale of Devastation", "flexible-finish", "Finds or recovers a creature and provides an outlet for abundant mana.", "Removes artifact-team evasion and indestructibility; Finale still needs substantial mana for its team buff."));

    private static List<Recommendation> recommend(CardCatalog catalog, DeckEditor.Snapshot deck, FormatProfile format, Profile profile,
                                                  List<CardReview> reviews, List<Finding> findings, Eligibility eligibility) {
        if (format.poolRequired() && profile.pool().isEmpty()) return List.of();
        var result = new ArrayList<Recommendation>();
        boolean toph = reviews.stream().anyMatch(c -> c.name().equals(TOPH) && (c.section().equals("Main") || c.section().equals("Commander") && format.commanderIdentity()));
        var swaps = new ArrayList<Swap>();
        if (toph && !profile.strategy().equals("aggro") && !profile.strategy().equals("control")) swaps.addAll(TOPH_SWAPS);
        // Format-neutral efficiency comparisons; strategic intent and protected engines take precedence.
        swaps.add(new Swap("Divination", "Quick Study", "draw-timing", "The same mana cost and card count with instant-speed timing.", "Sorcery triggers or set restrictions can make Divination preferable."));
        swaps.add(new Swap("Cancel", "Counterspell", "counter-cost", "Counters the same range of spells for one less total mana.", "Still requires two blue mana; legality differs by format."));
        swaps.add(new Swap("Murder", "Go for the Throat", "removal-cost", "Answers a nonartifact creature for one less mana and one fewer black pip.", "Cannot answer artifact creatures; assess that matchup coverage before replacing Murder."));
        swaps.add(new Swap("Naturalize", "Nature's Claim", "removal-tempo", "Answers an artifact or enchantment for one mana.", "Its controller gains four life, which can matter for an aggressive plan."));
        if (profile.pace().equals("fast")) swaps.add(new Swap("Manalith", "Mind Stone", "early-ramp", "Accelerates a turn earlier and can later draw a card.", "Loses colored mana production; inspect colored costs before accepting."));
        Integer identity = DeckInsights.identity(deck, format.commanderIdentity() ? "Commander" : "Constructed");
        for (var swap : swaps) {
            var cut = reviews.stream().filter(c -> c.section().equals("Main") && c.name().equals(swap.cut()) && !c.protectedCard()).findFirst().orElse(null);
            if (cut == null) continue;
            var add = catalog.named(swap.add());
            if (add == null || !add.deckSection().equals("Main")) continue;
            if (format.poolRequired() && profile.pool().stream().noneMatch(name -> name.equalsIgnoreCase(add.name()))) continue;
            if (format.commanderIdentity() && (identity == null || (add.colorIdentity() & ~identity) != 0)) continue;
            int copies = deck.entries().stream().filter(e -> (e.section().equals("Main") || e.section().equals("Sideboard") || format.commanderIdentity() && e.section().equals("Commander")) && e.card().name().equals(add.name())).mapToInt(DeckEditor.Entry::quantity).sum();
            if (format.copyLimit() > 0 && copies >= format.copyLimit()) continue;
            String legal = eligibility == null ? "unknown" : eligibility.check(add, format.name());
            if (legal.equals("ineligible")) continue;
            if (legal.equals("restricted") && copies > 0) continue;
            var delta = new LinkedHashMap<String, Integer>();
            cut.functions().stream().map(Function::role).distinct().forEach(role -> delta.merge(role, -1, Integer::sum));
            functions(add).stream().map(Function::role).distinct().forEach(role -> delta.merge(role, 1, Integer::sum));
            delta.values().removeIf(value -> value == 0);
            String id = "swap:" + swap.cut() + ":" + swap.add();
            result.add(new Recommendation(id, swap.finding(), cut.cardId(), cut.name(), add, swap.benefit(), swap.tradeoff(),
                    Map.copyOf(delta), add.manaValue() - cut.manaValue(), legal, "strategic-estimate"));
            if (findings.stream().noneMatch(f -> f.id().equals(swap.finding()))) findings.add(finding(swap.finding(), "Card fit", "review", "Review " + cut.name(), swap.benefit(), List.of(cut.name())));
        }
        result.sort(Comparator.comparing(Recommendation::id));
        return List.copyOf(result);
    }

    public static double atLeast(int population, int successes, int draws, int required) {
        if (population < 0 || successes < 0 || successes > population || draws < 0 || draws > population) throw new IllegalArgumentException("Invalid probability inputs");
        if (required <= 0) return 1;
        if (required > draws || required > successes) return 0;
        double total = 0;
        for (int k = required; k <= Math.min(successes, draws); k++) if (draws - k <= population - successes)
            total += Math.exp(logChoose(successes, k) + logChoose(population - successes, draws - k) - logChoose(population, draws));
        return Math.min(1, total);
    }
    private static double logChoose(int n, int k) { double value = 0; for (int i = 1; i <= Math.min(k, n - k); i++) value += Math.log(n - i + 1) - Math.log(i); return value; }
    private static Finding finding(String id, String dimension, String severity, String title, String evidence, List<String> cards) {
        return new Finding(id, dimension, severity, title, evidence, cards, dimension.equals("Rules") || dimension.equals("Dependencies") ? "curated-rules-line" : "strategic-estimate");
    }
    private static boolean has(CardReview card, String role) { return card.functions().stream().anyMatch(f -> f.role().equals(role)); }
    private static int sum(List<CardReview> cards, java.util.function.Predicate<CardReview> filter) { return cards.stream().filter(filter).mapToInt(CardReview::quantity).sum(); }
    private static int count(DeckEditor.Snapshot deck, String section) { return deck.entries().stream().filter(e -> e.section().equals(section)).mapToInt(DeckEditor.Entry::quantity).sum(); }
    private static String checked(String text, int limit) { if (text == null || text.length() > limit) throw new IllegalArgumentException("Analysis text exceeds its limit"); return text.strip(); }
    private static List<String> names(List<String> names) { if (names == null || names.size() > 2000) throw new IllegalArgumentException("Too many analysis cards"); return names.stream().map(n -> checked(n, 200)).filter(n -> !n.isEmpty()).distinct().toList(); }
}
