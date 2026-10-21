using Mana.Contracts;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// A real engine drives these frames. This covers the complete tune/test/return
// loop, while source HTTP parsing and merge conflicts have deterministic API tests.
internal sealed class DeckTuningSmoke(TableWindow.Probe view) : ITableAutomation
{
    private readonly List<string> checks = [];
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(7);
    private int stage, next, mousePhase;
    private Point mouse;
    private string version = "", sampleId = "", candidateId = "";
    private long beforeTestRevision;
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    private DeckDetails Deck => view.DeckDetails!;
    private WorkshopState Workshop => view.Workshop!;
    private DeckEntry Entry(string name) => Deck.Entries.First(e => e.Card.Name == name);
    private void Check(bool valid, string description)
    {
        if (!valid) { Capture("tuning-failure"); throw new InvalidOperationException("Deck tuning stage " + stage + ": " + description); }
        checks.Add(description); File.AppendAllText(Path.Combine(view.Profile, "deck-tuning-progress.log"), description + "\n");
    }
    private void Capture(string name)
    {
        using var output = File.Create(Path.Combine(view.Profile, name + ".png"));
        view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
        File.WriteAllText(Path.Combine(view.Profile, name + ".json"), JsonSerializer.Serialize(new { Deck = view.DeckDetails, Workshop = view.Workshop, Tests = view.WorkshopTests, Hand = view.WorkshopHand, Match = view.Match }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void Click(string id, int target)
    {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException($"Missing {id} at tuning stage {stage}");
        var p = hit.Bounds.Center; var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + p.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + p.Y * scale));
        ExpectedClick = id; ClickAccepted = false; next = target; mousePhase = 1;
    }
    private void Row(string name, int target) { var card = Entry(name); Click("deck-card:" + card.Section + ":" + card.Id, target); }
    public MouseState ReadMouse()
    {
        bool down = mousePhase == 1;
        if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    public void Frame(Texture2D surface)
    {
        if (DateTime.UtcNow > deadline) { Capture("tuning-failure"); throw new TimeoutException("Deck tuning timed out at stage " + stage); }
        if (view.Error.Length > 0 || view.WorkshopError.Length > 0) { Capture("tuning-failure"); throw new InvalidOperationException(view.Error + " " + view.WorkshopError); }
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) {
            if (!ClickAccepted) { Capture("tuning-failure"); throw new InvalidOperationException("Rejected tuning click " + ExpectedClick); }
            mousePhase = 0; ExpectedClick = null; stage = next;
        }
        if (!view.Loaded || view.Busy || view.ActivityOpen) return;
        if (view.DeckEditor && !view.WorkshopCurrent) return;
        switch (stage) {
            case 0:
                view.ImportDeck("Deck tuning journey", "Commander\n1 Rhys the Redeemed\n\nMain\n98 Forest\n1 Sol Ring"); stage++; break;
            case 1:
                Check(view.DeckEditor && Deck.Problem.Length == 0 && Workshop.Versions.Length > 0, "A saved Commander list opens with a durable playable version and live tools");
                Check(view.Hits.Any(h => h.Id == "workshop-test") && view.Hits.Any(h => h.Id == "workshop-source"), "Testing and Archidekt are available in the deck workspace");
                version = Workshop.VersionId;
                view.SetWorkshopPlan("Make tokens and keep the lands flowing.", "Can I make my first three land drops?"); stage++; break;
            case 2:
                Check(Workshop.Plan.StartsWith("Make tokens") && Workshop.Question.StartsWith("Can I") && Workshop.VersionId == version, "Plan and question save without changing the playable version");
                Row("Sol Ring", 3); break;
            case 3: Click("workshop-card-tools", 4); break;
            case 4: Click("workshop-card-role:Draw", 5); break;
            case 5: Click("workshop-card-pin", 6); break;
            case 6:
                Check(Workshop.Roles.Any(r => r.CardId == Entry("Sol Ring").Id && r.Pinned && r.Roles.Contains("Draw")), "A card can have overlapping custom roles and a favorite marker");
                Click("workshop-card-consider", 7); break;
            case 7:
                Check(Deck.Problem.Length > 0 && Workshop.Considering.Any(c => c.Card.Card.Name == "Sol Ring") && Deck.Entries.All(e => e.Card.Name != "Sol Ring"), "Moving a card to Considering excludes it from the played list and updates deck checks");
                Check(view.DeckIssueBounds != null && view.Hits.Any(h => h.Id == "workshop-insights"), "A draft keeps its live deck checks and tuning insights available");
                Capture("tuning-01-invalid-draft"); Click("workshop-insights", 8); break;
            case 8:
                Check(view.WorkshopDialog == "insights" && Workshop.Insights.RoleCounts.Count > 0, "Composition and role counts remain available while a deck is invalid");
                Click("workshop-dialog-close", 9); break;
            case 9: Click("deck-considering", 10); break;
            case 10:
                var option = Workshop.Considering.Single(c => c.Card.Card.Name == "Sol Ring");
                Click("deck-card:Considering:" + option.CardId, 11); break;
            case 11: Click("deck-move", 12); break;
            case 12:
                Check(Deck.Problem.Length == 0 && Workshop.Considering.Length == 0 && Entry("Sol Ring").Section == "Main", "Returning a considered card restores a legal list without losing its roles");
                Click("workshop-test", 13); break;
            case 13:
                beforeTestRevision = Deck.Revision;
                Click("workshop-hand-new", 14); break;
            case 14:
                Check(view.WorkshopHand is { Hand.Length: 7, LibraryCount: 92, Draws: 0 }, "The sampler draws seven from the actual 99-card Commander library");
                sampleId = view.WorkshopHand!.TestId; version = view.WorkshopHand.VersionId;
                Click("workshop-hand-draw", 15); break;
            case 15:
                Check(view.WorkshopHand is { Hand.Length: 8, Draws: 1, LibraryCount: 91 }, "Sample draws conserve cards and record an observed draw count");
                Click("workshop-hand-redraw", 16); break;
            case 16:
                Check(view.WorkshopHand is { Hand.Length: 7, Redraws: 1, Draws: 0 }, "Draw-only redraw returns to a shuffled seven without claiming London mulligans");
                Click("workshop-hand-keep", 17); break;
            case 17:
                Check(view.WorkshopHand!.Status == "completed" && !view.Hits.Any(h => h.Id == "workshop-hand-draw"), "Recording a keep freezes the sample and disables further changes to it");
                view.SaveWorkshopNote(sampleId, "Kept this sample. I want more meaningful early plays."); stage++; break;
            case 18: Click("workshop-results", 19); break;
            case 19:
                Check(view.WorkshopTests.Single(t => t.Id == sampleId) is { Note.Length: > 0, Kind: "hand", Status: "completed" }, "Results show the real sample with its exact version and saved player note");
                Check(Deck.Revision == beforeTestRevision, "Preparing a test preserves the open editor revision and undo history");
                Capture("tuning-02-recorded-hand"); Click("workshop-review-saved-hand", 190); break;
            case 190:
                Check(view.WorkshopDialog == "saved-hand" && view.WorkshopTests.Single(t => t.Id == sampleId).Hand.Length == 7, "Recorded results retain the actual saved hand for later inspection");
                Click("workshop-saved-card:0", 191); break;
            case 191:
                Check(view.WorkshopDialog == "saved-card", "A recorded card opens with its readable rules and printing");
                Click("workshop-dialog-close", 192); break;
            case 192: Click("workshop-dialog-close", 193); break;
            case 193: Click("workshop-results-build", 20); break;
            case 20: Click("deck-search-library", 21); break;
            case 21: view.TypeDeckText("Farseek"); stage++; break;
            case 22:
                if (view.CatalogBusy || view.Catalog == null) break;
                var farseek = view.Catalog.Cards.First(c => c.Card.Name == "Farseek"); candidateId = farseek.Id;
                Click("catalog-card:" + farseek.Id, 23); break;
            case 23: Click("workshop-card-tools", 24); break;
            case 24: Click("workshop-card-consider", 25); break;
            case 25:
                Check(Workshop.Considering.Any(c => c.CardId == candidateId) && Deck.Entries.All(e => e.Id != candidateId), "A searched library card can be saved as an option without entering the playable deck");
                Click("deck-considering", 26); break;
            case 26: Click("deck-card:Considering:" + candidateId, 27); break;
            case 27: Click("workshop-card-tools", 28); break;
            case 28: Click("workshop-card-swap", 29); break;
            case 29: Click("workshop-swap:" + Entry("Forest").Id, 30); break;
            case 30:
                Check(Deck.Problem.Length == 0 && Entry("Forest").Quantity == 97 && Entry("Farseek").Quantity == 1 && Workshop.Considering.Any(c => c.Card.Card.Name == "Forest"), "A paired swap brings in the candidate, moves the cut to Considering, and preserves 100 playable cards");
                Check(Workshop.VersionId != version && view.WorkshopTests.Single(t => t.Id == sampleId).VersionId == version, "Tuning creates a new playable version while old evidence stays attached to the tested list");
                Click("deck-undo", 300); break;
            case 300:
                Check(Entry("Forest").Quantity == 98 && Deck.Entries.All(e => e.Card.Name != "Farseek") && Workshop.Considering.Any(c => c.CardId == candidateId) && Workshop.Considering.All(c => c.Card.Card.Name != "Forest"), "Undo reverses the entire paired swap, including candidate and cut membership in Considering");
                Click("deck-redo", 301); break;
            case 301:
                Check(Entry("Forest").Quantity == 97 && Entry("Farseek").Quantity == 1 && Workshop.Considering.All(c => c.CardId != candidateId) && Workshop.Considering.Any(c => c.Card.Card.Name == "Forest"), "Redo reapplies the paired swap and its Considering changes together");
                Click("workshop-history", 31); break;
            case 31:
                Check(view.WorkshopDialog == "versions" && Workshop.Versions.Length > 1, "Durable version history is available directly from the workspace");
                Capture("tuning-03-history"); Click("workshop-dialog-close", 32); break;
            case 32: Click("workshop-test", 33); break;
            case 33:
                Check(!view.Hits.Any(h => h.Id == "workshop-players:6"), "The native workspace offers only table sizes supported by its renderer");
                Click("workshop-players:2", 34); break;
            case 34:
                Check(view.Hits.Any(h => h.Id == "workshop-start-game"), "A complete tuned list can launch a two-player real AI test");
                Capture("tuning-04-table-setup"); Click("workshop-start-game", 35); break;
            case 35:
                if (view.Match == null || view.Match.Status == "starting" || view.Match.Players.Length == 0) break;
                Check(view.LocalMatch && view.Match.Players.Length == 2 && view.Match.Status != "error", "The configured real Forge test starts with the human and one AI opponent");
                view.ConcedeWorkshop(); stage++; break;
            case 36:
                if (view.Match != null || !view.DeckEditor || !view.WorkshopCurrent) break;
                Check(view.WorkshopTab == "results" && view.WorkshopTests.Any(t => t.Kind == "game" && t.Status == "conceded"), "Conceding returns directly to the deck results with a durable conceded test record");
                Check(view.WorkshopTests.Any(t => t.Id == sampleId && t.Note.StartsWith("Kept")) && Deck.Problem.Length == 0, "Returning from a real game retains earlier notes and the tuned playable deck");
                Capture("tuning-05-return-to-tune");
                File.WriteAllText(Path.Combine(view.Profile, "deck-tuning-ui-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                stage++; view.Exit(); break;
        }
    }
}
