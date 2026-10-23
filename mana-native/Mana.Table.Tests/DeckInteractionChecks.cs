using Mana.Contracts;
using Mana.Table.Interaction;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class DeckInteractionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var state = new DeckInteractionState();
        var first = new Card { Name = "Forest" };
        var names = new[] { "Forest", "Earthbending Student" };
        state.SelectDetail(new("Landfall relationship", "Evidence", names, Missing: ["Felidar Retreat"], Index: 4,
            Payoffs: ["Earthbending Student"]), "deck:7:metadata2:page0");
        state.ToggleReading(first, "analysis:Forest", "deck:7:relationship4");
        state.ShowAdvanced("Long evidence", "deck:7:metadata2:page0");
        state.PrepareEvidence(new Rectangle(532, 340, 552, 400), 1200);
        state.ScrollEvidence(-5);
        check(state.Advanced && state.EvidenceScroll == 180 && state.Reading?.Card == first,
            "Opening and scrolling advanced evidence preserves the selected relationship and its separate reader");
        state.CloseAdvanced();
        check(state.Detail.Index == 4 && state.EvidenceScroll == 0 && state.Detail.CardNames.SequenceEqual(names),
            "Returning from evidence restores the same relationship with its source identities");
        names[0] = "Changed caller buffer";
        check(state.Detail.CardNames[0] == "Forest", "A relationship keeps its captured card names when a caller reuses its construction buffer");
        bool cardsProtected = RejectMutation(() => ((IList<string>)state.Detail.CardNames)[0] = "Changed consumer card");
        bool missingProtected = RejectMutation(() => ((IList<string>)state.Detail.MissingNames)[0] = "Changed consumer dependency");
        bool payoffsProtected = RejectMutation(() => ((IList<string>)state.Detail.PayoffNames)[0] = "Changed consumer payoff");
        check(cardsProtected && missingProtected && payoffsProtected && state.Detail.CardNames[0] == "Forest"
            && state.Detail.MissingNames[0] == "Felidar Retreat" && state.Detail.PayoffNames[0] == "Earthbending Student",
            "Relationship cards, missing dependencies and payoffs cannot be rewritten through consumer collection casts");
        state.ShowRelated();
        check(state.RelatedCards.SetEquals(["Forest", "Earthbending Student"]),
            "Editing related cards uses the selected relationship without inventing another deck filter");
        var publishedRelated = state.RelatedCards;
        check(RejectMutation(() => ((ISet<string>)publishedRelated).Add("Changed consumer filter")) && state.RelatedCards.Count == 2,
            "The published related-card filter cannot mutate the interaction owner's backing set");
        state.ClearRelated();
        check(state.RelatedCards.Count == 0 && publishedRelated.SetEquals(["Forest", "Earthbending Student"]),
            "Changing the current related-card filter preserves an earlier published selection snapshot");
        state.SelectTab(2); state.PageAnalysis(10); state.ClampAnalysisPage(2);
        check(state.AnalysisTab == 2 && state.AnalysisPage == 1 && state.NeedsSelection("deck:7:metadata2:page1", [9, 10]),
            "Changing the list page requires a current visible relationship before another frame is emitted");
        state.SelectDetail(new("Another swap", Index: 9), "deck:7:metadata2:page1");
        check(state.Reading == null && !state.NeedsSelection("deck:7:metadata2:page1", [9, 10])
            && state.NeedsSelection("deck:7:metadata3:page1", [9, 10]),
            "A changed report invalidates selected detail authority even when its row index remains visible");
        state.ToggleReading(first, "practice:test1:slot0", "deck:7:sample1");
        state.ReconcileReading("deck:7:sample2");
        check(state.Reading == null, "A new hand sample revokes the preceding sample's full-card reader");
        state.ToggleReading(first, "practice:test2:slot0", "deck:7:sample2");
        state.ToggleReading(first, "practice:test2:slot0", "deck:7:sample2");
        check(state.Reading == null, "Clicking an already read card closes it without moving the underlying sample");
        state.Reset();
        check(state.AnalysisTab == 0 && state.AnalysisPage == 0 && state.RelatedCards.Count == 0
            && state.Detail.Index == -1 && state.Reading == null,
            "Selecting a different saved document clears relationship navigation and retained readers together");
    }

    private static bool RejectMutation(Action mutation)
    {
        try { mutation(); return false; } catch (NotSupportedException) { return true; }
    }
}
