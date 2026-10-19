using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine
{
    private static WorkshopState MapWorkshop(JsonElement data)
    {
        // CardInfo is the engine catalog representation. Keep that translation at
        // the adapter boundary, as with ordinary saved-deck entries.
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        var considering = data.GetProperty("considering").EnumerateArray().Select(entry =>
            new ConsideringCard(Text(entry, "cardId"), MapCatalogCard(entry.GetProperty("card")),
                entry.GetProperty("quantity").GetInt32(), Read<string[]>(entry.GetProperty("categories")))).ToArray();
        node["considering"] = new JsonArray();
        return node.Deserialize<WorkshopState>(Json)! with { Considering = considering };
    }
    private static DeckSourcePreview MapSourcePreview(JsonElement data)
    {
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        var cards = data.GetProperty("cards").EnumerateArray().Select(entry =>
            new DeckSourceCard(Text(entry, "cardId"), MapCatalogCard(entry.GetProperty("card")),
                entry.GetProperty("quantity").GetInt32(), Text(entry, "section"),
                Read<string[]>(entry.GetProperty("categories")), entry.GetProperty("included").GetBoolean())).ToArray();
        node["cards"] = new JsonArray();
        return node.Deserialize<DeckSourcePreview>(Json)! with { Cards = cards };
    }
    public async Task<WorkshopState> WorkshopAsync(string deckId, long revision) =>
        MapWorkshop(await Request("workshop", new { deckId, revision }));
    public async Task<WorkshopState> UpdateWorkshopAsync(string deckId, long revision, WorkshopMetadataEdit edit)
    {
        var args = new Dictionary<string, object?> { ["deckId"] = deckId, ["revision"] = revision };
        if (edit.MetadataRevision != null) args["metadataRevision"] = edit.MetadataRevision;
        if (edit.Plan != null) args["plan"] = edit.Plan;
        if (edit.Question != null) args["question"] = edit.Question;
        if (edit.Roles != null) args["roles"] = edit.Roles.Select(r => new { cardId = r.CardId, roles = r.Roles, pinned = r.Pinned, origin = r.Origin });
        if (edit.Considering != null) args["considering"] = edit.Considering.Select(c => new { cardId = c.CardId, quantity = c.Quantity, categories = c.Categories });
        return MapWorkshop(await Request("workshopMetadata", args));
    }
    public async Task<DeckDetails> RestoreWorkshopVersionAsync(string deckId, long revision, string versionId) =>
        MapDeck(await Request("workshopRestore", new { deckId, revision, versionId }));
    public async Task<DeckDetails> MoveWorkshopCardAsync(string deckId, long revision, long metadataRevision, string cardId, string to, string? cutCardId = null) =>
        MapDeck(await Request("workshopMove", new { deckId, revision, metadataRevision, cardId, to, cutCardId }));
    public async Task<DeckSourcePreview> PreviewDeckSourceAsync(string url) =>
        MapSourcePreview(await Request("workshopSourcePreview", new { url }));
    public async Task<DeckSourcePreview> RefreshDeckSourceAsync(string deckId, long revision) =>
        MapSourcePreview(await Request("workshopSourceRefresh", new { deckId, revision }));
    public async Task<DeckDetails> ApplyDeckSourceAsync(string token, string? deckId, long? revision, string choice) =>
        MapDeck(await Request("workshopSourceApply", new { token, deckId, revision, choice }));
    private static DeckTest MapTest(JsonElement data)
    {
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        var hand = data.TryGetProperty("hand", out var cards) && cards.ValueKind == JsonValueKind.Array
            ? cards.EnumerateArray().Select(MapCatalogCard).ToArray() : [];
        node["hand"] = new JsonArray();
        return node.Deserialize<DeckTest>(Json)! with { Hand = hand };
    }
    public async Task<DeckTest[]> GetTestsAsync(string deckId) => (await Request("workshopTests", new { deckId })).EnumerateArray().Select(MapTest).ToArray();
    public async Task<HandPractice> PracticeAsync(string deckId, long revision, string action, int? index = null, string? question = null)
    {
        var data = await Request("workshopPractice", new { deckId, revision, action, index, question });
        return new(Text(data, "testId"), Text(data, "versionId"),
            data.GetProperty("hand").EnumerateArray().Select(MapCatalogCard).ToArray(),
            data.GetProperty("libraryCount").GetInt32(), data.GetProperty("draws").GetInt32(),
            data.GetProperty("redraws").GetInt32(), Text(data, "status"));
    }
    public async Task<DeckTest> StartDeckTestAsync(string deckId, long revision, IReadOnlyList<string> opponents, string question) =>
        MapTest(await Request("workshopStartTest", new { deckId, revision, opponents, question }));
    public async Task<DeckTest> SaveTestNoteAsync(string deckId, string testId, string note) =>
        MapTest(await Request("workshopTestNote", new { deckId, testId, note }));
}
