package forge.api;

import forge.deck.Deck;
import forge.deck.DeckFormat;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;

/** The complete playable list uses the same offline format rules as proposed additions. */
final class DeckLegality {
    static DeckEditor.Validation validate(CardCatalog catalog, Deck deck, DeckFormat format, String structuralProblem) {
        var issues = new ArrayList<>(DeckValidation.issues(deck, format, structuralProblem));
        var rules = catalog.bundledFormats;
        boolean known = rules != null && rules.available(format.name());
        if (known) {
            var totals = new LinkedHashMap<String, Integer>();
            for (var section : deck) for (var row : section.getValue())
                totals.merge(row.getKey().getName(), row.getValue(), Integer::sum);
            for (var section : deck) for (var row : section.getValue()) {
                var card = CardCatalog.describe(row.getKey());
                String eligibility = rules.check(card, format.name());
                if (eligibility.equals("ineligible") || eligibility.equals("restricted") && totals.get(card.name()) > 1)
                    issues.add(new DeckEditor.Issue("bundled-eligibility", card.name() + (eligibility.equals("restricted")
                            ? " exceeds the one-copy restriction in the bundled " : " is not eligible in the bundled ")
                            + format.name() + " snapshot.", section.getKey().name(), List.of(card.id())));
            }
        }
        boolean failed = issues.stream().anyMatch(i -> i.code().equals("bundled-eligibility"));
        String problem = structuralProblem != null ? structuralProblem : failed
                ? issues.stream().filter(i -> i.code().equals("bundled-eligibility")).findFirst().orElseThrow().message() : null;
        return new DeckEditor.Validation(format.name(), problem == null, problem, issues,
                structuralProblem == null, known ? failed ? "failed" : "bundled-rules" : "unknown");
    }
}
