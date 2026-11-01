using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Extends the existing real commander-payment journey. All values below come
// through the shipping Java history endpoint and native decoder, not a second
// separately authored wire fixture or predicted snapshot differences.
internal static class HistoryEngineJourney
{
    internal static async Task CheckAsync(ForgeEngine engine, GameSnapshot current, string profile, Action<bool, string> check)
    {
        check(engine.SupportsHistory, "The packaged real engine advertises its typed match-history capability");
        var batches = ImmutableArray.CreateBuilder<HistoryBatch>();
        var model = MatchHistoryReadModel.Empty(current.Id, current.ViewerId);
        bool more = true;
        while (more && batches.Count < 256) {
            var batch = await engine.HistoryAsync(current.Id, model.Cursor);
            check(batch.ViewerId == current.ViewerId && batch.GameId == current.Id,
                "A real history chunk belongs to the exact observed game and viewing seat");
            long cursor = model.Cursor;
            model = model.Append(batch, out _); batches.Add(batch);
            more = batch.HasMore;
            if (more && model.Cursor <= cursor) throw new InvalidDataException("Real history failed to advance its bounded scan.");
        }
        check(!more && model.CoverageComplete && !model.Records.IsEmpty,
            "The real provider-to-native history stream catches up without a fabricated gap or unbounded query");
        var commander = model.Actions.LastOrDefault(action => action.SourceName == "Rhys the Redeemed"
            && action.Records.Any(record => record.Payment?.Kind == "opened"));
        check(commander != null && commander.Records.Any(record => record.Payment is { Kind: "opened", BillBefore: { Length: > 0 } }),
            "Actual commander payment keeps its symbolic bill under the paid execution through the shipping decoder");
        var mana = commander!.Children.Select(id => model.Actions.Single(action => action.Id == id))
            .Where(action => action.Kind == ExecutionKind.Mana).ToArray();
        check(mana.Any(action => action.SourceName == "Forest" && action.ParentId == commander.Id
                && action.Records.Any(record => record.Payment is { Kind: "produced", Produced: { } produced }
                    && produced.GetValueOrDefault("G") == 1)),
            "Actual Forest production is a named one-green mana child of its paid commander, not an adjacent-poll guess");
        check(commander.Records.Any(record => record.Payment is { Kind: "spent", Spent: { } spent }
                && spent.GetValueOrDefault("G") == 1)
            && commander.Records.Any(record => record.Payment is { Kind: "completed", BillAfter: { IsEmpty: true } }),
            "The same real commander receipt records actual green consumption and a known empty completed bill");
        check(model.Records.Any(record => record.Payment is { Kind: "restored", RestoredExecutionId: not null }),
            "The earlier certified manual-mana Undo remains an explicit restored occurrence instead of erasing its production history");
        var replay = MatchHistoryReadModel.Read(batches);
        check(replay.Actions.Select(action => action.Id).SequenceEqual(model.Actions.Select(action => action.Id)),
            "The actual Java-created history rebuilds identically without another engine request or command");
        await File.WriteAllTextAsync(Path.Combine(profile, "actual-provider-history-batches.json"),
            JsonSerializer.Serialize(new { scope = "Synthetic real-engine test; authorized viewing-seat facts, not a support export.", batches },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
