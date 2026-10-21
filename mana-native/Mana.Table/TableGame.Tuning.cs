using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void TestAnalysisFinding(TuningQuestion question)
    {
        if (!AnalysisCurrent || !WorkshopCurrent || !DeckTuning.Matches(question, workshop)
            || selectedDeckDetails is not { } deck || WorkshopEngine is not { } service) return;
        long metadataRevision = workshop!.MetadataRevision;
        Work(async () => {
            var saved = await service.UpdateWorkshopAsync(deck.Id, deck.Revision,
                new(MetadataRevision: metadataRevision, Question: question.Question));
            updates.Enqueue(() => {
                if (selectedDeckDetails?.Id != deck.Id || selectedDeckDetails.Revision != deck.Revision) return;
                workshop = saved; workshopGeneration++;
                ShowWorkshopTab("test");
                message = "Test question saved. Hand samples and games keep it with the exact deck version you test.";
            });
        }, "Preparing this experiment", "Keeping the review question with this version of your deck.", false);
    }
}
