package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.deck.DeckRule;
import forge.deck.DeckRuleSize;
import forge.deck.DeckSection;
import forge.item.PaperCard;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Row-level explanations of Forge's existing structural conformance checks. */
final class DeckValidation {
    private DeckValidation() { }

    static List<DeckEditor.Issue> issues(Deck deck, DeckFormat format, String problem) {
        // The existing engine result remains authoritative, including its exceptions.
        if (problem == null) return List.of();
        var issues = new ArrayList<DeckEditor.Issue>();
        if (!Set.of(DeckFormat.Commander, DeckFormat.Constructed, DeckFormat.Limited).contains(format)) {
            return List.of(issue("ENGINE_CONFORMANCE", problem, null, List.of()));
        }
        var commanders = deck.getCommanders();
        boolean commanderSetup = true;
        if (format.hasCommander()) {
            if (commanders.isEmpty()) {
                issues.add(issue("MISSING_COMMANDER", "Choose a commander for this deck.", DeckSection.Commander, List.of()));
                commanderSetup = false;
            }
            if (commanders.size() > 2) {
                issues.add(issue("COMMANDER_COUNT", "Choose one commander or two compatible partners.", DeckSection.Commander, commanders));
                commanderSetup = false;
            }
            for (var card : commanders) {
                if (!format.isLegalCommander(card.getRules())) {
                    issues.add(issue("ILLEGAL_COMMANDER", card.getName() + " cannot be a commander in this format.", DeckSection.Commander, List.of(card)));
                    commanderSetup = false;
                }
            }
            if (commanders.size() == 2 && !commanders.get(0).getRules().canBePartnerCommanders(commanders.get(1).getRules())) {
                issues.add(issue("COMMANDER_PARTNERSHIP", "These commanders are not compatible partners.", DeckSection.Commander, commanders));
                commanderSetup = false;
            }
            if (commanderSetup) {
                // This is the same identity loop used by getDeckConformanceProblem,
                // including wild colors, basic-land handling and active DeckRules.
                for (var violation : format.getColorIdentityViolations(deck)) {
                    var card = violation.card();
                    issues.add(issue("COLOR_IDENTITY", card.getName() + " does not match the commander's color identity.", violation.section(), List.of(card)));
                }
            }
        }
        sizes(deck, format, issues);
        copies(deck, format, issues);
        for (var section : List.of(DeckSection.Attractions, DeckSection.Contraptions)) {
            if (!deck.has(section)) continue;
            String error = section == DeckSection.Attractions
                    ? format.getAttractionDeckConformanceProblem(deck) : format.getContraptionDeckConformanceProblem(deck);
            if (error != null) {
                var cards = new ArrayList<PaperCard>();
                for (var entry : deck.get(section)) cards.add(entry.getKey());
                issues.add(issue("SECTION_CONFORMANCE", error, section, cards));
            }
        }
        if (issues.isEmpty()) issues.add(issue("ENGINE_CONFORMANCE", problem, null, List.of()));
        return List.copyOf(issues);
    }

    private static void sizes(Deck deck, DeckFormat format, List<DeckEditor.Issue> issues) {
        int min = format.getMainRange().getMinimum();
        int max = format.getMainRange().getMaximum();
        var conspiracies = deck.get(DeckSection.Conspiracy);
        if (conspiracies != null) min -= 5 * conspiracies.countByName("Advantageous Proclamation");
        if (format.hasCommander()) {
            for (var commander : deck.getCommanders()) for (var rule : DeckRule.parseAll(commander)) {
                if (rule instanceof DeckRuleSize size && rule.isActiveFor(DeckSection.Commander)) {
                    if (size.removesMaxDeckSize()) max = Integer.MAX_VALUE;
                    else if (max != Integer.MAX_VALUE) max += size.getMaxDelta();
                }
            }
            if (deck.getCommanders().size() == 2) { min--; max--; }
        }
        int count = deck.getMain().countAll();
        if (count < min || count > max) {
            String expected = min == max ? "exactly " + min
                    : count < min ? "at least " + min : "no more than " + max;
            issues.add(issue("MAIN_COUNT", "Main deck needs " + expected + " cards; currently " + count + ".", DeckSection.Main, List.of()));
        }
        var side = deck.get(DeckSection.Sideboard);
        int sideCount = side == null ? 0 : side.countAll();
        var range = format.getSideRange();
        if (range != null && sideCount > 0 && !range.contains(sideCount)) {
            issues.add(issue("SIDEBOARD_COUNT", "Sideboard needs " + range.getMinimum() + " to " + range.getMaximum()
                    + " cards, or none; currently " + sideCount + ".", DeckSection.Sideboard, List.of()));
        }
    }

    private static void copies(Deck deck, DeckFormat format, List<DeckEditor.Issue> issues) {
        var data = StaticData.instance();
        // Legacy conformance can return an early size/leader problem without a
        // database. Do not introduce a later null access for those API consumers.
        if (data == null) return;
        Map<String, List<PaperCard>> printings = new LinkedHashMap<>();
        Map<String, Integer> totals = new LinkedHashMap<>();
        for (var entry : deck.getAllCardsInASinglePool(format.hasCommander(), false)) {
            String name = data.getCommonCards().getNormalizedName(entry.getKey().getName());
            printings.computeIfAbsent(name, ignored -> new ArrayList<>()).add(entry.getKey());
            totals.merge(name, entry.getValue(), Integer::sum);
        }
        for (var group : printings.entrySet()) {
            var card = data.getCommonCards().getCard(group.getKey());
            if (card != null && card.getRules().isCustom() && !format.allowCustomCards()) {
                issues.add(issue("ENGINE_CONFORMANCE", "Enable custom cards in Forge preferences to use " + group.getKey() + ".", null, group.getValue()));
            }
            if (card == null) card = data.getVariantCards().getCard(group.getKey());
            if (card == null) {
                issues.add(issue("ENGINE_CONFORMANCE", "Card is not available in Forge's rules database: " + group.getKey(), null, group.getValue()));
                continue;
            }
            if (DeckFormat.canHaveAnyNumberOf(card)) continue;
            Integer specific = DeckFormat.canHaveSpecificNumberInDeck(card);
            // Forge applies DeckLimit to Main only; ordinary limits include Main,
            // Sideboard and (for commander formats) Commander across all printings.
            int total = specific == null ? totals.get(group.getKey()) : deck.getMain().countByName(group.getKey());
            int limit = specific == null ? format.getMaxCardCopies() : specific;
            if (total > limit) {
                List<PaperCard> cards = group.getValue();
                String section = null;
                if (specific != null) {
                    cards = cards.stream().filter(printing -> deck.getMain().count(printing) > 0).toList();
                    section = DeckSection.Main.name();
                }
                issues.add(new DeckEditor.Issue("CARD_COPY_LIMIT", group.getKey() + " allows at most " + limit
                        + (limit == 1 ? " copy" : " copies") + (specific == null ? " across the deck" : " in Main") + "; currently " + total + ".",
                        section, cards.stream().map(CardCatalog::id).distinct().sorted().toList()));
            }
        }
    }

    private static DeckEditor.Issue issue(String code, String message, DeckSection section, List<PaperCard> cards) {
        return new DeckEditor.Issue(code, message, section == null ? null : section.name(),
                cards.stream().map(CardCatalog::id).distinct().sorted().toList());
    }
}
