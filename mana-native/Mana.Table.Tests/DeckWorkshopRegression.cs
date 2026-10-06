using Mana.Contracts;
using Mana.Forge;
using System.Text.Json;

namespace Mana.Table;

internal static class DeckWorkshopRegression
{
    public static async Task RunAsync(ForgePaths paths)
    {
        await using var engine = new ForgeEngine(paths);
        var checks = new List<string>();
        void Check(bool valid, string description) { if (!valid) throw new InvalidOperationException(description); checks.Add(description); }
        async Task Reject(Func<Task> action, string description) {
            try { await action(); } catch (InvalidOperationException) { Check(true, description); return; }
            throw new InvalidOperationException(description);
        }
        string Contents(DeckDetails d) => string.Join("\n", d.Entries.Select(e => $"{e.Section}:{e.Id}:{e.Quantity}").Order());
        await engine.InitializeAsync();
        var page = await engine.SearchCardsAsync(new("Forest", Type: "Land", ColorIdentity: 16, MaxManaValue: 0));
        Check(page.Cards.Any(c => c.Card.Name == "Forest") && page.Cards.All(c => c.Card.Type.Contains("Land") && (c.ColorIdentity & ~16) == 0 && c.ManaValue == 0), "Search combines name/type/color identity/mana filters");
        var forest = page.Cards.Single(c => c.Card.Name == "Forest");
        var next = await engine.SearchCardsAsync(new("Forest", Type: "Land", ColorIdentity: 16, MaxManaValue: 0, Offset: 6));
        Check(page.Total > 6 && !page.Cards.Select(c => c.Id).Intersect(next.Cards.Select(c => c.Id)).Any(), "Catalog pagination does not repeat printings");
        Check((await engine.SearchCardsAsync(new("Lightning Bolt", ColorIdentity: 17))).Cards.Length == 0, "Commander color identity excludes off-color cards");
        var rhys = (await engine.SearchCardsAsync(new("Rhys the Redeemed"))).Cards.Single(c => c.Card.Name == "Rhys the Redeemed");
        var ring = (await engine.SearchCardsAsync(new("Sol Ring"))).Cards.Single(c => c.Card.Name == "Sol Ring");
        var bolt = (await engine.SearchCardsAsync(new("Lightning Bolt"))).Cards.Single(c => c.Card.Name == "Lightning Bolt");
        var doubleFace = (await engine.SearchCardsAsync(new("Bala Ged Recovery"))).Cards.First(c => c.Card.OtherFace != null);
        Check(doubleFace.Card.OtherFace!.ArtFace == "back" && doubleFace.Card.OtherFace.OracleText.Length > 0, "Catalog inspection preserves the other face and its rules");
        var delver = (await engine.SearchCardsAsync(new("Delver of Secrets"))).Cards.First(c => c.Card.Name == "Delver of Secrets");
        Check(delver.Card.OtherFace is { Power: 3, Toughness: 2 }, "Alternate creature faces map printed power and toughness");
        var deck = await engine.CreateDeckAsync("Workshop regression");
        Check(deck.Entries.Length == 0 && deck.Problem.Length > 0, "New Commander deck starts as a saved, incomplete draft");
        deck = await engine.EditDeckAsync(deck.Id, deck.Revision, [new("Main", forest.Id, 98), new("Main", rhys.Id, 1), new("Main", ring.Id, 1)]);
        deck = await engine.SetCommandersAsync(deck.Id, deck.Revision, [rhys.Id]);
        Check(deck.Problem.Length == 0 && deck.Entries.Sum(e => e.Quantity) == 100, "A deck built from catalog cards passes Commander validation");
        string valid = Contents(deck);
        await Reject(() => engine.EditDeckAsync(deck.Id, deck.Revision - 1, [new("Main", forest.Id, 2)]), "Stale edit revisions are rejected");
        await Reject(() => engine.EditDeckAsync(deck.Id, deck.Revision, [new("Main", forest.Id, 2), new("Main", "missing-card", 1)]), "A bad card in a batch rejects the entire edit");
        deck = await engine.EditDeckAsync(deck.Id, deck.Revision, [new("Main", ring.Id, 0), new("Sideboard", ring.Id, 1)]);
        Check(deck.Entries.Sum(e => e.Quantity) == 100 && deck.Entries.Single(e => e.Id == ring.Id).Section == "Sideboard" && deck.Problem.Length > 0, "Moving cards to sideboard preserves copies and updates main-deck legality");
        deck = await engine.UndoDeckAsync(deck.Id, deck.Revision);
        Check(Contents(deck) == valid && deck.CanRedo, "Undo restores the entire section move atomically");
        deck = await engine.RedoDeckAsync(deck.Id, deck.Revision);
        Check(deck.Entries.Single(e => e.Id == ring.Id).Section == "Sideboard", "Redo restores the moved card");
        deck = await engine.UndoDeckAsync(deck.Id, deck.Revision);
        deck = await engine.EditDeckAsync(deck.Id, deck.Revision, [new("Main", forest.Id, 97), new("Main", ring.Id, 2)]);
        Check(deck.Entries.Sum(e => e.Quantity) == 100 && deck.Problem.Length > 0, "Singleton violations remain visible even when the total is exactly 100");
        deck = await engine.UndoDeckAsync(deck.Id, deck.Revision);
        deck = await engine.EditDeckAsync(deck.Id, deck.Revision, [new("Main", forest.Id, 97), new("Main", bolt.Id, 1)]);
        Check(deck.Entries.Sum(e => e.Quantity) == 100 && deck.Problem.Contains("Lightning Bolt"), "Off-color cards are identified by authoritative validation");
        deck = await engine.UndoDeckAsync(deck.Id, deck.Revision);
        deck = await engine.RenameDeckAsync(deck.Id, deck.Revision, "Renamed workshop regression");
        Check(deck.Name == "Renamed workshop regression" && Contents(deck) == valid, "Renaming preserves the exact deck contents");
        string originalId = deck.Id;
        var copy = await engine.DuplicateDeckAsync(deck.Id, deck.Revision, "Separate copy");
        Check(copy.Id != deck.Id && Contents(copy) == valid, "Duplicate keeps exact printings in a separate saved deck");
        await Reject(() => engine.EditDeckAsync(originalId, copy.Revision, [new("Main", forest.Id, 1)]), "An action for another deck cannot edit the active copy, even at the same revision");
        copy = await engine.EditDeckAsync(copy.Id, copy.Revision, [new("Main", ring.Id, 0), new("Sideboard", ring.Id, 1)]);
        string exported = await engine.ExportDeckAsync(copy.Id, copy.Revision);
        Check(exported.Contains("Commander") && exported.Contains("Sideboard") && exported.Contains("98 Forest"), "Export includes quantities and separate commander/sideboard sections");
        var imported = await engine.ImportAsync("Export round trip", exported);
        var restored = await engine.OpenDeckAsync(imported.Id);
        Check(restored.Entries.Sum(e => e.Quantity) == 100 && restored.CommanderIds.Length == 1 && restored.Entries.Any(e => e.Section == "Sideboard" && e.Card.Name == "Sol Ring"), "Export/import round trip preserves section counts");
        deck = await engine.OpenDeckAsync(originalId);
        Check(deck.Name == "Renamed workshop regression" && Contents(deck) == valid && deck.Problem.Length == 0, "Editing the duplicate leaves the original saved deck intact");
        await File.WriteAllTextAsync(Path.Combine(paths.Profile, "workshop-adapter-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
