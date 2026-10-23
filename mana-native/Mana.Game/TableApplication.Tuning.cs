using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void TestAnalysisFinding(TuningQuestion question)
    {
        if (!AnalysisCurrent || !WorkshopCurrent || !DeckTuning.Matches(question, deckWorkspace.View.Workshop)
            || selectedDeckDetails is not { } deck || WorkshopEngine is not { } service) return;
        long metadataRevision = deckWorkspace.View.Workshop!.MetadataRevision;
        var stamp = deckWorkspace.Capture();
        Work(async () => {
            var saved = await service.UpdateWorkshopAsync(deck.Id, deck.Revision,
                new(MetadataRevision: metadataRevision, Question: question.Question));
            updates.Enqueue(() => {
                if (!deckWorkspace.AcceptWorkshop(stamp, saved)) return;
                ShowWorkshopTab("test");
                message = "Test question saved. Hand samples and games keep it with the exact deck version you test.";
            });
        }, "Preparing this experiment", "Keeping the review question with this version of your deck.", false);
    }
}
